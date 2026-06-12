using MangaReader.Native.Models;
using System.Windows.Media.Imaging;

namespace MangaReader.Native.Services;

public sealed class CoverCache
{
    private readonly AppStorage _storage;

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

        var coverPage = book.Pages[Math.Clamp(book.CoverPageIndex, 0, book.Pages.Count - 1)];
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

        var coverPage = book.Pages[Math.Clamp(book.CoverPageIndex, 0, book.Pages.Count - 1)];
        return GetCachePath(book, coverPage);
    }

    private string GetCachePath(MangaBook book, string coverPage)
    {
        var modifiedTicks = File.GetLastWriteTimeUtc(coverPage).Ticks;
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
}
