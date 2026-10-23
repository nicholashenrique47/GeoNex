using GeoNex.Services;
using SkiaSharp;

int assertions = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    assertions++;
}

var firstCategory = new List<SKPath> { Rectangle(0, 0, 2, 2), Rectangle(100, 0, 102, 2) };
var secondCategory = new List<SKPath> { Rectangle(1, 1, 3, 3) };
var categories = new Dictionary<string, List<SKPath>>
{
    ["A"] = firstCategory,
    ["B"] = secondCategory
};
var index = new CategorizedPathSpatialIndex(categories);
var results = new CategorizedPathQueryBuffer();

index.Query(new SKRect(-1, -1, 4, 4), results);
Check(results.Items.Count == 2, "Viewport query returns only intersecting chunks.");
Check(results.Items[0].Category == "A" && ReferenceEquals(results.Items[0].Path, firstCategory[0]) &&
      results.Items[1].Category == "B" && ReferenceEquals(results.Items[1].Path, secondCategory[0]),
    "Spatial query preserves the original category draw order.");

index.Query(new SKRect(99, -1, 103, 4), results);
Check(results.Items.Count == 1 && ReferenceEquals(results.Items[0].Path, firstCategory[1]),
    "Reusing the query buffer clears prior results.");

var manyPaths = new List<SKPath>(10_000);
for (int i = 0; i < 10_000; i++) manyPaths.Add(Rectangle(i * 10, 0, i * 10 + 2, 2));
var largeIndex = new CategorizedPathSpatialIndex(new Dictionary<string, List<SKPath>> { ["many"] = manyPaths });
largeIndex.Query(new SKRect(50_000, -1, 50_005, 3), results);
Check(largeIndex.Count == 10_000, "Large layer index keeps all precompiled chunks.");
Check(results.Items.Count == 1 && results.Items[0].DrawOrder == 5_000,
    "Large-layer viewport lookup returns the single visible chunk instead of scanning all 10,000.");
for (int pathIndex = 0; pathIndex < manyPaths.Count; pathIndex += 499)
{
    var viewport = new SKRect(pathIndex * 10 + 0.1f, -1, pathIndex * 10 + 2.9f, 3);
    int[] expectedOrder = manyPaths.Select((path, order) => (path, order))
        .Where(item => viewport.IntersectsWith(item.path.Bounds))
        .Select(item => item.order)
        .ToArray();
    largeIndex.Query(viewport, results);
    Check(results.Items.Select(item => item.DrawOrder).SequenceEqual(expectedOrder),
        $"Spatial query matches linear viewport filtering at sample {pathIndex}.");
}

foreach (SKPath path in firstCategory.Concat(secondCategory).Concat(manyPaths)) path.Dispose();
Console.WriteLine($"Categorized vector render index passed: {assertions} assertions; 1 of 10,000 chunks selected.");

static SKPath Rectangle(float left, float top, float right, float bottom)
{
    var path = new SKPath();
    path.AddRect(new SKRect(left, top, right, bottom));
    return path;
}
