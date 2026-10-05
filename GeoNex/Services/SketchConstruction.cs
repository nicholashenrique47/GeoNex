using SkiaSharp;

namespace GeoNex.Services;

public enum ConstructionMode { Vertices, Rectangle, OrientedRectangle, Circle, Ellipse, RegularPolygon }

/// <summary>Planar constructions in project coordinates. Control points remain independent of generated vertices.</summary>
public static class SketchConstruction
{
    public const int CircleSegments = 128;
    public const int EllipseSegments = 128;

    public static int RequiredControls(ConstructionMode mode) => mode switch
    {
        ConstructionMode.Vertices => 0,
        ConstructionMode.Rectangle or ConstructionMode.Circle => 2,
        ConstructionMode.OrientedRectangle or ConstructionMode.Ellipse => 3,
        ConstructionMode.RegularPolygon => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    /// <summary>Returns an owned open ring. Circle is an inscribed 128-segment polygon, not a geodesic buffer.</summary>
    public static SKPoint[] Build(ConstructionMode mode, IReadOnlyList<SKPoint> controls, int regularPolygonSides = 6)
    {
        ArgumentNullException.ThrowIfNull(controls);
        int required = RequiredControls(mode);
        if (required > 0 && controls.Count != required)
            throw new ArgumentException($"Esta construção precisa de {required} pontos de controle.");
        if (controls.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y)))
            throw new ArgumentException("A construção contém coordenadas inválidas.");
        if (mode == ConstructionMode.Vertices) return controls.ToArray();
        var a = controls[0];
        var b = controls[1];
        double dx = (double)b.X - a.X, dy = (double)b.Y - a.Y;
        if (dx == 0 && dy == 0) throw new ArgumentException("Os pontos de controle precisam ser distintos.");
        SKPoint[] result;
        if (mode == ConstructionMode.Rectangle)
        {
            if (dx == 0 || dy == 0) throw new ArgumentException("O retângulo precisa de largura e altura maiores que zero.");
            result = [a, new(b.X, a.Y), b, new(a.X, b.Y)];
        }
        else if (mode == ConstructionMode.OrientedRectangle)
        {
            // Signed perpendicular projection preserves the side selected by the third control.
            double heightFactor = (((double)controls[2].X - a.X) * -dy +
                ((double)controls[2].Y - a.Y) * dx) / (dx * dx + dy * dy);
            if (heightFactor == 0) throw new ArgumentException("O terceiro ponto deve ficar fora da linha da base.");
            double ox = -dy * heightFactor, oy = dx * heightFactor;
            result = [a, b, new((float)(b.X + ox), (float)(b.Y + oy)),
                new((float)(a.X + ox), (float)(a.Y + oy))];
        }
        else if (mode == ConstructionMode.Ellipse)
        {
            var axisX = (double)controls[1].X - a.X;
            var axisY = (double)controls[1].Y - a.Y;
            var cross = axisX * ((double)controls[2].Y - a.Y) - axisY * ((double)controls[2].X - a.X);
            if (axisX == 0 && axisY == 0 || cross == 0)
                throw new ArgumentException("A elipse precisa de dois eixos distintos e não colineares.");
            result = new SKPoint[EllipseSegments];
            for (int i = 0; i < result.Length; i++)
            {
                double angle = 2 * Math.PI * i / result.Length;
                double cos = Math.Cos(angle), sin = Math.Sin(angle);
                result[i] = new((float)(a.X + axisX * cos + ((double)controls[2].X - a.X) * sin),
                    (float)(a.Y + axisY * cos + ((double)controls[2].Y - a.Y) * sin));
            }
        }
        else if (mode == ConstructionMode.RegularPolygon)
        {
            if (regularPolygonSides is < 3 or > 32)
                throw new ArgumentOutOfRangeException(nameof(regularPolygonSides), "O polígono regular precisa de 3 a 32 lados.");
            result = new SKPoint[regularPolygonSides];
            double startAngle = Math.Atan2(dy, dx);
            double radius = Math.Sqrt(dx * dx + dy * dy);
            for (int i = 0; i < result.Length; i++)
            {
                double angle = startAngle + 2 * Math.PI * i / result.Length;
                result[i] = new((float)(a.X + radius * Math.Cos(angle)),
                    (float)(a.Y + radius * Math.Sin(angle)));
            }
            result[0] = b;
        }
        else
        {
            result = new SKPoint[CircleSegments];
            // Rotate the radius vector so the clicked radius endpoint is exactly vertex 0.
            for (int i = 0; i < result.Length; i++)
            {
                double angle = 2 * Math.PI * i / result.Length;
                double cos = Math.Cos(angle), sin = Math.Sin(angle);
                result[i] = new((float)(a.X + dx * cos - dy * sin), (float)(a.Y + dx * sin + dy * cos));
            }
            result[0] = b;
        }
        for (int i = 0; i < result.Length; i++)
            if (!float.IsFinite(result[i].X) || !float.IsFinite(result[i].Y) || result[i] == result[(i + 1) % result.Length])
                throw new ArgumentException("A construção excede a precisão das coordenadas do projeto; ajuste os pontos.");
        return result;
    }

    /// <summary>Uses the same generator for hover and finish; incomplete/degenerate input displays guides only.</summary>
    public static (SKPoint[] Points, bool Closed) Preview(ConstructionMode mode,
        IReadOnlyList<SKPoint> controls, SKPoint? cursor, int regularPolygonSides = 6)
    {
        var sample = controls.ToList();
        if (sample.Count > 0 && sample.Count < RequiredControls(mode) && cursor is { } next)
            sample.Add(next);
        try { return (Build(mode, sample, regularPolygonSides), true); }
        catch (ArgumentException) { return (sample.ToArray(), false); }
    }
}
