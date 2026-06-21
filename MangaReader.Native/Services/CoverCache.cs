using MangaReader.Native.Models;
using System.Windows.Media.Imaging;

namespace MangaReader.Native.Services;

public sealed class CoverCache
{
    private readonly AppStorage _storage;
    private readonly Dictionary<string, long> _coverTimestampCache = new(StringComparer.OrdinalIgnoreCase);

    public CoverCache(AppStorage storage)
    {
        _storage = storage;
    }

    public BitmapSource? LoadOrCreate(MangaBook book)
    {
        if (book.Pages.Count == 0)
        {
            return null;
        }

        var coverIndex = Math.Clamp(book.CoverPageIndex, 0, book.Pages.Count - 1);
        var coverPage = book.Pages[coverIndex];
        var cachePath = GetCachePath(book, coverPage);

        if (!File.Exists(cachePath))
        {
            CreateCover(coverPage, cachePath);
        }

        return File.Exists(cachePath) ? ImageLoader.LoadBitmap(cachePath, 240) : null;
    }

    public string GetCacheKey(MangaBook book)
    {
        if (book.Pages.Count == 0)
        {
            return book.Id;
        }

        var coverIndex = Math.Clamp(book.CoverPageIndex, 0, book.Pages.Count - 1);
        var coverPage = book.Pages[coverIndex];
        return GetCachePath(book, coverPage);
    }

    private string GetCachePath(MangaBook book, string coverPage)
    {
        if (!_coverTimestampCache.TryGetValue(coverPage, out var modifiedTicks))
        {
            modifiedTicks = File.GetLastWriteTimeUtc(coverPage).Ticks;
            _coverTimestampCache[coverPage] = modifiedTicks;
        }
        var fileName = $"{book.Id}_{book.CoverPageIndex}_{modifiedTicks}.png";
        return Path.Combine(_storage.CoverCachePath, fileName);
    }

    private static void CreateCover(string sourcePath, string cachePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        var source = ImageLoader.LoadBitmap(sourcePath, 360);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = File.Create(cachePath);
        encoder.Save(stream);
    }

    public void SweepStaleCovers(IEnumerable<string> validBookIds)
    {
        var validSet = new HashSet<string>(validBookIds, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_storage.CoverCachePath, "*.png"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var bookId = fileName.Split('_')[0];
                if (!validSet.Contains(bookId))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }
}
