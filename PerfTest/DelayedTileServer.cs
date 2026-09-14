using System.Net;
using SkiaSharp;

/// <summary>Deterministic local tile provider; no public service or timing assumption.</summary>
internal sealed class DelayedTileServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly byte[] _tile;
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int StatusCode = 200;
    public System.Collections.Concurrent.ConcurrentQueue<int> RequestedZooms { get; } = new();
    public string Url { get; }
    public DelayedTileServer()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        Url = $"http://localhost:{port}/tiles/";
        _listener.Prefixes.Add(Url); _listener.Start();
        using var bitmap = new SKBitmap(256, 256);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(73, 116, 41));
            using var paint = new SKPaint { Color = new SKColor(125, 154, 76) };
            for (int x = 0; x < 256; x += 16) canvas.DrawRect(x, 0, 8, 256, paint);
            paint.Color = new SKColor(41, 90, 21);
            for (int y = 0; y < 256; y += 14) canvas.DrawRect(0, y, 256, 3, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        _tile = encoded.ToArray();
        _ = Listen();
    }
    public void Release() => _release.TrySetResult();
    private async Task Listen()
    {
        try
        {
            while (_listener.IsListening)
            {
                var context = await _listener.GetContextAsync();
                _ = Respond(context);
            }
        }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
    }
    private async Task Respond(HttpListenerContext context)
    {
        string[] parts = context.Request.Url!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[1], out int zoom)) RequestedZooms.Enqueue(zoom);
        Arrived.TrySetResult();
        await _release.Task;
        try
        {
            context.Response.StatusCode = Volatile.Read(ref StatusCode);
            if (context.Response.StatusCode == 200)
            {
                context.Response.ContentType = "image/png";
                context.Response.ContentLength64 = _tile.Length;
                await context.Response.OutputStream.WriteAsync(_tile);
            }
            context.Response.Close();
        }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
    }
    public void Dispose() { Release(); _listener.Close(); }
}
