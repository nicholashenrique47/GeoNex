using System.Diagnostics;
using OSGeo.GDAL;

namespace GeoNex.Services;

/// <summary>Identifies the loaded instance, not just its potentially reused layer name.</summary>
public sealed record RasterOverviewRefreshRequest(
    string LayerName, Dataset ExpectedSource, string SourcePath, long SourceLength, DateTime LastWriteUtc);

public partial class MapRenderingService
{
    public RasterOverviewRefreshRequest? CaptureRasterOverviewRefresh(string layerName, string sourcePath)
    {
        string path = Path.GetFullPath(sourcePath);
        var info = new FileInfo(path);
        lock (_rasterResourceGate)
        {
            if (_disposeState != 0 || !info.Exists || _onlineRasterNames.Contains(layerName) ||
                !_rasters.TryGetValue(layerName, out Dataset? source)) return null;
            return new(layerName, source, path, info.Length, info.LastWriteTimeUtc);
        }
    }

    /// <summary>
    /// Call on the UI mutation context while holding LocalMapServer.PauseForRasterMaintenanceAsync().
    /// The frame pause prevents a renderer acquiring a source and VRT from different generations.
    /// Only metadata and a virtual warp are prepared here, never a full-resolution pixel image.
    /// </summary>
    public bool TryRefreshRasterOverviews(RasterOverviewRefreshRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        long started = Stopwatch.GetTimestamp();
        Dataset? replacement = null;
        Dataset? warped = null;
        bool published = false;
        try
        {
            lock (GdalRasterLock)
            lock (_rasterResourceGate)
            {
                if (_disposeState != 0 || !_rasters.TryGetValue(request.LayerName, out Dataset? current) ||
                    !ReferenceEquals(current, request.ExpectedSource)) return false;
                var info = new FileInfo(request.SourcePath);
                if (!info.Exists || info.Length != request.SourceLength || info.LastWriteTimeUtc != request.LastWriteUtc)
                    return false;
                // Keep source immutable between validation and publication on Windows.
                using var sourceGuard = new FileStream(request.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                info.Refresh();
                if (info.Length != request.SourceLength || info.LastWriteTimeUtc != request.LastWriteUtc) return false;
                if (!string.Equals(Path.GetFullPath(current.GetDescription()), request.SourcePath, StringComparison.OrdinalIgnoreCase))
                    return false;
                using var oldLease = _rasterResources.Acquire(request.LayerName);
                replacement = Gdal.Open(request.SourcePath, Access.GA_ReadOnly)
                    ?? throw new InvalidOperationException("Could not reopen raster after overview publication.");
                using var driver = replacement.GetDriver();
                if (driver.ShortName != "GTiff" || replacement.RasterCount == 0) return false;
                ValidateRefreshGrid(current, replacement);
                for (int b = 1; b <= replacement.RasterCount; b++)
                {
                    using Band band = replacement.GetRasterBand(b);
                    if (band.GetOverviewCount() == 0) return false;
                }

                // Use the current project CRS, not the CRS captured before a potentially long build.
                string sourceSrs = replacement.GetProjection();
                string targetSrs = ProjetoSRS;
                if (!string.IsNullOrWhiteSpace(sourceSrs) && !string.IsNullOrWhiteSpace(targetSrs) &&
                    !SrsFactory.IsSame(sourceSrs, targetSrs))
                {
                    using var options = new GDALWarpAppOptions([
                        "-of", "VRT", "-t_srs", targetSrs,
                        "-r", RasterDatasetPolicy.SelectRenderResampling(replacement, false)]);
                    // Unnamed VRT avoids orphan /vsimem files and holds native references to its source.
                    warped = Gdal.Warp("", new[] { replacement }, options, null, null)
                        ?? throw new InvalidOperationException("Could not refresh the warped raster view.");
                }

                // All fallible preparation is complete. Existing leases retain the old resources.
                Dataset newSource = replacement;
                PublishRaster(request.LayerName, newSource);
                replacement = null; // registry now owns it
                if (warped != null)
                {
                    PublishWarpedRaster(request.LayerName, newSource, warped);
                    warped = null;
                }
                InvalidateGlobalCache();
                published = true;
            }
        }
        catch (Exception error)
        {
            DebugLogger.Log($"Raster overview refresh failed type={error.GetType().Name} error={error.Message}");
        }
        finally
        {
            warped?.Dispose();
            replacement?.Dispose();
            DebugLogger.Log(FormattableString.Invariant(
                $"Raster overview refresh published={published} elapsed_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}"));
        }
        if (published) OnMapInvalidated?.Invoke();
        return published;
    }

    private static void ValidateRefreshGrid(Dataset previous, Dataset next)
    {
        if (previous.RasterXSize != next.RasterXSize || previous.RasterYSize != next.RasterYSize ||
            previous.RasterCount != next.RasterCount || previous.GetProjection() != next.GetProjection())
            throw new InvalidOperationException("Raster grid/CRS changed while preparing overviews.");
        var before = new double[6];
        var after = new double[6];
        previous.GetGeoTransform(before);
        next.GetGeoTransform(after);
        if (!before.SequenceEqual(after)) throw new InvalidOperationException("Raster transform changed while preparing overviews.");
        for (int b = 1; b <= next.RasterCount; b++)
        {
            using Band oldBand = previous.GetRasterBand(b);
            using Band newBand = next.GetRasterBand(b);
            if (oldBand.DataType != newBand.DataType) throw new InvalidOperationException("Raster band type changed.");
        }
    }
}
