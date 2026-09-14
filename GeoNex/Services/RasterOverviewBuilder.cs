using System.Diagnostics;
using OSGeo.GDAL;

namespace GeoNex.Services;

public sealed record RasterOverviewMetrics(
    string Stage, double QueueMilliseconds, double BuildMilliseconds,
    double PublicationWaitMilliseconds, long OutputBytes);

/// <summary>Builds isolated GeoTIFF overviews and publishes complete sidecars without replacing existing data.</summary>
public static class RasterOverviewBuilder
{
    private const int MaximumPendingJobs = 32;
    private const int LockPollMilliseconds = 25;
    private const int PerDatasetMask = 2;
    private static readonly object JobsGate = new();
    private static readonly Dictionary<string, CancellationTokenSource> Jobs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim BuildSlot = new(1, 1);
    private static RasterOverviewMetrics _metrics = new("idle", 0, 0, 0, 0);

    // Last active/completed build only: bounded retention without paths or dataset references.
    public static RasterOverviewMetrics Metrics => Volatile.Read(ref _metrics);

    public static bool Schedule(string sourcePath, object gdalLock, Action? requestRedraw = null,
        Func<Task>? onPublishedAsync = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(gdalLock);
        if (!IsEnabled()) return false;
        string fullPath = Path.GetFullPath(sourcePath);
        lock (JobsGate)
        {
            if (Jobs.ContainsKey(fullPath) || Jobs.Count >= MaximumPendingJobs) return false;
            var cancellation = new CancellationTokenSource();
            Jobs.Add(fullPath, cancellation);
            _ = Task.Run(() => BuildAsync(fullPath, gdalLock, requestRedraw, onPublishedAsync, cancellation));
            return true;
        }
    }

    public static void Cancel(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return;
        string fullPath = Path.GetFullPath(sourcePath);
        // Cancel and Dispose share a gate, preventing disposed-CTS races on layer removal.
        lock (JobsGate)
            if (Jobs.TryGetValue(fullPath, out CancellationTokenSource? cancellation)) cancellation.Cancel();
    }

    public static void CancelAll()
    {
        lock (JobsGate)
            foreach (CancellationTokenSource cancellation in Jobs.Values) cancellation.Cancel();
    }

    public static bool IsActive(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return false;
        string fullPath = Path.GetFullPath(sourcePath);
        lock (JobsGate) return Jobs.ContainsKey(fullPath);
    }

    private static async Task BuildAsync(string sourcePath, object gdalLock, Action? requestRedraw, Func<Task>? onPublishedAsync,
        CancellationTokenSource cancellation)
    {
        CancellationToken token = cancellation.Token;
        long queued = Stopwatch.GetTimestamp();
        bool ownsSlot = false;
        try
        {
            int delayMs = ReadBoundedEnvironment("GEONEX_OVERVIEW_DELAY_MS", 1000, 0, 60_000);
            if (delayMs > 0) await Task.Delay(delayMs, token).ConfigureAwait(false);
            await BuildSlot.WaitAsync(token).ConfigureAwait(false);
            ownsSlot = true;
            double queueMs = Stopwatch.GetElapsedTime(queued).TotalMilliseconds;
            Volatile.Write(ref _metrics, new("building", queueMs, 0, 0, 0));
            if (BuildAndPublish(sourcePath, gdalLock, token, queueMs))
            {
                // Publication succeeded even if a consumer was removed or its refresh callback fails.
                try
                {
                    if (onPublishedAsync != null) await onPublishedAsync().ConfigureAwait(false);
                    requestRedraw?.Invoke();
                }
                catch (Exception error) { DebugLogger.Log($"Raster overview consumer refresh failed: {error.Message}"); }
            }
        }
        catch (OperationCanceledException)
        {
            if (ownsSlot) Volatile.Write(ref _metrics, Metrics with { Stage = "canceled" });
        }
        catch (Exception exception)
        {
            if (ownsSlot) Volatile.Write(ref _metrics, Metrics with { Stage = "failed" });
            DebugLogger.Log($"Raster overview failed error={exception.Message}");
        }
        finally
        {
            if (ownsSlot)
            {
                RasterOverviewMetrics result = Metrics;
                DebugLogger.Log(FormattableString.Invariant(
                    $"Raster overview stage={result.Stage} queue_ms={result.QueueMilliseconds:F2} build_ms={result.BuildMilliseconds:F2} publish_wait_ms={result.PublicationWaitMilliseconds:F2} bytes={result.OutputBytes}"));
                BuildSlot.Release();
            }
            lock (JobsGate)
            {
                Jobs.Remove(sourcePath);
                cancellation.Dispose();
            }
        }
    }

    private static bool BuildAndPublish(string sourcePath, object gdalLock, CancellationToken token, double queueMs)
    {
        string outputPath = sourcePath + ".ovr";
        if (File.Exists(outputPath)) return Skip("existing-overviews");
        GdalRuntimeConfiguration.Apply();
        // Windows: deny source writes/replacement during preparation. Source pixels are never copied or changed.
        using var sourceGuard = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using Dataset source = Gdal.Open(sourcePath, Access.GA_ReadOnly)
            ?? throw new InvalidOperationException("Could not open raster read-only.");
        using Driver driver = source.GetDriver();
        if (driver.ShortName != "GTiff" || source.RasterCount == 0) return Skip("unsupported-driver");
        // Auxiliary files/masks need a multi-file publication contract before automatic preparation is safe.
        if (File.Exists(sourcePath + ".msk") || File.Exists(sourcePath + ".aux.xml")) return Skip("auxiliary-source");
        for (int i = 1; i <= source.RasterCount; i++)
        {
            using Band band = source.GetRasterBand(i);
            if (band.GetOverviewCount() > 0) return Skip("existing-overviews");
            if ((band.GetMaskFlags() & PerDatasetMask) != 0) return Skip("dataset-mask");
        }
        int[] factors = RasterRenderingPolicy.BuildOverviewFactors(source.RasterXSize, source.RasterYSize);
        if (factors.Length == 0) return Skip("small-raster");

        string tempDirectory = Path.Combine(Path.GetDirectoryName(sourcePath)!, ".geonex-overview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string vrtPath = Path.Combine(tempDirectory, "source.vrt");
        string stagedPath = vrtPath + ".ovr";
        long started = Stopwatch.GetTimestamp();
        try
        {
            token.ThrowIfCancellationRequested();
            using (var options = new GDALTranslateOptions(["-of", "VRT"]))
            using (Dataset vrt = Gdal.wrapper_GDALTranslate(vrtPath, source, options, null, null)
                ?? throw new InvalidOperationException("Could not create overview staging VRT."))
            {
                Gdal.GDALProgressFuncDelegate progress = (_, _, _) => token.IsCancellationRequested ? 0 : 1;
                int status = vrt.BuildOverviews(RasterDatasetPolicy.SelectOverviewResampling(source), factors, progress, null);
                token.ThrowIfCancellationRequested();
                if (status != 0) throw new InvalidOperationException($"BuildOverviews returned {status}.");
            }
            // Writers are closed and every level validated before the final filename can become visible.
            ValidateStaged(stagedPath, source, factors);
            if (File.Exists(stagedPath + ".msk") || File.Exists(stagedPath + ".aux.xml"))
                throw new InvalidOperationException("Overview requires unsupported companion files.");
            long bytes = new FileInfo(stagedPath).Length;
            double buildMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Volatile.Write(ref _metrics, new("awaiting-publication", queueMs, buildMs, 0, bytes));
            long waiting = Stopwatch.GetTimestamp();
            bool entered = false;
            try
            {
                while (!entered)
                {
                    token.ThrowIfCancellationRequested();
                    Monitor.TryEnter(gdalLock, LockPollMilliseconds, ref entered);
                }
                token.ThrowIfCancellationRequested();
                if (File.Exists(outputPath)) return Skip("publication-conflict");
                // Same-volume rename, no overwrite, including races with another publisher.
                File.Move(stagedPath, outputPath, overwrite: false);
            }
            finally
            {
                double waitMs = Stopwatch.GetElapsedTime(waiting).TotalMilliseconds;
                if (entered) Monitor.Exit(gdalLock);
                Volatile.Write(ref _metrics, Metrics with { PublicationWaitMilliseconds = waitMs });
            }
            Volatile.Write(ref _metrics, Metrics with { Stage = "published" });
            return true;
        }
        finally
        {
            // Exact paths in this job's private directory. Never recurse into a source directory.
            foreach (string path in new[] { vrtPath, stagedPath, vrtPath + ".aux.xml", stagedPath + ".aux.xml", stagedPath + ".msk" })
            {
                try { File.Delete(path); }
                catch (IOException ex) { DebugLogger.Log($"Overview temporary cleanup failed: {ex.Message}"); }
                catch (UnauthorizedAccessException ex) { DebugLogger.Log($"Overview temporary cleanup denied: {ex.Message}"); }
            }
            try { Directory.Delete(tempDirectory, recursive: false); }
            catch (IOException ex) { DebugLogger.Log($"Overview temporary directory retained: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { DebugLogger.Log($"Overview temporary directory retained: {ex.Message}"); }
        }
    }

    private static void ValidateStaged(string path, Dataset source, int[] factors)
    {
        using Dataset staged = Gdal.Open(path, Access.GA_ReadOnly)
            ?? throw new InvalidOperationException("No readable overview was produced.");
        if (staged.RasterCount != source.RasterCount) throw new InvalidOperationException("Overview band count mismatch.");
        for (int b = 1; b <= source.RasterCount; b++)
        {
            using Band original = source.GetRasterBand(b);
            using Band first = staged.GetRasterBand(b);
            if (first.GetOverviewCount() != factors.Length - 1) throw new InvalidOperationException("Overview level count mismatch.");
            for (int i = 0; i < factors.Length; i++)
            {
                Band level = i == 0 ? first : first.GetOverview(i - 1);
                try
                {
                    int width = (int)(((long)source.RasterXSize + factors[i] - 1) / factors[i]);
                    int height = (int)(((long)source.RasterYSize + factors[i] - 1) / factors[i]);
                    if (level.XSize != width || level.YSize != height || level.DataType != original.DataType)
                        throw new InvalidOperationException("Overview dimensions/type mismatch.");
                }
                finally { if (i != 0) level.Dispose(); }
            }
        }
    }

    private static bool Skip(string reason)
    {
        Volatile.Write(ref _metrics, Metrics with { Stage = "skipped-" + reason });
        return false;
    }

    private static bool IsEnabled()
    {
        string? value = Environment.GetEnvironmentVariable("GEONEX_BUILD_OVERVIEWS");
        return !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadBoundedEnvironment(string name, int fallback, int minimum, int maximum) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
