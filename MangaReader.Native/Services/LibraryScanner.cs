using MangaReader.Native.Models;

namespace MangaReader.Native.Services;

public sealed class LibraryScanner
{
    private readonly NaturalPathComparer _pathComparer = new();

    public List<MangaBook> Scan(
        string rootPath,
        Dictionary<string, MangaBook> savedBooks,
        IProgress<LibraryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var books = new List<MangaBook>();
        if (!Directory.Exists(rootPath))
        {
            return books;
        }

        var folders = new Queue<string>();
        folders.Enqueue(rootPath);
        var discoveredFolders = 1;
        var completedFolders = 0;

        while (folders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folders.Dequeue();
            progress?.Report(new LibraryScanProgress(rootPath, completedFolders, discoveredFolders, books.Count, folder));

            foreach (var childFolder in EnumerateChildFolders(folder, cancellationToken))
            {
                folders.Enqueue(childFolder);
                discoveredFolders++;
            }

            var pages = EnumerateImages(folder, cancellationToken);

            if (pages.Count > 0)
            {
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

            completedFolders++;
            progress?.Report(new LibraryScanProgress(rootPath, completedFolders, discoveredFolders, books.Count, folder));
        }

        return books.OrderBy(book => book.Author).ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private List<string> EnumerateChildFolders(string folder, CancellationToken cancellationToken)
    {
        try
        {
            return Directory.EnumerateDirectories(folder, "*", SearchOption.TopDirectoryOnly)
                .Select(path =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return path;
                })
                .OrderBy(path => path, _pathComparer)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or NotSupportedException)
        {
            return [];
        }
    }

    private List<string> EnumerateImages(string folder, CancellationToken cancellationToken)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Select(path =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return path;
                })
                .Where(ImageLoader.IsSupportedImage)
                .OrderBy(path => path, _pathComparer)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or NotSupportedException)
        {
            return [];
        }
    }

    private static string TryGetAuthorName(string rootPath, string folder)
    {
        var relative = Path.GetRelativePath(rootPath, folder);
        var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstSegment) || firstSegment == "." ? "" : firstSegment;
    }
}

public readonly record struct LibraryScanProgress(
    string RootPath,
    int CompletedFolders,
    int TotalFolders,
    int BooksFound,
    string CurrentFolder);
