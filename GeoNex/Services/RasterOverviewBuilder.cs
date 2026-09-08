using System.Collections.Concurrent;
using OSGeo.GDAL;

namespace GeoNex.Services;

public static class RasterOverviewBuilder
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Jobs =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool Schedule(string sourcePath, object gdalLock, Action? requestRedraw = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(gdalLock);
        if (!IsEnabled()) return false;

        string fullPath = Path.GetFullPath(sourcePath);
        var cancellation = new CancellationTokenSource();
        if (!Jobs.TryAdd(fullPath, cancellation))
        {
            cancellation.Dispose();
            return false;
        }

        _ = Task.Run(() => BuildAsync(fullPath, gdalLock, requestRedraw, cancellation));
        return true;
    }

    public static void Cancel(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return;
        string fullPath = Path.GetFullPath(sourcePath);
        if (Jobs.TryGetValue(fullPath, out CancellationTokenSource? cancellation))
            cancellation.Cancel();
    }

    public static void CancelAll()
    {
        foreach (CancellationTokenSource cancellation in Jobs.Values)
            cancellation.Cancel();
    }

    public static bool IsActive(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return false;
        return Jobs.ContainsKey(Path.GetFullPath(sourcePath));
    }

    private static async Task BuildAsync(
        string sourcePath,
        object gdalLock,
        Action? requestRedraw,
        CancellationTokenSource cancellation)
    {
        CancellationToken token = cancellation.Token;
        try
        {
            int delayMs = ReadBoundedEnvironment("GEONEX_OVERVIEW_DELAY_MS", 1000, 0, 60_000);
            if (delayMs > 0) await Task.Delay(delayMs, token).ConfigureAwait(false);

            GdalRuntimeConfiguration.Apply();
            using Dataset dataset = Gdal.Open(sourcePath, Access.GA_ReadOnly)
                ?? throw new InvalidOperationException("GDAL could not reopen the raster read-only.");
            if (!RasterDatasetPolicy.SupportsExternalOverviews(dataset) || dataset.RasterCount <= 0) return;

            using Band firstBand = dataset.GetRasterBand(1);
            if (firstBand.GetOverviewCount() > 0) return;

            int[] factors = RasterRenderingPolicy.BuildOverviewFactors(
                dataset.RasterXSize,
                dataset.RasterYSize);
            if (factors.Length == 0) return;

            string resampling = RasterDatasetPolicy.SelectOverviewResampling(dataset);
            Gdal.GDALProgressFuncDelegate progress = (_, _, _) => token.IsCancellationRequested ? 0 : 1;
            int status;
            lock (gdalLock)
            {
                token.ThrowIfCancellationRequested();
                // A read-only handle forces an external sidecar when the driver supports it;
                // source pixels and source metadata are never updated in place.
                status = dataset.BuildOverviews(resampling, factors, progress, sourcePath);
            }

            token.ThrowIfCancellationRequested();
            if (status != 0)
                throw new InvalidOperationException($"GDAL BuildOverviews failed with status {status}: {Gdal.GetLastErrorMsg()}");

            DebugLogger.Log(
                $"Raster overviews ready path={sourcePath} resampling={resampling} levels={string.Join(',', factors)}");
            requestRedraw?.Invoke();
        }
        catch (OperationCanceledException)
        {
            DebugLogger.Log($"Raster overview build cancelled path={sourcePath}");
        }
        catch (Exception exception)
        {
            DebugLogger.Log($"Raster overview build skipped path={sourcePath} error={exception.Message}");
        }
        finally
        {
            if (Jobs.TryGetValue(sourcePath, out CancellationTokenSource? current) &&
                ReferenceEquals(current, cancellation))
                Jobs.TryRemove(sourcePath, out _);
            cancellation.Dispose();
        }
    }

    private static bool IsEnabled()
    {
        string? value = Environment.GetEnvironmentVariable("GEONEX_BUILD_OVERVIEWS");
        return !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadBoundedEnvironment(string name, int fallback, int minimum, int maximum)
    {
        string? text = Environment.GetEnvironmentVariable(name);
        return int.TryParse(text, out int value) ? Math.Clamp(value, minimum, maximum) : fallback;
    }
}
