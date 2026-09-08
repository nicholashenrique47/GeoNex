using System.Collections.Concurrent;
using GeoNex.Services;

internal static class ResourceLeaseContracts
{
    public static void Run()
    {
        ReplacementDefersDisposal();
        RemovalWaitsForEveryReader();
        RegistryDisposalWaitsForReader();
        ConcurrentReplacementIsSafe();

        Console.WriteLine("Resource lease contracts: PASS (replace, remove, shutdown, concurrent readers, exactly-once dispose)");
    }

    private static void ReplacementDefersDisposal()
    {
        using var registry = new LeasedResourceRegistry<string, ProbeResource>(StringComparer.Ordinal);
        var first = new ProbeResource();
        var second = new ProbeResource();
        registry.Publish("raster", first);

        ResourceLease<ProbeResource> oldLease = registry.Acquire("raster")!;
        registry.Publish("raster", second);

        Assert(first.DisposeCount == 0, "replacement disposed an active reader");
        using ResourceLease<ProbeResource> newLease = registry.Acquire("raster")!;
        Assert(ReferenceEquals(newLease.Resource, second), "new readers did not see replacement");

        oldLease.Dispose();
        oldLease.Dispose();
        Assert(first.DisposeCount == 1, "retired resource was not disposed exactly once");
    }

    private static void RemovalWaitsForEveryReader()
    {
        using var registry = new LeasedResourceRegistry<string, ProbeResource>();
        var resource = new ProbeResource();
        registry.Publish("vrt", resource);
        ResourceLease<ProbeResource> first = registry.Acquire("vrt")!;
        ResourceLease<ProbeResource> second = registry.Acquire("vrt")!;

        Assert(registry.Remove("vrt"), "registered resource was not removed");
        Assert(registry.Acquire("vrt") == null, "removed resource remained visible");
        first.Dispose();
        Assert(resource.DisposeCount == 0, "resource disposed before final reader");
        second.Dispose();
        Assert(resource.DisposeCount == 1, "resource was not disposed after final reader");
    }

    private static void RegistryDisposalWaitsForReader()
    {
        var registry = new LeasedResourceRegistry<string, ProbeResource>();
        var resource = new ProbeResource();
        registry.Publish("cache", resource);
        ResourceLease<ProbeResource> lease = registry.Acquire("cache")!;

        registry.Dispose();
        Assert(resource.DisposeCount == 0, "registry shutdown disposed an active reader");
        lease.Dispose();
        Assert(resource.DisposeCount == 1, "shutdown resource was not released");
    }

    private static void ConcurrentReplacementIsSafe()
    {
        using var registry = new LeasedResourceRegistry<string, ProbeResource>();
        var resources = new ConcurrentBag<ProbeResource>();
        var initial = new ProbeResource();
        resources.Add(initial);
        registry.Publish("layer", initial);

        using var stop = new CancellationTokenSource();
        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using ResourceLease<ProbeResource>? lease = registry.Acquire("layer");
                if (lease == null) continue;
                lease.Resource.AssertAlive();
                Thread.SpinWait(32);
                lease.Resource.AssertAlive();
            }
        })).ToArray();

        for (int index = 0; index < 1_000; index++)
        {
            var replacement = new ProbeResource();
            resources.Add(replacement);
            registry.Publish("layer", replacement);
        }

        stop.Cancel();
        Task.WaitAll(readers);
        registry.Remove("layer");

        Assert(resources.All(resource => resource.DisposeCount == 1),
            "concurrent replacement leaked or double-disposed a resource");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Resource lease contract failed: {message}");
    }

    private sealed class ProbeResource : IDisposable
    {
        private int _disposed;

        public int DisposeCount => Volatile.Read(ref _disposed);

        public void AssertAlive()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ProbeResource), "resource was disposed while leased");
        }

        public void Dispose()
        {
            int count = Interlocked.Increment(ref _disposed);
            if (count != 1) throw new InvalidOperationException("resource disposed more than once");
        }
    }
}
