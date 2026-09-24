using GeoNex.Services;
using NetTopologySuite.Geometries;

internal static class VectorDisplayContracts
{
    public static void Run()
    {
        var layer = new Envelope(0, 100, 0, 100);
        Check(!VectorDisplayPolicy.ShouldUseOverviewLod(249_999, layer, layer), "threshold is bounded");
        Check(VectorDisplayPolicy.ShouldUseOverviewLod(250_000, layer, layer), "full dense overview uses LOD");
        Check(!VectorDisplayPolicy.ShouldUseOverviewLod(1_000_000, new Envelope(0, 10, 0, 10), layer),
            "small local view keeps exact geometry");
        Check(!VectorDisplayPolicy.ShouldUseOverviewLod(1_000_000, new Envelope(), layer), "null view keeps exact geometry");
        Check(!VectorDisplayPolicy.ShouldUseOverviewLod(1_000_000, layer, new Envelope(5, 5, 0, 100)),
            "degenerate layer keeps exact geometry");
        Console.WriteLine("Vector display contracts: PASS (dense overview LOD, local exact fallback)");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
