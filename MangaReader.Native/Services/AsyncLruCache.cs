namespace MangaReader.Native.Services;

internal sealed class AsyncLruCache<TValue> where TValue : class
{
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _loaderGate;
    private readonly Func<TValue, long> _sizeEstimator;
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Dictionary<string, InFlightEntry> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private long _cachedBytes;

    public AsyncLruCache(int maxEntries, long maxBytes, int maxConcurrency, Func<TValue, long> sizeEstimator)
    {
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
        _loaderGate = new SemaphoreSlim(maxConcurrency);
        _sizeEstimator = sizeEstimator;
    }

    public async Task<TValue?> GetOrLoadAsync(
        string key,
        Func<CancellationToken, TValue?> loader,
        CancellationToken cancellationToken = default)
    {
        if (TryGet(key, out var cached))
        {
            return cached;
        }

        InFlightEntry entry;
        lock (_syncRoot)
        {
            if (!_inFlight.TryGetValue(key, out entry!))
            {
                var loaderCancellation = new CancellationTokenSource();
                var sharedTask = LoadCoreAsync(key, loader, loaderCancellation.Token);
                entry = new InFlightEntry(sharedTask, loaderCancellation);
                _inFlight[key] = entry;
                _ = sharedTask.ContinueWith(
                    completedTask => RemoveInFlight(key, entry, completedTask),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            entry.WaiterCount++;
        }

        try
        {
            return await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseWaiter(key, entry);
        }
    }

    private void ReleaseWaiter(string key, InFlightEntry entry)
    {
        lock (_syncRoot)
        {
            if (!_inFlight.TryGetValue(key, out var current) || !ReferenceEquals(current, entry))
            {
                return;
            }

            entry.WaiterCount--;
            if (entry.WaiterCount <= 0 && !entry.Task.IsCompleted)
            {
                entry.LoaderCancellation.Cancel();
            }
        }
    }

    private void RemoveInFlight(string key, InFlightEntry entry, Task<TValue?> completedTask)
    {
        lock (_syncRoot)
        {
            if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                _inFlight.Remove(key);
            }
        }

        _ = completedTask.Exception;
        entry.LoaderCancellation.Dispose();
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _cache.Clear();
            _lru.Clear();
            _cachedBytes = 0;
        }
    }

    private async Task<TValue?> LoadCoreAsync(
        string key,
        Func<CancellationToken, TValue?> loader,
        CancellationToken cancellationToken)
    {
        await _loaderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var value = await Task.Run(() => loader(cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (value is not null)
            {
                Add(key, value);
            }
            return value;
        }
        finally
        {
            _loaderGate.Release();
        }
    }

    private bool TryGet(string key, out TValue? value)
    {
        lock (_syncRoot)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    private void Add(string key, TValue value)
    {
        var byteSize = Math.Max(0, _sizeEstimator(value));
        lock (_syncRoot)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                _cachedBytes -= existing.Value.ByteSize;
                existing.Value = new CacheEntry(key, value, byteSize);
                _cachedBytes += byteSize;
                _lru.Remove(existing);
                _lru.AddFirst(existing);
            }
            else
            {
                var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, value, byteSize));
                _lru.AddFirst(node);
                _cache[key] = node;
                _cachedBytes += byteSize;
            }

            while ((_cache.Count > _maxEntries || _cachedBytes > _maxBytes) && _lru.Last is not null)
            {
                var last = _lru.Last;
                _lru.RemoveLast();
                _cache.Remove(last.Value.Key);
                _cachedBytes -= last.Value.ByteSize;
            }
        }
    }

    private sealed record CacheEntry(string Key, TValue Value, long ByteSize);

    private sealed class InFlightEntry(Task<TValue?> task, CancellationTokenSource loaderCancellation)
    {
        public Task<TValue?> Task { get; } = task;
        public CancellationTokenSource LoaderCancellation { get; } = loaderCancellation;
        public int WaiterCount { get; set; }
    }
}
