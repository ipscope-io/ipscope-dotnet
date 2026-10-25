using System.Collections.Concurrent;

namespace IpScope.Sdk;

/// <summary>
/// Thread-safe O(1) Least-Recently-Used (LRU) cache with TTL expiration and
/// singleflight <see cref="Task{TResult}"/> request coalescing.
/// </summary>
internal sealed class LruTtlCache<TValue> where TValue : class
{
    private sealed record CacheNode(string Key, TValue Value, long ExpiresAtTicks);

    public readonly record struct CoalescedResult(TValue Value, bool FromCache);

    private readonly bool _enabled;
    private readonly int _maxEntries;
    private readonly TimeSpan _ttl;
    private readonly LinkedList<CacheNode> _lruList = new();
    private readonly Dictionary<string, LinkedListNode<CacheNode>> _map = new(StringComparer.Ordinal);
    private readonly object _syncRoot = new();
    private readonly ConcurrentDictionary<string, Task<TValue>> _inFlight = new(StringComparer.Ordinal);

    private long _hits;
    private long _misses;
    private long _coalescedHits;

    public LruTtlCache(CacheOptions? options = null)
    {
        var opts = options ?? new CacheOptions();
        _enabled = opts.Enabled;
        _maxEntries = Math.Max(1, opts.MaxEntries);
        _ttl = opts.EffectiveTtl > TimeSpan.Zero ? opts.EffectiveTtl : TimeSpan.FromSeconds(60);
    }

    public TValue? Get(string key)
    {
        if (!_enabled)
        {
            Interlocked.Increment(ref _misses);
            return null;
        }

        long nowTicks = Environment.TickCount64;
        lock (_syncRoot)
        {
            if (!_map.TryGetValue(key, out LinkedListNode<CacheNode>? node))
            {
                Interlocked.Increment(ref _misses);
                return null;
            }

            if (nowTicks >= node.Value.ExpiresAtTicks)
            {
                _lruList.Remove(node);
                _map.Remove(key);
                Interlocked.Increment(ref _misses);
                return null;
            }

            _lruList.Remove(node);
            _lruList.AddLast(node);
            Interlocked.Increment(ref _hits);
            return node.Value.Value;
        }
    }

    public void Set(string key, TValue value)
    {
        if (!_enabled)
        {
            return;
        }

        long expiresAtTicks = Environment.TickCount64 + (long)_ttl.TotalMilliseconds;
        lock (_syncRoot)
        {
            if (_map.TryGetValue(key, out LinkedListNode<CacheNode>? existing))
            {
                _lruList.Remove(existing);
                _map.Remove(key);
            }

            while (_map.Count >= _maxEntries && _lruList.First is not null)
            {
                LinkedListNode<CacheNode> oldest = _lruList.First;
                _lruList.RemoveFirst();
                _map.Remove(oldest.Value.Key);
            }

            var newNode = new LinkedListNode<CacheNode>(new CacheNode(key, value, expiresAtTicks));
            _lruList.AddLast(newNode);
            _map[key] = newNode;
        }
    }

    public async Task<CoalescedResult> GetOrComputeAsync(
        string key,
        bool bypassCache,
        Func<Task<TValue>> producer
    )
    {
        if (!bypassCache)
        {
            TValue? cached = Get(key);
            if (cached is not null)
            {
                return new CoalescedResult(cached, true);
            }

            bool created = false;
            Task<TValue> sharedTask = _inFlight.GetOrAdd(key, k =>
            {
                created = true;
                return ExecuteAndCleanupAsync(k, producer);
            });

            if (!created)
            {
                Interlocked.Increment(ref _coalescedHits);
                TValue coalescedVal = await sharedTask.ConfigureAwait(false);
                return new CoalescedResult(coalescedVal, true);
            }

            TValue val = await sharedTask.ConfigureAwait(false);
            return new CoalescedResult(val, false);
        }

        TValue directVal = await producer().ConfigureAwait(false);
        return new CoalescedResult(directVal, false);
    }

    private async Task<TValue> ExecuteAndCleanupAsync(string key, Func<Task<TValue>> producer)
    {
        try
        {
            TValue result = await producer().ConfigureAwait(false);
            Set(key, result);
            return result;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _lruList.Clear();
            _map.Clear();
        }
        _inFlight.Clear();
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _misses, 0);
        Interlocked.Exchange(ref _coalescedHits, 0);
    }

    public CacheStats GetStats()
    {
        int size;
        lock (_syncRoot)
        {
            size = _map.Count;
        }
        return new CacheStats(
            _enabled,
            size,
            _maxEntries,
            _ttl,
            Interlocked.Read(ref _hits),
            Interlocked.Read(ref _misses),
            Interlocked.Read(ref _coalescedHits)
        );
    }
}
