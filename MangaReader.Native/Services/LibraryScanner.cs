using MangaReader.Native.Models;

namespace MangaReader.Native.Services;

public sealed class LibraryScanner
{
    private readonly NaturalPathComparer _pathComparer = new();

    public List<MangaBook> Scan(string rootPath, Dictionary<string, MangaBook> savedBooks)
    {
        var books = new List<MangaBook>();
        if (!Directory.Exists(rootPath))
        {
            return books;
        }

        foreach (var folder in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories).Prepend(rootPath))
        {
            var pages = Directory.EnumerateFiles(folder)
                .Where(ImageLoader.IsSupportedImage)
                .OrderBy(path => path, _pathComparer)
                .ToList();

            if (pages.Count == 0)
            {
                continue;
            }

            var id = BookId.FromFolderPath(folder);
            savedBooks.TryGetValue(folder, out var saved);

            var book = saved ?? new MangaBook
            {
                Id = id,
                Title = Path.GetFileName(folder),
                Author = TryGetAuthorName(rootPath, folder),
                Tags = "",
                FolderPath = folder,
                ImportedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd"),
                CoverPageIndex = 0,
                LastReadPageIndex = 0
            };

            book.Id = id;
            book.FolderPath = folder;
            book.PageCount = pages.Count;
            book.TotalBytes = ImageLoader.SumFileBytes(pages);
            book.CoverPageIndex = Math.Clamp(book.CoverPageIndex, 0, pages.Count - 1);
            book.LastReadPageIndex = Math.Clamp(book.LastReadPageIndex, 0, pages.Count - 1);
            book.IsMissing = false;
            book.Pages.Clear();
            foreach (var page in pages)
            {
                book.Pages.Add(page);
            }

            books.Add(book);
        }

        return books.OrderBy(book => book.Author).ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string TryGetAuthorName(string rootPath, string folder)
    {
        var relative = Path.GetRelativePath(rootPath, folder);
        var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstSegment) || firstSegment == "." ? "" : firstSegment;
    }
}
