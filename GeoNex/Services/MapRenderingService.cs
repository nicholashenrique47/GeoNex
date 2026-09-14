using SkiaSharp;
using OSGeo.GDAL;
using System;
using System.Collections.Generic;
using NetTopologySuite.Features;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Geometries;
using System.Linq;

namespace GeoNex.Services
{
    public readonly record struct RasterCacheMetadata(
        string CacheKey,
        float PanX,
        float PanY,
        float Zoom,
        MapCoordinateFrame? Frame = null);

    public readonly record struct GlobalCacheMetadata(
        float Zoom,
        float PanX,
        float PanY,
        int CssWidth,
        int CssHeight,
        int PhysicalWidth,
        int PhysicalHeight,
        MapCoordinateFrame Frame = default,
        float CameraZoom = 1)
    {
        public bool Matches(MapViewportMetrics viewport) =>
            CssWidth == viewport.CssWidth &&
            CssHeight == viewport.CssHeight &&
            PhysicalWidth == viewport.PhysicalWidth &&
            PhysicalHeight == viewport.PhysicalHeight;
    }

    public class DbfField
    {
        public string Name { get; set; } = "";
        public int Offset { get; set; }
        public int Length { get; set; }
    }

    public unsafe class MemoryMappedShapefile : IDisposable
    {
        public System.IO.MemoryMappedFiles.MemoryMappedFile? File { get; set; }
        public System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? Accessor { get; set; }
        public long FileLength { get; set; }
        
        // Ponteiro persistente do SHP (adquirido uma vez, libertado no Dispose)
        private byte* _shpPtr;
        private bool _shpAcquired;
        
        // DBF memory-mapped persistente (zero I/O para LerAtributos)
        private System.IO.MemoryMappedFiles.MemoryMappedFile? _dbfMmf;
        private System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? _dbfAccessor;
        private byte* _dbfPtr;
        private bool _dbfAcquired;
        
        public string DbfPath { get; set; } = "";
        public int DbfHeaderBytes { get; set; }
        public int DbfRecordBytes { get; set; }
        public List<DbfField> DbfFields { get; set; } = new();
        public string LayerSRS { get; set; } = "";
        
        // Transformador de coordenadas (Thread-Safe para renderização paralela de alta performance)
        public System.Threading.ThreadLocal<OSGeo.OSR.CoordinateTransformation>? TransformLocal { get; set; }

        // Cache de geometria simplificada com overscan. Uma copia de SKPath e barata
        // (copy-on-write no Skia) e impede corridas entre render/compositor.
        private static readonly RenderPathCache SharedRenderPaths = new(
            budgetProvider: () => VectorRuntimeResources.Current.CacheBytes);
        private static long _nextRenderPathOwner;
        private readonly long _renderPathOwner = System.Threading.Interlocked.Increment(ref _nextRenderPathOwner);

        /// <summary>Ponteiro bruto do SHP — adquirido uma vez, zero syscalls depois.</summary>
        public byte* ShpPointer
        {
            get
            {
                if (!_shpAcquired && Accessor != null)
                {
                    Accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _shpPtr);
                    _shpAcquired = true;
                }
                return _shpPtr;
            }
        }

        public void AcquireShpPointer()
        {
            if (!_shpAcquired && Accessor != null)
            {
                Accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _shpPtr);
                _shpAcquired = true;
            }
        }

        public bool TryGetRenderPath(SKRect viewport, float zoom, bool interactive, out SKPath? path, bool compact = false)
            => SharedRenderPaths.TryGet(_renderPathOwner, viewport, zoom, interactive, compact, out path);

        public void StoreRenderPath(SKPath path, SKRect coverage, float zoom, bool interactive, bool compact = false,
            bool scaleIndependent = false)
            => SharedRenderPaths.Store(_renderPathOwner, path, coverage, zoom, interactive, compact, scaleIndependent);

        public void InvalidateRenderPath()
            => SharedRenderPaths.Invalidate(_renderPathOwner);

        // Separate names preserve reflection consumers of the original methods.
        public bool TryGetPreciseRenderPath(SKRect viewport, SKPoint origin, float zoom, bool interactive,
            out SKPath? path, bool compact = false)
            => SharedRenderPaths.TryGet(_renderPathOwner, viewport, zoom, interactive, compact, out path, origin);

        public void StorePreciseRenderPath(SKPath path, SKRect coverage, SKPoint origin, float zoom,
            bool interactive, bool compact, bool scaleIndependent)
            => SharedRenderPaths.Store(_renderPathOwner, path, coverage, zoom, interactive, compact, scaleIndependent, origin);

        /// <summary>Ponteiro bruto do DBF — acesso direto sem syscall.</summary>
        public byte* DbfPointerDirect => _dbfPtr;

        public void SetDbfMapped(System.IO.MemoryMappedFiles.MemoryMappedFile mmf, System.IO.MemoryMappedFiles.MemoryMappedViewAccessor accessor)
        {
            _dbfMmf = mmf;
            _dbfAccessor = accessor;
            if (!_dbfAcquired)
            {
                _dbfAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref _dbfPtr);
                _dbfAcquired = true;
            }
        }

        /// <summary>
        /// Zero I/O — lê atributos diretamente do ponteiro memory-mapped do DBF.
        /// Elimina FileStream.Open/Seek/Read/Close que acontecia a cada clique.
        /// </summary>
        public Dictionary<string, string> LerAtributos(int featureIndex)
        {
            try
            {
                var dict = new Dictionary<string, string>(DbfFields.Count);
                if (_dbfPtr == null || DbfRecordBytes == 0) return dict;
                
                byte* recBase = _dbfPtr + DbfHeaderBytes + ((long)featureIndex * DbfRecordBytes);
                
                foreach (var f in DbfFields)
                {
                    if (f.Offset + f.Length <= DbfRecordBytes)
                    {
                        dict[f.Name] = GeoHelpers.ReadDbfString(recBase + f.Offset, f.Length, GeoHelpers.Iso8859);
                    }
                }
                return dict;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("LerAtributos Error: " + ex.ToString());
                return new Dictionary<string, string>();
            }
        }

        public void Dispose()
        {
            InvalidateRenderPath();
            if (_shpAcquired && Accessor != null)
            {
                try { Accessor.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
                _shpAcquired = false;
            }
            if (_dbfAcquired && _dbfAccessor != null)
            {
                try { _dbfAccessor.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
                _dbfAcquired = false;
            }
            if (TransformLocal != null)
            {
                TransformLocal.Dispose();
            }
            Accessor?.Dispose();
            File?.Dispose();
            _dbfAccessor?.Dispose();
            _dbfMmf?.Dispose();
        }
    }

    public partial class MapRenderingService : IDisposable
    {
        private int _disposeState;
        private long _sceneRevision;
        public long SceneRevision => System.Threading.Interlocked.Read(ref _sceneRevision);
        private long _vectorPresentationRevision;
        public long VectorPresentationRevision => System.Threading.Interlocked.Read(ref _vectorPresentationRevision);
        public string LocalServerBaseUrl { get; set; } = "";
        
        // Mestre das Projeções (Project CRS em WKT ou EPSG)
        public string ProjetoSRS { get; set; } = "EPSG:4326";
        
        public string TipoGeometriaAtiva { get; set; } = "POLIGONO";
        public bool TravaDistanciaAtiva { get; set; } = false;
        public double TravaDistanciaValor { get; set; } = 50;
        public bool TravaModoFixo { get; set; } = true;
        public float CameraZoom { get; set; } = 1.0f;
        public float CameraPanX { get; set; } = 0f;
        public float CameraPanY { get; set; } = 0f;
        
        // Dados do Viewport atual (alimentados pelo LocalMapServer)
        public float ViewportEscalaAutoFit { get; set; } = 1f;

        public SKRect GetSceneBounds()
        {
            var bounds = TemRaster ? LimitesRasterGlobal : SKRect.Empty;
            if (!LimitesGlobaisVetor.IsEmpty)
            {
                if (bounds.IsEmpty) bounds = LimitesGlobaisVetor;
                else bounds.Union(LimitesGlobaisVetor);
            }
            return bounds;
        }
        public float ViewportMidX { get; set; } = 0f;
        public float ViewportMidY { get; set; } = 0f;
        public float ViewportWidth { get; set; } = 1920f;
        public float ViewportHeight { get; set; } = 1080f;

        public double DistanciaTotal { get; set; }
        
        public double AreaTotal { get; set; }
        public SkiaSharp.SKPath? CaminhoFeicaoDestacada { get; set; }
        public SKPaint PincelFill { get; private set; }
        public SKPaint PincelBorda { get; private set; }
        
        public SKPath? CaminhoDestaquePoligono { get; private set; }
        public SKPath? CaminhoDestaqueLinha { get; private set; }
        public SKPaint PincelDestaqueFill { get; private set; } = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Yellow.WithAlpha(100), IsAntialias = true };
        public SKPaint PincelDestaqueBorda { get; private set; } = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Yellow, StrokeWidth = 2, IsAntialias = true };
        
        public Dictionary<string, List<CompiledFeature>> FeaturesPorCamada { get; } = new();
        public Dictionary<string, NetTopologySuite.Features.FeatureCollection> FeicoesOriginais { get; } = new();
        public Dictionary<string, List<SKPath>> VetoresPorCamada { get; } = new();
        public Dictionary<string, List<SKPath>> LinhasPorCamada { get; } = new();
        public Dictionary<string, SKPath> PontosPorCamada { get; } = new();

        public Dictionary<string, Dictionary<string, List<SKPath>>> VetoresCategorizados { get; } = new();
        public Dictionary<string, Dictionary<string, List<SKPath>>> LinhasCategorizadas { get; } = new();

        public List<string> OrdemCamadas { get; set; } = new();
        public List<SkiaSharp.SKPoint> PontosMedicao { get; set; } = new();
        public List<SkiaSharp.SKPoint> PontosAquisicao { get; set; } = new();
        public SkiaSharp.SKPoint? PontoCursorSnap { get; set; }
        public SkiaSharp.SKPoint? PontoCursorMundo { get; set; }
        public bool MostrarAreaMedicao { get; set; } = false; 
        
        private STRtree<SkiaSharp.SKPath> _indiceEspacialEstatico = new STRtree<SkiaSharp.SKPath>();
        private List<SkiaSharp.SKPath> _bufferEdicaoAtiva = new List<SkiaSharp.SKPath>();
        private bool _indiceNecessitaReconstrucao = true;

        public unsafe void AtualizarRotulosCamada(string nomeCamada, string colunaRotulo)
        {
            try
            {
                if (string.IsNullOrEmpty(colunaRotulo)) return;
                if (!FeaturesPorCamada.TryGetValue(nomeCamada, out var feicoes)) return;
                using ResourceLease<MemoryMappedShapefile>? shapefileLease = AcquireShapefile(nomeCamada);
                if (shapefileLease == null) return;
                MemoryMappedShapefile shp = shapefileLease.Resource;
                if (string.IsNullOrEmpty(shp.DbfPath) || !System.IO.File.Exists(shp.DbfPath)) return;

                var dbfField = shp.DbfFields.FirstOrDefault(f => f.Name == colunaRotulo);
                if (dbfField == null) return;

                // Usar ponteiro DBF persistente do MemoryMappedShapefile (zero I/O)
                byte* dbfPtr = shp.DbfPointerDirect;
                if (dbfPtr == null) return;

                var ancoras = new List<(SkiaSharp.SKPoint Ponto, NetTopologySuite.Features.IAttributesTable Atributos, float LarguraMundo)>(feicoes.Count);

                for (int i = 0; i < feicoes.Count; i++)
                {
                    var f = feicoes[i];
                    if (f.EnvelopeWorld.IsNull) continue;
                    
                    long dbfRecOffset = shp.DbfHeaderBytes + (f.FID * shp.DbfRecordBytes);
                    string valor = GeoHelpers.ReadDbfString(dbfPtr + dbfRecOffset + dbfField.Offset, dbfField.Length, GeoHelpers.Iso8859);
                    
                    var attrs = new NetTopologySuite.Features.AttributesTable { { colunaRotulo, valor } };
                    float larguraMundo = (float)f.EnvelopeWorld.Width;
                    if (larguraMundo == 0) larguraMundo = 999999f;
                    
                    ancoras.Add((f.CentroidLocal, attrs, larguraMundo));
                }

                PontosAncoragemRotulo[nomeCamada] = ancoras;
                RequestRedraw();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("AtualizarRotulosCamada Error: " + ex.ToString());
            }
        }

        public unsafe void CompilarCategorias(string nomeCamada, string colunaSimbologia)
        {
            try
            {
                if (!FeaturesPorCamada.TryGetValue(nomeCamada, out var feicoes)) return;
                using ResourceLease<MemoryMappedShapefile>? shapefileLease = AcquireShapefile(nomeCamada);
                MemoryMappedShapefile? shp = shapefileLease?.Resource;

                if (!string.IsNullOrEmpty(colunaSimbologia) && shp != null && System.IO.File.Exists(shp.DbfPath))
                {
                    var dbfField = shp.DbfFields.FirstOrDefault(f => f.Name == colunaSimbologia);
                    if (dbfField != null)
                    {
                        // Usar ponteiro DBF persistente (zero I/O)
                        byte* dbfPtr = shp.DbfPointerDirect;
                        if (dbfPtr != null)
                        {
                            for (int i = 0; i < feicoes.Count; i++)
                            {
                                var f = feicoes[i];
                                long dbfRecOffset = shp.DbfHeaderBytes + (f.FID * shp.DbfRecordBytes);
                                byte* strStart = dbfPtr + dbfRecOffset + dbfField.Offset;
                                f.CategoryValue = GeoHelpers.ReadDbfString(strStart, dbfField.Length, GeoHelpers.Iso8859);
                            }
                        }
                    }
                }
                
                var dicCategoriasPoly = new Dictionary<string, List<SKPath>>();
                var dicCategoriasLine = new Dictionary<string, List<SKPath>>();

                var currentPolyChunk = new Dictionary<string, SKPath>();
                var currentLineChunk = new Dictionary<string, SKPath>();
                var chunkCounter = new Dictionary<string, int>();

                const int CHUNK_SIZE = 200;

                foreach (var feicao in feicoes)
                {
                    var path = feicao.GetPath(shp, OffsetMundoX, OffsetMundoY);
                    if (path == null) continue;

                    string valorCategoria = string.IsNullOrEmpty(feicao.CategoryValue) ? "OUTROS" : feicao.CategoryValue;

                    if (!dicCategoriasPoly.TryGetValue(valorCategoria, out _))
                    {
                        dicCategoriasPoly[valorCategoria] = new List<SKPath>();
                        currentPolyChunk[valorCategoria] = new SKPath { FillType = SKPathFillType.EvenOdd };
                        chunkCounter[valorCategoria] = 0;
                    }
                    
                    if (!dicCategoriasLine.TryGetValue(valorCategoria, out _))
                    {
                        dicCategoriasLine[valorCategoria] = new List<SKPath>();
                        currentLineChunk[valorCategoria] = new SKPath();
                    }

                    if (chunkCounter[valorCategoria] >= CHUNK_SIZE)
                    {
                        if (!currentPolyChunk[valorCategoria].IsEmpty) dicCategoriasPoly[valorCategoria].Add(currentPolyChunk[valorCategoria]);
                        if (!currentLineChunk[valorCategoria].IsEmpty) dicCategoriasLine[valorCategoria].Add(currentLineChunk[valorCategoria]);
                        currentPolyChunk[valorCategoria] = new SKPath { FillType = SKPathFillType.EvenOdd };
                        currentLineChunk[valorCategoria] = new SKPath();
                        chunkCounter[valorCategoria] = 0;
                    }
                    
                    chunkCounter[valorCategoria]++;

                    if (feicao.Kind == GeometryKind.Polygon)
                    {
                        currentPolyChunk[valorCategoria].AddPath(path);
                    }
                    else if (feicao.Kind == GeometryKind.Line)
                    {
                        currentLineChunk[valorCategoria].AddPath(path);
                    }
                }

                foreach (var cat in currentPolyChunk.Keys)
                {
                    if (!currentPolyChunk[cat].IsEmpty) dicCategoriasPoly[cat].Add(currentPolyChunk[cat]);
                    if (!currentLineChunk[cat].IsEmpty) dicCategoriasLine[cat].Add(currentLineChunk[cat]);
                }
                
                VetoresCategorizados[nomeCamada] = dicCategoriasPoly;
                LinhasCategorizadas[nomeCamada] = dicCategoriasLine;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("CompilarCategorias Error: " + ex.ToString());
            }
        }

        public void AtualizarBufferEdicao(List<SkiaSharp.SKPath> buffer)
        {
            _bufferEdicaoAtiva = buffer;
        }

        private void VerificarCandidatos(SkiaSharp.SKPoint[] pontosDaGeometria, int numPontos, SkiaSharp.SKPoint ptClique, bool checarVertices, bool checarArestas, ref float menorDistanciaSq, ref SkiaSharp.SKPoint? melhorPonto)
        {
            if (checarVertices)
            {
                for (int i = 0; i < numPontos; i++)
                {
                    var pt = pontosDaGeometria[i];
                    float distSq = (pt.X - ptClique.X) * (pt.X - ptClique.X) + (pt.Y - ptClique.Y) * (pt.Y - ptClique.Y);
                    if (distSq < menorDistanciaSq)
                    {
                        menorDistanciaSq = distSq;
                        melhorPonto = pt;
                    }
                }
            }

            if (checarArestas)
            {
                for (int i = 0; i < numPontos - 1; i++)
                {
                    var p1 = pontosDaGeometria[i];
                    var p2 = pontosDaGeometria[i + 1];
                    float l2 = (p1.X - p2.X) * (p1.X - p2.X) + (p1.Y - p2.Y) * (p1.Y - p2.Y);
                    if (l2 == 0) continue;

                    float t = Math.Max(0, Math.Min(1, ((ptClique.X - p1.X) * (p2.X - p1.X) + (ptClique.Y - p1.Y) * (p2.Y - p1.Y)) / l2));
                    float projX = p1.X + t * (p2.X - p1.X);
                    float projY = p1.Y + t * (p2.Y - p1.Y);
                    float distSqSegmento = (ptClique.X - projX) * (ptClique.X - projX) + (ptClique.Y - projY) * (ptClique.Y - projY);

                    if (distSqSegmento < menorDistanciaSq)
                    {
                        menorDistanciaSq = distSqSegmento;
                        melhorPonto = new SkiaSharp.SKPoint(projX, projY);
                    }
                }
            }
        }

        public void ConstruirIndiceEspacialEstatico()
        {
            if (_indiceEspacialEstatico == null)
            {
                _indiceEspacialEstatico = new STRtree<SKPath>(100);
                foreach (var camada in FeaturesPorCamada)
                {
                    using ResourceLease<MemoryMappedShapefile>? shapefileLease = AcquireShapefile(camada.Key);
                    MemoryMappedShapefile? shapefile = shapefileLease?.Resource;
                    foreach (var f in camada.Value)
                    {
                        var path = f.GetPath(shapefile, OffsetMundoX, OffsetMundoY);
                        if (path != null && !path.IsEmpty)
                        {
                            var bounds = path.Bounds;
                            var envelope = new Envelope(bounds.Left, bounds.Right, bounds.Top, bounds.Bottom);
                            _indiceEspacialEstatico.Insert(envelope, path);
                        }
                    }
                }
                _indiceEspacialEstatico.Build();
            }
            _indiceNecessitaReconstrucao = false;
        }

        public SkiaSharp.SKPoint? EncontrarVerticeProximo(SkiaSharp.SKPoint ptClique, float toleranciaMundo, bool checarVertices = true, bool checarArestas = false)
        {
            SkiaSharp.SKPoint? melhorPonto = null;
            float menorDistanciaSq = toleranciaMundo * toleranciaMundo;

            double lng = ptClique.X + OffsetMundoX;
            double lat = OffsetMundoY - ptClique.Y;

            var envTarget = new NetTopologySuite.Geometries.Envelope(
                lng - toleranciaMundo, lng + toleranciaMundo,
                lat - toleranciaMundo, lat + toleranciaMundo
            );
            var rectClique = new SkiaSharp.SKRect(
                ptClique.X - toleranciaMundo, ptClique.Y - toleranciaMundo,
                ptClique.X + toleranciaMundo, ptClique.Y + toleranciaMundo
            );

            var todasFeicoesAValidar = new List<CompiledFeature>();
            foreach (string layerName in _spatialIndexResources.SnapshotKeys())
            {
                using ResourceLease<NativeShapeSpatialIndex>? indexLease = AcquireSpatialIndex(layerName);
                using ResourceLease<MemoryMappedShapefile>? shapefileLease = AcquireShapefile(layerName);
                if (indexLease == null) continue;

                NativeShapeSpatialIndex arvore = indexLease.Resource;
                MemoryMappedShapefile? shapefile = shapefileLease?.Resource;
                using var candidatos = arvore.Query(envTarget);
                foreach (var c in candidatos)
                {
                    var path = c.GetPath(shapefile, OffsetMundoX, OffsetMundoY);
                    if (path != null && !path.IsEmpty)
                    {
                        todasFeicoesAValidar.Add(c);
                    }
                }
            }

            foreach (var feat in todasFeicoesAValidar)
            {
                if (feat.Path == null || !feat.Path.Bounds.IntersectsWith(rectClique)) continue;

                var pontosDaGeometria = feat.Path.Points;
                int numPontos = pontosDaGeometria.Length;
                if (numPontos < 2) continue;

                VerificarCandidatos(pontosDaGeometria, numPontos, ptClique, checarVertices, checarArestas, ref menorDistanciaSq, ref melhorPonto);
            }

            foreach (var path in _bufferEdicaoAtiva)
            {
                if (!path.Bounds.IntersectsWith(rectClique)) continue;

                var pontosDaGeometria = path.Points;
                int numPontos = pontosDaGeometria.Length;
                if (numPontos < 2) continue;

                VerificarCandidatos(pontosDaGeometria, numPontos, ptClique, checarVertices, checarArestas, ref menorDistanciaSq, ref melhorPonto);
            }

            if (checarVertices && PontosAquisicao.Count > 0)
            {
                foreach (var pt in PontosAquisicao)
                {
                    float distSq = (pt.X - ptClique.X) * (pt.X - ptClique.X) + (pt.Y - ptClique.Y) * (pt.Y - ptClique.Y);
                    if (distSq < menorDistanciaSq)
                    {
                        menorDistanciaSq = distSq;
                        melhorPonto = pt;
                    }
                }
            }

            return melhorPonto;
        }

        private readonly object _vectorResourceGate = new();
        private readonly LeasedResourceRegistry<string, MemoryMappedShapefile> _shapefileResources = new(StringComparer.Ordinal);
        private readonly LeasedResourceRegistry<string, NativeShapeSpatialIndex> _spatialIndexResources = new(StringComparer.Ordinal);
        public Dictionary<string, Envelope> LimitesVetoresWorld { get; } = new();
        public SkiaSharp.SKRect LimitesGlobaisVetor { get; set; } = SkiaSharp.SKRect.Empty;
        public Dictionary<string, List<(SkiaSharp.SKPoint Ponto, NetTopologySuite.Features.IAttributesTable Atributos, float LarguraMundo)>> PontosAncoragemRotulo { get; set; } = new(); 
        public System.Collections.Concurrent.ConcurrentDictionary<string, EstiloCamada> EstilosPorCamada { get; } = new();
        private const string GlobalCacheResourceKey = "global";
        private readonly object _rasterResourceGate = new();
        private readonly Dictionary<string, Dataset> _rasters = new();
        private readonly Dictionary<string, RasterCacheMetadata> _rasterCacheMetadata = new();
        private readonly LeasedResourceRegistry<string, Dataset> _rasterResources = new(StringComparer.Ordinal);
        private readonly LeasedResourceRegistry<string, Dataset> _warpedRasterResources = new(StringComparer.Ordinal);
        private readonly LeasedResourceRegistry<string, SKBitmap> _rasterCacheResources = new(StringComparer.Ordinal);
        private readonly LeasedResourceRegistry<string, SKBitmap> _globalCacheResources = new(StringComparer.Ordinal);
        public Dictionary<string, SkiaSharp.SKRect> LimitesRasters { get; set; } = new();

        public bool TemRaster
        {
            get { lock (_rasterResourceGate) return _rasters.Count > 0; }
        }
        
        // Bloqueio global de exclusão mútua (Mutex) para operações do GDAL C++ (que não é thread-safe)
        public readonly object GdalRasterLock = new object();
        
        public SkiaSharp.SKRect LimitesRasterGlobal 
        { 
            get 
            {
                if (LimitesRasters.Count == 0) return SkiaSharp.SKRect.Empty;
                SkiaSharp.SKRect union = SkiaSharp.SKRect.Empty;
                bool first = true;
                foreach (var kvp in LimitesRasters)
                {
                    // Ignora mapas base do cálculo do Bounding Box, para que o Auto-Fit respeite os arquivos locais!
                    if (kvp.Key.Contains("Satellite", StringComparison.OrdinalIgnoreCase) || 
                        kvp.Key.Contains("Satelite", StringComparison.OrdinalIgnoreCase) || 
                        kvp.Key.Contains("Satélite", StringComparison.OrdinalIgnoreCase) || 
                        kvp.Key.Contains("OpenStreetMap", StringComparison.OrdinalIgnoreCase)) continue;

                    if (first) { union = kvp.Value; first = false; }
                    else union.Union(kvp.Value);
                }
                
                // Se só tem mapas base, retorna o mundo inteiro
                if (first && LimitesRasters.Count > 0)
                {
                    return LimitesRasters.First().Value;
                }

                return union;
            }
        }
        
        public string CacheKey { get; set; } = ""; // Kept for legacy vector cache if needed
        public bool IsPanning { get; set; } = false; 
        
        // --- GLOBAL INTERACTION CACHE ---
        private GlobalCacheMetadata _globalCacheMetadata = new(1, 0, 0, 0, 0, 0, 0);
        
        public event Action? OnMapInvalidated;
        public HashSet<string> CamadasInvisiveis { get; } = new();

        public MapRenderingService()
        {
            PincelBorda = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Cyan.WithAlpha(200), StrokeWidth = 0, IsAntialias = true };
            PincelFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Cyan.WithAlpha(25), IsAntialias = true };

            GdalRuntimeConfiguration.Apply();
        }

        public ResourceLease<MemoryMappedShapefile>? AcquireShapefile(string layerName) =>
            _shapefileResources.Acquire(layerName);

        public void PublishShapefile(string layerName, MemoryMappedShapefile shapefile)
        {
            lock (_vectorResourceGate)
            {
                _spatialIndexResources.Remove(layerName);
                _shapefileResources.Publish(layerName, shapefile);
            }
        }

        public ResourceLease<NativeShapeSpatialIndex>? AcquireSpatialIndex(string layerName) =>
            _spatialIndexResources.Acquire(layerName);

        private bool TryPublishSpatialIndex(
            string layerName,
            MemoryMappedShapefile source,
            NativeShapeSpatialIndex index)
        {
            lock (_vectorResourceGate)
            {
                using ResourceLease<MemoryMappedShapefile>? current = _shapefileResources.Acquire(layerName);
                if (current == null || !ReferenceEquals(current.Resource, source)) return false;
                _spatialIndexResources.Publish(layerName, index);
                return true;
            }
        }

        public void RemoveVectorResources(string layerName)
        {
            lock (_vectorResourceGate)
            {
                _spatialIndexResources.Remove(layerName);
                _shapefileResources.Remove(layerName);
            }
        }

        public bool HasRaster(string layerName)
        {
            lock (_rasterResourceGate) return _rasters.ContainsKey(layerName);
        }

        private readonly HashSet<string> _onlineRasterNames = new();
        private readonly Dictionary<string, string> _onlineRasterXml = new();
        public string? GetOnlineRasterXml(string layerName)
        { lock (_rasterResourceGate) return _onlineRasterXml.GetValueOrDefault(layerName); }
        public bool HasOnlineBasemap { get { lock (_rasterResourceGate) return _onlineRasterNames.Count > 0; } }
        public bool IsOnlineRaster(string layerName) { lock (_rasterResourceGate) return _onlineRasterNames.Contains(layerName); }

        public ResourceLease<Dataset>? AcquireRaster(string layerName) =>
            _rasterResources.Acquire(layerName);

        public void PublishRaster(string layerName, Dataset dataset, string? onlineXml = null)
        {
            using var driver = dataset.GetDriver();
            bool online = driver?.ShortName == "WMS";
            lock (_rasterResourceGate)
            {
                if (online) _onlineRasterNames.Add(layerName);
                else _onlineRasterNames.Remove(layerName);
                if (online && !string.IsNullOrWhiteSpace(onlineXml)) _onlineRasterXml[layerName] = onlineXml;
                else _onlineRasterXml.Remove(layerName);
                _rasterCacheMetadata.Remove(layerName);
                _rasterCacheResources.Remove(layerName);
                _warpedRasterResources.Remove(layerName);
                _rasterResources.Publish(layerName, dataset);
                _rasters[layerName] = dataset;
            }
        }

        public bool RemoveRaster(string layerName)
        {
            lock (_rasterResourceGate)
            {
                _rasters.Remove(layerName);
                _onlineRasterNames.Remove(layerName);
                _onlineRasterXml.Remove(layerName);
                _rasterCacheMetadata.Remove(layerName);
                _rasterCacheResources.Remove(layerName);
                _warpedRasterResources.Remove(layerName);
                return _rasterResources.Remove(layerName);
            }
        }

        public ResourceLease<Dataset>? AcquireWarpedRaster(string layerName) =>
            _warpedRasterResources.Acquire(layerName);

        public bool PublishWarpedRaster(string layerName, Dataset source, Dataset dataset)
        {
            lock (_rasterResourceGate)
            {
                using ResourceLease<Dataset>? current = _rasterResources.Acquire(layerName);
                if (current == null || !ReferenceEquals(current.Resource, source))
                {
                    dataset.Dispose();
                    return false;
                }
                _warpedRasterResources.Publish(layerName, dataset);
                return true;
            }
        }

        public bool RemoveWarpedRaster(string layerName)
        {
            lock (_rasterResourceGate) return _warpedRasterResources.Remove(layerName);
        }

        public void ClearWarpedRasters()
        {
            lock (_rasterResourceGate) _warpedRasterResources.Clear();
        }

        public ResourceLease<SKBitmap>? AcquireRasterCache(
            string layerName,
            out RasterCacheMetadata metadata)
        {
            lock (_rasterResourceGate)
            {
                ResourceLease<SKBitmap>? lease = _rasterCacheResources.Acquire(layerName);
                if (lease != null && _rasterCacheMetadata.TryGetValue(layerName, out metadata))
                    return lease;

                lease?.Dispose();
                metadata = default;
                return null;
            }
        }

        public bool PublishRasterCache(
            string layerName,
            Dataset source,
            SKBitmap bitmap,
            string cacheKey,
            float panX,
            float panY,
            float zoom, MapCoordinateFrame? frame = null)
        {
            lock (_rasterResourceGate)
            {
                using ResourceLease<Dataset>? current = _rasterResources.Acquire(layerName);
                if (current == null || !ReferenceEquals(current.Resource, source))
                {
                    bitmap.Dispose();
                    return false;
                }
                _rasterCacheResources.Publish(layerName, bitmap);
                _rasterCacheMetadata[layerName] = new RasterCacheMetadata(cacheKey, panX, panY, zoom, frame);
                return true;
            }
        }

        public bool RemoveRasterCache(string layerName)
        {
            lock (_rasterResourceGate)
            {
                _rasterCacheMetadata.Remove(layerName);
                return _rasterCacheResources.Remove(layerName);
            }
        }

        public void ClearRasterCaches()
        {
            lock (_rasterResourceGate)
            {
                _rasterCacheMetadata.Clear();
                _rasterCacheResources.Clear();
            }
        }

        public ResourceLease<SKBitmap>? AcquireGlobalCache(out GlobalCacheMetadata metadata)
        {
            lock (_rasterResourceGate)
            {
                ResourceLease<SKBitmap>? lease = _globalCacheResources.Acquire(GlobalCacheResourceKey);
                metadata = _globalCacheMetadata;
                return lease;
            }
        }

        public void PublishGlobalCache(
            SKBitmap bitmap,
            float zoom,
            float panX,
            float panY,
            MapViewportMetrics viewport, MapCoordinateFrame frame, long sceneRevision, float cameraZoom)
        {
            lock (_rasterResourceGate)
            {
                if (sceneRevision != _sceneRevision) { bitmap.Dispose(); return; }
                _globalCacheResources.Publish(GlobalCacheResourceKey, bitmap);
                _globalCacheMetadata = new GlobalCacheMetadata(
                    zoom,
                    panX,
                    panY,
                    viewport.CssWidth,
                    viewport.CssHeight,
                    viewport.PhysicalWidth,
                    viewport.PhysicalHeight, frame, cameraZoom);
            }
        }

        public void RequestRedraw() 
        {
            InvalidateGlobalCache();
            OnMapInvalidated?.Invoke();
        }

        public void InvalidateGlobalCache()
            => InvalidatePresentationCache(vectorChanged: true);

        // Only newly published raster pixels may bypass vector invalidation.
        // Edits, source/style changes and ordinary RequestRedraw remain conservative.
        public void InvalidateRasterPresentationCache()
            => InvalidatePresentationCache(vectorChanged: false);

        private void InvalidatePresentationCache(bool vectorChanged)
        {
            lock (_rasterResourceGate)
            {
                if (vectorChanged) System.Threading.Interlocked.Increment(ref _vectorPresentationRevision);
                System.Threading.Interlocked.Increment(ref _sceneRevision);
                _globalCacheResources.Remove(GlobalCacheResourceKey);
            }
        }

        public double OffsetMundoX { get; set; } = 0;
        public double OffsetMundoY { get; set; } = 0;
        public bool OffsetMundoDefinido { get; set; } = false;

        public void DefinirOffset(double minX, double maxX, double minY, double maxY)
        {
            if (!OffsetMundoDefinido)
            {
                OffsetMundoX = (maxX + minX) / 2.0;
                OffsetMundoY = (maxY + minY) / 2.0;
                OffsetMundoDefinido = true;
            }
        }

        /// <summary>
        /// Troca o SRC do projeto em runtime preservando a posição da câmera.
        /// Converte o centro atual para WGS84 e depois para o novo SRC.
        /// Invalida todos os caches de paths para forçar rebuild com o novo SRC.
        /// </summary>
        public void TrocarSRCProjeto(string novoSRS)
        {
            if (string.IsNullOrEmpty(novoSRS) || SrsFactory.IsSame(ProjetoSRS, novoSRS)) return;

            try
            {
                // 1. Salvar o centro atual em WGS84 (ponte universal entre SRCs)
                using var toWgs = SrsFactory.CreateTransform(ProjetoSRS, "EPSG:4326");
                double[] ptCentro = { OffsetMundoX, OffsetMundoY, 0 };
                toWgs.TransformPoint(ptCentro);

                // 2. Atualizar o SRC
                string antigoSRS = ProjetoSRS;
                ProjetoSRS = novoSRS;

                // 3. Converter o centro para o novo SRC
                using var fromWgs = SrsFactory.CreateTransform("EPSG:4326", novoSRS);
                double[] ptNovo = { ptCentro[0], ptCentro[1], 0 };
                fromWgs.TransformPoint(ptNovo);
                OffsetMundoX = ptNovo[0];
                OffsetMundoY = ptNovo[1];

                // 4. Invalidar TODOS os caches de paths vetoriais (forçar rebuild com novo SRC)
                foreach (var camada in FeaturesPorCamada.Values)
                {
                    foreach (var f in camada)
                    {
                        System.Threading.Volatile.Write(ref f.Path, null);
                    }
                }

                // 5. Atualizar transformadores nas camadas que têm SRS diferente do novo projeto
                foreach (string layerName in _shapefileResources.SnapshotKeys())
                {
                    using ResourceLease<MemoryMappedShapefile>? shapefileLease = AcquireShapefile(layerName);
                    if (shapefileLease == null) continue;
                    MemoryMappedShapefile shp = shapefileLease.Resource;
                    shp.InvalidateRenderPath();
                    if (!string.IsNullOrEmpty(shp.LayerSRS) && !SrsFactory.IsSame(shp.LayerSRS, novoSRS))
                    {
                        shp.TransformLocal?.Dispose();
                        shp.TransformLocal = SrsFactory.CreateThreadLocalTransform(shp.LayerSRS, novoSRS);
                    }
                    else
                    {
                        shp.TransformLocal?.Dispose();
                        shp.TransformLocal = null; // Mesma projeção, sem necessidade de transformar
                    }
                }

                // 6. Invalidar cache raster, VRTs e global
                InvalidateGlobalCache();
                ClearRasterCaches();
                ClearWarpedRasters();
                // Recriar VRTs no novo SRC
                string[] rasterNames;
                lock (_rasterResourceGate) rasterNames = _rasters.Keys.ToArray();
                foreach (string rasterName in rasterNames)
                {
                    using ResourceLease<Dataset>? rasterLease = AcquireRaster(rasterName);
                    if (rasterLease == null) continue;

                    try
                    {
                        Dataset raster = rasterLease.Resource;
                        string srcWkt = raster.GetProjection();
                        if (!string.IsNullOrEmpty(srcWkt))
                        {
                            string tempVrt = $"/vsimem/warped_{Guid.NewGuid():N}.vrt";
                            string vrtResampling = RasterDatasetPolicy.SelectRenderResampling(raster, false);
                            var warpOptions = new OSGeo.GDAL.GDALWarpAppOptions(new[] {
                                "-t_srs", novoSRS,
                                "-r", vrtResampling,
                                "-of", "VRT"
                            });
                            var vrt = Gdal.Warp(tempVrt, new[] { raster }, warpOptions, null, null);
                            if (vrt != null) PublishWarpedRaster(rasterName, raster, vrt);
                        }
                    }
                    catch { }
                }

                RequestRedraw();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("TrocarSRCProjeto Error: " + ex.ToString());
            }
        }

        public void PreCompilarPoligonos(string nomeCamada, List<CompiledFeature> feicoes)
        {
            if (FeaturesPorCamada.ContainsKey(nomeCamada))
            {
                foreach (var f in FeaturesPorCamada[nomeCamada]) f.Path?.Dispose();
                FeaturesPorCamada.Remove(nomeCamada);
            }
            
            if (PontosAncoragemRotulo.ContainsKey(nomeCamada)) PontosAncoragemRotulo.Remove(nomeCamada);
            PontosAncoragemRotulo[nomeCamada] = new();

            FeaturesPorCamada[nomeCamada] = feicoes;
            
            ConstruirIndiceEspacial(nomeCamada, feicoes);

            foreach (var feicao in feicoes)
            {
                if (!string.IsNullOrEmpty(feicao.CategoryValue))
                {
                    var attr = new NetTopologySuite.Features.AttributesTable { { "rotulo", feicao.CategoryValue } };
                    PontosAncoragemRotulo[nomeCamada].Add((feicao.CentroidLocal, attr, (float)feicao.EnvelopeWorld.Width));
                }
            }

            RequestRedraw();
        }

        public void ConstruirIndiceEspacial() {}
        public void ConstruirIndiceEspacial(string nomeCamada, List<CompiledFeature> feicoes)
        {
            using ResourceLease<MemoryMappedShapefile>? shapefileLease = AcquireShapefile(nomeCamada);
            if (shapefileLease == null)
                throw new InvalidOperationException($"Shapefile não registrado para a camada '{nomeCamada}'.");

            var arvore = new NativeShapeSpatialIndex(shapefileLease.Resource, feicoes);
            try
            {
                if (!TryPublishSpatialIndex(nomeCamada, shapefileLease.Resource, arvore))
                {
                    arvore.Dispose();
                    return;
                }
            }
            catch
            {
                arvore.Dispose();
                throw;
            }
            Console.WriteLine(
                $"[GEONEX PERF] Índice C++: {arvore.NativeBytes / (1024.0 * 1024.0):F1} MB, " +
                $"células={arvore.GridCells:N0}, entradas={arvore.GridEntries:N0}, " +
                $"oversized={arvore.OversizedFeatures:N0}, workers={GeoNexHardware.WorkersFor(feicoes.Count)}");
            if (feicoes.Count > 0) {
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach(var f in feicoes) {
                    if (!f.EnvelopeWorld.IsNull) {
                        if (f.EnvelopeWorld.MinX < minX) minX = f.EnvelopeWorld.MinX;
                        if (f.EnvelopeWorld.MinY < minY) minY = f.EnvelopeWorld.MinY;
                        if (f.EnvelopeWorld.MaxX > maxX) maxX = f.EnvelopeWorld.MaxX;
                        if (f.EnvelopeWorld.MaxY > maxY) maxY = f.EnvelopeWorld.MaxY;
                    }
                }
                if (minX <= maxX) {
                    LimitesVetoresWorld[nomeCamada] = new Envelope(minX, maxX, minY, maxY);
                    float skiaMinX = (float)(minX - OffsetMundoX);
                    float skiaMaxX = (float)(maxX - OffsetMundoX);
                    float skiaMinY = -(float)(maxY - OffsetMundoY);
                    float skiaMaxY = -(float)(minY - OffsetMundoY);
                    var rect = new SkiaSharp.SKRect(skiaMinX, skiaMinY, skiaMaxX, skiaMaxY);
                    if (LimitesGlobaisVetor.IsEmpty) LimitesGlobaisVetor = rect;
                    else LimitesGlobaisVetor.Union(rect);
                }
            }
        }

        public CompiledFeature? DispararRaycast(double lat, double lng, double toleranciaMetros, out string camadaAtingida)
        {
            camadaAtingida = string.Empty;
            var ptTarget = new NetTopologySuite.Geometries.Point(lng, lat);
            var envTarget = new NetTopologySuite.Geometries.Envelope(lng - toleranciaMetros, lng + toleranciaMetros, lat - toleranciaMetros, lat + toleranciaMetros);

            float skiaX = (float)(lng - OffsetMundoX);
            float skiaY = (float)-(lat - OffsetMundoY);
            var rectClique = new SkiaSharp.SKRect(skiaX - (float)toleranciaMetros, skiaY - (float)toleranciaMetros, skiaX + (float)toleranciaMetros, skiaY + (float)toleranciaMetros);

            for (int i = OrdemCamadas.Count - 1; i >= 0; i--)
            {
                string camada = OrdemCamadas[i];
                if (CamadasInvisiveis.Contains(camada)) continue;

                if (HasRaster(camada))
                {
                    if (LimitesRasters.TryGetValue(camada, out var limites) && limites.Contains(skiaX, skiaY)) 
                        return null; // Clicou no raster
                }
                using ResourceLease<NativeShapeSpatialIndex>? indexLease = AcquireSpatialIndex(camada);
                if (indexLease != null)
                {
                    using ResourceLease<MemoryMappedShapefile>? shapefileLease = AcquireShapefile(camada);
                    NativeShapeSpatialIndex arvore = indexLease.Resource;
                    MemoryMappedShapefile? shapefile = shapefileLease?.Resource;
                    using var candidatos = arvore.Query(envTarget);
                    CompiledFeature? melhorCandidato = null;
                    
                    foreach (var c in candidatos)
                    {
                        var path = c.GetPath(shapefile, OffsetMundoX, OffsetMundoY);
                        if (path != null)
                        {
                            bool hit = false;
                            if (c.Kind == GeometryKind.Polygon)
                            {
                                hit = path.Contains(skiaX, skiaY);
                            }
                            else
                            {
                                hit = path.Bounds.IntersectsWith(rectClique);
                            }

                            if (hit)
                            {
                                melhorCandidato = c;
                                break;
                            }
                        }
                    }

                    if (melhorCandidato != null)
                    {
                        camadaAtingida = camada;
                        return melhorCandidato;
                    }
                }
            }
            return null;
        }

        public void DestacarFeicao(CompiledFeature? feicao)
        {
            CaminhoDestaquePoligono?.Dispose();
            CaminhoDestaqueLinha?.Dispose();
            CaminhoDestaquePoligono = null;
            CaminhoDestaqueLinha = null;

            using ResourceLease<MemoryMappedShapefile>? shapefileLease = feicao == null
                ? null
                : AcquireShapefile(feicao.LayerName);
            var path = feicao?.GetPath(shapefileLease?.Resource, OffsetMundoX, OffsetMundoY);

            if (feicao != null && path != null)
            {
                if (feicao.Kind == GeometryKind.Polygon)
                {
                    CaminhoDestaquePoligono = new SKPath(path);
                }
                else if (feicao.Kind == GeometryKind.Line)
                {
                    CaminhoDestaqueLinha = new SKPath(path);
                }
            }
            RequestRedraw();
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposeState, 1) != 0) return;

            lock (_rasterResourceGate)
            {
                _rasterCacheMetadata.Clear();
                _rasters.Clear();
                _onlineRasterNames.Clear();
                _onlineRasterXml.Clear();
                _globalCacheResources.Dispose();
                _rasterCacheResources.Dispose();
                _warpedRasterResources.Dispose();
                _rasterResources.Dispose();
            }

            lock (_vectorResourceGate)
            {
                _spatialIndexResources.Dispose();
                _shapefileResources.Dispose();
            }

            OnMapInvalidated = null;
        }
    }
}
