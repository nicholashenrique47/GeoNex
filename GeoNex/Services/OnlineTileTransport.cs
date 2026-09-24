using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;

namespace GeoNex.Services;

/// <summary>Loopback adapter for GDAL: pooled HTTP, one in-flight request per tile and the existing GDAL disk cache.</summary>
public sealed class OnlineTileTransport : IDisposable
{
    private const int MaximumTileBytes = 2 * 1024 * 1024;
    private const int MaximumSources = 16;
    private sealed record Tile(int Status, byte[] Bytes, string ContentType);
    private sealed class Source : IDisposable
    {
        public required string Template, CachePath;
        public required int Depth, Expiry;
        public required long MaxBytes;
        public required HttpClient Client;
        public required SemaphoreSlim Slots;
        public readonly Dictionary<string, Task<Tile>> Flights = new(StringComparer.Ordinal);
        public long WrittenBytes;
        public bool Trimming;
        public void Dispose() => Client.Dispose();
    }
    private readonly object _gate = new();
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _xml = new(StringComparer.Ordinal);
    private readonly string _prefix;
    private bool _disposed;
    private long _downloads, _merged, _hits, _failures;
    public long Downloads => Interlocked.Read(ref _downloads);
    public long MergedRequests => Interlocked.Read(ref _merged);
    public long CacheHits => Interlocked.Read(ref _hits);
    public long Failures => Interlocked.Read(ref _failures);

    public OnlineTileTransport()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        _prefix = $"http://localhost:{port}/{Guid.NewGuid():N}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _ = Listen();
    }

    /// <summary>Only registered TMS templates can be requested; no arbitrary URL forwarding.</summary>
    public string Register(string xml)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_xml.TryGetValue(xml, out string? cached)) return cached;
            var doc = XDocument.Parse(xml);
            var root = doc.Root!;
            var service = root.Element("Service");
            var cache = root.Element("Cache");
            string? template = service?.Element("ServerUrl")?.Value;
            if (service?.Attribute("name")?.Value != "TMS" || cache == null || string.IsNullOrEmpty(template) ||
                !template.Contains("${x}") || !template.Contains("${y}") || !template.Contains("${z}")) return xml;
            var probe = new Uri(template.Replace("${x}", "0").Replace("${y}", "0").Replace("${z}", "0"));
            if (probe.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(probe.UserInfo)) return xml;
            // Custom authenticated/header-bearing sources keep their original GDAL transport.
            if (root.Element("UserPwd") != null || root.Element("Referer") != null || root.Element("UnsafeSSL") != null ||
                cache.Element("Extension") != null || (cache.Element("Type")?.Value ?? "file") != "file" ||
                (root.Element("BlockSizeX")?.Value ?? "256") != "256" ||
                (root.Element("BlockSizeY")?.Value ?? "256") != "256" ||
                template.Replace("${x}", "").Replace("${y}", "").Replace("${z}", "").Contains("${")) return xml;
            if (_sources.Count >= MaximumSources) return xml;
            int Number(XElement parent, string name, int fallback) => int.TryParse(parent.Element(name)?.Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
            int connections = Math.Clamp(Number(root, "MaxConnections", 2), 1, 4);
            string path = Path.GetFullPath(cache.Element("Path")?.Value ?? GdalRuntimeConfiguration.BasemapCache.Path);
            if (!string.Equals(cache.Element("Unique")?.Value, "false", StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(path, Hash(template));
            Directory.CreateDirectory(path);
            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = connections,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(OnlineBasemapPolicy.ConnectTimeoutSeconds),
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(OnlineBasemapPolicy.RequestTimeoutSeconds),
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(root.Element("UserAgent")?.Value ?? OnlineBasemapPolicy.UserAgent);
            string id = _sources.Count.ToString(CultureInfo.InvariantCulture);
            var source = new Source
            {
                Template = template, CachePath = path, Depth = Math.Clamp(Number(cache, "Depth", 2), 0, 4),
                Expiry = Math.Max(0, Number(cache, "Expires", 604800)),
                MaxBytes = long.TryParse(cache.Element("MaxSize")?.Value, out long max) && max > 0 ? max : 512L * 1024 * 1024,
                Client = client, Slots = new SemaphoreSlim(connections, connections)
            };
            _sources.Add(id, source);
            service!.Element("ServerUrl")!.Value = _prefix + id + "/${z}/${x}/${y}";
            cache.Remove(); // Cache keys remain ORIGINAL URLs, independent of this process's loopback port.
            root.SetElementValue("GeoNexTileTransport", "shared");
            string result = doc.ToString(SaveOptions.DisableFormatting);
            _xml[xml] = result;
            ScheduleTrim(source, force: true);
            return result;
        }
    }

    private async Task Listen()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Respond(context);
            }
        }
        catch (HttpListenerException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    private async Task Respond(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            string path = request.Url!.AbsolutePath;
            string basePath = new Uri(_prefix).AbsolutePath;
            string[] parts = path.StartsWith(basePath, StringComparison.Ordinal) ? path[basePath.Length..].Split('/') : Array.Empty<string>();
            if (request.HttpMethod != "GET" || !IPAddress.IsLoopback(request.RemoteEndPoint.Address) || parts.Length != 4 ||
                !int.TryParse(parts[1], out int z) || z < 0 || z > 22 ||
                !int.TryParse(parts[2], out int x) || x < 0 || x >= 1 << z ||
                !int.TryParse(parts[3], out int y) || y < 0 || y >= 1 << z)
            { context.Response.StatusCode = 404; return; }
            Source? source;
            lock (_gate) _sources.TryGetValue(parts[0], out source);
            if (source == null) { context.Response.StatusCode = 404; return; }
            string url = source.Template.Replace("${z}", parts[1]).Replace("${x}", parts[2]).Replace("${y}", parts[3]);
            Tile tile = await Get(source, url).ConfigureAwait(false);
            context.Response.StatusCode = tile.Status;
            context.Response.ContentType = tile.ContentType;
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.ContentLength64 = tile.Bytes.Length;
            if (tile.Bytes.Length > 0) await context.Response.OutputStream.WriteAsync(tile.Bytes, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { context.Response.Abort(); }
        catch (HttpListenerException) { }
        catch (IOException) { context.Response.Abort(); }
        catch (ObjectDisposedException) { }
        finally
        {
            try { context.Response.Close(); }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private Task<Tile> Get(Source source, string url)
    {
        lock (_gate)
        {
            if (_disposed) return Task.FromCanceled<Tile>(new CancellationToken(true));
            if (source.Flights.TryGetValue(url, out var task))
            { Interlocked.Increment(ref _merged); return task; }
            if (source.Flights.Count >= 128) return Task.FromResult(new Tile(503, Array.Empty<byte>(), "text/plain"));
            var ready = new TaskCompletionSource<Tile>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.Flights.Add(url, ready.Task);
            _ = Complete(source, url, ready);
            return ready.Task;
        }
    }

    private async Task Complete(Source source, string url, TaskCompletionSource<Tile> ready)
    {
        try { ready.TrySetResult(await Fetch(source, url).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { ready.TrySetCanceled(); }
        catch (Exception error)
        {
            Interlocked.Increment(ref _failures);
            DebugLogger.Log($"Tile transport: {error.GetType().Name}: {error.Message}");
            ready.TrySetResult(new Tile(502, Array.Empty<byte>(), "text/plain"));
        }
        finally { lock (_gate) source.Flights.Remove(url); }
    }

    private async Task<Tile> Fetch(Source source, string url)
    {
        string hash = Hash(url), path = source.CachePath;
        for (int i = 0; i < source.Depth; i++) path = Path.Combine(path, hash[i].ToString());
        path = Path.Combine(path, hash);
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length <= MaximumTileBytes && DateTime.UtcNow - info.LastWriteTimeUtc < TimeSpan.FromSeconds(source.Expiry))
            {
                byte[] cached = await File.ReadAllBytesAsync(path, _stop.Token).ConfigureAwait(false);
                if (ContentType(cached) is string type)
                { Interlocked.Increment(ref _hits); return new Tile(200, cached, type); }
            }
        }
        catch (IOException) { } // Concurrent cache eviction: download normally.
        catch (UnauthorizedAccessException) { }
        await source.Slots.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            Interlocked.Increment(ref _downloads);
            using var response = await source.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _stop.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            { Interlocked.Increment(ref _failures); return new Tile((int)response.StatusCode, Array.Empty<byte>(), "text/plain"); }
            using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            bodyTimeout.CancelAfter(TimeSpan.FromSeconds(OnlineBasemapPolicy.RequestTimeoutSeconds));
            await using var input = await response.Content.ReadAsStreamAsync(bodyTimeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[16384];
            int n;
            while ((n = await input.ReadAsync(chunk, bodyTimeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + n > MaximumTileBytes) throw new InvalidDataException("Oversized tile.");
                buffer.Write(chunk, 0, n);
            }
            byte[] bytes = buffer.ToArray();
            string type = ContentType(bytes) ?? throw new InvalidDataException("Invalid tile image.");
            var control = response.Headers.CacheControl;
            double age = response.Headers.Age?.TotalSeconds ?? 0;
            double lifetime = control?.MaxAge?.TotalSeconds ??
                (response.Content.Headers.Expires is DateTimeOffset expires
                    ? (expires - (response.Headers.Date ?? DateTimeOffset.UtcNow)).TotalSeconds : source.Expiry);
            double ttl = Math.Min(source.Expiry, lifetime - age);
            if (control?.NoStore != true && control?.NoCache != true && ttl > 0)
            {
                await Save(source, path, bytes, ttl).ConfigureAwait(false);
            }
            return new Tile(200, bytes, type);
        }
        finally { source.Slots.Release(); }
    }

    private async Task Save(Source source, string path, byte[] bytes, double ttl)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(temporary, bytes, _stop.Token).ConfigureAwait(false);
            // GDAL uses mtime + Expires. Preserve a shorter server TTL in that format.
            File.SetLastWriteTimeUtc(temporary, DateTime.UtcNow.AddSeconds(ttl - source.Expiry));
            File.Move(temporary, path, overwrite: true);
            Interlocked.Add(ref source.WrittenBytes, bytes.Length);
            ScheduleTrim(source, force: false);
        }
        catch (IOException error) { DebugLogger.Log("Tile cache write: " + error.Message); }
        catch (UnauthorizedAccessException error) { DebugLogger.Log("Tile cache write: " + error.Message); }
        finally { if (File.Exists(temporary)) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }

    private void ScheduleTrim(Source source, bool force)
    {
        lock (_gate)
        {
            if (_disposed || source.Trimming || (!force && source.WrittenBytes < Math.Min(16L * 1024 * 1024, source.MaxBytes / 4))) return;
            source.Trimming = true; source.WrittenBytes = 0;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var files = new DirectoryInfo(source.CachePath).EnumerateFiles("*", SearchOption.AllDirectories)
                    .Where(f => f.Name.Length == 32 && f.Name.All(Uri.IsHexDigit)).OrderBy(f => f.LastWriteTimeUtc).ToArray();
                long total = files.Sum(f => f.Length);
                foreach (var file in files)
                {
                    if (_stop.IsCancellationRequested || total <= source.MaxBytes) break;
                    long length = file.Length; file.Delete(); total -= length;
                }
            }
            catch (IOException error) { DebugLogger.Log("Tile cache maintenance: " + error.Message); }
            catch (UnauthorizedAccessException error) { DebugLogger.Log("Tile cache maintenance: " + error.Message); }
            finally { lock (_gate) source.Trimming = false; }
        });
    }

    private static string Hash(string value) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string? ContentType(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec == null || codec.Info.Width != 256 || codec.Info.Height != 256) return null;
        string? type = codec.EncodedFormat switch { SKEncodedImageFormat.Jpeg => "image/jpeg", SKEncodedImageFormat.Png => "image/png", _ => null };
        if (type == null) return null;
        using var decoded = new SKBitmap(new SKImageInfo(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul));
        return codec.GetPixels(decoded.Info, decoded.GetPixels()) == SKCodecResult.Success ? type : null;
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _stop.Cancel(); _listener.Close();
        foreach (var source in _sources.Values) source.Dispose();
    }
}
