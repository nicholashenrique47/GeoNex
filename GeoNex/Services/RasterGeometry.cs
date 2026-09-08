namespace GeoNex.Services;

public readonly record struct RasterPoint(double X, double Y);

public readonly record struct RasterBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
}

public static class RasterGeometry
{
    public static RasterPoint PixelToWorld(
        IReadOnlyList<double> geoTransform,
        double pixel,
        double line)
    {
        ValidateGeoTransform(geoTransform);
        return new RasterPoint(
            geoTransform[0] + pixel * geoTransform[1] + line * geoTransform[2],
            geoTransform[3] + pixel * geoTransform[4] + line * geoTransform[5]);
    }

    public static RasterPoint[] GetBoundaryPoints(
        IReadOnlyList<double> geoTransform,
        int rasterWidth,
        int rasterHeight,
        int segmentsPerEdge = 32)
    {
        ValidateGeoTransform(geoTransform);
        if (rasterWidth <= 0) throw new ArgumentOutOfRangeException(nameof(rasterWidth));
        if (rasterHeight <= 0) throw new ArgumentOutOfRangeException(nameof(rasterHeight));
        if (segmentsPerEdge is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(segmentsPerEdge));

        var points = new RasterPoint[checked(segmentsPerEdge * 4)];
        int cursor = 0;
        for (int segment = 0; segment < segmentsPerEdge; ++segment)
        {
            double t = segment / (double)segmentsPerEdge;
            points[cursor++] = PixelToWorld(geoTransform, rasterWidth * t, 0);
            points[cursor++] = PixelToWorld(geoTransform, rasterWidth, rasterHeight * t);
            points[cursor++] = PixelToWorld(geoTransform, rasterWidth * (1.0 - t), rasterHeight);
            points[cursor++] = PixelToWorld(geoTransform, 0, rasterHeight * (1.0 - t));
        }
        return points;
    }

    public static RasterBounds GetBounds(IEnumerable<RasterPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        bool found = false;
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;

        foreach (RasterPoint point in points)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                throw new ArgumentException("Raster boundary contains a non-finite coordinate.", nameof(points));
            found = true;
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        if (!found) throw new ArgumentException("Raster boundary cannot be empty.", nameof(points));
        return new RasterBounds(minX, minY, maxX, maxY);
    }

    public static bool IsAxisAligned(IReadOnlyList<double> geoTransform, double tolerance = 1.0e-12)
    {
        ValidateGeoTransform(geoTransform);
        if (!double.IsFinite(tolerance) || tolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        return Math.Abs(geoTransform[2]) <= tolerance && Math.Abs(geoTransform[4]) <= tolerance;
    }

    private static void ValidateGeoTransform(IReadOnlyList<double> geoTransform)
    {
        ArgumentNullException.ThrowIfNull(geoTransform);
        if (geoTransform.Count < 6)
            throw new ArgumentException("A GDAL geotransform must contain six coefficients.", nameof(geoTransform));
        for (int i = 0; i < 6; ++i)
            if (!double.IsFinite(geoTransform[i]))
                throw new ArgumentException("A GDAL geotransform must contain only finite coefficients.", nameof(geoTransform));
    }
}
