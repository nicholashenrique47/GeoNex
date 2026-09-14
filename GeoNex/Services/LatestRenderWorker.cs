namespace GeoNex.Services;

/// <summary>One background renderer, bounded latest-per-layer queue, no wait on the UI/render thread.</summary>
public sealed class LatestRenderWorker<T> : IDisposable where T : class, IDisposable
{
    private const int MaximumPendingLayers = 16;
    private const int FailureCooldownMilliseconds = 3000;
    private sealed record Job(string Layer, string Key, Func<CancellationToken, T> Render, Action<T> Publish,
        Func<bool>? IsRelevant, int Attempt = 0)
    {
        public CancellationTokenSource Cancellation { get; } = new();
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _pending = new();
    private readonly Dictionary<string, (Job Owner, long Until)> _failures = new();
    private readonly Timer _retryTimer;
    private long _retryArmedFor = long.MaxValue;
    private readonly Action<Exception> _report;
    private readonly Action _ready;
    private Job? _active;
    private bool _running, _stopped;
    public LatestRenderWorker(Action ready, Action<Exception> report)
    {
        ArgumentNullException.ThrowIfNull(ready);
        ArgumentNullException.ThrowIfNull(report);
        _ready = ready; _report = report;
        _retryTimer = new Timer(_ => RetryPending(), null, Timeout.Infinite, Timeout.Infinite);
    }
    public int PendingCount { get { lock (_gate) return _pending.Count; } }
    public int FailureCount { get { lock (_gate) return _failures.Count; } }
    public bool HasWork { get { lock (_gate) return _active != null || _pending.Count != 0; } }

    // Relevance callbacks must be fast, thread-safe, nonthrowing reads (no I/O).
    // Unlike cancellation alone, rechecking also rejects a late request from an
    // old frame after the UI has removed/hidden its source.
    private static bool Relevant(Job job) => job.IsRelevant?.Invoke() ?? true;

    public void DiscardIrrelevant()
    {
        lock (_gate)
        {
            if (_stopped) return;
            if (_active is { } active && !Relevant(active)) active.Cancellation.Cancel();
            foreach (var pair in _pending.Where(pair => !Relevant(pair.Value)).ToArray())
            { _pending.Remove(pair.Key); pair.Value.Cancellation.Dispose(); }
            foreach (var pair in _failures.Where(pair => !Relevant(pair.Value.Owner)).ToArray())
                _failures.Remove(pair.Key);
            ScheduleRetryLocked();
        }
    }

    public void Request(string layer, string key, Func<CancellationToken, T> render, Action<T> publish,
        Func<bool>? isRelevant = null)
    {
        lock (_gate)
        {
            if (_stopped || !(isRelevant?.Invoke() ?? true)) return;
            if (_active is { } active && active.Layer == layer && active.Key == key && !active.Cancellation.IsCancellationRequested) return;
            if (_pending.TryGetValue(layer, out var pending) && pending.Key == key) return;
            if (_failures.TryGetValue(layer, out var failed) && failed.Owner.Key == key && Environment.TickCount64 < failed.Until) return;
            _failures.Remove(layer); // Invalidate any delayed retry of the previous camera.
            if (_active?.Layer == layer) _active.Cancellation.Cancel();
            if (_pending.Remove(layer, out var replaced)) replaced.Cancellation.Dispose();
            if (_pending.Count >= MaximumPendingLayers)
            {
                var oldest = _pending.First();
                _pending.Remove(oldest.Key);
                oldest.Value.Cancellation.Dispose();
            }
            _pending[layer] = new Job(layer, key, render, publish, isRelevant);
            if (!_running) { _running = true; _ = Task.Run(Pump); }
        }
    }

    private void Pump()
    {
        while (true)
        {
            Job job;
            lock (_gate)
            {
                if (_stopped || _pending.Count == 0) { _running = false; return; }
                var next = _pending.First();
                _pending.Remove(next.Key);
                if (!Relevant(next.Value)) { next.Value.Cancellation.Dispose(); continue; }
                _active = job = next.Value;
            }
            T? result = null;
            bool published = false;
            try
            {
                result = job.Render(job.Cancellation.Token);
                lock (_gate)
                {
                    if (!_stopped && !job.Cancellation.IsCancellationRequested && Relevant(job))
                    {
                        // Publish transfers ownership on normal return. Serialize this
                        // short handoff against replacement/stop, never the network read.
                        job.Publish(result);
                        result = null;
                        published = true;
                        _failures.Remove(job.Layer);
                    }
                }
            }
            catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                bool report = false;
                lock (_gate)
                {
                    if (!_stopped && !job.Cancellation.IsCancellationRequested && Relevant(job))
                    {
                        // Evict one oldest failure, not all other sources' cooldowns.
                        if (!_failures.ContainsKey(job.Layer) && _failures.Count >= MaximumPendingLayers)
                            _failures.Remove(_failures.MinBy(pair => pair.Value.Until).Key);
                        _failures[job.Layer] = (job, Environment.TickCount64 + FailureCooldownMilliseconds);
                        ScheduleRetryLocked();
                        report = true;
                    }
                }
                if (report) _report(error);
            }
            finally
            {
                result?.Dispose();
                lock (_gate) { _active = null; job.Cancellation.Dispose(); }
            }
            if (published)
            {
                try { _ready(); }
                catch (Exception error) { _report(error); }
            }
        }
    }

    // One timer for the whole worker, one automatic retry per camera. No tasks
    // or bitmaps accumulate while the provider is unavailable.
    private void RetryPending()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _retryArmedFor = long.MaxValue;
            long now = Environment.TickCount64;
            foreach (var failure in _failures.ToArray())
            {
                Job job = failure.Value.Owner;
                if (!Relevant(job)) { _failures.Remove(failure.Key); continue; }
                if (job.Attempt != 0) continue;
                if (failure.Value.Until > now) continue;
                // Keep the due retry if capacity is temporarily exhausted.
                if (_active?.Layer == job.Layer || _pending.ContainsKey(job.Layer) || _pending.Count >= MaximumPendingLayers) continue;
                _failures.Remove(failure.Key);
                _pending[job.Layer] = new Job(job.Layer, job.Key, job.Render, job.Publish, job.IsRelevant, 1);
            }
            ScheduleRetryLocked();
            if (_pending.Count > 0 && !_running) { _running = true; _ = Task.Run(Pump); }
        }
    }

    private void ScheduleRetryLocked()
    {
        if (_stopped) return;
        long earliest = long.MaxValue;
        foreach (var failed in _failures.Values)
            if (failed.Owner.Attempt == 0) earliest = Math.Min(earliest, failed.Until);
        // A new failure cannot postpone an older source's deadline. The small
        // floor also prevents timer spinning while the pending queue is full.
        long now = Environment.TickCount64;
        int delay = earliest == long.MaxValue ? Timeout.Infinite :
            (int)Math.Clamp(earliest - now, 100, int.MaxValue);
        long due = delay == Timeout.Infinite ? long.MaxValue : now + delay;
        if (delay != Timeout.Infinite && _retryArmedFor <= due) return;
        _retryArmedFor = due;
        _retryTimer.Change(delay, Timeout.Infinite);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            _retryTimer.Dispose();
            _active?.Cancellation.Cancel();
            foreach (var job in _pending.Values) job.Cancellation.Dispose();
            _pending.Clear();
            _failures.Clear();
        }
    }
}
