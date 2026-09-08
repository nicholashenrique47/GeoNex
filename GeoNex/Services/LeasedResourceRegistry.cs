using System;
using System.Collections.Generic;
using System.Threading;

namespace GeoNex.Services;

internal interface IResourceLeaseOwner<T> where T : class, IDisposable
{
    void Release(object token);
}

/// <summary>
/// Keeps disposable resources alive while readers hold a lease. Replaced or
/// removed resources are retired immediately and disposed after the last reader.
/// </summary>
public sealed class ResourceLease<T> : IDisposable where T : class, IDisposable
{
    private IResourceLeaseOwner<T>? _owner;
    private readonly object _token;

    internal ResourceLease(T resource, IResourceLeaseOwner<T> owner, object token)
    {
        Resource = resource;
        _owner = owner;
        _token = token;
    }

    public T Resource { get; }

    public void Dispose()
    {
        IResourceLeaseOwner<T>? owner = Interlocked.Exchange(ref _owner, null);
        owner?.Release(_token);
    }
}

public sealed class LeasedResourceRegistry<TKey, T> : IDisposable, IResourceLeaseOwner<T>
    where TKey : notnull
    where T : class, IDisposable
{
    private sealed class Entry
    {
        public Entry(T resource) => Resource = resource;

        public T Resource { get; }
        public int Readers { get; set; }
        public bool Retired { get; set; }
        public bool DisposalClaimed { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _entries;
    private bool _disposed;

    public LeasedResourceRegistry(IEqualityComparer<TKey>? comparer = null)
    {
        _entries = new Dictionary<TKey, Entry>(comparer);
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public ResourceLease<T>? Acquire(TKey key)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_entries.TryGetValue(key, out Entry? entry)) return null;

            checked { entry.Readers++; }
            return new ResourceLease<T>(entry.Resource, this, entry);
        }
    }

    public TKey[] SnapshotKeys()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var keys = new TKey[_entries.Count];
            _entries.Keys.CopyTo(keys, 0);
            return keys;
        }
    }

    public void Publish(TKey key, T resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        T? pendingDisposal = null;

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_entries.TryGetValue(key, out Entry? current) &&
                ReferenceEquals(current.Resource, resource))
            {
                return;
            }

            _entries[key] = new Entry(resource);
            if (current != null) pendingDisposal = Retire(current);
        }

        pendingDisposal?.Dispose();
    }

    public bool Remove(TKey key)
    {
        T? pendingDisposal;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_entries.Remove(key, out Entry? entry)) return false;
            pendingDisposal = Retire(entry);
        }

        pendingDisposal?.Dispose();
        return true;
    }

    public void Clear()
    {
        List<T>? pendingDisposals = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            foreach (Entry entry in _entries.Values)
            {
                T? resource = Retire(entry);
                if (resource != null) (pendingDisposals ??= new()).Add(resource);
            }
            _entries.Clear();
        }

        DisposeAll(pendingDisposals);
    }

    public void Dispose()
    {
        List<T>? pendingDisposals = null;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (Entry entry in _entries.Values)
            {
                T? resource = Retire(entry);
                if (resource != null) (pendingDisposals ??= new()).Add(resource);
            }
            _entries.Clear();
        }

        DisposeAll(pendingDisposals);
    }

    private void Release(Entry entry)
    {
        T? pendingDisposal = null;
        lock (_gate)
        {
            if (entry.Readers <= 0)
                throw new InvalidOperationException("Resource lease released more than once.");

            entry.Readers--;
            if (entry.Retired && entry.Readers == 0 && !entry.DisposalClaimed)
            {
                entry.DisposalClaimed = true;
                pendingDisposal = entry.Resource;
            }
        }

        pendingDisposal?.Dispose();
    }

    void IResourceLeaseOwner<T>.Release(object token) => Release((Entry)token);

    private static T? Retire(Entry entry)
    {
        entry.Retired = true;
        if (entry.Readers != 0 || entry.DisposalClaimed) return null;

        entry.DisposalClaimed = true;
        return entry.Resource;
    }

    private static void DisposeAll(List<T>? resources)
    {
        if (resources == null) return;
        foreach (T resource in resources) resource.Dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
