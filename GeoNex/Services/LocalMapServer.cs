using System;
using System.Net;
using System.Threading.Tasks;
using SkiaSharp;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Globalization;
using OSGeo.GDAL;

namespace GeoNex.Services
{
    public class LocalMapServer
    {
        public static string RenderEngine { get; set; } = "CPU (Skia)";
        private HttpListener _listener;
        private readonly MapRenderingService _mapService;
        private bool _isRunning;
        private int _port;
        private readonly SemaphoreSlim _renderGate = new(1, 1);
        private readonly SemaphoreSlim _previewGate = new(1, 1);
        private CancellationTokenSource? _activeRender;
        private long _latestRenderGeneration;
        private readonly RenderTelemetryCollector _telemetry;
        private readonly LatestRenderWorker<SKBitmap> _onlineWorker;
        private readonly OnlineRasterSession _onlineSession = new();
        private readonly ProgressiveOnlineRaster _progressiveOnline = new();
        private readonly PolygonImageCache _polygonImages = new();
        private readonly EncodedFrameCache _encodedFrames = new();
        private readonly RasterPixelBuffer _previewPixels = new();
        private readonly RasterPixelBuffer _framePixels = new(128L * 1024 * 1024);
        public long EncodedFrameCacheHits => _encodedFrames.Hits;
        public long EncodedFrameCacheBytes => _encodedFrames.RetainedBytes;
        public long PolygonImageCacheHits => _polygonImages.Hits;
        public long PolygonImageCacheBuilds => _polygonImages.Builds;
        public long PolygonImageCacheBytes => _polygonImages.Bytes;
        public long OnlineDatasetOpens => _onlineSession.OpenedCount;
        public long OnlineDatasetReuses => _onlineSession.ReusedCount;
        public long OnlinePreviewUpdates => _progressiveOnline.PublishedPreviews;
        public long OnlineCompletedRegions => _progressiveOnline.CompletedRegions;
        public long OnlineTileDownloads => _progressiveOnline.TileDownloads;
        public long OnlineSharedTileRequests => _progressiveOnline.SharedTileRequests;
        public long OnlineTileCacheHits => _progressiveOnline.TileCacheHits;
        public event Action? OnOnlineFrameReady;
        private long _onlineReadFailures;
        public long OnlineReadFailures => Interlocked.Read(ref _onlineReadFailures);
        public void DiscardObsoleteOnlineWork() => _onlineWorker.DiscardIrrelevant();
        public bool HasOnlineWork => _onlineWorker.HasWork;
        private static readonly bool UseWebpEncoding = string.Equals(
            Environment.GetEnvironmentVariable("GEONEX_RENDER_ENCODING"),
            "webp",
            StringComparison.OrdinalIgnoreCase);
        private static readonly bool LogRenderMetrics = string.Equals(
            Environment.GetEnvironmentVariable("GEONEX_RENDER_METRICS"),
            "1",
            StringComparison.Ordinal);
        
        // === MEMÓRIA ANTI-EXPLOSÃO DE CÂMARA ===
        private static float _ultimoMidX = 0f;
        private static float _ultimoMidY = 0f;
        private static float _ultimaEscalaAutoFit = 1f;
        
        public string BaseUrl => $"http://localhost:{_port}/";
        public bool IsTelemetryEnabled => _telemetry.Enabled;

        public LocalMapServer(MapRenderingService mapService)
        {
            _mapService = mapService;
            _onlineWorker = new LatestRenderWorker<SKBitmap>(
                () => { if (_isRunning) OnOnlineFrameReady?.Invoke(); },
                error =>
                {
                    Interlocked.Increment(ref _onlineReadFailures);
                    DebugLogger.Log($"Online render retained last valid image: {error.Message}");
                });
            int telemetryCapacity = int.TryParse(
                Environment.GetEnvironmentVariable("GEONEX_RENDER_TRACE_CAPACITY"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int configuredCapacity)
                ? configuredCapacity
                : 256;
            _telemetry = new RenderTelemetryCollector(LogRenderMetrics, telemetryCapacity);
        }

        public async ValueTask<IDisposable> PauseRenderingAsync()
        {
            Interlocked.Increment(ref _latestRenderGeneration);
            CancellationTokenSource? active = Interlocked.Exchange(ref _activeRender, null);
            try { active?.Cancel(); } catch (ObjectDisposedException) { }
            await _renderGate.WaitAsync().ConfigureAwait(false);
            return new RenderGateLease(_renderGate);
        }

        // Maintenance waits for the current frame (including print) instead of canceling it.
        // Cached bitmap previews remain safe because they hold immutable resource leases.
        public async ValueTask<IDisposable> PauseForRasterMaintenanceAsync()
        {
            await _renderGate.WaitAsync().ConfigureAwait(false);
            return new RenderGateLease(_renderGate);
        }

        private sealed class RenderGateLease : IDisposable
        {
            private SemaphoreSlim? _gate;

            public RenderGateLease(SemaphoreSlim gate) => _gate = gate;

            public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
        }

        public void Start()
        {
            if (_isRunning) return;
            using (var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
            {
                tcp.Start();
                _port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                tcp.Stop();
            }
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUrl + "mapa/");
            _listener.Start();
            _mapService.LocalServerBaseUrl = BaseUrl; // Guarda a URL gerada
            _isRunning = true;
            Task.Run(EscutarRequisicoes);
        }

        private async Task EscutarRequisicoes()
        {
            while (_isRunning)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    long acceptedTicks = Stopwatch.GetTimestamp();
                    var owner = new CancellationTokenSource();
                    var previous = Interlocked.Exchange(ref _activeRender, owner);
                    // The previous request can finish and dispose its CTS after Exchange.
                    // That race must not abandon this newly accepted HTTP request.
                    try { previous?.Cancel(); } catch (ObjectDisposedException) { }
                    long generation = Interlocked.Increment(ref _latestRenderGeneration);
                    long clientFrameId = long.TryParse(
                        context.Request.QueryString["fid"],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out long parsedFrameId)
                        ? parsedFrameId
                        : 0;
                    RenderFrameTrace? trace = _telemetry.BeginFrame(clientFrameId, generation, acceptedTicks);
                    _ = Task.Run(() => ProcessarRequisicaoAgendada(context, owner, generation, trace));
                }
                catch { }
            }
        }

        private async Task ProcessarRequisicaoAgendada(
            HttpListenerContext context,
            CancellationTokenSource owner,
            long generation,
            RenderFrameTrace? trace)
        {
            bool entered = false;
            try
            {
                // Read-only cached previews do not queue behind an uninterruptible
                // GDAL/Skia call. The resource lease pins the immutable source bitmap.
                if (await TryServeCachedPreviewAsync(context, owner.Token, generation, trace).ConfigureAwait(false)) return;
                await _renderGate.WaitAsync(owner.Token).ConfigureAwait(false);
                entered = true;
                trace?.MarkDequeued();
                owner.Token.ThrowIfCancellationRequested();
                if (generation != Volatile.Read(ref _latestRenderGeneration))
                    throw new OperationCanceledException(owner.Token);

                ProcessarRequisicao(context, owner.Token, generation, trace);
            }
            catch (OperationCanceledException)
            {
                trace?.MarkDequeued();
                _telemetry.CompleteResponse(trace, "canceled", 0);
                try { context.Response.StatusCode = 204; context.Response.Close(); } catch { }
            }
            catch (Exception error)
            {
                _telemetry.CompleteResponse(trace, "failed", 0);
                Console.Error.WriteLine($"Preview/render error: {error}");
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            }
            finally
            {
                if (trace is { IsResponseCompleted: false })
                    _telemetry.CompleteResponse(trace, "abandoned", 0);
                if (entered) _renderGate.Release();
                Interlocked.CompareExchange(ref _activeRender, null, owner);
                owner.Dispose();
            }
        }

        private async Task<bool> TryServeCachedPreviewAsync(HttpListenerContext context, CancellationToken token,
            long generation, RenderFrameTrace? trace)
        {
            var q = context.Request.QueryString;
            if (q["nav"] != "1" || q["i"] != "1" || q["c"] == "1" || q["rot"] != null ||
                q["ox"] != null || q["oy"] != null) return false;
            // A superseded preview may still be in an uninterruptible native PNG
            // call. Keep its successor here, away from the full-render gate.
            // Cancellation removes obsolete waiters; only the latest survives.
            await _previewGate.WaitAsync(token).ConfigureAwait(false);
            try { return TryServeCachedPreviewCore(context, token, generation, trace); }
            finally { _previewGate.Release(); }
        }

        private bool TryServeCachedPreviewCore(HttpListenerContext context, CancellationToken token,
            long generation, RenderFrameTrace? trace)
        {
            var q = context.Request.QueryString;
            if (q["nav"] != "1" || q["i"] != "1" || q["c"] == "1" || q["rot"] != null ||
                q["ox"] != null || q["oy"] != null) return false;
            if (!int.TryParse(q["w"], out int width) || !int.TryParse(q["h"], out int height) ||
                !float.TryParse(q["dpi"], NumberStyles.Float, CultureInfo.InvariantCulture, out float dpi) ||
                !MapViewportMetrics.TryCreate(width, height, dpi, out _)) return false;
            if (!float.TryParse(q["panx"], NumberStyles.Float, CultureInfo.InvariantCulture, out float panX) ||
                !float.TryParse(q["pany"], NumberStyles.Float, CultureInfo.InvariantCulture, out float panY) ||
                !float.TryParse(q["zoom"], NumberStyles.Float, CultureInfo.InvariantCulture, out float zoom) ||
                !float.IsFinite(panX) || !float.IsFinite(panY) || !float.IsFinite(zoom) || zoom <= 0) return false;
            int padding = NavigationFramePolicy.Padding(width, height, dpi);
            var viewport = MapViewportMetrics.Create(width + padding * 2, height + padding * 2, dpi);
            using var lease = _mapService.AcquireGlobalPreviewCache(out var metadata);
            if (lease == null || !metadata.Matches(viewport) || !viewport.MatchesBitmap(lease.Resource) ||
                metadata.Zoom <= 0 || metadata.CameraZoom <= 0) return false;
            var target = NavigationFramePolicy.Rebase(metadata.Frame, metadata.Zoom,
                new MapCameraState(metadata.PanX, metadata.PanY, metadata.CameraZoom),
                new MapCameraState(panX, panY, zoom));
            // The frame already contains geometry built around a precise local origin.
            // Reuse composes camera deltas in double precision, so high zoom is safe.
            if (!NavigationFramePolicy.TryReuse(metadata.Frame, target, width, height, out var matrix)) return false;
            token.ThrowIfCancellationRequested();
            trace?.MarkDequeued();
            trace?.Configure(width, height, dpi, true, 0);
            _previewPixels.Maintain(Environment.GetEnvironmentVariable("GEONEX_PREVIEW_BUFFER") == "0"
                ? 0 : VectorRuntimeResources.Current.CacheBytes / 8);
            SKImage preview;
            using (trace?.Measure("draw", "__global_cache__"))
                preview = CachedPreviewImage.Create(lease.Resource, matrix, token, _previewPixels);
            using var image = preview;
            context.Response.AppendHeader("Access-Control-Allow-Origin", "*");
            context.Response.AppendHeader("Cache-Control", "no-store");
            trace?.MarkRenderReady();
            WriteEncodedFrame(image, context.Response, true, token, generation, trace, navigation: true,
                cacheIdentityPreview: matrix.Equals(SKMatrix.Identity));
            return true;
        }

        private void ProcessarRequisicao(
            HttpListenerContext context,
            CancellationToken cancellationToken,
            long generation,
            RenderFrameTrace? trace)
        {
            long requestStarted = Stopwatch.GetTimestamp();
            trace?.MarkRenderStarted(requestStarted);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var req = context.Request;
                var res = context.Response;
                long sceneRevision = _mapService.SceneRevision;
                long vectorPresentationRevision = _mapService.VectorPresentationRevision;
                long polygonImageBudget = Environment.GetEnvironmentVariable("GEONEX_POLYGON_IMAGE_CACHE") == "0"
                    ? 0 : VectorRuntimeResources.Current.CacheBytes / 2;
                _polygonImages.Maintain(polygonImageBudget, vectorPresentationRevision);
                res.AppendHeader("Access-Control-Allow-Origin", "*");
                res.AppendHeader("Cache-Control", "no-cache, no-store, must-revalidate");

                bool isPrint = req.QueryString["c"] == "1";
                // Freeze camera values per HTTP request; a newer JS gesture can
                // update MapService while this render is still inside GDAL/Skia.
                float cameraPanX = _mapService.CameraPanX;
                float cameraPanY = _mapService.CameraPanY;
                float cameraZoom = _mapService.CameraZoom;
                if (!isPrint && req.QueryString["nav"] == "1")
                {
                    if (!float.TryParse(req.QueryString["panx"], NumberStyles.Float, CultureInfo.InvariantCulture, out cameraPanX) ||
                        !float.TryParse(req.QueryString["pany"], NumberStyles.Float, CultureInfo.InvariantCulture, out cameraPanY) ||
                        !float.TryParse(req.QueryString["zoom"], NumberStyles.Float, CultureInfo.InvariantCulture, out cameraZoom) ||
                        !float.IsFinite(cameraPanX) || !float.IsFinite(cameraPanY) || !float.IsFinite(cameraZoom) || cameraZoom <= 0)
                    {
                        res.StatusCode = 400; res.Close(); return;
                    }
                }
                bool hasPrintScale = isPrint && req.QueryString["cs"] != null;
                SKPoint printCenter = default;
                float printZoom = 0;
                double printWidth = 0, printHeight = 0;
                if (hasPrintScale && (!PrintMapContext.TryRead(req.QueryString, PrintMapContext.Capture(_mapService), out printCenter, out printZoom) ||
                    !PrintMapContext.TryReadLayoutSize(req.QueryString, out printWidth, out printHeight)))
                {
                    res.StatusCode = (int)HttpStatusCode.BadRequest;
                    res.Close();
                    _telemetry.CompleteResponse(trace, "invalid-print-frame", 0);
                    return;
                }

                // --- 1. APLICANDO DPI PARA ALTA RESOLUÇÃO ---
                bool validWidth = int.TryParse(
                    req.QueryString["w"] ?? "1920",
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int cssWidth);
                bool validHeight = int.TryParse(
                    req.QueryString["h"] ?? "1080",
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int cssHeight);
                bool validDpi = float.TryParse(
                    req.QueryString["dpi"] ?? "1.0",
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float dpi);
                if (!validWidth || !validHeight || !validDpi ||
                    !MapViewportMetrics.TryCreate(cssWidth, cssHeight, dpi, out MapViewportMetrics viewport))
                {
                    res.StatusCode = (int)HttpStatusCode.BadRequest;
                    res.Close();
                    _telemetry.CompleteResponse(trace, "rejected", 0);
                    return;
                }
                int visibleWidth = cssWidth, visibleHeight = cssHeight;
                int navigationPadding = !isPrint && req.QueryString["nav"] == "1"
                    ? NavigationFramePolicy.Padding(cssWidth, cssHeight, dpi) : 0;
                cssWidth += navigationPadding * 2;
                cssHeight += navigationPadding * 2;
                viewport = MapViewportMetrics.Create(cssWidth, cssHeight, dpi);
                float faseSelecao = float.Parse(req.QueryString["phase"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                float panOffsetX = float.Parse(req.QueryString["ox"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                float panOffsetY = float.Parse(req.QueryString["oy"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                float rotation = float.Parse(req.QueryString["rot"] ?? "0", System.Globalization.CultureInfo.InvariantCulture);
                 
                int physicalWidth = viewport.PhysicalWidth;
                int physicalHeight = viewport.PhysicalHeight;

                var info = new SKImageInfo(physicalWidth, physicalHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                // Não existe um contexto OpenGL válido nas threads do HttpListener.
                // Criar GRContext por request só acrescentava overhead e nunca aproveitava
                // corretamente a RTX. O backend CPU permanece explícito até existir uma
                // superfície GPU persistente, sem readback por frame.
                GRContext? grContext = null;
                GRGlInterface? glInterface = null;
                LocalMapServer.RenderEngine = "CPU (Skia AVX2, latest-frame-wins)";

                try
                {
                    // Fallback inteligente: Se falhar (Thread sem contexto OpenGL), desenha na CPU.
                    _framePixels.Maintain(!isPrint && req.QueryString["nav"] == "1" &&
                        Environment.GetEnvironmentVariable("GEONEX_FRAME_BUFFER") != "0"
                            ? VectorRuntimeResources.Current.CacheBytes / 4 : 0);
                    using var renderTarget = grContext != null
                        ? new RasterRenderTarget(SKSurface.Create(grContext, true, info))
                        : RasterRenderTarget.Create(info, _framePixels);
                    var surface = renderTarget.Surface;
                        
                    var canvas = surface.Canvas;

                canvas.Scale(viewport.PhysicalScaleX, viewport.PhysicalScaleY);

                int width = cssWidth;
                int height = cssHeight;

                canvas.Clear(new SKColor(0, 0, 0, 0)); // Fundo Transparente para não cobrir o OpenStreetMap
                canvas.Save();

                // 2. LIMITES TOTAIS
                SKRect limitesTotais = _mapService.GetSceneBounds();

                // THREAD-SAFE SNAPSHOTS
                List<SKPoint> ptsMedicao;
                try { ptsMedicao = _mapService.PontosMedicao.ToList(); } catch { ptsMedicao = new List<SKPoint>(); }
                List<SKPoint> ptsAquisicao;
                try { ptsAquisicao = _mapService.PontosAquisicao.ToList(); } catch { ptsAquisicao = new List<SKPoint>(); }

                bool isInteracting = !isPrint && (req.QueryString["i"] == "1" ||
                    faseSelecao > 0 || _mapService.IsPanning);
                bool deferOnline = !isPrint && req.QueryString["deferOnline"] == "1";
                bool onlinePending = false;
                trace?.Configure(
                    cssWidth,
                    cssHeight,
                    dpi,
                    isInteracting,
                    _mapService.OrdemCamadas.Count);

                float escalaAutoFit; float midX, midY;

                // === CORREÇÃO BUG 3: FALLBACK ANTI-EXPLOSÃO ===
                // Se for pedido pelo Compositor de Impressão (c=1), herda o zoom/pan exato do projeto sem tentar espremer o mapa na caixa!
                if (req.QueryString["c"] == "1")
                {
                    escalaAutoFit = _ultimaEscalaAutoFit > 0 ? _ultimaEscalaAutoFit : 1f;
                    midX = _ultimoMidX; midY = _ultimoMidY;
                }
                else if (limitesTotais.IsEmpty || limitesTotais.Width < 0.001f || limitesTotais.Height < 0.001f)
                {
                    escalaAutoFit = _ultimaEscalaAutoFit > 0 ? _ultimaEscalaAutoFit : 1f;
                    midX = _ultimoMidX; midY = _ultimoMidY;
                }
                else
                {
                    float escalaX = visibleWidth / limitesTotais.Width;
                    float escalaY = visibleHeight / limitesTotais.Height;
                    escalaAutoFit = Math.Min(escalaX, escalaY) * 0.8f;
                    midX = limitesTotais.MidX; midY = limitesTotais.MidY;

                    _ultimaEscalaAutoFit = escalaAutoFit;
                    _ultimoMidX = midX; _ultimoMidY = midY;
                }
                
                // Sincronizar com o MapService para que ele conheça a janela atual
                if (!isPrint)
                {
                    _mapService.ViewportEscalaAutoFit = escalaAutoFit;
                    _mapService.ViewportMidX = midX;
                    _mapService.ViewportMidY = midY;
                    _mapService.ViewportWidth = visibleWidth;
                    _mapService.ViewportHeight = visibleHeight;
                }

                float zoomReal = escalaAutoFit * cameraZoom;

                // O pan da câmara principal mantém a semântica histórica sem rotação;
                // o pan do compositor permanece alinhado aos eixos CSS após a rotação.
                SKPoint baseCenter = MapCoordinateSpace.ApplyCssPanToLocalCenter(
                    new SKPoint(midX, midY),
                    zoomReal,
                    cameraPanX,
                    cameraPanY);
                SKPoint currentCenter = MapCoordinateSpace.ApplyCssPanToLocalCenter(
                    baseCenter,
                    zoomReal,
                    panOffsetX,
                    panOffsetY,
                    rotation);

                if (hasPrintScale)
                {
                    zoomReal = printZoom;
                    currentCenter = printCenter;
                    // Existing render keys include auto-fit; preserve their differentiation without editing cache code.
                    escalaAutoFit = printZoom;
                }

                if (!MapCoordinateFrame.TryCreate(
                    viewport,
                    currentCenter,
                    zoomReal,
                    rotation,
                    out MapCoordinateFrame coordinateFrame, printWidth, printHeight))
                {
                    res.StatusCode = (int)HttpStatusCode.BadRequest;
                    res.Close();
                    _telemetry.CompleteResponse(trace, "rejected", 0);
                    return;
                }
                SKMatrix matriz = coordinateFrame.LocalToPhysicalMatrix;
                SKRect viewportMundo = coordinateFrame.LocalViewportBounds;
                // Subtract the camera origin in DOUBLE while building geometry, not
                // after SKPath has already quantized world coordinates to floats.
                bool precisionOrigin = RenderPrecisionPolicy.NeedsLocalOrigin(currentCenter,
                    zoomReal * Math.Max(viewport.PhysicalScaleX, viewport.PhysicalScaleY));
                MapCoordinateFrame.TryCreate(viewport, SKPoint.Empty, zoomReal, rotation,
                    out var centeredFrame, printWidth, printHeight);

                // =========================================================================
                // FAST-PATH: GLOBAL INTERACTION CACHE
                // =========================================================================
                GlobalCacheMetadata globalCacheMetadata = default;
                using ResourceLease<SKBitmap>? globalCacheLease = !isPrint && rotation == 0 && panOffsetX == 0 && panOffsetY == 0
                    ? (isInteracting ? _mapService.AcquireGlobalPreviewCache(out globalCacheMetadata)
                        : _mapService.AcquireGlobalCache(out globalCacheMetadata))
                    : null;
                if (globalCacheLease != null &&
                    (isInteracting || zoomReal == globalCacheMetadata.Zoom) &&
                    globalCacheMetadata.Matches(viewport) &&
                    viewport.MatchesBitmap(globalCacheLease.Resource) &&
                    NavigationFramePolicy.TryReuse(globalCacheMetadata.Frame, coordinateFrame,
                        visibleWidth, visibleHeight, out var globalMatrix))
                {
                    SKBitmap globalCache = globalCacheLease.Resource;
                    canvas.ResetMatrix();
                    canvas.SetMatrix(globalMatrix);
                     
                    // Desenha o Cache Global Instantaneamente!
                    using (trace?.Measure("draw", "__global_cache__"))
                        canvas.DrawBitmap(globalCache, 0, 0);
                    
                    // Salvar e responder (Interativo = Máxima fluidez)
                    using var fastImage = renderTarget.Finish();
                    trace?.MarkRenderReady();
                    WriteEncodedFrame(
                        fastImage, res, isInteracting, cancellationToken,
                        generation, trace, navigation: !isPrint, reusePayloadId: req.QueryString["reuse"]);
                    return;
                }
                // =========================================================================

                // CRIA OS PINCÉIS UMA ÚNICA VEZ ANTES DO LOOP
                // OTIMIZAÇÃO: Desativa AntiAlias durante Pan/Zoom para renderização ~30% mais rápida
                bool suavizar = !isInteracting;
                using var pincelBordaLOD = _mapService.PincelBorda.Clone();
                using var pincelPonto = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Cyan, IsAntialias = suavizar };
                using var pincelDinamicoFill = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = suavizar };
                using var pincelDinamicoBorda = new SKPaint { Style = SKPaintStyle.Stroke, IsAntialias = suavizar, StrokeJoin = SKStrokeJoin.Round };
                using var pincelDinamicoPonto = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = suavizar };
                
                // 3. MOTOR HIERÁRQUICO DE Z-INDEX
                // Home publishes complete lists; retain this frame's list even
                // if visibility changes while a native call is in flight.
                var frameLayerOrder = _mapService.OrdemCamadas;
                for (int i = 0; i < frameLayerOrder.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string camadaAtual = frameLayerOrder[i];
                    using var layerTrace = trace?.Measure("layer", camadaAtual);

                    // RASTER
                    using ResourceLease<Dataset>? rasterLease = _mapService.AcquireRaster(camadaAtual);
                    Dataset? datasetRaster = rasterLease?.Resource;
                    
                    if (datasetRaster != null)
                    {
                        bool hasRotation = Math.Abs(rotation) > 0.001f;
                        string targetCacheKey = $"{width}_{height}_{physicalWidth}_{physicalHeight}_{cameraPanX}_{cameraPanY}_{cameraZoom}_{camadaAtual}_{currentCenter.X}_{currentCenter.Y}_{escalaAutoFit}_{panOffsetX}_{panOffsetY}_{rotation}";
                        targetCacheKey = RasterRenderingPolicy.CacheKeyForQuality(targetCacheKey, isInteracting);
                        
                        using ResourceLease<SKBitmap>? rasterCacheLease =
                            _mapService.AcquireRasterCache(camadaAtual, out RasterCacheMetadata rasterCacheMetadata);
                        SKBitmap? rasterCacheImg = rasterCacheLease?.Resource;
                        string? rasterCacheKeyStr = rasterCacheLease == null ? null : rasterCacheMetadata.CacheKey;

                        string? onlineXml = !isPrint && !hasRotation ? _mapService.GetOnlineRasterXml(camadaAtual) : null;
                        if (onlineXml != null)
                        {
                            string finalKey = RasterRenderingPolicy.CacheKeyForQuality(
                                $"{width}_{height}_{physicalWidth}_{physicalHeight}_{cameraPanX}_{cameraPanY}_{cameraZoom}_{camadaAtual}_{currentCenter.X}_{currentCenter.Y}_{escalaAutoFit}_{panOffsetX}_{panOffsetY}_{rotation}", false);
                            if (rasterCacheKeyStr != finalKey)
                            {
                                onlinePending = true;
                                if (!isInteracting && !deferOnline)
                                {
                                    string srs = _mapService.ProjetoSRS;
                                    double offsetX = _mapService.OffsetMundoX, offsetY = _mapService.OffsetMundoY;
                                    string jobKey = FormattableString.Invariant($"{finalKey}:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(datasetRaster)}:{srs}:{offsetX:R}:{offsetY:R}");
                                    void PublishOnline(SKBitmap bitmap, string key)
                                    {
                                        if (_mapService.ProjetoSRS != srs || _mapService.OffsetMundoX != offsetX || _mapService.OffsetMundoY != offsetY)
                                        { bitmap.Dispose(); return; }
                                        if (_mapService.PublishRasterCache(camadaAtual, datasetRaster, bitmap, key,
                                            cameraPanX, cameraPanY, zoomReal, coordinateFrame)) _mapService.InvalidateRasterPresentationCache();
                                    }
                                    _onlineWorker.RequestProgressive(camadaAtual, jobKey,
                                        (token, progress) => _progressiveOnline.Read(_onlineSession, onlineXml, srs, centeredFrame.LocalViewportBounds,
                                            offsetX + (double)currentCenter.X, offsetY - (double)currentCenter.Y,
                                            physicalWidth, physicalHeight, token, progress,
                                            (background, w, h) =>
                                            {
                                                using var previous = _mapService.AcquireRasterCache(camadaAtual, out var metadata);
                                                if (previous == null || metadata.Frame is not MapCoordinateFrame previousFrame) return;
                                                var matrix = NavigationFramePolicy.RasterPreviewMatrix(previousFrame,
                                                    previous.Resource.Width, previous.Resource.Height, coordinateFrame);
                                                background.SetMatrix(SKMatrix.Concat(SKMatrix.CreateScale(w / (float)physicalWidth, h / (float)physicalHeight), matrix));
                                                background.DrawBitmap(previous.Resource, 0, 0);
                                            }),
                                        bitmap => PublishOnline(bitmap, finalKey),
                                        bitmap => PublishOnline(bitmap, finalKey + ":partial"),
                                        () => _mapService.OrdemCamadas.Contains(camadaAtual) &&
                                            _mapService.GetOnlineRasterXml(camadaAtual) == onlineXml &&
                                            _mapService.ProjetoSRS == srs &&
                                            _mapService.OffsetMundoX == offsetX && _mapService.OffsetMundoY == offsetY);
                                }
                            }
                            if (rasterCacheImg != null && rasterCacheMetadata.Frame is MapCoordinateFrame sourceFrame)
                            {
                                using var cachedImage = SKImage.FromBitmap(rasterCacheImg);
                                using (trace?.Measure("draw", camadaAtual))
                                {
                                    if (rasterCacheKeyStr == finalKey || rasterCacheKeyStr == finalKey + ":partial")
                                    {
                                        // Exact camera: avoid a float world-coordinate roundtrip,
                                        // which can shift the final imagery by a fraction of a pixel.
                                        canvas.ResetMatrix();
                                        canvas.DrawImage(cachedImage, new SKRect(0, 0, physicalWidth, physicalHeight),
                                            new SKSamplingOptions(SKFilterMode.Linear));
                                    }
                                    else
                                    {
                                        canvas.SetMatrix(NavigationFramePolicy.RasterPreviewMatrix(
                                            sourceFrame, rasterCacheImg.Width, rasterCacheImg.Height, coordinateFrame));
                                        canvas.DrawImage(cachedImage, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
                                    }
                                }
                            }
                            continue;
                        }

                        // This preview performs no online tile I/O. Reproject the last
                        // immutable basemap bitmap at this layer's original Z position;
                        // the settled request fetches full-resolution tiles normally.
                        if ((isInteracting || deferOnline) && _mapService.IsOnlineRaster(camadaAtual))
                        {
                            if (rasterCacheImg != null && rasterCacheMetadata.Frame is MapCoordinateFrame sourceFrame)
                            {
                                var cacheToTarget = NavigationFramePolicy.RasterPreviewMatrix(
                                    sourceFrame, rasterCacheImg.Width, rasterCacheImg.Height, coordinateFrame);
                                canvas.SetMatrix(cacheToTarget);
                                using (trace?.Measure("draw", camadaAtual)) canvas.DrawBitmap(rasterCacheImg, 0, 0);
                            }
                            continue;
                        }
                            
                            if (!hasRotation && rasterCacheImg != null && rasterCacheKeyStr == targetCacheKey)
                            {
                                canvas.ResetMatrix();
                                canvas.Scale(viewport.PhysicalScaleX, viewport.PhysicalScaleY);
                                using (trace?.Measure("draw", camadaAtual))
                                    canvas.DrawBitmap(rasterCacheImg, new SKRect(0, 0, width, height));
                            }
                            else
                            {
                                // Raster e vetor usam exatamente o mesmo viewport local. Antes,
                                // estes limites eram invertidos com width/height CSS numa matriz
                                // física, recortando o raster quando DPI era diferente de 1.
                                float minX = viewportMundo.Left;
                                float maxX = viewportMundo.Right;
                                float minY = viewportMundo.Top;
                                float maxY = viewportMundo.Bottom;

                                // Otimização extrema e prevenção de travamentos do GDAL:
                                // Se o raster ocupar menos de 2 pixels lógicos na tela (zoom out extremo), nem tentamos renderizar
                                if (_mapService.LimitesRasters.TryGetValue(camadaAtual, out var rasterLimites))
                                {
                                    SKRect viewportRect = new SKRect(minX, minY, maxX, maxY);
                                    if (!viewportRect.IntersectsWith(rasterLimites)) continue;
                                    
                                    viewportRect.Intersect(rasterLimites);
                                    if (viewportRect.Width * zoomReal < 2f || viewportRect.Height * zoomReal < 2f) continue;
                                }

                                double minLng = minX + _mapService.OffsetMundoX;
                                double maxLng = maxX + _mapService.OffsetMundoX;
                                double minLat = -maxY + _mapService.OffsetMundoY;
                                double maxLat = -minY + _mapService.OffsetMundoY;

                                int warpW = hasRotation
                                    ? (int)Math.Ceiling((maxX - minX) * zoomReal * viewport.PhysicalScaleX)
                                    : physicalWidth;
                                int warpH = hasRotation
                                    ? (int)Math.Ceiling((maxY - minY) * zoomReal * viewport.PhysicalScaleY)
                                    : physicalHeight;
                                if (warpW < 1) warpW = 1;
                                if (warpH < 1) warpH = 1;

                                GeoNexResourceBudget resourceBudget = GdalRuntimeConfiguration.Apply();
                                long maximumFramePixels = RasterRenderingPolicy.CalculateMaxFramePixels(
                                    resourceBudget.AvailablePhysicalMb);
                                RasterDimensions fittedDimensions = RasterRenderingPolicy.FitDimensions(
                                    warpW, warpH, maximumFramePixels);
                                warpW = fittedDimensions.Width;
                                warpH = fittedDimensions.Height;

                                bool isWms = datasetRaster.GetDriver()?.ShortName == "WMS";

                                // Tiles online are bounded by screen resolution. Fetching a larger
                                // frame only downloads extra tiles that Skia would immediately reduce.
                                if (isWms)
                                {
                                    // Only previews are deliberately downsampled. Final frames follow
                                    // physical pixels/DPI, subject to the shared raster memory budget.
                                    RasterDimensions onlineDimensions = OnlineBasemapPolicy.CalculateRenderDimensions(
                                        warpW, warpH, resourceBudget.AvailablePhysicalMb, isInteracting);
                                    warpW = onlineDimensions.Width;
                                    warpH = onlineDimensions.Height;
                                }

                                // Paletas e valores inteiros sem semântica declarada preservam identidade
                                // com nearest. Dados contínuos usam bilinear/cubic conforme a fase.
                                // Web tiles already have provider-generated zoom levels; cubic spline
                                // would cost more CPU and blur labels without revealing more detail.
                                string resampleAlg = isWms
                                    ? "bilinear"
                                    : RasterDatasetPolicy.SelectRenderResampling(datasetRaster, isInteracting);

                                // ===================================================================
                                // TIER 1: VRT FAST-PATH (Inspirado no QGIS QgsRasterLayerRenderer)
                                // Em vez de Gdal.Warp() completo a cada frame, usa ReadRaster no
                                // WarpedVRT que já tem a reprojeção e Overviews pré-computadas.
                                // ===================================================================
                                bool vrtRendered = false;
                                Dataset vrtDs = null;
                                using ResourceLease<Dataset>? warpedRasterLease = !hasRotation && !isWms
                                    ? _mapService.AcquireWarpedRaster(camadaAtual)
                                    : null;
                                
                                if (!hasRotation)
                                {
                                    if (isWms)
                                    {
                                        string sourceSrs = datasetRaster.GetProjection();
                                        if (OnlineBasemapPolicy.CanUseDirectRasterIo(
                                            true,
                                            false,
                                            sourceSrs,
                                            _mapService.ProjetoSRS,
                                            SrsFactory.IsSame))
                                        {
                                            // RasterIO asks the TMS dataset only for the visible window.
                                            // GDAL selects the nearest overview, which maps directly to ${z}.
                                            vrtDs = datasetRaster;
                                        }
                                    }
                                    else if (warpedRasterLease != null)
                                    {
                                        vrtDs = warpedRasterLease.Resource;
                                    }
                                    else
                                    {
                                        vrtDs = datasetRaster;
                                    }
                                }

                                if (vrtDs != null)
                                {
                                    double[] fastPathGeoTransform = new double[6];
                                    vrtDs.GetGeoTransform(fastPathGeoTransform);
                                    if (!RasterGeometry.IsAxisAligned(fastPathGeoTransform))
                                        vrtDs = null; // Rotated/sheared grids require the Warp path.
                                }

                                if (vrtDs != null)
                                {
                                    try
                                    {
                                        double[] vrtGeo = new double[6];
                                        vrtDs.GetGeoTransform(vrtGeo);

                                        // Converter viewport bbox (mundo) → VRT pixel coords
                                        double pxLeft  = (minLng - vrtGeo[0]) / vrtGeo[1];
                                        double pxRight = (maxLng - vrtGeo[0]) / vrtGeo[1];
                                        double pyTop   = (maxLat - vrtGeo[3]) / vrtGeo[5];
                                        double pyBot   = (minLat - vrtGeo[3]) / vrtGeo[5];

                                        // Normalizar (garantir left < right, top < bottom) independentemente da orientação Y do Raster
                                        bool eixoXInvertido = pxLeft > pxRight;
                                        bool eixoYInvertido = pyTop > pyBot;
                                        
                                        if (eixoXInvertido) { double t = pxLeft; pxLeft = pxRight; pxRight = t; }
                                        if (eixoYInvertido) { double t = pyTop; pyTop = pyBot; pyBot = t; }

                                        // Matemática de Interseção Estrita (Clipping)
                                        double cLeft  = Math.Max(0, pxLeft);
                                        double cTop   = Math.Max(0, pyTop);
                                        double cRight = Math.Min(vrtDs.RasterXSize, pxRight);
                                        double cBot   = Math.Min(vrtDs.RasterYSize, pyBot);

                                        // O Fast-Path é acionado com sucesso. Mesmo que o resultado seja "vazio" (fora das bordas), não queremos que o fallback lento tente processar o vazio.
                                        vrtRendered = true;

                                        if (cRight > cLeft && cBot > cTop)
                                        {
                                            int srcX = (int)Math.Floor(cLeft);
                                            int srcY = (int)Math.Floor(cTop);
                                            int srcW = Math.Max(1, Math.Min((int)Math.Ceiling(cRight) - srcX, vrtDs.RasterXSize - srcX));
                                            int srcH = Math.Max(1, Math.Min((int)Math.Ceiling(cBot) - srcY, vrtDs.RasterYSize - srcY));

                                            if (srcW > 0 && srcH > 0)
                                            {
                                                // Escala estrita Píxeis Fonte -> Píxeis Tela
                                                double scaleX = (double)warpW / (pxRight - pxLeft);
                                                double scaleY = (double)warpH / (pyBot - pyTop);

                                                // Evitar Upscaling Massivo pelo GDAL no Zoom In! (Causa OOM Exceptions e desaparecimento do mapa)
                                                // Se scaleX > 1, estamos a fazer Zoom In. Nesse caso, basta ler srcW píxeis nativos (Ex: 5 píxeis) e a Skia trata de esticá-los no ecrã.
                                                int dstW = scaleX > 1.0 ? srcW : Math.Max(1, (int)Math.Round(srcW * scaleX));
                                                int dstH = scaleY > 1.0 ? srcH : Math.Max(1, (int)Math.Round(srcH * scaleY));

                                                double exactDstX = (srcX - pxLeft) * scaleX;
                                                double exactDstY = (srcY - pyTop) * scaleY;
                                                double exactDstRight = (srcX + srcW - pxLeft) * scaleX;
                                                double exactDstBot = (srcY + srcH - pyTop) * scaleY;

                                                // Inversão visual em caso de raster upside-down ou mirrored
                                                if (eixoXInvertido) 
                                                { 
                                                    double t = exactDstX; 
                                                    exactDstX = warpW - exactDstRight; 
                                                    exactDstRight = warpW - t; 
                                                }
                                                if (eixoYInvertido) 
                                                { 
                                                    double t = exactDstY; 
                                                    exactDstY = warpH - exactDstBot; 
                                                    exactDstBot = warpH - t; 
                                                }

                                                int numBandas = Math.Min(vrtDs.RasterCount, 4);
                                                int[] listaBandas = new int[numBandas];
                                                for (int b = 0; b < numBandas; b++) listaBandas[b] = b + 1;

                                                using var rasterBitmap = new SKBitmap(warpW, warpH, SKColorType.Rgba8888, SKAlphaType.Premul);
                                                using (var tmpCanvas = new SKCanvas(rasterBitmap)) 
                                                { 
                                                    tmpCanvas.Clear(new SKColor(0, 0, 0, 0)); 
                                                    
                                                    // Ler GDAL para um buffer isolado perfeitamente alinhado e sem distorção
                                                    using var tileBitmap = new SKBitmap(dstW, dstH, SKColorType.Rgba8888, SKAlphaType.Premul);
                                                    IntPtr tilePtr = tileBitmap.GetPixels();
                                                    
                                                    if (tilePtr != IntPtr.Zero)
                                                    {
                                                        using var extraArg = new OSGeo.GDAL.RasterIOExtraArg();
                                                        extraArg.eResampleAlg = OSGeo.GDAL.RIOResampleAlg.GRIORA_NearestNeighbour;
                                                        if (resampleAlg == "bilinear") extraArg.eResampleAlg = OSGeo.GDAL.RIOResampleAlg.GRIORA_Bilinear;
                                                        else if (resampleAlg == "cubicspline") extraArg.eResampleAlg = OSGeo.GDAL.RIOResampleAlg.GRIORA_CubicSpline;

                                                        using (RasterReadLock.Enter(_mapService.GdalRasterLock, cancellationToken, trace, camadaAtual))
                                                        {
                                                            using (trace?.Measure("io", camadaAtual))
                                                            {
                                                                cancellationToken.ThrowIfCancellationRequested();
                                                                vrtDs.ReadRaster(srcX, srcY, srcW, srcH,
                                                                    tilePtr, dstW, dstH, DataType.GDT_Byte,
                                                                    numBandas, listaBandas,
                                                                    4, dstW * 4, 1, extraArg);
                                                            }
                                                        }
                                                        cancellationToken.ThrowIfCancellationRequested();

                                                        if (numBandas == 3)
                                                        {
                                                            unsafe
                                                            {
                                                                byte* pBase = (byte*)tilePtr.ToPointer();
                                                                for (int y = 0; y < dstH; y++)
                                                                {
                                                                    byte* pRow = pBase + (y * dstW * 4);
                                                                    for (int x = 0; x < dstW; x++) pRow[x * 4 + 3] = 255;
                                                                }
                                                            }
                                                        }

                                                        // SkiaSharp trata do posicionamento Sub-Pixel para evitar o desfoque das bordas!
                                                        using var tilePaint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
                                                        using (trace?.Measure("draw", camadaAtual))
                                                            tmpCanvas.DrawBitmap(tileBitmap, new SKRect((float)exactDstX, (float)exactDstY, (float)exactDstRight, (float)exactDstBot), tilePaint);
                                                    }
                                                }

                                                        // Cache + Draw (mesmo padrão do código original)
                                                        _mapService.PublishRasterCache(
                                                            camadaAtual,
                                                            datasetRaster,
                                                            rasterBitmap.Copy(),
                                                            targetCacheKey,
                                                            (float)cameraPanX,
                                                            (float)cameraPanY,
                                                            zoomReal, coordinateFrame);

                                                        canvas.ResetMatrix();
                                                        canvas.Scale(viewport.PhysicalScaleX, viewport.PhysicalScaleY);
                                                        // OTIMIZAÇÃO DE QUALIDADE: Usar Medium (Bilinear) no Drag, High (Bicubic Spline) na frame final
                                                        var filterQual = isInteracting ? SKFilterQuality.Medium : SKFilterQuality.High;
                                                        using var drawPaint = new SKPaint { FilterQuality = filterQual, IsAntialias = true };
                                                        using (trace?.Measure("draw", camadaAtual))
                                                            canvas.DrawBitmap(rasterBitmap, new SKRect(0, 0, width, height), drawPaint);
                                            }
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        throw;
                                    }
                                    catch (Exception vrtEx)
                                    {
                                        Console.WriteLine($"[VRT FAST-PATH ERROR]: {vrtEx.Message}");
                                        vrtRendered = false; // Fallback para Warp
                                    }
                                }

                                // ===================================================================
                                // FALLBACK: Gdal.Warp tradicional (reprojeção, rotação ou VRT indisponível)
                                // ===================================================================
                                if (!vrtRendered)
                                {
                                    List<string> warpArgsList = new List<string> {
                                        "-te", minLng.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                               minLat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                               maxLng.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                               maxLat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                        "-ts", warpW.ToString(), warpH.ToString(),
                                        "-r", resampleAlg, "-dstalpha", "-wm",
                                        resourceBudget.GdalWarpMb.ToString(CultureInfo.InvariantCulture),
                                        "-of", "MEM"
                                    };

                                    if (!isWms)
                                    {
                                        if (resourceBudget.GdalThreads > 1) warpArgsList.Add("-multi");
                                        warpArgsList.Add("-wo");
                                        warpArgsList.Add($"NUM_THREADS={resourceBudget.GdalThreads}");
                                    }

                                    string srsParaWarp = !string.IsNullOrEmpty(_mapService.ProjetoSRS) ? _mapService.ProjetoSRS : "EPSG:4326";
                                    warpArgsList.Add("-te_srs");
                                    warpArgsList.Add(srsParaWarp);
                                    warpArgsList.Add("-t_srs");
                                    warpArgsList.Add(srsParaWarp);

                                    string[] warpArgs = warpArgsList.ToArray();

                                    Dataset memDs = null;
                                    try
                                    {
                                        using (GDALWarpAppOptions warpOptions = new GDALWarpAppOptions(warpArgs))
                                        {
                                            using (RasterReadLock.Enter(_mapService.GdalRasterLock, cancellationToken, trace, camadaAtual))
                                            {
                                                using (trace?.Measure("io", camadaAtual))
                                                {
                                                    cancellationToken.ThrowIfCancellationRequested();
                                                    memDs = Gdal.Warp("", new[] { datasetRaster }, warpOptions, null, null);
                                                }
                                            }
                                        }
                                        cancellationToken.ThrowIfCancellationRequested();

                                        if (memDs != null && memDs.RasterCount > 0)
                                        {
                                            int numBandas = Math.Min(memDs.RasterCount, 4);
                                            int[] listaBandas = new int[numBandas];
                                            for (int b = 0; b < numBandas; b++) listaBandas[b] = b + 1;

                                            using var rasterBitmap = new SKBitmap(warpW, warpH, SKColorType.Rgba8888, SKAlphaType.Premul);
                                            using (var tmpCanvas = new SKCanvas(rasterBitmap)) { tmpCanvas.Clear(new SKColor(0, 0, 0, 0)); }

                                            IntPtr ptr = rasterBitmap.GetPixels();
                                            if (ptr != IntPtr.Zero)
                                            {
                                                using (trace?.Measure("io", camadaAtual))
                                                    memDs.ReadRaster(0, 0, warpW, warpH, ptr, warpW, warpH, DataType.GDT_Byte, numBandas, listaBandas, 4, warpW * 4, 1);

                                                if (!hasRotation)
                                                {
                                                    _mapService.PublishRasterCache(
                                                        camadaAtual,
                                                        datasetRaster,
                                                        rasterBitmap.Copy(),
                                                        targetCacheKey,
                                                        (float)cameraPanX,
                                                        (float)cameraPanY,
                                                        zoomReal, coordinateFrame);

                                                    canvas.ResetMatrix();
                                                    canvas.Scale(viewport.PhysicalScaleX, viewport.PhysicalScaleY);
                                                    
                                                    // Se a imagem sofreu downscale agressivo, o Skia precisa usar interpolação Bicúbica para ampliar a tela.
                                                    var filterQualFallback = isInteracting ? SKFilterQuality.Medium : SKFilterQuality.High;
                                                    using var drawPaint = new SKPaint { FilterQuality = filterQualFallback, IsAntialias = true };
                                                    using (trace?.Measure("draw", camadaAtual))
                                                        canvas.DrawBitmap(rasterBitmap, new SKRect(0, 0, width, height), drawPaint);
                                                }
                                                else
                                                {
                                                    canvas.SetMatrix(matriz);
                                                    using (trace?.Measure("draw", camadaAtual))
                                                        canvas.DrawBitmap(rasterBitmap, new SKRect(minX, minY, maxX, maxY));
                                                }
                                            }
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        throw;
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[GDAL WARP ERROR]: {ex.Message}");
                                        Console.WriteLine($"[GDAL INFO]: {Gdal.GetLastErrorMsg()}");
                                    }
                                    finally
                                    {
                                        if (memDs != null)
                                        {
                                            memDs.Dispose();
                                        }
                                    }
                                }
                            }
                        }

                    // --- VETORES (Simbologia Dinâmica e Categorizada) ---
                    if (!viewportMundo.IsEmpty)
                    {
                        if (!_mapService.EstilosPorCamada.TryGetValue(camadaAtual, out var estiloCamada))
                        {
                            estiloCamada = new GeoNex.Services.EstiloCamada();
                        }
                        byte alphaCalculado = (byte)(estiloCamada.Opacidade * 255);

                        SKColor corBorda = estiloCamada.CorBorda == "transparent" ? SKColors.Transparent : SKColor.Parse(estiloCamada.CorBorda);
                        SKColor corFill = estiloCamada.CorPreenchimento == "transparent" ? SKColors.Transparent : SKColor.Parse(estiloCamada.CorPreenchimento);

                        // Aplica opacidade diretamente nas cores para evitar o overhead e bugs do SaveLayer
                        if (alphaCalculado < 255)
                        {
                            if (corBorda != SKColors.Transparent) corBorda = corBorda.WithAlpha((byte)(corBorda.Alpha * alphaCalculado / 255));
                            if (corFill != SKColors.Transparent) corFill = corFill.WithAlpha((byte)(corFill.Alpha * alphaCalculado / 255));
                        }

                        pincelDinamicoFill.Color = corFill;
                        pincelDinamicoBorda.Color = corBorda;
                        float espessuraCalculadaLinha = estiloCamada.EspessuraLinha / zoomReal;
                        float espessuraCalculadaBorda = estiloCamada.EspessuraBorda / zoomReal;
                        pincelDinamicoBorda.StrokeWidth = espessuraCalculadaBorda;

                        if (estiloCamada.TipoLinha != "Solid")
                        {
                            float e = espessuraCalculadaLinha;
                            if (e < 0.0001f) e = 0.0001f; // Prevenir divisão por zero ou padrão vazio
                            
                            float[] dashPattern = estiloCamada.TipoLinha switch
                            {
                                "Dash" => new float[] { e * 4, e * 4 },
                                "Dot" => new float[] { e, e * 2 },
                                "DashDot" => new float[] { e * 4, e * 2, e, e * 2 },
                                _ => null
                            };
                            
                            pincelDinamicoBorda.PathEffect = dashPattern != null ? SkiaSharp.SKPathEffect.CreateDash(dashPattern, 0) : null;
                        }
                        else
                        {
                            pincelDinamicoBorda.PathEffect = null;
                        }

                        pincelDinamicoPonto.Color = corFill;

                        // 4. DESENHA POLÍGONOS E LINHAS (COM FALLBACK LOD)
                        if (estiloCamada.TipoSimbologia == "UNICA")
                        {
                            // Overscan de 128 px: o path continua valido durante pequenos pans.
                            // O custo extra ocorre uma vez; os frames seguintes reutilizam a
                            // geometria simplificada sem reler vertices do SHP.
                            float overscanWorld = 128f / zoomReal;
                            var renderViewport = new SKRect(
                                viewportMundo.Left - overscanWorld,
                                viewportMundo.Top - overscanWorld,
                                viewportMundo.Right + overscanWorld,
                                viewportMundo.Bottom + overscanWorld);

                            var preciseViewport = centeredFrame.LocalViewportBounds;
                            preciseViewport.Inflate(overscanWorld, overscanWorld);
                            double minX = precisionOrigin ? _mapService.OffsetMundoX + (double)currentCenter.X + preciseViewport.Left : renderViewport.Left + _mapService.OffsetMundoX;
                            double maxX = precisionOrigin ? _mapService.OffsetMundoX + (double)currentCenter.X + preciseViewport.Right : renderViewport.Right + _mapService.OffsetMundoX;
                            double minY = precisionOrigin ? _mapService.OffsetMundoY - (double)currentCenter.Y - preciseViewport.Bottom : _mapService.OffsetMundoY - renderViewport.Bottom;
                            double maxY = precisionOrigin ? _mapService.OffsetMundoY - (double)currentCenter.Y - preciseViewport.Top : _mapService.OffsetMundoY - renderViewport.Top;
                            var envelopeView = new NetTopologySuite.Geometries.Envelope(minX, maxX, minY, maxY);
                            using ResourceLease<MemoryMappedShapefile>? shapefileLease =
                                _mapService.AcquireShapefile(camadaAtual);
                            MemoryMappedShapefile? shp = shapefileLease?.Resource;
                            using ResourceLease<NativeShapeSpatialIndex>? spatialIndexLease =
                                 _mapService.AcquireSpatialIndex(camadaAtual);
                            _mapService.FeaturesPorCamada.TryGetValue(camadaAtual, out List<CompiledFeature>? layerFeaturesForRender);
                            bool useOverviewLod = !isPrint && !isInteracting &&
                                !estiloCamada.ExibirRotulos && estiloCamada.TipoLinha == "Solid" &&
                                shp?.TransformLocal == null && spatialIndexLease?.Resource.UniformRenderIndex != null &&
                                layerFeaturesForRender is { Count: > 0 } &&
                                layerFeaturesForRender[0].Kind == GeometryKind.Polygon &&
                                _mapService.LimitesVetoresWorld.TryGetValue(camadaAtual, out var lodLayerBounds) &&
                                VectorDisplayPolicy.ShouldUseOverviewLod(layerFeaturesForRender.Count, envelopeView, lodLayerBounds);
                            bool compactPolygons = isInteracting && !isPrint && estiloCamada.TipoLinha == "Solid" &&
                                shp?.TransformLocal == null && spatialIndexLease?.Resource.UniformRenderIndex != null;
                            compactPolygons = compactPolygons || useOverviewLod;

                            // O cache de geometria deve ser consultado antes do índice.
                            // Consultar o índice só para descobrir que o path já
                            // estava pronto anulava o ganho em camadas grandes.
                            SKPath? cachedPolygonPath = null;
                            bool polygonCacheHit = false;
                            if (!estiloCamada.ExibirRotulos && shp != null &&
                                 layerFeaturesForRender is { Count: > 0 } && layerFeaturesForRender[0].Kind == GeometryKind.Polygon)
                            using (trace?.Measure("geometry", camadaAtual))
                            {
                                polygonCacheHit = precisionOrigin
                                    ? (shp.TryGetPreciseRenderPath(centeredFrame.LocalViewportBounds, currentCenter, zoomReal,
                                            isInteracting, out cachedPolygonPath, compactPolygons) ||
                                        (!isPrint && !isInteracting && !compactPolygons && shp.TryGetProjectedRenderPath(
                                            centeredFrame.LocalViewportBounds, currentCenter, zoomReal,
                                            _mapService.OffsetMundoX, _mapService.OffsetMundoY,
                                            out cachedPolygonPath, cancellationToken)))
                                    : shp.TryGetRenderPath(viewportMundo, zoomReal, isInteracting, out cachedPolygonPath, compactPolygons);
                            }
                            if (polygonCacheHit)
                            {
                                try
                                {
                                    canvas.SetMatrix(precisionOrigin ? centeredFrame.LocalToPhysicalMatrix : matriz);
                                    using (trace?.Measure("draw", camadaAtual))
                                    {
                                        cancellationToken.ThrowIfCancellationRequested();
                                        bool skipCachedBorder = !precisionOrigin && TransformedRingWriter.SkipPreviewBorder(
                                            isInteracting, layerFeaturesForRender!.Count, cachedPolygonPath!.PointCount);
                                        DrawPolygon(camadaAtual, shp!, vectorPresentationRevision, viewportMundo, currentCenter,
                                            canvas, cachedPolygonPath!, estiloCamada.PreenchimentoTransparente ? null : pincelDinamicoFill,
                                            estiloCamada.BordaTransparente || skipCachedBorder ? null : pincelDinamicoBorda,
                                            physicalWidth, physicalHeight, polygonImageBudget,
                                            !isPrint && !isInteracting && rotation == 0 && estiloCamada.TipoLinha == "Solid",
                                            cancellationToken);
                                    }
                                }
                                finally
                                {
                                    canvas.SetMatrix(matriz);
                                    cachedPolygonPath?.Dispose();
                                }
                                continue;
                            }
                            
                            if (spatialIndexLease != null)
                            {
                                NativeShapeSpatialIndex arvores = compactPolygons
                                    ? spatialIndexLease.Resource.UniformRenderIndex! : spatialIndexLease.Resource;
                                NativeFeatureQuery? queryLease = null;
                                try
                                {
                                System.Collections.Generic.IList<CompiledFeature> feicoesVisiveis;
                                if (_mapService.LimitesVetoresWorld.TryGetValue(camadaAtual, out var layerBounds) &&
                                    envelopeView.MinX <= layerBounds.MinX && envelopeView.MaxX >= layerBounds.MaxX &&
                                    envelopeView.MinY <= layerBounds.MinY && envelopeView.MaxY >= layerBounds.MaxY &&
                                    _mapService.FeaturesPorCamada.TryGetValue(camadaAtual, out var allLayerFeatures))
                                {
                                    // Visão geral: a lista original já contém todas as feições em
                                    // ordem. Não materializar outra lista com milhões de referências.
                                    feicoesVisiveis = compactPolygons ? arvores.AllFeatures : allLayerFeatures;
                                }
                                else
                                {
                                    using (trace?.Measure("geometry", camadaAtual))
                                    {
                                        queryLease = arvores.Query(envelopeView);
                                        feicoesVisiveis = queryLease;
                                    }
                                }
                                // STRtree.Query ja devolve apenas envelopes intersectantes.
                                // Consumir a lista diretamente remove uma copia/alocacao por frame
                                // e, principalmente, nao elimina feicoes arbitrariamente.
                                var feicoesList = feicoesVisiveis;
                                
                                if (feicoesList.Count == 0) continue;
                                
                                // RENDERIZAÇÃO VETORIAL (COM DELEGAÇÃO BATCH PARA C++)
                                using var lineOuter = new SkiaSharp.SKPaint { Style = SkiaSharp.SKPaintStyle.Stroke, StrokeJoin = SkiaSharp.SKStrokeJoin.Round, StrokeCap = SkiaSharp.SKStrokeCap.Round, IsAntialias = suavizar, Color = pincelDinamicoBorda.Color, StrokeWidth = espessuraCalculadaBorda, PathEffect = pincelDinamicoBorda.PathEffect };
                                using var lineInner = new SkiaSharp.SKPaint { Style = SkiaSharp.SKPaintStyle.Stroke, StrokeJoin = SkiaSharp.SKStrokeJoin.Round, StrokeCap = SkiaSharp.SKStrokeCap.Round, IsAntialias = suavizar, Color = pincelDinamicoFill.Color, BlendMode = SkiaSharp.SKBlendMode.SrcOver, StrokeWidth = espessuraCalculadaLinha, PathEffect = pincelDinamicoBorda.PathEffect };
                                using var ptPaint = new SkiaSharp.SKPaint { Style = SkiaSharp.SKPaintStyle.Fill, Color = pincelDinamicoFill.Color, IsAntialias = false, StrokeWidth = 2.0f, StrokeCap = SkiaSharp.SKStrokeCap.Square };
                                
                                var kind = feicoesList[0].Kind;
                                float resolution = 1.0f / zoomReal;

                                if (kind == GeoNex.Services.GeometryKind.Point)
                                {
                                    canvas.SetMatrix(matriz);
                                    float radius = 4f / zoomReal;
                                    bool hasBorder = !estiloCamada.BordaTransparente;
                                    using var ptBorder = new SkiaSharp.SKPaint { Style = SkiaSharp.SKPaintStyle.Stroke, Color = pincelDinamicoBorda.Color, IsAntialias = true, StrokeWidth = 1f / zoomReal };
                                    using (trace?.Measure("draw", camadaAtual))
                                    {
                                        foreach (var f in feicoesList)
                                        {
                                            cancellationToken.ThrowIfCancellationRequested();
                                            canvas.DrawCircle(f.CentroidLocal, radius, ptPaint);
                                            if (hasBorder) canvas.DrawCircle(f.CentroidLocal, radius, ptBorder);
                                        }
                                    }
                                }
                                else
                                {
                                    SkiaSharp.SKPath? batchPath = null;
                                    try
                                    {
                                        bool cacheHit = shp != null && (precisionOrigin
                                            ? shp.TryGetPreciseRenderPath(centeredFrame.LocalViewportBounds, currentCenter, zoomReal,
                                                isInteracting, out batchPath, compactPolygons)
                                            : shp.TryGetRenderPath(viewportMundo, zoomReal, isInteracting, out batchPath, compactPolygons));

                                        using (trace?.Measure("geometry", camadaAtual))
                                        {
                                            if (!cacheHit)
                                            {
                                                // SHP define orientacao oposta para aneis externos e buracos.
                                                // Winding preserva esses buracos e evita que duas feicoes
                                                // independentes sobrepostas se anulem como no EvenOdd global.
                                                batchPath = new SkiaSharp.SKPath { FillType = SkiaSharp.SKPathFillType.Winding };
                                                if (shp != null)
                                                {
                                                    // Se a camada tem transformação de coordenadas (ex: UTM → Web Mercator),
                                                    // usa o Batch Transform que faz UMA ÚNICA chamada GDAL para milhões de pontos.
                                                    var transform = shp.TransformLocal?.Value;
                                                    var projectedBuilder = transform != null && precisionOrigin && !isPrint && !isInteracting &&
                                                        !compactPolygons && kind == GeometryKind.Polygon && !estiloCamada.ExibirRotulos
                                                        ? new ProjectedPathGeometry.Builder(VectorRuntimeResources.Current.CacheBytes / 4,
                                                            _mapService.OffsetMundoX, _mapService.OffsetMundoY) : null;
                                                    double pathOffsetX = _mapService.OffsetMundoX + (precisionOrigin ? (double)currentCenter.X : 0);
                                                    double pathOffsetY = _mapService.OffsetMundoY - (precisionOrigin ? (double)currentCenter.Y : 0);
                                                    var pathViewport = precisionOrigin
                                                        ? preciseViewport : renderViewport;
                                                    if (transform != null)
                                                    {
                                                        GeoNex.Services.CompiledFeature.BuildBatchPathWithProjectedCache(
                                                            batchPath, shp, feicoesList,
                                                            pathOffsetX, pathOffsetY,
                                                            resolution, zoomReal, transform, cancellationToken, isInteracting, projectedBuilder
                                                        );
                                                    }
                                                    else
                                                    {
                                                        GeoNex.Services.CompiledFeature.BuildBatchPathFromNative(
                                                            batchPath,
                                                            shp,
                                                            feicoesList,
                                                            pathOffsetX,
                                                            pathOffsetY,
                                                            zoomReal,
                                                            ref pathViewport,
                                                            isInteracting,
                                                            cancellationToken
                                                        );
                                                    }

                                                    cancellationToken.ThrowIfCancellationRequested();
                                                    // Only the exact transformed path is independent of scale.
                                                    // Native LOD and previews retain their exact-zoom cache identity.
                                                    if (!precisionOrigin) shp.StoreRenderPath(batchPath, renderViewport, zoomReal, isInteracting,
                                                        compactPolygons, scaleIndependent: transform != null && !isInteracting);
                                                    else if (projectedBuilder != null) shp.StoreProjectedRenderPath(
                                                        batchPath, preciseViewport, currentCenter, zoomReal, projectedBuilder.Build());
                                                    else shp.StorePreciseRenderPath(batchPath, preciseViewport, currentCenter, zoomReal,
                                                        isInteracting, compactPolygons, scaleIndependent: transform != null && !isInteracting);
                                                }
                                            }
                                        }

                                        canvas.SetMatrix(precisionOrigin ? centeredFrame.LocalToPhysicalMatrix : matriz);

                                        using (trace?.Measure("draw", camadaAtual))
                                        {
                                            cancellationToken.ThrowIfCancellationRequested();
                                            if (kind == GeoNex.Services.GeometryKind.Polygon)
                                            {
                                                // A borda e o segundo DrawPath mais caro; durante interacao
                                                // o cache visual ja preserva a leitura e podemos omiti-la.
                                                bool skipBorder = !precisionOrigin && TransformedRingWriter.SkipPreviewBorder(
                                                    isInteracting, feicoesList.Count, batchPath!.PointCount);
                                                DrawPolygon(camadaAtual, shp!, vectorPresentationRevision, viewportMundo, currentCenter,
                                                    canvas, batchPath!, estiloCamada.PreenchimentoTransparente ? null : pincelDinamicoFill,
                                                    estiloCamada.BordaTransparente || skipBorder ? null : pincelDinamicoBorda,
                                                    physicalWidth, physicalHeight, polygonImageBudget,
                                                    shp != null && !isPrint && !isInteracting && rotation == 0 &&
                                                        !estiloCamada.ExibirRotulos && estiloCamada.TipoLinha == "Solid",
                                                    cancellationToken);
                                            }
                                            else if (kind == GeoNex.Services.GeometryKind.Line)
                                            {
                                                if (!estiloCamada.BordaTransparente && zoomReal > 0.0005f) canvas.DrawPath(batchPath, lineOuter);
                                                cancellationToken.ThrowIfCancellationRequested();
                                                canvas.DrawPath(batchPath, lineInner);
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        canvas.SetMatrix(matriz); // Other layers, labels and editing use project-local coordinates.
                                        batchPath?.Dispose();
                                    }
                                }
                                }
                                finally
                                {
                                    queryLease?.Dispose();
                                }
                            }
                        }
                        else if (estiloCamada.TipoSimbologia == "CATEGORIZADA")
                        {
                            if (_mapService.VetoresCategorizados.TryGetValue(camadaAtual, out var fragmentosDaCamada))
                            {
                                using var categorizedPolygonDrawTrace = trace?.Measure("draw", camadaAtual);
                                using var ptPaint = new SkiaSharp.SKPaint { Style = SkiaSharp.SKPaintStyle.Fill, IsAntialias = false, StrokeWidth = 2.0f, StrokeCap = SkiaSharp.SKStrokeCap.Square };
                                
                                foreach (var categoria in fragmentosDaCamada)
                                {
                                    string nomeCategoria = categoria.Key;
                                    
                                    string corHex = estiloCamada.CoresCategorizadas.ContainsKey(nomeCategoria)
                                        ? estiloCamada.CoresCategorizadas[nomeCategoria]
                                        : "#808080";
                                    SKColor corBase = SKColor.Parse(corHex);
                                    pincelDinamicoFill.Color = corBase;
                                    ptPaint.Color = corBase;
                                    
                                    foreach (var polyPathFragmento in categoria.Value)
                                    {
                                        cancellationToken.ThrowIfCancellationRequested();
                                        if (viewportMundo.IntersectsWith(polyPathFragmento.Bounds))
                                        {
                                            float larguraNoEcra = (float)polyPathFragmento.Bounds.Width * zoomReal;
                                            float alturaNoEcra = (float)polyPathFragmento.Bounds.Height * zoomReal;

                                            canvas.SetMatrix(matriz);

                                            if (!estiloCamada.PreenchimentoTransparente)
                                            {
                                                bool pequeno = larguraNoEcra < 10f && alturaNoEcra < 10f;
                                                pincelDinamicoFill.IsAntialias = !pequeno;
                                                canvas.DrawPath(polyPathFragmento, pincelDinamicoFill);
                                                pincelDinamicoFill.IsAntialias = true;
                                            }

                                            // 2. FAST-PATH LOD
                                            float borderCullSizeCat = isInteracting ? 25f : 10f;
                                            cancellationToken.ThrowIfCancellationRequested();
                                            if (!estiloCamada.BordaTransparente && larguraNoEcra >= borderCullSizeCat && alturaNoEcra >= borderCullSizeCat)
                                            {
                                                canvas.DrawPath(polyPathFragmento, pincelDinamicoBorda);
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        // 5. DESENHA LINHAS (Redes, arruamentos)
                        if (estiloCamada.TipoSimbologia == "CATEGORIZADA")
                        {
                            if (_mapService.LinhasCategorizadas.TryGetValue(camadaAtual, out var fragmentosDaLinha))
                            {
                                using var categorizedLineDrawTrace = trace?.Measure("draw", camadaAtual);
                                using var lineOuter = new SKPaint
                                {
                                    Style = SKPaintStyle.Stroke,
                                    StrokeJoin = SKStrokeJoin.Round,
                                    StrokeCap = SKStrokeCap.Round,
                                    IsAntialias = true,
                                    Color = pincelDinamicoBorda.Color,
                                    StrokeWidth = espessuraCalculadaBorda,
                                    PathEffect = pincelDinamicoBorda.PathEffect
                                };
                                using var lineInner = new SKPaint
                                {
                                    Style = SKPaintStyle.Stroke,
                                    StrokeJoin = SKStrokeJoin.Round,
                                    StrokeCap = SKStrokeCap.Round,
                                    IsAntialias = true,
                                    Color = pincelDinamicoFill.Color,
                                    BlendMode = SKBlendMode.SrcOver,
                                    StrokeWidth = espessuraCalculadaLinha,
                                    PathEffect = pincelDinamicoBorda.PathEffect
                                };
                                
                                foreach (var categoria in fragmentosDaLinha)
                                {
                                    string nomeCategoria = categoria.Key;
                                    
                                    string corHex = estiloCamada.CoresCategorizadas.ContainsKey(nomeCategoria)
                                        ? estiloCamada.CoresCategorizadas[nomeCategoria]
                                        : "#808080";
                                    SKColor corBase = SKColor.Parse(corHex);
                                    lineInner.Color = corBase;

                                    foreach (var linePathFragmento in categoria.Value)
                                    {
                                        cancellationToken.ThrowIfCancellationRequested();
                                        if (viewportMundo.IntersectsWith(linePathFragmento.Bounds))
                                        {
                                            canvas.SetMatrix(matriz);

                                            if (!estiloCamada.BordaTransparente)
                                            {
                                                canvas.DrawPath(linePathFragmento, lineOuter);
                                            }

                                            cancellationToken.ThrowIfCancellationRequested();
                                            if (!estiloCamada.PreenchimentoTransparente)
                                            {
                                                canvas.DrawPath(linePathFragmento, lineInner);
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        // 6. DESENHA PONTOS (Postes, árvores)
                        if (_mapService.PontosPorCamada.TryGetValue(camadaAtual, out var pointPath))
                        {
                            using var pointDrawTrace = trace?.Measure("draw", camadaAtual);
                            if (viewportMundo.IntersectsWith(pointPath.Bounds))
                            {
                                canvas.SetMatrix(matriz);

                                // 1. Borda (raio * 2 + espessura * 2)
                                using var pBorda = new SKPaint
                                {
                                    Style = SKPaintStyle.Stroke,
                                    StrokeCap = SKStrokeCap.Round,
                                    IsAntialias = true,
                                    Color = pincelDinamicoBorda.Color,
                                    StrokeWidth = (estiloCamada.Tamanho * 2 + estiloCamada.EspessuraBorda * 2) / zoomReal
                                };

                                // 2. Núcleo (raio * 2)
                                using var pFill = new SKPaint
                                {
                                    Style = SKPaintStyle.Stroke,
                                    StrokeCap = SKStrokeCap.Round,
                                    IsAntialias = true,
                                    Color = pincelDinamicoPonto.Color,
                                    StrokeWidth = (estiloCamada.Tamanho * 2) / zoomReal
                                };

                                if (!estiloCamada.BordaTransparente)
                                    canvas.DrawPath(pointPath, pBorda);

                                if (!estiloCamada.PreenchimentoTransparente)
                                    canvas.DrawPath(pointPath, pFill);
                            }
                        }

                        // FECHA O COMPOSITING DA CAMADA (Aplica a opacidade de uma só vez a tudo!)
                        // (SaveLayer removido)
                        // 7. MOTOR DE RÓTULOS DINÂMICOS
                        if (estiloCamada.ExibirRotulos && !string.IsNullOrEmpty(estiloCamada.ColunaRotulo))
                        {
                            using var labelDrawTrace = trace?.Measure("draw", camadaAtual);
                            if (_mapService.PontosAncoragemRotulo.TryGetValue(camadaAtual, out var ancoras) && ancoras.Count > 0)
                            {
                                using var fonteTypeface = estiloCamada.RotuloNegrito
                                    ? SKTypeface.FromFamilyName(null, SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                                    : SKTypeface.Default;

                                using var pincelTexto = new SKPaint
                                {
                                    Typeface = fonteTypeface,
                                    TextSize = estiloCamada.TamanhoTextoRotulo * viewport.PixelScale,
                                    IsAntialias = true,
                                    Color = SKColor.Parse(estiloCamada.CorTextoRotulo),
                                    TextAlign = SKTextAlign.Center
                                };

                                using var pincelHalo = new SKPaint
                                {
                                    Typeface = fonteTypeface,
                                    TextSize = estiloCamada.TamanhoTextoRotulo * viewport.PixelScale,
                                    IsAntialias = true,
                                    Color = SKColor.Parse(estiloCamada.CorHaloRotulo),
                                    TextAlign = SKTextAlign.Center,
                                    Style = SKPaintStyle.Stroke,
                                    StrokeWidth = estiloCamada.TamanhoHaloRotulo * viewport.PixelScale,
                                    StrokeJoin = SKStrokeJoin.Round
                                };

                                float ajusteY = (pincelTexto.FontMetrics.Descent - pincelTexto.FontMetrics.Ascent) / 2f - pincelTexto.FontMetrics.Descent;

                                canvas.SetMatrix(SKMatrix.Identity);

                                foreach (var ancora in ancoras)
                                {
                                    float larguraNoEcra = ancora.LarguraMundo * zoomReal;

                                    if (larguraNoEcra < 35f) continue;

                                    if (viewportMundo.Contains(ancora.Ponto))
                                    {
                                        if (ancora.Atributos != null && ancora.Atributos.Exists(estiloCamada.ColunaRotulo))
                                        {
                                            var valorObjeto = ancora.Atributos[estiloCamada.ColunaRotulo];
                                            if (valorObjeto != null)
                                            {
                                                string texto = valorObjeto.ToString();
                                                SKPoint pontoMonitor = matriz.MapPoint(ancora.Ponto);

                                                if (estiloCamada.TamanhoHaloRotulo > 0)
                                                {
                                                    canvas.DrawText(texto, pontoMonitor.X, pontoMonitor.Y + ajusteY, pincelHalo);
                                                }
                                                canvas.DrawText(texto, pontoMonitor.X, pontoMonitor.Y + ajusteY, pincelTexto);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                } // Fim do loop de camadas

                // 8. FERRAMENTA DE MEDIÇÃO
                if (_mapService.PontosMedicao.Count > 0 || _mapService.PontoCursorMundo.HasValue)
                {
                    canvas.SetMatrix(matriz);
                    using var pincelLinha = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Cyan, StrokeWidth = 2.5f / zoomReal, IsAntialias = true };
                    using var pincelLinhaTracejada = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White.WithAlpha(180), StrokeWidth = 1.5f / zoomReal, PathEffect = SKPathEffect.CreateDash(new float[] { 10f / zoomReal, 10f / zoomReal }, 0), IsAntialias = true };
                    using var pincelPontoMed = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
                    using var pincelBordaPontoMed = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Cyan, StrokeWidth = 1.5f / zoomReal, IsAntialias = true };
                    using var pincelArea = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Cyan.WithAlpha(40), IsAntialias = true };

                    var pathMedicao = new SKPath();
                    for (int i = 0; i < ptsMedicao.Count; i++)
                    {
                        if (i == 0) pathMedicao.MoveTo(ptsMedicao[i]);
                        else pathMedicao.LineTo(ptsMedicao[i]);
                    }

                    if (_mapService.MostrarAreaMedicao && ptsMedicao.Count > 2)
                    {
                        var pathArea = new SKPath(pathMedicao);
                        pathArea.Close();
                        canvas.DrawPath(pathArea, pincelArea);
                        canvas.DrawLine(ptsMedicao[^1], ptsMedicao[0], pincelLinhaTracejada);
                    }

                    if (ptsMedicao.Count > 0) canvas.DrawPath(pathMedicao, pincelLinha);
                    if (ptsMedicao.Count > 0 && _mapService.PontoCursorMundo.HasValue)
                    {
                        canvas.DrawLine(ptsMedicao[^1], _mapService.PontoCursorMundo.Value, pincelLinhaTracejada);
                    }

                    foreach (var pt in ptsMedicao)
                    {
                        canvas.DrawCircle(pt, 4.5f / zoomReal, pincelPontoMed);
                        canvas.DrawCircle(pt, 4.5f / zoomReal, pincelBordaPontoMed);
                    }
                }
                // 8.1. FERRAMENTA DE AQUISIÇÃO (DESENHO DE LOTE)
                if (ptsAquisicao.Count > 0)
                {
                    canvas.SetMatrix(matriz);

                    // ==========================================================
                    // >>> ANEL VISUAL DE RESTRIÇÃO (COMPASSO AZUL) <<<
                    // ==========================================================
                    // ==========================================================
                    // >>> ANEL VISUAL DE RESTRIÇÃO (COMPASSO AZUL) <<<
                    // ==========================================================
                    // ==========================================================
                    // >>> ANEL VISUAL DE RESTRIÇÃO (COMPASSO DE ALTO CONTRASTE) <<<
                    // ==========================================================
                    if (_mapService.TravaDistanciaAtiva && _mapService.TravaDistanciaValor > 0)
                    {
                        var ultimoPonto = ptsAquisicao[^1];

                        // 1. O HALO PRETO (Fundo para garantir contraste em telhados brancos/ortofotos claras)
                        using var paintAnelFundo = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            Color = SKColors.Black.WithAlpha(180), // Preto meio transparente
                            StrokeWidth = 4.0f / zoomReal,         // Mais grosso que a linha cyan
                            IsAntialias = true
                        };

                        // 2. A LINHA CYAN PRINCIPAL (Agora 100% sólida e ligeiramente mais grossa)
                        using var paintAnelGuia = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            Color = SKColors.Cyan,                 // Removido o alpha, agora brilha a 100%
                            StrokeWidth = 2.0f / zoomReal,         // Aumentado de 1.5 para 2.0
                            IsAntialias = true,
                            PathEffect = SKPathEffect.CreateDash(new float[] { 8f / zoomReal, 8f / zoomReal }, 0) // Traços maiores
                        };

                        // Desenha primeiro o fundo preto e depois o tracejado cyan por cima
                        canvas.DrawCircle(ultimoPonto.X, ultimoPonto.Y, (float)_mapService.TravaDistanciaValor, paintAnelFundo);
                        canvas.DrawCircle(ultimoPonto.X, ultimoPonto.Y, (float)_mapService.TravaDistanciaValor, paintAnelGuia);
                    }
                    // ==========================================================
                    // ==========================================================
                    // ==========================================================
                    // ==========================================================

                    // Estética Profissional para o modo de Desenho (Verde Primavera)
                    using var pincelLinhaAq = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.SpringGreen, StrokeWidth = 2.5f / zoomReal, IsAntialias = true };
                    using var pincelTracejadoAq = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.SpringGreen.WithAlpha(180), StrokeWidth = 1.5f / zoomReal, PathEffect = SKPathEffect.CreateDash(new float[] { 10f / zoomReal, 10f / zoomReal }, 0), IsAntialias = true };
                    using var pincelPontoAq = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
                    using var pincelBordaPontoAq = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.SpringGreen, StrokeWidth = 1.5f / zoomReal, IsAntialias = true };
                    using var pincelAreaAq = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.SpringGreen.WithAlpha(60), IsAntialias = true };

                    var pathAq = new SKPath();
                    for (int i = 0; i < ptsAquisicao.Count; i++)
                    {
                        if (i == 0) pathAq.MoveTo(ptsAquisicao[i]);
                        else pathAq.LineTo(ptsAquisicao[i]);
                    }

                    // ==========================================================
                    // >>> UPGRADE PROFISSIONAL: CROSSHAIR E HUD DINÂMICO <<<
                    // ==========================================================
                    if (_mapService.PontoCursorMundo.HasValue)
                    {
                        var cursorPts = _mapService.PontoCursorMundo.Value;

                        // 1. MIRA ORTOGONAL (CROSSHAIR ESTILO AUTOCAD)
                        using var paintMira = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White.WithAlpha(100), StrokeWidth = 1f / zoomReal, IsAntialias = false };
                        // Linha Horizontal infinita
                        canvas.DrawLine(viewportMundo.Left, cursorPts.Y, viewportMundo.Right, cursorPts.Y, paintMira);
                        // Linha Vertical infinita
                        canvas.DrawLine(cursorPts.X, viewportMundo.Top, cursorPts.X, viewportMundo.Bottom, paintMira);

                        // 2. HUD DINÂMICO NO CURSOR (LIVE TOOLTIP)
                        if (ptsAquisicao.Count > 0)
                        {
                            var ultimoPt = ptsAquisicao.Last();

                            // Calcula Distância e Azimute Real
                            double dx = cursorPts.X - ultimoPt.X;
                            double dy = cursorPts.Y - ultimoPt.Y;
                            double distanciaReal = Math.Sqrt(dx * dx + dy * dy);

                            // Calcula o Azimute Geográfico (Norte = 0º, sentido horário)
                            double azimuteRad = Math.Atan2(dx, dy);
                            double azimuteDeg = azimuteRad * (180.0 / Math.PI);
                            if (azimuteDeg < 0) azimuteDeg += 360;

                            string textoHud = $"D: {distanciaReal:F2}m  |  Az: {azimuteDeg:F1}°";

                            // Estilo do Texto
                            using var paintTextoHud = new SKPaint { Typeface = SKTypeface.Default, TextSize = 12f / zoomReal, Color = SKColors.White, IsAntialias = true };

                            // Mede o tamanho do texto para criar a caixa de fundo
                            var rectTexto = new SKRect();
                            paintTextoHud.MeasureText(textoHud, ref rectTexto);

                            // Define a posição da caixa flutuante (25px para a direita e para baixo do rato)
                            float offsetCaixa = 25f / zoomReal;
                            float padding = 6f / zoomReal;
                            var caixaFundo = new SKRect(
                                cursorPts.X + offsetCaixa,
                                cursorPts.Y + offsetCaixa,
                                cursorPts.X + offsetCaixa + rectTexto.Width + (padding * 2),
                                cursorPts.Y + offsetCaixa + rectTexto.Height + (padding * 2)
                            );

                            // Desenha o Fundo Translúcido (Glassmorphism)
                            using var paintFundoHud = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Black.WithAlpha(180), IsAntialias = true };
                            using var paintBordaHud = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Cyan.WithAlpha(150), StrokeWidth = 1f / zoomReal, IsAntialias = true };

                            canvas.DrawRoundRect(caixaFundo, 4f / zoomReal, 4f / zoomReal, paintFundoHud);
                            canvas.DrawRoundRect(caixaFundo, 4f / zoomReal, 4f / zoomReal, paintBordaHud);

                            // Escreve o texto dentro da caixa
                            canvas.DrawText(textoHud, caixaFundo.Left + padding, caixaFundo.Bottom - padding, paintTextoHud);
                        }
                    }
                    // ==========================================================

                    // Desenha os vértices (bolinhas brancas com borda verde) por cima de tudo
                    // ==========================================================
                    // >>> RENDERIZAÇÃO FINAL (Área, Esqueleto e Vértices) <<<
                    // ==========================================================

                    if (ptsAquisicao.Count >= 2 && _mapService.PontoCursorMundo.HasValue)
                    {
                        var pathAreaAq = new SKPath(pathAq);
                        pathAreaAq.Close();
                        canvas.DrawPath(pathAreaAq, pincelAreaAq);
                    }

                    canvas.DrawPath(pathAq, pincelLinhaAq);

                    for (int i = 0; i < ptsAquisicao.Count; i++)
                    {
                        var pt = ptsAquisicao[i];

                        if (i == 0) // PONTO DE ORIGEM LARANJA LIMPO
                        {
                            using var pincelOrigemFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Orange, IsAntialias = true };
                            using var pincelOrigemBorda = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White, StrokeWidth = 2f / zoomReal, IsAntialias = true };

                            canvas.DrawCircle(pt, 5.5f / zoomReal, pincelOrigemFill);
                            canvas.DrawCircle(pt, 5.5f / zoomReal, pincelOrigemBorda);
                        }
                        else // RESTANTES VÉRTICES
                        {
                            canvas.DrawCircle(pt, 4.5f / zoomReal, pincelPontoAq);
                            canvas.DrawCircle(pt, 4.5f / zoomReal, pincelBordaPontoAq);
                        }
                    }
                } // Fim do bloco if (ptsAquisicao.Count > 0)

                // 9. SNAP HOVER MAGNÉTICO
                if (_mapService.PontoCursorSnap.HasValue)
                {
                    canvas.SetMatrix(matriz);
                    var snapPt = _mapService.PontoCursorSnap.Value;
                    using var pincelSnap = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Yellow, StrokeWidth = 2.0f / zoomReal, IsAntialias = true };
                    using var pincelSnapFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Yellow.WithAlpha(80), IsAntialias = true };
                    float size = 14f / zoomReal;
                    var rect = new SKRect(snapPt.X - size / 2, snapPt.Y - size / 2, snapPt.X + size / 2, snapPt.Y + size / 2);
                    canvas.DrawRect(rect, pincelSnapFill);
                    canvas.DrawRect(rect, pincelSnap);
                    canvas.DrawLine(snapPt.X - size, snapPt.Y, snapPt.X + size, snapPt.Y, pincelSnap);
                    canvas.DrawLine(snapPt.X, snapPt.Y - size, snapPt.X, snapPt.Y + size, pincelSnap);
                }
                // =========================================================================
                // 10. CONTORNO ANIMADO DA FEIÇÃO SELECIONADA (MARCHING ANTS)
                // =========================================================================
                if (_mapService.CaminhoDestaquePoligono != null)
                {
                    canvas.SetMatrix(matriz);
                    using var paintM = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = SKColors.Cyan,
                        StrokeWidth = 3f / zoomReal,
                        PathEffect = SKPathEffect.CreateDash(new float[] { 10f / zoomReal, 10f / zoomReal }, (DateTime.Now.Millisecond % 1000) / 1000f * 20f / zoomReal),
                        IsAntialias = true
                    };
                    using var paintFill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Cyan.WithAlpha(50) };
                    canvas.DrawPath(_mapService.CaminhoDestaquePoligono, paintFill);
                    canvas.DrawPath(_mapService.CaminhoDestaquePoligono, paintM);
                }
                if (_mapService.CaminhoDestaqueLinha != null)
                {
                    canvas.SetMatrix(matriz);
                    using var paintM = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = SKColors.Cyan,
                        StrokeWidth = 4f / zoomReal,
                        PathEffect = SKPathEffect.CreateDash(new float[] { 10f / zoomReal, 10f / zoomReal }, (DateTime.Now.Millisecond % 1000) / 1000f * 20f / zoomReal),
                        IsAntialias = true
                    };
                    canvas.DrawPath(_mapService.CaminhoDestaqueLinha, paintM);
                }

                canvas.Restore();
                cancellationToken.ThrowIfCancellationRequested();
                res.AppendHeader("X-GeoNex-Online-Pending", onlinePending ? "1" : "0");
                using var frameSnapshot = new RasterFrameSnapshot(renderTarget.Finish());
                var image = frameSnapshot.Image;

                // Retain one scene image for gestures even while online pixels are
                // pending. Only a completed render may satisfy a final request.
                if (!isInteracting && !isPrint && rotation == 0 && panOffsetX == 0 && panOffsetY == 0)
                {
                    SKBitmap? snapshot = frameSnapshot.CreateCacheBitmap();
                    try
                    {
                        if (!deferOnline && !onlinePending)
                            _mapService.PublishGlobalCache(snapshot, zoomReal, cameraPanX, cameraPanY,
                                viewport, coordinateFrame, sceneRevision, cameraZoom);
                        else
                            _mapService.PublishGlobalPreviewCache(snapshot, zoomReal, cameraPanX, cameraPanY,
                                viewport, coordinateFrame, sceneRevision, cameraZoom);
                        snapshot = null; // Published (or rejected and disposed) by the scene cache.
                    }
                    finally { snapshot?.Dispose(); }
                }

                trace?.MarkRenderReady();
                WriteEncodedFrame(
                    image, res, isInteracting, cancellationToken,
                    generation, trace, navigation: !isPrint, reusePayloadId: req.QueryString["reuse"]);
                }
                finally
                {
                    grContext?.Dispose();
                    glInterface?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _telemetry.CompleteResponse(trace, "failed", 0);
                Console.Error.WriteLine("Render Error: " + ex.ToString());
                try { context.Response.StatusCode = 500; context.Response.Close(); } catch { }
            }
        }

        private void WriteEncodedFrame(
            SKImage image,
            HttpListenerResponse response,
            bool isInteracting,
            CancellationToken cancellationToken,
            long generation,
            RenderFrameTrace? trace, bool navigation = false, string? reusePayloadId = null,
            bool cacheIdentityPreview = false)
        {
            SKEncodedImageFormat format = UseWebpEncoding
                ? SKEncodedImageFormat.Webp
                : SKEncodedImageFormat.Png;
            int quality = UseWebpEncoding ? (isInteracting ? 70 : 95) : 100;
            int pngCompression = !UseWebpEncoding && navigation
                ? MapFrameEncoding.NavigationCompressionLevel(image, GdalRuntimeConfiguration.Apply().AvailablePhysicalMb)
                : 1;
            string encoding = UseWebpEncoding ? $"webp-{quality}" : pngCompression == 0 ? "png-store" : "png";
            trace?.SetEncoding(encoding);

            cancellationToken.ThrowIfCancellationRequested();
            long encodeStarted = Stopwatch.GetTimestamp();
            string? payloadId = null;
            // Identity previews can reuse the final PNG after exact all-pixel
            // validation. Moving previews avoid hashing every new pan frame.
            bool useEncodedCache = !UseWebpEncoding && navigation && (!isInteracting || cacheIdentityPreview);
            long encodedBudget = !useEncodedCache || Environment.GetEnvironmentVariable("GEONEX_ENCODED_FRAME_CACHE") == "0" ? 0 :
                EncodedFrameCache.Budget(GdalRuntimeConfiguration.Apply().AvailablePhysicalMb);
            using ResourceLease<SKData>? encodedLease = useEncodedCache
                ? (isInteracting
                    ? _encodedFrames.TryEncodePreview(image, pngCompression, encodedBudget, cancellationToken, out payloadId)
                    : _encodedFrames.Encode(image, pngCompression, encodedBudget, cancellationToken, out payloadId))
                : null;
            using SKData? uncachedData = encodedLease != null ? null :
                UseWebpEncoding ? image.Encode(format, quality) : navigation
                    ? MapFrameEncoding.EncodeNavigationPng(image, pngCompression, cancellationToken)
                    : MapFrameEncoding.EncodePng(image, pngCompression);
            SKData? data = encodedLease?.Resource ?? uncachedData;
            long encodeCompleted = Stopwatch.GetTimestamp();
            trace?.AddSpan("encode", null, encodeStarted, encodeCompleted);
            double encodeMilliseconds = (encodeCompleted - encodeStarted) * 1000.0 / Stopwatch.Frequency;
            if (data == null) throw new InvalidOperationException("Nao foi possivel codificar o frame do mapa.");

            // Encoding is not cooperatively cancellable, so stale frames are
            // rejected immediately after it and before any response bytes.
            cancellationToken.ThrowIfCancellationRequested();
            response.AppendHeader("Timing-Allow-Origin", "*");
            RenderFrameTraceSnapshot? serverTrace = trace?.Snapshot();
            string serverTiming = serverTrace == null
                ? FormattableString.Invariant($"encode;dur={encodeMilliseconds:F3}")
                : string.Join(", ",
                    FormattableString.Invariant($"queue;dur={TraceMilliseconds(serverTrace, "queue"):F3}"),
                    FormattableString.Invariant($"gdal_wait;dur={TraceMilliseconds(serverTrace, "gdal_wait"):F3}"),
                    FormattableString.Invariant($"io;dur={TraceMilliseconds(serverTrace, "io"):F3}"),
                    FormattableString.Invariant($"geometry;dur={TraceMilliseconds(serverTrace, "geometry"):F3}"),
                    FormattableString.Invariant($"draw;dur={TraceMilliseconds(serverTrace, "draw"):F3}"),
                    FormattableString.Invariant($"render;dur={TraceMilliseconds(serverTrace, "render"):F3}"),
                    FormattableString.Invariant($"encode;dur={encodeMilliseconds:F3}"));
            response.AppendHeader("Server-Timing", serverTiming);
            response.AppendHeader("X-GeoNex-Frame-Id", generation.ToString(CultureInfo.InvariantCulture));
            response.AppendHeader(
                "X-GeoNex-Trace-Id",
                (trace?.CorrelationId ?? -generation).ToString(CultureInfo.InvariantCulture));
            response.AppendHeader("X-GeoNex-Image-Format", encoding);
            response.AppendHeader("Access-Control-Expose-Headers", "X-GeoNex-Payload-Id, X-GeoNex-Reused");
            if (payloadId != null) response.AppendHeader("X-GeoNex-Payload-Id", payloadId);
            if (payloadId != null && reusePayloadId == payloadId)
            {
                response.AppendHeader("X-GeoNex-Reused", "1");
                response.StatusCode = 204;
                response.ContentLength64 = 0;
                response.Close();
                _telemetry.CompleteResponse(trace, "ok", 0);
                return;
            }
            response.ContentType = UseWebpEncoding ? "image/webp" : "image/png";
            response.ContentLength64 = data.Size;

            long writeStarted = Stopwatch.GetTimestamp();
            data.SaveTo(response.OutputStream);
            response.OutputStream.Close();
            trace?.AddSpan("write", null, writeStarted, Stopwatch.GetTimestamp(), bytes: data.Size);
            _telemetry.CompleteResponse(trace, "ok", data.Size);
        }

        private static double TraceMilliseconds(RenderFrameTraceSnapshot trace, string stage) =>
            RenderTelemetryStatistics.StageMilliseconds(trace, stage);

        public void RecordPresentation(
            long frameId,
            double networkMilliseconds,
            double decodeMilliseconds,
            double drawMilliseconds,
            double endToEndMilliseconds,
            long transferBytes) =>
            _telemetry.RecordPresentation(
                frameId,
                networkMilliseconds,
                decodeMilliseconds,
                drawMilliseconds,
                endToEndMilliseconds,
                transferBytes);

        public RenderTelemetrySummary GetTelemetrySummary() => _telemetry.Summarize();

        private void DrawPolygon(string layer, object source, long revision, SKRect viewport,
            SKPoint origin, SKCanvas canvas, SKPath path, SKPaint? fill, SKPaint? stroke,
            int width, int height, long budget, bool eligible, CancellationToken token)
        {
            // Tiny paths are cheaper to paint directly. Never magnify a previous image:
            // matrix, physical dimensions, CRS, origin, data and paints are exact keys.
            if (eligible && path.PointCount >= 2048 &&
                _polygonImages.TryDraw(layer, new PolygonImageContext(source, revision, viewport, origin,
                    _mapService.ProjetoSRS, _mapService.OffsetMundoX, _mapService.OffsetMundoY),
                    canvas, path, fill, stroke, width, height, budget, token)) return;
            token.ThrowIfCancellationRequested();
            if (fill != null) canvas.DrawPath(path, fill);
            token.ThrowIfCancellationRequested();
            if (stroke != null) canvas.DrawPath(path, stroke);
        }

        public void Stop()
        {
            _isRunning = false;
            _onlineWorker.Dispose();
            _progressiveOnline.Dispose();
            _onlineSession.Dispose();
            _polygonImages.Dispose();
            _encodedFrames.Dispose();
            _previewPixels.Dispose();
            _framePixels.Dispose();
            OnOnlineFrameReady = null;
            Interlocked.Exchange(ref _activeRender, null)?.Cancel();
            _listener?.Stop();
        }
    }
}
