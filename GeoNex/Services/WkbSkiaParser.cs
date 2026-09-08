using System;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using SkiaSharp;
using NetTopologySuite.Geometries;

namespace GeoNex.Services
{
    public enum GeometryKind { Unknown, Point, Line, Polygon }

    public struct FeatureEnvelope
    {
        public double MinX;
        public double MaxX;
        public double MinY;
        public double MaxY;

        public static FeatureEnvelope Empty => new()
        {
            MinX = double.PositiveInfinity,
            MaxX = double.NegativeInfinity,
            MinY = double.PositiveInfinity,
            MaxY = double.NegativeInfinity
        };

        public FeatureEnvelope(double minX, double maxX, double minY, double maxY)
        {
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
        }

        public readonly bool IsNull => MinX > MaxX || MinY > MaxY;
        public readonly double Width => IsNull ? 0.0 : MaxX - MinX;
        public readonly (double X, double Y) Centre =>
            IsNull ? (0.0, 0.0) : ((MinX + MaxX) * 0.5, (MinY + MaxY) * 0.5);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Init(double minX, double maxX, double minY, double maxY)
        {
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ExpandToInclude(double x, double y)
        {
            if (x < MinX) MinX = x;
            if (x > MaxX) MaxX = x;
            if (y < MinY) MinY = y;
            if (y > MaxY) MaxY = y;
        }
    }

    public sealed class CompiledFeature
    {
        public long FID; // Usado para Lazy Loading dos Atributos
        public GeometryKind Kind;
        public SKPath? Path;
        public long DataOffset; // Offset de 64 bits: SHP pode ultrapassar o limite de 2 GB
        public SKPoint CentroidLocal;
        public FeatureEnvelope EnvelopeWorld;
        public string? CategoryValue; // Opcional, apenas quando Symbology ativa
        public string LayerName = ""; // Referência à camada para lazy loading

        /// <summary>
        /// Lock-free lazy path construction.
        /// Usa Interlocked.CompareExchange em vez de lock(this) para eliminar contenção.
        /// Recebe MemoryMappedShapefile diretamente para usar o ponteiro persistente (zero syscalls).
        /// </summary>
        public unsafe SKPath? GetPath(MemoryMappedShapefile? shp, double offsetX, double offsetY, OSGeo.OSR.CoordinateTransformation? overrideTransform = null, bool useLod = false)
        {
            // Fast path — já compilado
            SKPath? existing = Volatile.Read(ref Path);
            if (existing != null) return existing;
            if (shp == null) return null;

            byte* basePtr = shp.ShpPointer;
            if (basePtr == null) return null;
            
            // Pega o transformador da thread atual (se existir)
            var transform = overrideTransform ?? shp.TransformLocal?.Value;

            // Construir path sem lock
            SKPath? newPath = null;
            try
            {
                byte* dataPtr = basePtr + DataOffset + 8; // Avança o header do record
                int shapeType = *(int*)dataPtr;

                // 3 = PolyLine, 5 = Polygon, 13 = PolyLineZ, 15 = PolygonZ, 23 = PolyLineM, 25 = PolygonM
                if (shapeType == 3 || shapeType == 5 || shapeType == 13 || shapeType == 15 || shapeType == 23 || shapeType == 25)
                {
                    int numParts = *(int*)(dataPtr + 36);
                    int numPoints = *(int*)(dataPtr + 40);

                    if (numParts > 0 && numPoints > 0)
                    {
                        int* parts = (int*)(dataPtr + 44);
                        byte* pointsBase = dataPtr + 44 + (numParts * 4);

                        bool isPolygon = Kind == GeometryKind.Polygon;
                        var p = new SKPath { FillType = isPolygon ? SKPathFillType.EvenOdd : SKPathFillType.Winding };

                        for (int i = 0; i < numParts; i++)
                        {
                            int startIdx = parts[i];
                            int endIdx = (i == numParts - 1) ? numPoints : parts[i + 1];
                            int count = endIdx - startIdx;

                            if (count > 0)
                            {
                                float[] partPoints = new float[count * 2];

                                if (transform != null)
                                {
                                    // BATCH TRANSFORM (Estilo ArcGIS/QGIS)
                                    double[] xArray = new double[count];
                                    double[] yArray = new double[count];
                                    double[] zArray = new double[count]; // Dummy
                                    
                                    // Extração rápida de memória
                                    for (int j = 0; j < count; j++) {
                                        int idx = startIdx + j;
                                        xArray[j] = *(double*)(pointsBase + (idx * 16));
                                        yArray[j] = *(double*)(pointsBase + (idx * 16) + 8);
                                    }
                                    
                                    // Transformação nativa ultra-rápida interna no PROJ (GDAL) em um único call C++!
                                    transform.TransformPoints(count, xArray, yArray, zArray);
                                    
                                    // Aplica offset e flip Y convertendo para float
                                    for (int j = 0; j < count; j++) {
                                        partPoints[j * 2] = (float)(xArray[j] - offsetX);
                                        partPoints[j * 2 + 1] = -(float)(yArray[j] - offsetY);
                                    }
                                }
                                else
                                {
                                    // FAST PATH - ZERO TRANSFORM (Rota Direta AVX2 Nativa C++)
                                    fixed (float* outPtr = partPoints) {
                                        NativeMethods.ParseShapefilePartAVX2(
                                            pointsBase + (startIdx * 16), 
                                            outPtr, 
                                            count, 
                                            offsetX, 
                                            offsetY);
                                    }
                                }

                                int finalCount = count;
                                float[] finalPoints = partPoints;

                                // --- FIM DA TRANSFORMAÇÃO DE PONTOS (Douglas-Peucker removido para máxima qualidade QGIS) ---
                                // A matriz de Skia vai cuidar das transformações nativas a nível de GPU.
                                
                                if (count > 0)
                                {
                                    // CAST ZERO-COST DE FLOAT PARA SKPOINT! 
                                    // Em vez de um loop lento 'LineTo', despejamos o array nativo inteiro no motor C++ do Skia.
                                    var pointSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<float, SKPoint>(new ReadOnlySpan<float>(partPoints, 0, count * 2));
                                    p.AddPoly(pointSpan, isPolygon);
                                }
                            }
                        }

                        if (!p.IsEmpty)
                        {
                            newPath = p;
                        }
                        else
                        {
                            p.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                try { Console.Error.WriteLine($"FID {FID} Error: {ex.Message}\n{ex.StackTrace}"); } catch { }
            }

            if (newPath == null) return null;

            // CAS atômico — primeira thread ganha, as demais descartam
            var winner = Interlocked.CompareExchange(ref Path, newPath, null);
            if (winner != null)
            {
                // Outra thread já preencheu — descartar o nosso
                newPath.Dispose();
                return winner;
            }

            return newPath;
        }

        [ThreadStatic]
        private static float[]? _threadBufferIn;
        [ThreadStatic]
        private static float[]? _threadBufferOut;

        public static unsafe bool BuildBatchPathFromNative(
            SKPath batchPath,
            MemoryMappedShapefile shp,
            System.Collections.Generic.IList<CompiledFeature> feicoesList,
            double offsetX,
            double offsetY,
            float zoomReal,
            ref SKRect viewport,
            bool isInteracting,
            CancellationToken cancellationToken = default)
        {
            if (feicoesList.Count == 0 || shp.ShpPointer == null) return true;

            int count = feicoesList.Count;
            const int nativeFeatureBatchSize = 4096;
            long[] offsets = System.Buffers.ArrayPool<long>.Shared.Rent(nativeFeatureBatchSize);
            float[] output = System.Buffers.ArrayPool<float>.Shared.Rent(1_048_576); // 4 MB, cresce apenas se uma feicao exigir
            int gridWidth = Math.Clamp((int)Math.Ceiling(viewport.Width * zoomReal) + 2, 1, 16_384);
            int gridHeight = Math.Clamp((int)Math.Ceiling(viewport.Height * zoomReal) + 2, 1, 16_384);
            int gridCellCount = checked(gridWidth * gridHeight);
            byte[] microGrid = System.Buffers.ArrayPool<byte>.Shared.Rent(gridCellCount);
            Array.Clear(microGrid, 0, gridCellCount);
            nint nativeCancellation = 0;
            CancellationTokenRegistration nativeCancellationRegistration = default;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cancellationToken.CanBeCanceled)
                {
                    nativeCancellation = NativeMethods.CreateRenderCancellation();
                    if (nativeCancellation == 0)
                        throw new InvalidOperationException("Nao foi possivel criar o token de cancelamento nativo.");

                    nativeCancellationRegistration = cancellationToken.UnsafeRegister(
                        static state => NativeMethods.CancelRender((nint)state!),
                        nativeCancellation);
                }

                // O erro visual da simplificacao e definido em pixels, nao em metros.
                // Assim o resultado permanece estavel em qualquer CRS/escala.
                float tolerancePixels = isInteracting ? 0.85f : 0.30f;
                float microLodPixels = isInteracting ? 1.25f : 0.65f;
                bool closeParts = feicoesList[0].Kind == GeometryKind.Polygon;
                int featureCursor = 0;
                int loadedOffsetCount = 0;
                int loadedOffsetCursor = 0;

                while (featureCursor < count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (loadedOffsetCursor == loadedOffsetCount)
                    {
                        loadedOffsetCount = Math.Min(nativeFeatureBatchSize, count - featureCursor);
                        loadedOffsetCursor = 0;
                        for (int i = 0; i < loadedOffsetCount; ++i)
                        {
                            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                            offsets[i] = feicoesList[featureCursor + i].DataOffset;
                        }
                    }
                    int requested = loadedOffsetCount - loadedOffsetCursor;
                    int floatsWritten;
                    int processed;
                    int required;

                    fixed (long* pOffsets = &offsets[loadedOffsetCursor])
                    fixed (float* outBuffer = output)
                    fixed (byte* occupancy = microGrid)
                    {
                        floatsWritten = NativeMethods.ProcessShapeBatchV4(
                            shp.ShpPointer,
                            shp.FileLength,
                            nativeCancellation,
                            pOffsets,
                            requested,
                            outBuffer,
                            output.Length,
                            offsetX,
                            offsetY,
                            zoomReal,
                            tolerancePixels,
                            microLodPixels,
                            viewport.Left,
                            viewport.Top,
                            viewport.Right,
                            viewport.Bottom,
                            occupancy,
                            gridWidth,
                            gridHeight,
                            out processed,
                            out required,
                            out _,
                            out _,
                            out _);
                    }

                    if (floatsWritten == -1)
                        throw new OperationCanceledException("O processamento vetorial nativo foi cancelado.", cancellationToken);
                    if (floatsWritten == -2)
                        throw new System.IO.InvalidDataException("O SHP contem um record invalido ou truncado.");
                    if (floatsWritten < 0)
                        throw new InvalidOperationException($"Falha no processador vetorial nativo (status {floatsWritten}).");
                    if (floatsWritten > output.Length || required < 0)
                        throw new InvalidOperationException("O processador vetorial nativo retornou tamanhos invalidos.");
                    if (processed < 0 || processed > requested)
                        throw new InvalidOperationException("O processador vetorial nativo retornou um cursor de batch invalido.");
                    cancellationToken.ThrowIfCancellationRequested();

                    int outputIndex = 0;
                    while (outputIndex < floatsWritten)
                    {
                        float command = output[outputIndex++];
                        if (command == -4.0f)
                        {
                            if (outputIndex + 4 > floatsWritten) break;
                            batchPath.AddRect(new SKRect(
                                output[outputIndex], output[outputIndex + 1],
                                output[outputIndex + 2], output[outputIndex + 3]));
                            outputIndex += 4;
                            continue;
                        }

                        int pointCount = (int)command;
                        int coordinateCount = pointCount * 2;
                        if (pointCount <= 0 || outputIndex + coordinateCount > floatsWritten) break;

                        var points = System.Runtime.InteropServices.MemoryMarshal.Cast<float, SKPoint>(
                            new ReadOnlySpan<float>(output, outputIndex, coordinateCount));
                        batchPath.AddPoly(points, closeParts);
                        outputIndex += coordinateCount;
                    }

                    featureCursor += processed;
                    loadedOffsetCursor += processed;
                    if (processed > 0) continue;

                    // Uma unica feicao e maior que o buffer corrente. O nativo informa
                    // exatamente o necessario; crescemos sem manter 200 MB por thread.
                    if (required > output.Length)
                    {
                        float[] replacement = System.Buffers.ArrayPool<float>.Shared.Rent(required);
                        System.Buffers.ArrayPool<float>.Shared.Return(output);
                        output = replacement;
                        continue;
                    }

                    // Defesa contra SHP corrompido ou ABI incompativel: nunca entrar em loop infinito.
                    throw new InvalidOperationException("O processador vetorial nativo nao conseguiu avancar no batch.");
                }

                // Mescla pixels contíguos na horizontal. Mesmo que milhões de lotes
                // sejam subpixel, o SKPath recebe no máximo O(altura do viewport)
                // retângulos para uma área totalmente preenchida.
                float inverseZoom = 1.0f / zoomReal;
                for (int y = 0; y < gridHeight; ++y)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int row = y * gridWidth;
                    int x = 0;
                    while (x < gridWidth)
                    {
                        while (x < gridWidth && microGrid[row + x] == 0) ++x;
                        if (x >= gridWidth) break;
                        int startX = x++;
                        while (x < gridWidth && microGrid[row + x] != 0) ++x;

                        batchPath.AddRect(new SKRect(
                            viewport.Left + startX * inverseZoom,
                            viewport.Top + y * inverseZoom,
                            viewport.Left + x * inverseZoom,
                            viewport.Top + (y + 1) * inverseZoom));
                    }
                }

                return true;
            }
            finally
            {
                try
                {
                    // Dispose aguarda qualquer callback em curso antes de liberar o handle.
                    nativeCancellationRegistration.Dispose();
                }
                finally
                {
                    if (nativeCancellation != 0)
                        NativeMethods.DestroyRenderCancellation(nativeCancellation);
                    System.Buffers.ArrayPool<long>.Shared.Return(offsets);
                    System.Buffers.ArrayPool<float>.Shared.Return(output);
                    System.Buffers.ArrayPool<byte>.Shared.Return(microGrid);
                }
            }
        }

        private const int TransformPointBatchSize = 262_144;

        /// <summary>
        /// Reprojeta as geometrias em lotes limitados. A versão anterior alugava três
        /// vetores do tamanho de TODOS os vértices visíveis; em camadas grandes isso
        /// podia transformar um único frame em vários GB temporários. Os buffers de
        /// reprojecao ficam limitados (~6 MB de doubles) e voltam ao pool mesmo quando
        /// um pan cancela o frame. Este e o caminho de referencia: preserva todos os
        /// vertices, sem stride ou LOD por amostragem. O SKPath final e o custo de CPU
        /// crescem com a geometria ate existir simplificacao com erro visual controlado.
        /// </summary>
        public static unsafe void BuildBatchPathWithTransform(
            SKPath batchPath, MemoryMappedShapefile shp,
            System.Collections.Generic.IList<CompiledFeature> feicoesList,
            double offsetX, double offsetY,
            float resolution, float zoomReal,
            OSGeo.OSR.CoordinateTransformation transform,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (feicoesList.Count == 0 || shp.ShpPointer == null) return;

            byte* basePtr = shp.ShpPointer;
            int featureCount = feicoesList.Count;
            double[] x = System.Buffers.ArrayPool<double>.Shared.Rent(TransformPointBatchSize);
            double[] y = System.Buffers.ArrayPool<double>.Shared.Rent(TransformPointBatchSize);
            double[] z = System.Buffers.ArrayPool<double>.Shared.Rent(TransformPointBatchSize);

            try
            {
                int featureIndex = 0;
                while (featureIndex < featureCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte* firstData = basePtr + feicoesList[featureIndex].DataOffset + 8;
                    int firstType = *(int*)firstData;
                    int firstPoints = IsLineOrPolygonShape(firstType) ? *(int*)(firstData + 40) : 0;

                    // Uma única geometria pode, excepcionalmente, ultrapassar o lote.
                    // Transformar em chunks preserva cada vertice sem buffers gigantes.
                    if (firstPoints > TransformPointBatchSize)
                    {
                        AppendOversizedTransformedFeature(
                            batchPath, firstData, offsetX, offsetY,
                            transform, x, y, z, cancellationToken);
                        featureIndex++;
                        continue;
                    }

                    int batchStart = featureIndex;
                    int batchEnd = featureIndex;
                    int totalPoints = 0;
                    while (batchEnd < featureCount)
                    {
                        if ((batchEnd & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                        byte* data = basePtr + feicoesList[batchEnd].DataOffset + 8;
                        int shapeType = *(int*)data;
                        int numPoints = IsLineOrPolygonShape(shapeType) ? *(int*)(data + 40) : 0;
                        if (numPoints > TransformPointBatchSize ||
                            (totalPoints > 0 && numPoints > TransformPointBatchSize - totalPoints))
                            break;

                        totalPoints += Math.Max(numPoints, 0);
                        batchEnd++;
                        if (totalPoints >= TransformPointBatchSize) break;
                    }

                    // Defesa contra um registro inválido que não permitiu avanço.
                    if (batchEnd == batchStart)
                    {
                        featureIndex++;
                        continue;
                    }

                    if (totalPoints > 0)
                    {
                        int cursor = 0;
                        for (int i = batchStart; i < batchEnd; ++i)
                        {
                            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                            byte* data = basePtr + feicoesList[i].DataOffset + 8;
                            int shapeType = *(int*)data;
                            if (!IsLineOrPolygonShape(shapeType)) continue;
                            int numParts = *(int*)(data + 36);
                            int numPoints = *(int*)(data + 40);
                            if (numParts <= 0 || numPoints <= 0 || numParts > numPoints) continue;
                            byte* points = data + 44 + numParts * 4;
                            for (int point = 0; point < numPoints; ++point)
                            {
                                if ((point & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                                x[cursor + point] = *(double*)(points + point * 16);
                                y[cursor + point] = *(double*)(points + point * 16 + 8);
                            }
                            cursor += numPoints;
                        }

                        Array.Clear(z, 0, cursor);
                        transform.TransformPoints(cursor, x, y, z);
                        cancellationToken.ThrowIfCancellationRequested();

                        int pointCursor = 0;
                        for (int i = batchStart; i < batchEnd; ++i)
                        {
                            if ((i & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
                            byte* data = basePtr + feicoesList[i].DataOffset + 8;
                            int shapeType = *(int*)data;
                            if (!IsLineOrPolygonShape(shapeType)) continue;
                            int numParts = *(int*)(data + 36);
                            int numPoints = *(int*)(data + 40);
                            if (numParts <= 0 || numPoints <= 0 || numParts > numPoints) continue;

                            AppendTransformedFeature(
                                batchPath, data, shapeType, numParts, numPoints, pointCursor,
                                x, y, offsetX, offsetY, cancellationToken);
                            pointCursor += numPoints;
                        }
                    }

                    featureIndex = batchEnd;
                }
            }
            finally
            {
                System.Buffers.ArrayPool<double>.Shared.Return(x);
                System.Buffers.ArrayPool<double>.Shared.Return(y);
                System.Buffers.ArrayPool<double>.Shared.Return(z);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsLineOrPolygonShape(int shapeType) =>
            shapeType == 3 || shapeType == 13 || shapeType == 23 ||
            shapeType == 5 || shapeType == 15 || shapeType == 25;

        private static void EnsureThreadOutputCapacity(int pointCount)
        {
            int needed = checked((pointCount + 2) * 2);
            if (_threadBufferOut == null || _threadBufferOut.Length < needed)
                _threadBufferOut = new float[Math.Max(needed + 1024, 50_000)];
        }

        private static unsafe void AppendTransformedFeature(
            SKPath batchPath, byte* data, int shapeType, int numParts, int numPoints,
            int pointCursor, double[] x, double[] y, double offsetX, double offsetY,
            CancellationToken cancellationToken)
        {
            bool isPolygon = shapeType == 5 || shapeType == 15 || shapeType == 25;
            int* parts = (int*)(data + 44);
            for (int part = 0; part < numParts; ++part)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int start = parts[part];
                int end = part == numParts - 1 ? numPoints : parts[part + 1];
                int count = end - start;
                if (start < 0 || end > numPoints || count < (isPolygon ? 3 : 2)) continue;

                // Este metodo recebe apenas feicoes que cabem no lote de transformacao.
                // A conversao para floats tambem fica limitada a TransformPointBatchSize.
                EnsureThreadOutputCapacity(count);
                for (int point = 0; point < count; ++point)
                {
                    if ((point & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    int global = pointCursor + start + point;
                    _threadBufferOut![point * 2] = (float)(x[global] - offsetX);
                    _threadBufferOut[point * 2 + 1] = -(float)(y[global] - offsetY);
                }

                var points = System.Runtime.InteropServices.MemoryMarshal.Cast<float, SKPoint>(
                    new ReadOnlySpan<float>(_threadBufferOut, 0, count * 2));
                batchPath.AddPoly(points, isPolygon);
            }
        }

        private static unsafe void AppendOversizedTransformedFeature(
            SKPath batchPath, byte* data, double offsetX, double offsetY,
            OSGeo.OSR.CoordinateTransformation transform,
            double[] x, double[] y, double[] z, CancellationToken cancellationToken)
        {
            int shapeType = *(int*)data;
            int numParts = *(int*)(data + 36);
            int numPoints = *(int*)(data + 40);
            if (numParts <= 0 || numPoints <= 0 || numParts > numPoints) return;
            bool isPolygon = shapeType == 5 || shapeType == 15 || shapeType == 25;

            int* parts = (int*)(data + 44);
            byte* pointsBase = data + 44 + (long)numParts * 4;
            for (int part = 0; part < numParts; ++part)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int start = parts[part];
                int end = part == numParts - 1 ? numPoints : parts[part + 1];
                int count = end - start;
                if (start < 0 || end > numPoints || count < (isPolygon ? 3 : 2)) continue;

                int consumed = 0;
                while (consumed < count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int chunkCount = Math.Min(TransformPointBatchSize, count - consumed);
                    for (int point = 0; point < chunkCount; ++point)
                    {
                        if ((point & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                        long source = (long)start + consumed + point;
                        x[point] = *(double*)(pointsBase + source * 16);
                        y[point] = *(double*)(pointsBase + source * 16 + 8);
                    }

                    Array.Clear(z, 0, chunkCount);
                    transform.TransformPoints(chunkCount, x, y, z);
                    cancellationToken.ThrowIfCancellationRequested();

                    // Continuar o mesmo contorno entre chunks: AddPoly por chunk
                    // criaria novos aneis e ligacoes de fechamento artificiais.
                    for (int point = 0; point < chunkCount; ++point)
                    {
                        if ((point & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                        float px = (float)(x[point] - offsetX);
                        float py = -(float)(y[point] - offsetY);
                        if (consumed == 0 && point == 0) batchPath.MoveTo(px, py);
                        else batchPath.LineTo(px, py);
                    }
                    consumed += chunkCount;
                }
                if (isPolygon) batchPath.Close();
            }
        }

        public unsafe void AppendToPath(SKPath batchPath, MemoryMappedShapefile shp, double offsetX, double offsetY, float resolution, ref SKRect viewport, OSGeo.OSR.CoordinateTransformation? overrideTransform = null)
        {
            byte* basePtr = shp.ShpPointer;
            if (basePtr == null) return;
            
            var transform = overrideTransform ?? shp.TransformLocal?.Value;

            byte* dataPtr = basePtr + DataOffset + 8;
            int shapeType = *(int*)dataPtr;

            if (shapeType == 3 || shapeType == 5 || shapeType == 13 || shapeType == 15 || shapeType == 23 || shapeType == 25)
            {
                int numParts = *(int*)(dataPtr + 36);
                int numPoints = *(int*)(dataPtr + 40);
                bool IsPolygon = (shapeType == 5 || shapeType == 15 || shapeType == 25);

                if (numParts > 0 && numPoints > 0)
                {
                    try 
                    {
                        if (_threadBufferIn == null || _threadBufferIn.Length < numPoints * 2) _threadBufferIn = new float[numPoints * 2 + 1024];
                        if (_threadBufferOut == null || _threadBufferOut.Length < numPoints * 2) _threadBufferOut = new float[numPoints * 2 + 1024];

                        int* parts = (int*)(dataPtr + 44);
                        byte* pointsBase = dataPtr + 44 + (numParts * 4);

                        for (int i = 0; i < numParts; i++)
                        {
                            int startIdx = parts[i];
                            int endIdx = (i == numParts - 1) ? numPoints : parts[i + 1];
                            int count = endIdx - startIdx;

                            if (count > 0)
                            {
                                if (transform != null)
                                {
                                    double[] xArray = new double[count];
                                    double[] yArray = new double[count];
                                    double[] zArray = new double[count];
                                    
                                    for (int j = 0; j < count; j++) {
                                        int idx = startIdx + j;
                                        xArray[j] = *(double*)(pointsBase + (idx * 16));
                                        yArray[j] = *(double*)(pointsBase + (idx * 16) + 8);
                                    }
                                    
                                    transform.TransformPoints(count, xArray, yArray, zArray);
                                    
                                    for (int j = 0; j < count; j++) {
                                        _threadBufferIn[j * 2] = (float)(xArray[j] - offsetX);
                                        _threadBufferIn[j * 2 + 1] = -(float)(yArray[j] - offsetY);
                                    }
                                }
                                else
                                {
                                    fixed (float* outPtr = _threadBufferIn) {
                                        NativeMethods.ParseShapefilePartAVX2(
                                            pointsBase + (startIdx * 16), 
                                            outPtr, 
                                            count, 
                                            offsetX, 
                                            offsetY);
                                    }
                                }

                                int finalCount = count;
                                float[] finalPoints = _threadBufferIn;

                                if (resolution > 0.0001f)
                                {
                                    fixed (float* inPtr = _threadBufferIn)
                                    fixed (float* outPtr = _threadBufferOut)
                                    {
                                        finalCount = NativeMethods.QuantizeGeometry(inPtr, count, outPtr, resolution);
                                    }
                                    finalPoints = _threadBufferOut;
                                }

                                if (finalCount > 0)
                                {
                                    // Adiciona margem de seguranca de 500px fisicos convertidos para coordenadas de mundo local
                                    float margin = 500f * (resolution <= 0 ? 1 : resolution);
                                    float minX = viewport.Left - margin;
                                    float minY = viewport.Top - margin;
                                    float maxX = viewport.Right + margin;
                                    float maxY = viewport.Bottom + margin;
                                    
                                    bool needsClipping = false;
                                    for (int k = 0; k < finalCount; k++) {
                                        float px = finalPoints[k * 2];
                                        float py = finalPoints[k * 2 + 1];
                                        if (px < minX || px > maxX || py < minY || py > maxY) {
                                            needsClipping = true;
                                            break;
                                        }
                                    }

                                    if (needsClipping && IsPolygon)
                                    {
                                        var clippedPts = ClipPolygonSutherlandHodgman(finalPoints, finalCount, minX, minY, maxX, maxY);
                                        if (clippedPts.Count >= 6) { // A polygon needs at least 3 points (6 floats)
                                            var pointSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<float, SKPoint>(new ReadOnlySpan<float>(clippedPts.ToArray(), 0, clippedPts.Count));
                                            batchPath.AddPoly(pointSpan, true);
                                        }
                                    }
                                    else
                                    {
                                        var pointSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<float, SKPoint>(new ReadOnlySpan<float>(finalPoints, 0, finalCount * 2));
                                        batchPath.AddPoly(pointSpan, IsPolygon);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        try { Console.Error.WriteLine($"FID {FID} Error: {ex.Message}\n{ex.StackTrace}"); } catch { }
                    }
                }
            }
        }

        private static List<float> ClipPolygonSutherlandHodgman(float[] points, int count, float minX, float minY, float maxX, float maxY)
        {
            var output = new List<float>(count * 2);
            for (int i = 0; i < count * 2; i++) output.Add(points[i]);

            output = ClipEdge(output, minX, minY, maxX, maxY, 0);
            if (output.Count == 0) return output;
            output = ClipEdge(output, minX, minY, maxX, maxY, 1);
            if (output.Count == 0) return output;
            output = ClipEdge(output, minX, minY, maxX, maxY, 2);
            if (output.Count == 0) return output;
            output = ClipEdge(output, minX, minY, maxX, maxY, 3);

            return output;
        }

        private static List<float> ClipEdge(List<float> input, float minX, float minY, float maxX, float maxY, int edge)
        {
            var output = new List<float>(input.Count);
            if (input.Count < 6) return output;

            float p1x = input[input.Count - 2];
            float p1y = input[input.Count - 1];

            for (int i = 0; i < input.Count; i += 2)
            {
                float p2x = input[i];
                float p2y = input[i + 1];

                bool p1Inside = IsInside(p1x, p1y, edge, minX, minY, maxX, maxY);
                bool p2Inside = IsInside(p2x, p2y, edge, minX, minY, maxX, maxY);

                if (p1Inside != p2Inside)
                {
                    Intersect(p1x, p1y, p2x, p2y, edge, minX, minY, maxX, maxY, out float ix, out float iy);
                    output.Add(ix);
                    output.Add(iy);
                }

                if (p2Inside)
                {
                    output.Add(p2x);
                    output.Add(p2y);
                }

                p1x = p2x;
                p1y = p2y;
            }

            return output;
        }

        private static bool IsInside(float x, float y, int edge, float minX, float minY, float maxX, float maxY)
        {
            return edge switch { 0 => x >= minX, 1 => x <= maxX, 2 => y >= minY, 3 => y <= maxY, _ => false };
        }

        private static void Intersect(float x1, float y1, float x2, float y2, int edge, float minX, float minY, float maxX, float maxY, out float ix, out float iy)
        {
            ix = 0; iy = 0;
            if (edge == 0) { ix = minX; iy = y1 + (y2 - y1) * (minX - x1) / (x2 - x1); }
            else if (edge == 1) { ix = maxX; iy = y1 + (y2 - y1) * (maxX - x1) / (x2 - x1); }
            else if (edge == 2) { iy = minY; ix = x1 + (x2 - x1) * (minY - y1) / (y2 - y1); }
            else if (edge == 3) { iy = maxY; ix = x1 + (x2 - x1) * (maxY - y1) / (y2 - y1); }
        }
    }

    /// <summary>
    /// Helpers estáticos de alta performance para leitura binária.
    /// </summary>
    public static class GeoHelpers
    {
        public static readonly Encoding Iso8859 = Encoding.GetEncoding("ISO-8859-1");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe string ReadDbfString(byte* strStart, int maxLen, Encoding enc)
        {
            int len = maxLen;
            while (len > 0 && strStart[len - 1] <= 0x20) len--;
            int start = 0;
            while (start < len && strStart[start] <= 0x20) start++;
            return len > start ? new string((sbyte*)strStart, start, len - start, enc) : "";
        }
    }

    public unsafe static class WkbSkiaParser
    {
        public static CompiledFeature ParseGeometry(long fid, byte[] wkb, double offsetX, double offsetY, string? categoryValue)
        {
            var feature = new CompiledFeature
            {
                FID = fid,
                CategoryValue = categoryValue,
                EnvelopeWorld = FeatureEnvelope.Empty,
                Kind = GeometryKind.Unknown
            };

            if (wkb == null || wkb.Length < 5) return feature;

            fixed (byte* ptr = wkb)
            {
                byte* current = ptr;

                // 1. Byte Order
                byte byteOrder = *current;
                current++;

                // 2. Geometry Type
                uint type = *(uint*)current;
                current += 4;

                bool subZ = false;
                bool subM = false;

                // Extrair as bit-flags das geometrias EWKB (ex: 2.5D ou M)
                if ((type & 0x80000000) != 0) { subZ = true; type &= 0x7FFFFFFF; }
                if ((type & 0x40000000) != 0) { subM = true; type &= 0xBFFFFFFF; }
                if ((type & 0x20000000) != 0) { type &= 0xDFFFFFFF; current += 4; } // Ignorar SRID embedado

                // NTS Extended Format (Tipo + 1000/2000/3000)
                if (type >= 1000 && type < 4000)
                {
                    uint d = type / 1000;
                    if (d == 1 || d == 3) subZ = true;
                    if (d == 2 || d == 3) subM = true;
                    type %= 1000;
                }

                if (type == 1) // Point
                {
                    feature.Kind = GeometryKind.Point;
                    ParsePoint(ref current, offsetX, offsetY, ref feature, subZ, subM);
                }
                else if (type == 2) // LineString
                {
                    feature.Kind = GeometryKind.Line;
                    feature.Path = new SKPath();
                    ParseLineString(ref current, offsetX, offsetY, feature.Path, ref feature.EnvelopeWorld, subZ, subM);
                }
                else if (type == 3) // Polygon
                {
                    feature.Kind = GeometryKind.Polygon;
                    feature.Path = new SKPath { FillType = SKPathFillType.EvenOdd };
                    ParsePolygon(ref current, offsetX, offsetY, feature.Path, ref feature.EnvelopeWorld, subZ, subM);
                }

                // Calcular centroide para efeitos de LOD point rendering
                if (feature.Kind != GeometryKind.Point && !feature.EnvelopeWorld.IsNull)
                {
                    feature.CentroidLocal = new SKPoint(
                        (float)(feature.EnvelopeWorld.Centre.X - offsetX),
                        -(float)(feature.EnvelopeWorld.Centre.Y - offsetY)
                    );
                }
            }

            return feature;
        }

        private static void ParsePoint(ref byte* current, double offsetX, double offsetY, ref CompiledFeature feature, bool hasZ, bool hasM)
        {
            double x = *(double*)current; current += 8;
            double y = *(double*)current; current += 8;

            if (hasZ) current += 8;
            if (hasM) current += 8;

            feature.EnvelopeWorld.ExpandToInclude(x, y);
            feature.CentroidLocal = new SKPoint((float)(x - offsetX), -(float)(y - offsetY));
        }

        private static void ParseLineString(ref byte* current, double offsetX, double offsetY, SKPath path, ref FeatureEnvelope env, bool hasZ, bool hasM)
        {
            uint numPoints = *(uint*)current; current += 4;
            if (numPoints == 0) return;

            for (uint i = 0; i < numPoints; i++)
            {
                double x = *(double*)current; current += 8;
                double y = *(double*)current; current += 8;

                if (hasZ) current += 8;
                if (hasM) current += 8;

                env.ExpandToInclude(x, y);

                float px = (float)(x - offsetX);
                float py = -(float)(y - offsetY);

                if (i == 0) path.MoveTo(px, py);
                else path.LineTo(px, py);
            }
        }

        private static void ParsePolygon(ref byte* current, double offsetX, double offsetY, SKPath path, ref FeatureEnvelope env, bool hasZ, bool hasM)
        {
            uint numRings = *(uint*)current; current += 4;

            for (uint r = 0; r < numRings; r++)
            {
                uint numPoints = *(uint*)current; current += 4;
                if (numPoints == 0) continue;

                for (uint i = 0; i < numPoints; i++)
                {
                    double x = *(double*)current; current += 8;
                    double y = *(double*)current; current += 8;

                    if (hasZ) current += 8;
                    if (hasM) current += 8;

                    env.ExpandToInclude(x, y);

                    float px = (float)(x - offsetX);
                    float py = -(float)(y - offsetY);

                    if (i == 0) path.MoveTo(px, py);
                    else path.LineTo(px, py);
                }
                path.Close();
            }
        }
    }
}
