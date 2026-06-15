namespace MangaReader.Core.Abstractions;

public interface IAppStorageProvider
{
    string Root { get; }
    string DatabasePath { get; }
    string CoverCachePath { get; }
    string LogsPath { get; }
    string BackupPath { get; }
}

public interface IImageThumbnailService<TImage>
{
    Task<TImage?> LoadCoverAsync(MangaReader.Core.Models.MangaBook book, CancellationToken cancellationToken = default);
}

public interface IFileDialogService
{
    Task<string?> PickFolderAsync(CancellationToken cancellationToken = default);
    Task<string?> PickDatabaseAsync(CancellationToken cancellationToken = default);
}

public interface IExternalLaunchService
{
    Task OpenFolderAsync(string path, CancellationToken cancellationToken = default);
    Task OpenUrlAsync(string url, CancellationToken cancellationToken = default);
}
