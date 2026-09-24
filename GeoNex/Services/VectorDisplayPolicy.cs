using NetTopologySuite.Geometries;

namespace GeoNex.Services;

/// <summary>Chooses a bounded visual representation without changing source data.</summary>
public static class VectorDisplayPolicy
{
    // A dense overview is dominated by sub-pixel polygons. Keep exact geometry
    // for local navigation, selection and print/export paths.
    public const int OverviewLodFeatureThreshold = 250_000;

    public static bool ShouldUseOverviewLod(int featureCount, Envelope view, Envelope layer)
    {
        if (featureCount < OverviewLodFeatureThreshold || view.IsNull || layer.IsNull)
            return false;

        double layerWidth = layer.Width;
        double layerHeight = layer.Height;
        if (!double.IsFinite(layerWidth) || !double.IsFinite(layerHeight) ||
            layerWidth <= 0 || layerHeight <= 0)
            return false;

        double overlapWidth = Math.Max(0, Math.Min(view.MaxX, layer.MaxX) - Math.Max(view.MinX, layer.MinX));
        double overlapHeight = Math.Max(0, Math.Min(view.MaxY, layer.MaxY) - Math.Max(view.MinY, layer.MinY));
        double visibleFraction = overlapWidth * overlapHeight / (layerWidth * layerHeight);
        if (!double.IsFinite(visibleFraction) || visibleFraction <= 0) return false;

        double estimatedVisible = featureCount * Math.Min(1, visibleFraction);
        return estimatedVisible >= OverviewLodFeatureThreshold;
    }
}
