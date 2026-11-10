namespace GeoNex.Services.Attributes;

public sealed class AttributeTableBlockCache
{
    public const int BlockSize = 256;
    private readonly int _maxBlocks;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<int, (IReadOnlyList<AttributeRow> Rows, LinkedListNode<int> Node)> _cache = new();
    private readonly LinkedList<int> _lruList = new();

    public AttributeTableBlockCache(int maxBlocks = 64)
    {
        _maxBlocks = Math.Max(4, maxBlocks);
    }

    public bool TryGetBlock(int blockIndex, out IReadOnlyList<AttributeRow>? rows)
    {
        _lock.EnterUpgradeableReadLock();
        try
        {
            if (_cache.TryGetValue(blockIndex, out var entry))
            {
                _lock.EnterWriteLock();
                try
                {
                    _lruList.Remove(entry.Node);
                    _lruList.AddFirst(entry.Node);
                }
                finally
                {
                    _lock.ExitWriteLock();
                }

                rows = entry.Rows;
                return true;
            }

            rows = null;
            return false;
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
    }

    public void PutBlock(int blockIndex, IReadOnlyList<AttributeRow> rows)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_cache.TryGetValue(blockIndex, out var existing))
            {
                _lruList.Remove(existing.Node);
            }
            else if (_cache.Count >= _maxBlocks)
            {
                var lru = _lruList.Last;
                if (lru != null)
                {
                    _cache.Remove(lru.Value);
                    _lruList.RemoveLast();
                }
            }

            var node = _lruList.AddFirst(blockIndex);
            _cache[blockIndex] = (rows, node);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void InvalidateAll()
    {
        _lock.EnterWriteLock();
        try
        {
            _cache.Clear();
            _lruList.Clear();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void InvalidateRow(long fid)
    {
        _lock.EnterWriteLock();
        try
        {
            var blocksToEvict = new List<int>();
            foreach (var kvp in _cache)
            {
                if (kvp.Value.Rows.Any(r => r.FID == fid))
                {
                    blocksToEvict.Add(kvp.Key);
                }
            }

            foreach (var b in blocksToEvict)
            {
                if (_cache.TryGetValue(b, out var entry))
                {
                    _lruList.Remove(entry.Node);
                    _cache.Remove(b);
                }
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
}
