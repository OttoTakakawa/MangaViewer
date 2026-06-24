namespace MangaReader.Native.Services;

public enum ImportFolderKind
{
    Empty,
    SingleBook,
    AuthorFolder,
    Mixed
}

public sealed class ImportFolderClassification
{
    public string RootPath { get; init; } = "";
    public ImportFolderKind Kind { get; init; }
    public int DirectImageCount { get; init; }
    public IReadOnlyList<string> ChildBookFolders { get; init; } = [];
}

public sealed class ImportFolderClassifier
{
    private readonly NaturalPathComparer _pathComparer = new();

    public ImportFolderClassification Classify(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return new ImportFolderClassification
            {
                RootPath = rootPath,
                Kind = ImportFolderKind.Empty
            };
        }

        var directImageCount = CountDirectImages(rootPath);
        var childBookFolders = FindChildBookFolders(rootPath);
        var kind = (directImageCount > 0, childBookFolders.Count > 0) switch
        {
            (true, true) => ImportFolderKind.Mixed,
            (true, false) => ImportFolderKind.SingleBook,
            (false, true) => ImportFolderKind.AuthorFolder,
            _ => ImportFolderKind.Empty
        };

        return new ImportFolderClassification
        {
            RootPath = rootPath,
            Kind = kind,
            DirectImageCount = directImageCount,
            ChildBookFolders = childBookFolders
        };
    }

    private int CountDirectImages(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder).Count(ImageLoader.IsSupportedImage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or NotSupportedException)
        {
            return 0;
        }
    }

    private IReadOnlyList<string> FindChildBookFolders(string rootPath)
    {
        try
        {
            return Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories)
                .Where(folder => CountDirectImages(folder) > 0)
                .OrderBy(path => path, _pathComparer)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or NotSupportedException)
        {
            return [];
        }
    }
}
