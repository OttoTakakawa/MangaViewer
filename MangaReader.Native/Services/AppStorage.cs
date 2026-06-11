namespace MangaReader.Native.Services;

public sealed class AppStorage
{
    public string Root { get; }
    public string DatabasePath { get; }
    public string CoverCachePath { get; }
    public string LogsPath { get; }
    public string BackupPath { get; }

    public AppStorage()
    {
        Root = Path.Combine(AppContext.BaseDirectory, "MangaReader_Data");
        DatabasePath = Path.Combine(Root, "app.db");
        CoverCachePath = Path.Combine(Root, "cache", "covers");
        LogsPath = Path.Combine(Root, "logs");
        BackupPath = Path.Combine(Root, "backups");
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CoverCachePath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(BackupPath);
    }
}
