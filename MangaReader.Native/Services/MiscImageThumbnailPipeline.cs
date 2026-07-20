using MangaReader.Native.Models;
using System.Windows.Media.Imaging;

namespace MangaReader.Native.Services;

// 杂图库缩略图管线。
// 与漫画 CoverThumbnailPipeline 隔离：
//   - 缓存目录独立：{AppStorage.CoverCachePath}/misc/，避免漫画 CoverCache.SweepStaleCovers 误删
//   - LRU 容量更大：杂图数量级远大于 books（480 vs 320）
//   - 并发门控更宽：单图解码无 Pages 集合依赖，可提至 6 路
//   - 缓存键独立：{id}_{fileSize}_{modifiedTicks}.png
public sealed class MiscImageThumbnailPipeline
{
    private const int MaxMemoryImages = 480;
    private const int ThumbnailDecodeWidth = 480;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _loaderGate = new(6);
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Dictionary<string, Task<BitmapSource?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public MiscImageThumbnailPipeline(AppStorage storage)
    {
        _cacheDirectory = Path.Combine(storage.CoverCachePath, "misc");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<BitmapSource?> LoadAsync(MiscImage image, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(image.FilePath) || !File.Exists(image.FilePath))
        {
            return null;
        }

        var cacheKey = GetCacheKey(image);
        if (TryGet(cacheKey, out var cached))
        {
            return cached;
        }

        Task<BitmapSource?> task;
        lock (_syncRoot)
        {
            if (!_inFlight.TryGetValue(cacheKey, out var existingTask))
            {
                task = LoadCoreAsync(image, cacheKey, cancellationToken);
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

    private async Task<BitmapSource?> LoadCoreAsync(MiscImage image, string cacheKey, CancellationToken cancellationToken)
    {
        await _loaderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var loaded = LoadOrCreate(image, cacheKey);
                if (loaded is not null)
                {
                    Add(cacheKey, loaded);
                }
                return loaded;
            }, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _loaderGate.Release();
        }
    }

    private BitmapSource? LoadOrCreate(MiscImage image, string cacheKey)
    {
        var cachePath = Path.Combine(_cacheDirectory, cacheKey + ".png");
        if (!File.Exists(cachePath))
        {
            try
            {
                CreateThumbnail(image.FilePath, cachePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                AppLogger.Warn("misc-thumb", $"生成缩略图失败：{image.FilePath}。{ex.Message}");
                return null;
            }
        }

        return File.Exists(cachePath) ? ImageLoader.LoadBitmap(cachePath, ThumbnailDecodeWidth) : null;
    }

    private string GetCacheKey(MiscImage image)
    {
        long modifiedTicks = 0;
        long sizeTicks = image.FileSize;
        try
        {
            var info = new FileInfo(image.FilePath);
            modifiedTicks = info.LastWriteTimeUtc.Ticks;
            sizeTicks = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return $"{image.Id}_{sizeTicks}_{modifiedTicks}";
    }

    private static void CreateThumbnail(string sourcePath, string cachePath)
    {
        var source = ImageLoader.LoadBitmap(sourcePath, ThumbnailDecodeWidth);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = File.Create(cachePath);
        encoder.Save(stream);
    }

    public void SweepStale(IEnumerable<MiscImage> images)
    {
        var validSet = new HashSet<string>(
            images.Where(img => !string.IsNullOrWhiteSpace(img.FilePath) && File.Exists(img.FilePath))
                  .Select(GetCacheKey),
            StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*.png"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (!validSet.Contains(stem))
            {
                try { File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        }
        catch (DirectoryNotFoundException)
        {
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

            while (_memoryCache.Count > MaxMemoryImages && _lru.Last is not null)
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
