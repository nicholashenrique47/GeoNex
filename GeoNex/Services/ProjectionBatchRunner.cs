using System.Buffers;
using OSGeo.OSR;

namespace GeoNex.Services;

/// <summary>Bounded projection workers; every GDAL transform and scratch buffer has one owner.</summary>
public static class ProjectionBatchRunner
{
    public const int FeaturesPerBatch = 32_768;
    public const int PointsPerFeature = 5;
    public const int MinimumParallelFeatures = 131_072;

    public static int WorkerCount(int features, int requested) => features < MinimumParallelFeatures ? 1 :
        Math.Clamp(requested, 1, Math.Min(16, (int)(((long)features + FeaturesPerBatch - 1) / FeaturesPerBatch)));

    public static void Run(int featureCount, int requestedWorkers, string sourceWkt, string targetCrs,
        Action<int, int, double[], double[], double[], CoordinateTransformation> batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(featureCount);
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        if (featureCount == 0) return;
        int workers = WorkerCount(featureCount, requestedWorkers);
        int capacity = Math.Min(FeaturesPerBatch, featureCount) * PointsPerFeature;
        if (workers == 1) Work(0);
        else Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers,
            CancellationToken = cancellationToken }, Work);
        cancellationToken.ThrowIfCancellationRequested();

        void Work(int worker)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var source = SrsFactory.FromWkt(sourceWkt);
            using var target = SrsFactory.FromUserInput(targetCrs);
            using var transform = new CoordinateTransformation(source, target);
            double[]? x = null, y = null, z = null;
            try
            {
                x = ArrayPool<double>.Shared.Rent(capacity);
                y = ArrayPool<double>.Shared.Rent(capacity);
                z = ArrayPool<double>.Shared.Rent(capacity);
                // Fixed, disjoint ranges: neither feature writes nor OGR objects are shared.
                for (long first = (long)worker * FeaturesPerBatch; first < featureCount;
                    first += (long)workers * FeaturesPerBatch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = Math.Min(FeaturesPerBatch, featureCount - (int)first);
                    batch((int)first, count, x, y, z, transform);
                }
            }
            finally
            {
                if (x != null) ArrayPool<double>.Shared.Return(x);
                if (y != null) ArrayPool<double>.Shared.Return(y);
                if (z != null) ArrayPool<double>.Shared.Return(z);
            }
        }
    }
}
