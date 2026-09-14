namespace GeoNex.Services;

/// <summary>Synchronous, thread-affine GDAL lock with cancellation and a separate wait span.</summary>
public sealed class RasterReadLock : IDisposable
{
    private const int CancellationPollMilliseconds = 25;
    private object? _gate;

    private RasterReadLock(object gate) => _gate = gate;

    public static RasterReadLock Enter(object gate, CancellationToken token,
        RenderFrameTrace? trace = null, string? layer = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        using var wait = trace?.Measure("gdal_wait", layer);
        bool entered = false;
        try
        {
            while (!entered)
            {
                token.ThrowIfCancellationRequested();
                Monitor.TryEnter(gate, CancellationPollMilliseconds, ref entered);
            }
            token.ThrowIfCancellationRequested();
            return new RasterReadLock(gate);
        }
        catch
        {
            if (entered) Monitor.Exit(gate);
            throw;
        }
    }

    public void Dispose()
    {
        // Callers must not await or transfer this scope to another thread.
        object? gate = _gate;
        if (gate == null) return;
        Monitor.Exit(gate);
        _gate = null;
    }
}
