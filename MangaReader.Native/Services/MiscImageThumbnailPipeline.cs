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
    private const long MaxMemoryImageBytes = 128L * 1024 * 1024;
    private const int ThumbnailDecodeWidth = 480;
    private readonly string _cacheDirectory;
    private readonly AsyncLruCache<BitmapSource> _memoryCache = new(
        MaxMemoryImages,
        MaxMemoryImageBytes,
        6,
        EstimateBitmapBytes);

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
        return await _memoryCache.GetOrLoadAsync(
            cacheKey,
            _ => LoadOrCreate(image, cacheKey),
            cancellationToken);
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

    public void ClearMemoryCache()
    {
        _memoryCache.Clear();
    }

    private static long EstimateBitmapBytes(BitmapSource image)
    {
        var bitsPerPixel = Math.Max(1, image.Format.BitsPerPixel);
        return (long)image.PixelWidth * image.PixelHeight * bitsPerPixel / 8;
    }
}
