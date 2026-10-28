using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using SkiaSharp;
using NetTopologySuite.Geometries;

namespace GeoNex.Services
{
    public unsafe class FastShapeReader
    {
        private static FileShare GetOptimalFileShare(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (full.StartsWith(@"\\", StringComparison.Ordinal))
                    return FileShare.Read;
                string? root = Path.GetPathRoot(full);
                if (!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network)
                    return FileShare.Read;
            }
            catch
            {
            }
            return FileShare.ReadWrite | FileShare.Delete;
        }

        public static (double MinX, double MaxX, double MinY, double MaxY) GetExtent(string shpPath)
        {
            using var handle = File.OpenHandle(shpPath, FileMode.Open, FileAccess.Read, GetOptimalFileShare(shpPath));
            Span<byte> headerBounds = stackalloc byte[32];
            int read = RandomAccess.Read(handle, headerBounds, 36);
            if (read < 32) return (0, 0, 0, 0);
            double minX = BinaryPrimitives.ReadDoubleLittleEndian(headerBounds.Slice(0, 8));
            double minY = BinaryPrimitives.ReadDoubleLittleEndian(headerBounds.Slice(8, 8));
            double maxX = BinaryPrimitives.ReadDoubleLittleEndian(headerBounds.Slice(16, 8));
            double maxY = BinaryPrimitives.ReadDoubleLittleEndian(headerBounds.Slice(24, 8));
            return (minX, maxX, minY, maxY);
        }

        public static string GetGeometryType(string shpPath)
        {
            try
            {
                if (!File.Exists(shpPath)) return "POLIGONO";
                using var handle = File.OpenHandle(shpPath, FileMode.Open, FileAccess.Read, GetOptimalFileShare(shpPath));
                Span<byte> typeBuffer = stackalloc byte[4];
                int read = RandomAccess.Read(handle, typeBuffer, 32);
                if (read < 4) return "POLIGONO";
                int shapeType = BinaryPrimitives.ReadInt32LittleEndian(typeBuffer);
                return shapeType switch
                {
                    1 or 8 or 11 or 18 or 21 or 28 => "PONTO",
                    3 or 13 or 23 => "LINHA",
                    5 or 15 or 25 => "POLIGONO",
                    _ => "POLIGONO"
                };
            }
            catch
            {
                return "POLIGONO";
            }
        }

        public unsafe static (List<CompiledFeature> Features, MemoryMappedShapefile ShpData) ReadAllFeatures(
            string filePath, string layerName, double offsetX, double offsetY,
            string? catColumnName = null, string? sourceFidMappingPath = null, bool sourceFidsAreNative = true)
        {
            var features = new List<CompiledFeature>();
            
            // 1. Carrega o DBF via MemoryMappedFile (ponteiro persistente)
            string dbfPath = Path.ChangeExtension(filePath, ".dbf");
            bool hasDbf = File.Exists(dbfPath);
            System.IO.MemoryMappedFiles.MemoryMappedFile? dbfMmf = null;
            System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? dbfAccessor = null;
            byte* dbfPtr = null;
            long dbfLength = 0;
            System.IO.MemoryMappedFiles.MemoryMappedFile? sourceFidMmf = null;
            System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? sourceFidAccessor = null;
            byte* sourceFidPtr = null;

            if (hasDbf)
            {
                var fs = new FileStream(dbfPath, FileMode.Open, FileAccess.Read, GetOptimalFileShare(filePath), 64 * 1024, FileOptions.SequentialScan);
                dbfLength = fs.Length;
                dbfMmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(fs, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read, System.IO.HandleInheritability.None, false);
                dbfAccessor = dbfMmf.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                dbfAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref dbfPtr);
            }

            var dbfFields = new List<(string Name, int Offset, int Length)>();
            int catFieldIndex = -1;
            int dbfHeaderBytes = 0;
            int dbfRecordBytes = 0;

            if (dbfPtr != null)
            {
                dbfHeaderBytes = *(short*)(dbfPtr + 8);
                dbfRecordBytes = *(short*)(dbfPtr + 10);
                
                int currentOffset = 1; 
                int fieldOffset = 32;
                while (dbfPtr[fieldOffset] != 0x0D && fieldOffset < dbfHeaderBytes)
                {
                    string fieldName = new string((sbyte*)dbfPtr, fieldOffset, 11, Encoding.ASCII).TrimEnd('\0', ' ');
                    int fieldLength = dbfPtr[fieldOffset + 16];
                    
                    dbfFields.Add((fieldName, currentOffset, fieldLength));
                    currentOffset += fieldLength;
                    fieldOffset += 32;
                }
                
                if (catColumnName != null) {
                    catFieldIndex = dbfFields.FindIndex(f => f.Name.Equals(catColumnName, StringComparison.OrdinalIgnoreCase));
                }
            }
            
            // 2. Carrega o SHP via MemoryMappedFile
            // The live map keeps this view open between frames. Share writes so GDAL can append
            // newly digitized features to the same Shapefile on Windows while this read map lives.
            var shpStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, GetOptimalFileShare(filePath), 64 * 1024, FileOptions.SequentialScan);
            long fileLength = shpStream.Length;
            var shpMmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(
                shpStream, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read,
                System.IO.HandleInheritability.None, leaveOpen: false);
            var shpAccessor = shpMmf.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            byte* ptr = null;
            shpAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

            bool metadataReady = false;
            try
            {
                if (!string.IsNullOrWhiteSpace(sourceFidMappingPath) && File.Exists(sourceFidMappingPath))
                {
                    var fidStream = new FileStream(sourceFidMappingPath, FileMode.Open, FileAccess.Read,
                        GetOptimalFileShare(filePath), 64 * 1024, FileOptions.SequentialScan);
                    sourceFidMmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(
                        fidStream, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read,
                        System.IO.HandleInheritability.None, leaveOpen: false);
                    sourceFidAccessor = sourceFidMmf.CreateViewAccessor(0, 0,
                        System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                    sourceFidAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref sourceFidPtr);
                }

                // O SHX tem uma entrada fixa de 8 bytes por feicao e fornece offsets
                // exatos. Num SHP de 2 GB com 7,8 milhões de lotes isto evita reservar mais de
                // 100 MB com a antiga heuristica fileLength/100 e evita varrer 2 GB.
                string shxPath = Path.ChangeExtension(filePath, ".shx");
                int indexedRecordCount = 0;
                FileStream? shxStream = null;
                if (File.Exists(shxPath))
                {
                    shxStream = new FileStream(shxPath, FileMode.Open, FileAccess.Read,
                        GetOptimalFileShare(filePath), ShapefileIndexReader.BufferBytes, FileOptions.SequentialScan);
                    long shxLength = shxStream.Length;
                    if (shxLength >= 100 && (shxLength - 100) % 8 == 0)
                    {
                        long count64 = (shxLength - 100) / 8;
                        if (count64 <= int.MaxValue) indexedRecordCount = (int)count64;
                    }
                    if (indexedRecordCount <= 0)
                    {
                        shxStream.Dispose();
                        shxStream = null;
                    }
                }

                var pool = ArrayPool<long>.Shared;
                long[] offsets = pool.Rent(Math.Max(1024, indexedRecordCount));
                try
                {
                    int recordCount = 0;

                    if (indexedRecordCount > 0 && shxStream != null)
                    {
                        using (shxStream)
                        {
                            shxStream.Position = 100;
                            ShapefileIndexReader.Read(shxStream, offsets.AsSpan(0, indexedRecordCount), fileLength);
                            recordCount = indexedRecordCount;
                        }
                    }
                    else
                    {
                        shxStream?.Dispose();
                        HardwareGovernor.PrefetchMappedMemory(ptr, fileLength);
                        long offset = 100; // fallback para SHP sem indice companheiro
                        while (offset + 8 <= fileLength)
                        {
                            if (recordCount >= offsets.Length)
                            {
                                var newOffsets = pool.Rent(checked(offsets.Length * 2));
                                Array.Copy(offsets, newOffsets, recordCount);
                                pool.Return(offsets);
                                offsets = newOffsets;
                            }

                            offsets[recordCount++] = offset;
                            byte* recPtr = ptr + offset;
                            uint contentLengthWords = ((uint)recPtr[4] << 24) | ((uint)recPtr[5] << 16) |
                                                      ((uint)recPtr[6] << 8) | recPtr[7];
                            long nextOffset = offset + 8L + contentLengthWords * 2L;
                            if (nextOffset <= offset || nextOffset > fileLength) break;
                            offset = nextOffset;
                        }
                    }

                    // Pilar 2: Prefetch assíncrono via DMA do sistema operacional nas páginas do SHP e DBF
                    if (recordCount > 0)
                    {
                        HardwareGovernor.PrefetchShapefileOffsets(ptr, fileLength, offsets.AsSpan(0, recordCount));
                        if (dbfPtr != null && dbfLength > 0)
                        {
                            HardwareGovernor.PrefetchMappedMemory(dbfPtr, dbfLength, dbfHeaderBytes);
                        }
                    }

                    // Pilar 2: Interning pool de CategoryValue para evitar milhões de strings duplicadas na Gen2 do GC
                    var categoryStringPool = catFieldIndex >= 0
                        ? new ConcurrentDictionary<string, string>(StringComparer.Ordinal)
                        : null;

                    // Reserva o backing array da List uma única vez e o preenche em
                    // paralelo por blocos contíguos (preservando read-ahead sequencial no NVMe e no SMB).
                    features = new List<CompiledFeature>(recordCount);
                    CollectionsMarshal.SetCount(features, recordCount);
                    int workers = GeoNexHardware.WorkersFor(recordCount);
                    long dbfCapacity = dbfAccessor?.Capacity ?? 0;
                    long sourceFidCapacity = sourceFidAccessor?.Capacity ?? 0;
                    int chunkSize = Math.Max(2048, (recordCount + workers - 1) / Math.Max(1, workers));
                    int chunkCount = (recordCount + chunkSize - 1) / chunkSize;

                    Parallel.For(0, chunkCount, new ParallelOptions { MaxDegreeOfParallelism = workers }, chunkIndex =>
                    {
                        int start = chunkIndex * chunkSize;
                        int end = Math.Min(recordCount, start + chunkSize);
                        Span<CompiledFeature> dstSpan = CollectionsMarshal.AsSpan(features);

                        for (int i = start; i < end; ++i)
                        {
                            long recOffset = offsets[i];
                            byte* recPtr = ptr + recOffset;
                            byte* dataPtr = recPtr + 8;
                            int shapeType = *(int*)dataPtr;

                            var feature = new CompiledFeature
                            {
                                FID = i,
                                SourceFeatureId = sourceFidPtr == null && sourceFidMappingPath is null && sourceFidsAreNative ? i : -1,
                                EnvelopeWorld = FeatureEnvelope.Empty,
                                Kind = GeometryKind.Unknown,
                                DataOffset = recOffset,
                                LayerName = layerName
                            };

                            // Shapefile deletions are tombstones in the DBF; the SHP/SHX
                            // records remain in place. Keep their original record index
                            // as the FID, but omit deleted rows from the live map.
                            if (dbfPtr != null && dbfRecordBytes > 0)
                            {
                                long rowOffset = dbfHeaderBytes + ((long)i * dbfRecordBytes);
                                if (rowOffset >= 0 && rowOffset < dbfCapacity && dbfPtr[rowOffset] == (byte)'*')
                                {
                                    dstSpan[i] = feature;
                                    continue;
                                }
                            }

                            if (dbfPtr != null && catFieldIndex >= 0)
                            {
                                var field = dbfFields[catFieldIndex];
                                long dbfRecOffset = dbfHeaderBytes + ((long)i * dbfRecordBytes);
                                if (dbfRecOffset + field.Offset + field.Length <= dbfCapacity)
                                {
                                    string rawCat = GeoHelpers.ReadDbfString(dbfPtr + dbfRecOffset + field.Offset, field.Length, GeoHelpers.Iso8859);
                                    feature.CategoryValue = categoryStringPool!.GetOrAdd(rawCat, rawCat);
                                }
                            }

                            if (sourceFidPtr != null && sourceFidCapacity > 0)
                            {
                                long sourceFidOffset = (long)i * sizeof(long);
                                if (sourceFidOffset >= 0 && sourceFidOffset + sizeof(long) <= sourceFidCapacity)
                                    feature.SourceFeatureId = *((long*)(sourceFidPtr + sourceFidOffset));
                            }

                            if (shapeType == 1 || shapeType == 11 || shapeType == 21)
                            {
                                feature.Kind = GeometryKind.Point;
                                double x = *(double*)(dataPtr + 4);
                                double y = *(double*)(dataPtr + 12);
                                feature.EnvelopeWorld.ExpandToInclude(x, y);
                                feature.SetCentroidLocalPrecise(x - offsetX, offsetY - y);
                            }
                            else if (shapeType == 3 || shapeType == 13 || shapeType == 23) // PolyLine
                            {
                                feature.Kind = GeometryKind.Line;
                                double minx = *(double*)(dataPtr + 4);
                                double miny = *(double*)(dataPtr + 12);
                                double maxx = *(double*)(dataPtr + 20);
                                double maxy = *(double*)(dataPtr + 28);
                                feature.EnvelopeWorld.Init(minx, maxx, miny, maxy);
                                feature.CentroidLocal = new SKPoint((float)((minx + maxx) / 2 - offsetX), -(float)((miny + maxy) / 2 - offsetY));
                            }
                            else if (shapeType == 5 || shapeType == 15 || shapeType == 25) // Polygon
                            {
                                feature.Kind = GeometryKind.Polygon;
                                double minx = *(double*)(dataPtr + 4);
                                double miny = *(double*)(dataPtr + 12);
                                double maxx = *(double*)(dataPtr + 20);
                                double maxy = *(double*)(dataPtr + 28);
                                feature.EnvelopeWorld.Init(minx, maxx, miny, maxy);
                                feature.CentroidLocal = new SKPoint((float)((minx + maxx) / 2 - offsetX), -(float)((miny + maxy) / 2 - offsetY));
                            }

                            dstSpan[i] = feature;
                        }
                    });

                    // SHPs normais não têm registros Unknown. Compacta in-place apenas
                    // se o arquivo contiver NullShape/tipos não suportados.
                    int validCount = 0;
                    var featureSpan = CollectionsMarshal.AsSpan(features);
                    for (int i = 0; i < featureSpan.Length; ++i)
                    {
                        var f = featureSpan[i];
                        if (f != null && f.Kind != GeometryKind.Unknown)
                            featureSpan[validCount++] = f;
                    }
                    if (validCount < features.Count)
                        features.RemoveRange(validCount, features.Count - validCount);
                }
                finally
                {
                    pool.Return(offsets);
                }
                metadataReady = true;
            }
            finally
            {
                // Os ponteiros usados na indexacao pertencem a esta chamada. O objeto
                // persistente abaixo adquire exatamente uma referencia propria.
                if (ptr != null)
                {
                    shpAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    ptr = null;
                }
                if (dbfPtr != null && dbfAccessor != null)
                {
                    dbfAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    dbfPtr = null;
                }
                if (sourceFidPtr != null && sourceFidAccessor != null)
                {
                    sourceFidAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    sourceFidPtr = null;
                }
                sourceFidAccessor?.Dispose();
                sourceFidMmf?.Dispose();
                if (!metadataReady)
                {
                    shpAccessor.Dispose();
                    shpMmf.Dispose();
                    dbfAccessor?.Dispose();
                    dbfMmf?.Dispose();
                }
            }

            // Construir MemoryMappedShapefile com ponteiros persistentes (zero syscall depois)
            var shpResult = new MemoryMappedShapefile 
            { 
                File = shpMmf, 
                Accessor = shpAccessor,
                FileLength = fileLength
            };
            // Adquirir ponteiro persistente do SHP
            shpResult.AcquireShpPointer();

            if (hasDbf)
            {
                shpResult.DbfPath = dbfPath;
                shpResult.DbfHeaderBytes = dbfHeaderBytes;
                shpResult.DbfRecordBytes = dbfRecordBytes;
                shpResult.DbfFields = dbfFields.Select(f => new DbfField { Name = f.Name, Offset = f.Offset, Length = f.Length }).ToList();
                // Transferir ponteiro DBF persistente
                shpResult.SetDbfMapped(dbfMmf!, dbfAccessor!);
            }
            else
            {
                dbfAccessor?.Dispose();
                dbfMmf?.Dispose();
            }

            return (features, shpResult);
        }
    }
}
