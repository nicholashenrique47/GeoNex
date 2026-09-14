using System.Security.Cryptography;
using SkiaSharp;

namespace GeoNex.Services;

/// <summary>One lossless payload, keyed by every pixel, not by scene/camera hints.</summary>
public sealed class EncodedFrameCache : IDisposable
{
    private const long MaximumBytes = 32L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly LeasedResourceRegistry<byte, SKData> _data = new();
    private sealed record Key(int Width, int Height, SKColorType Color, SKAlphaType Alpha,
        int Compression, string Digest);
    private Key? _key;
    private string? _payloadId;
    private long _bytes, _hits, _encodes;
    private bool _disposed;
    public long Hits { get { lock (_gate) return _hits; } }
    public long Encodes { get { lock (_gate) return _encodes; } }
    public long RetainedBytes { get { lock (_gate) return _bytes; } }

    public static long Budget(int availableMemoryMb) => availableMemoryMb < 256 ? 0 :
        Math.Min(MaximumBytes, (long)availableMemoryMb * 1024 * 1024 / 32);

    public ResourceLease<SKData> Encode(SKImage image, int compression, long budget, CancellationToken token)
        => Encode(image, compression, budget, token, out _);

    public ResourceLease<SKData> Encode(SKImage image, int compression, long budget, CancellationToken token, out string? payloadId)
    {
        payloadId = null;
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegative(compression);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(compression, 1);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            budget = Math.Clamp(budget, 0, MaximumBytes);
            if (_bytes > budget) Clear();
            Key? key = budget == 0 ? null : Fingerprint(image, compression, token);
            if (key != null && key == _key)
            {
                var lease = _data.Acquire(0)!;
                _hits++;
                payloadId = _payloadId;
                return lease;
            }

            Clear(); // Retire the previous payload before building another.
            SKData? encoded = null;
            try
            {
                encoded = MapFrameEncoding.EncodePng(image, compression);
                _encodes++;
                token.ThrowIfCancellationRequested();
                long size = encoded.Size;
                _data.Publish(0, encoded);
                encoded = null; // Ownership transferred to the registry.
                var lease = _data.Acquire(0)!;
                if (key != null && size <= budget)
                { _key = key; _bytes = size; payloadId = _payloadId = Guid.NewGuid().ToString("N"); }
                else _data.Remove(0); // Lease owns an uncached response until write completes.
                return lease;
            }
            finally { encoded?.Dispose(); }
        }
    }

    private static unsafe Key? Fingerprint(SKImage image, int compression, CancellationToken token)
    {
        using var pixels = image.PeekPixels();
        // Color-managed/other formats keep the original encoder path. No guessed
        // metadata identity, sampling, row padding or truncated pixel hashes.
        if (pixels == null || pixels.GetPixels() == IntPtr.Zero || pixels.ColorSpace != null ||
            pixels.ColorType is not (SKColorType.Rgba8888 or SKColorType.Bgra8888) ||
            (long)image.Width * image.Height > 128L * 1024 * 1024 / 4) return null;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int rowBytes = checked(image.Width * 4);
        for (int y = 0; y < image.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(new ReadOnlySpan<byte>((byte*)pixels.GetPixels() + (long)y * pixels.RowBytes, rowBytes));
        }
        return new Key(image.Width, image.Height, pixels.ColorType, pixels.AlphaType,
            compression, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private void Clear() { _data.Remove(0); _key = null; _payloadId = null; _bytes = 0; }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _key = null; _payloadId = null; _bytes = 0;
            _data.Dispose(); // Existing HTTP writers retain their leases.
        }
    }
}
