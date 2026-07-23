using MangaReader.Native.Models;
using System.Windows.Media.Imaging;

namespace MangaReader.Native.Services;

public sealed class CoverThumbnailPipeline
{
    private const int MaxMemoryCovers = 320;
    private const long MaxMemoryCoverBytes = 96L * 1024 * 1024;
    private readonly CoverCache _coverCache;
    private readonly AsyncLruCache<BitmapSource> _memoryCache = new(
        MaxMemoryCovers,
        MaxMemoryCoverBytes,
        4,
        EstimateBitmapBytes);

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
        return await _memoryCache.GetOrLoadAsync(
            cacheKey,
            _ => _coverCache.LoadOrCreate(book),
            cancellationToken);
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
