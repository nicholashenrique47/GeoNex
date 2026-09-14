using GeoNex.Services;

internal static class OnlineSchedulerContracts
{
    private sealed class Result : IDisposable
    {
        public int Disposals;
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task Run()
    {
        using var release = new ManualResetEventSlim();
        var entered = Signal(); var complete = Signal();
        int visible = 1, pendingDraws = 0, errors = 0, ready = 0;
        var stale = new Result();
        using (var worker = new LatestRenderWorker<Result>(() => { Interlocked.Increment(ref ready); complete.TrySetResult(); },
                   _ => Interlocked.Increment(ref errors)))
        {
            worker.Request("active", "old", _ =>
            { entered.TrySetResult(); if (!release.Wait(5000)) throw new TimeoutException(); return stale; },
                _ => throw new Exception("Hidden active layer published"), () => Volatile.Read(ref visible) == 1);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            worker.Request("pending", "old", _ => { Interlocked.Increment(ref pendingDraws); return new Result(); },
                r => r.Dispose(), () => Volatile.Read(ref visible) == 1);
            Volatile.Write(ref visible, 0);
            worker.DiscardIrrelevant();
            Check(worker.PendingCount == 0, "Hidden pending layer retained");
            worker.Request("pending", "late", _ => { Interlocked.Increment(ref pendingDraws); return new Result(); },
                r => r.Dispose(), () => Volatile.Read(ref visible) == 1);
            Check(worker.PendingCount == 0, "Old frame requeued hidden source");
            worker.Request("visible", "new", _ => new Result(), r => r.Dispose());
            release.Set(); await complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(stale.Disposals == 1 && pendingDraws == 0 && errors == 0 && ready == 1,
                "Hidden work consumed resources/published or blocked visible layer");
        }

        var failed = Signal(); int retryDraws = 0; visible = 1;
        using (var worker = new LatestRenderWorker<Result>(() => { }, _ => failed.TrySetResult()))
        {
            worker.Request("hidden-retry", "1", _ => { Interlocked.Increment(ref retryDraws); throw new IOException(); },
                r => r.Dispose(), () => Volatile.Read(ref visible) == 1);
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(worker.FailureCount == 1, "Failure not retained for retry");
            Volatile.Write(ref visible, 0); worker.DiscardIrrelevant();
            Check(worker.FailureCount == 0, "Hidden retry retained");
            await CheckRetryFairness(); // Wait across the canceled retry's deadline, with useful work.
            Check(retryDraws == 1, "Hidden retry performed I/O");
        }
        await CheckFailureCapacity();
        Console.WriteLine("Online scheduler: PASS (hidden active/pending/retry, late requests, exactly-once disposal, earliest retry, bounded failure eviction)");
    }

    private static async Task CheckRetryFairness()
    {
        var firstFailure = Signal(); var secondFailure = Signal(); var recovered = Signal();
        int firstAttempts = 0, errorCount = 0;
        using var worker = new LatestRenderWorker<Result>(() => recovered.TrySetResult(), _ =>
        {
            if (Interlocked.Increment(ref errorCount) == 1) firstFailure.TrySetResult();
            else secondFailure.TrySetResult();
        });
        worker.Request("first", "1", _ =>
        {
            if (Interlocked.Increment(ref firstAttempts) == 1) throw new IOException();
            return new Result();
        }, r => r.Dispose());
        await firstFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(2500);
        worker.Request("second", "1", _ => throw new IOException(), r => r.Dispose());
        await secondFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Old implementation reset the timer to another 3 seconds here.
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(firstAttempts == 2, "Old source retry was lost/postponed");
    }

    private static async Task CheckFailureCapacity()
    {
        using var notifications = new SemaphoreSlim(0);
        using var worker = new LatestRenderWorker<Result>(() => { }, _ => notifications.Release());
        for (int i = 0; i < 17; i++)
        {
            worker.Request($"layer-{i}", "1", _ => throw new IOException(), r => r.Dispose());
            Check(await notifications.WaitAsync(TimeSpan.FromSeconds(2)), "Failure fixture timed out");
        }
        Check(worker.FailureCount == 16, "Overflow erased other sources' cooldowns");
    }
}
