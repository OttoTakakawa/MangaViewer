using MangaReader.Native.Models;
using System.Windows.Media.Imaging;

namespace MangaReader.Native.Services;

public sealed class CoverThumbnailPipeline
{
    private const int MaxMemoryCovers = 320;
    private readonly CoverCache _coverCache;
    private readonly SemaphoreSlim _loaderGate = new(4);
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Dictionary<string, Task<BitmapSource?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public CoverThumbnailPipeline(CoverCache coverCache)
    {
        _coverCache = coverCache;
    }

    public async Task<BitmapSource?> LoadAsync(MangaBook book, CancellationToken cancellationToken = default)
    {
        if (book.Pages.Count == 0)
        {
            return null;
        }

        var cacheKey = _coverCache.GetCacheKey(book);
        if (TryGet(cacheKey, out var cached))
        {
            return cached;
        }

        Task<BitmapSource?> task;
        lock (_syncRoot)
        {
            if (!_inFlight.TryGetValue(cacheKey, out var existingTask))
            {
                task = LoadCoreAsync(book, cacheKey, cancellationToken);
                _inFlight[cacheKey] = task;
            }
            else
            {
                task = existingTask;
            }
        }

        try
        {
            return await task.ConfigureAwait(true);
        }
        finally
        {
            lock (_syncRoot)
            {
                if (_inFlight.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, task))
                {
                    _inFlight.Remove(cacheKey);
                }
            }
        }
    }

    private async Task<BitmapSource?> LoadCoreAsync(MangaBook book, string cacheKey, CancellationToken cancellationToken)
    {
        await _loaderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var image = _coverCache.LoadOrCreate(book);
                if (image is not null)
                {
                    Add(cacheKey, image);
                }
                return image;
            }, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _loaderGate.Release();
        }
    }

    private bool TryGet(string key, out BitmapSource? image)
    {
        lock (_syncRoot)
        {
            if (_memoryCache.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null;
        return false;
    }

    private void Add(string key, BitmapSource image)
    {
        lock (_syncRoot)
        {
            if (_memoryCache.TryGetValue(key, out var existing))
            {
                existing.Value = new CacheEntry(key, image);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, image));
            _lru.AddFirst(node);
            _memoryCache[key] = node;

            while (_memoryCache.Count > MaxMemoryCovers && _lru.Last is not null)
            {
                var last = _lru.Last;
                _lru.RemoveLast();
                _memoryCache.Remove(last.Value.Key);
            }
        }
    }

    public void ClearMemoryCache()
    {
        lock (_syncRoot)
        {
            _memoryCache.Clear();
            _lru.Clear();
            _inFlight.Clear();
        }
    }

    private readonly record struct CacheEntry(string Key, BitmapSource Image);
}
