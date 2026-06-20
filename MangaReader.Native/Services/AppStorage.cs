namespace MangaReader.Native.Services;

public sealed class AppStorage
{
    private const string DataLocationFileName = "MangaReader_DataLocation.txt";
    private const string PendingRestoreDatabaseFileName = "pending_restore_app.db";

    public string Root { get; }
    public string DatabasePath { get; }
    public string CoverCachePath { get; }
    public string LogsPath { get; }
    public string BackupPath { get; }
    public string PendingRestoreDatabasePath { get; }
    public bool UsesCustomRoot { get; }

    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "MangaReader_Data");
    public static string DataLocationPath => Path.Combine(AppContext.BaseDirectory, DataLocationFileName);

    public AppStorage()
    {
        Root = ResolveRoot();
        UsesCustomRoot = !Path.GetFullPath(Root).Equals(Path.GetFullPath(DefaultRoot), StringComparison.OrdinalIgnoreCase);
        DatabasePath = Path.Combine(Root, "app.db");
        CoverCachePath = Path.Combine(Root, "cache", "covers");
        LogsPath = Path.Combine(Root, "logs");
        BackupPath = Path.Combine(Root, "backups");
        PendingRestoreDatabasePath = Path.Combine(Root, PendingRestoreDatabaseFileName);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CoverCachePath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(BackupPath);
        ApplyPendingDatabaseRestore();
    }

    public void ScheduleDatabaseRestore(string sourceDatabasePath)
    {
        if (!File.Exists(sourceDatabasePath))
        {
            throw new FileNotFoundException("找不到要恢复的数据库文件。", sourceDatabasePath);
        }

        Directory.CreateDirectory(Root);
        File.Copy(sourceDatabasePath, PendingRestoreDatabasePath, overwrite: true);
        CopyPendingRestoreCompanion(sourceDatabasePath, "-wal");
        CopyPendingRestoreCompanion(sourceDatabasePath, "-shm");
    }

    private void ApplyPendingDatabaseRestore()
    {
        if (!File.Exists(PendingRestoreDatabasePath))
        {
            return;
        }

        BackupCurrentDatabaseBeforeRestore();
        ReplaceDatabaseFile(PendingRestoreDatabasePath, DatabasePath);
        ReplaceCompanionFile("-wal");
        ReplaceCompanionFile("-shm");
    }

    private void BackupCurrentDatabaseBeforeRestore()
    {
        if (!File.Exists(DatabasePath))
        {
            return;
        }

        Directory.CreateDirectory(BackupPath);
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss_fff");
        var backupDatabasePath = Path.Combine(BackupPath, $"app_{timestamp}_before-db-restore.db");
        File.Copy(DatabasePath, backupDatabasePath, overwrite: false);
        CopyCompanion(DatabasePath, backupDatabasePath, "-wal");
        CopyCompanion(DatabasePath, backupDatabasePath, "-shm");
    }

    private void CopyPendingRestoreCompanion(string sourceDatabasePath, string suffix)
    {
        var source = sourceDatabasePath + suffix;
        var target = PendingRestoreDatabasePath + suffix;
        if (File.Exists(source))
        {
            File.Copy(source, target, overwrite: true);
            return;
        }

        if (File.Exists(target))
        {
            File.Delete(target);
        }
    }

    private void ReplaceCompanionFile(string suffix)
    {
        var pending = PendingRestoreDatabasePath + suffix;
        var active = DatabasePath + suffix;
        if (File.Exists(active))
        {
            File.Delete(active);
        }

        if (File.Exists(pending))
        {
            File.Move(pending, active, overwrite: true);
        }
    }

    private static void ReplaceDatabaseFile(string source, string target)
    {
        File.Move(source, target, overwrite: true);
    }

    private static void CopyCompanion(string sourceDatabasePath, string backupDatabasePath, string suffix)
    {
        var source = sourceDatabasePath + suffix;
        if (File.Exists(source))
        {
            File.Copy(source, backupDatabasePath + suffix, overwrite: false);
        }
    }

    public static void SaveCustomRoot(string root)
    {
        var normalized = Path.GetFullPath(root.Trim());
        Directory.CreateDirectory(normalized);
        File.WriteAllText(DataLocationPath, normalized);
    }

    public static void ResetToDefaultRoot()
    {
        if (File.Exists(DataLocationPath))
        {
            File.Delete(DataLocationPath);
        }
    }

    private static string ResolveRoot()
    {
        try
        {
            if (!File.Exists(DataLocationPath))
            {
                return DefaultRoot;
            }

            var configuredRoot = File.ReadAllText(DataLocationPath).Trim();
            return string.IsNullOrWhiteSpace(configuredRoot)
                ? DefaultRoot
                : Path.GetFullPath(configuredRoot);
        }
        catch
        {
            return DefaultRoot;
        }
    }
}
