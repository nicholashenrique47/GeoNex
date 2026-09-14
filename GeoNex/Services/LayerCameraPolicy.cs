using SkiaSharp;

namespace GeoNex.Services;

/// <summary>Fits a layer against the current scene basis, never a previously rendered frame.</summary>
public static class LayerCameraPolicy
{
    public static bool TryFit(SKRect scene, SKRect layer, int width, int height, out MapCameraState camera)
    {
        camera = default;
        if (width <= 0 || height <= 0 || !Valid(scene) || !Valid(layer)) return false;
        // Match LocalMapServer's CSS auto-fit calculation exactly (overscan is not visible space).
        float sceneScale = Math.Min(width / scene.Width, height / scene.Height) * 0.8f;
        float layerScale = Math.Min(width / layer.Width, height / layer.Height) * 0.8f;
        float zoom = layerScale / sceneScale;
        float panX = (scene.MidX - layer.MidX) * layerScale;
        float panY = (scene.MidY - layer.MidY) * layerScale;
        if (!float.IsFinite(zoom) || zoom <= 0 || !float.IsFinite(panX) || !float.IsFinite(panY)) return false;
        camera = new MapCameraState(panX, panY, zoom);
        return true;
    }

    private static bool Valid(SKRect rect) => !rect.IsEmpty &&
        float.IsFinite(rect.Left) && float.IsFinite(rect.Top) &&
        float.IsFinite(rect.Right) && float.IsFinite(rect.Bottom) &&
        float.IsFinite(rect.Width) && float.IsFinite(rect.Height);
}
