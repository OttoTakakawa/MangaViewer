using MangaReader.Native.Services;
using System.Windows;

namespace MangaReader.Native;

public partial class DataSafetyDialog : Window
{
    private readonly AppStorage _storage;

    public DataSafetyAction RequestedAction { get; private set; } = DataSafetyAction.None;
    public string? SelectedDatabasePath { get; private set; }

    public DataSafetyDialog(AppStorage storage)
    {
        InitializeComponent();
        _storage = storage;
        DataRootText.Text = storage.Root;
        BackupSummaryText.Text = "正在读取数据库备份...";
        Loaded += DataSafetyDialog_Loaded;
    }

    private async void DataSafetyDialog_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= DataSafetyDialog_Loaded;
        await RefreshBackupListAsync();
    }

    private async Task RefreshBackupListAsync()
    {
        var backupPath = _storage.BackupPath;
        var backups = await Task.Run(() =>
        {
            Directory.CreateDirectory(backupPath);
            return Directory.EnumerateFiles(backupPath, "app_*.db")
                .Select(path => new BackupFileItem(path))
                .OrderByDescending(item => item.LastWriteTime)
                .ToList();
        });

        BackupList.ItemsSource = backups;
        BackupSummaryText.Text = backups.Count == 0
            ? "暂无数据库备份"
            : $"已有 {backups.Count} 份数据库备份，默认按时间倒序显示";
    }

    private void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = DataSafetyAction.CreateBackup;
        DialogResult = true;
        Close();
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = DataSafetyAction.OpenBackupFolder;
        DialogResult = true;
        Close();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = DataSafetyAction.OpenDataFolder;
        DialogResult = true;
        Close();
    }

    private void ChooseExternalDatabase_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = DataSafetyAction.RestoreDatabase;
        SelectedDatabasePath = null;
        DialogResult = true;
        Close();
    }

    private void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = DataSafetyAction.CheckUpdate;
        DialogResult = true;
        Close();
    }

    private void RestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        if (BackupList.SelectedItem is not BackupFileItem item)
        {
            BackupSummaryText.Text = "先在列表里选中一份备份，再恢复。";
            return;
        }

        RequestedAction = DataSafetyAction.RestoreDatabase;
        SelectedDatabasePath = item.FullPath;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = DataSafetyAction.None;
        DialogResult = false;
        Close();
    }

    private sealed class BackupFileItem
    {
        public BackupFileItem(string fullPath)
        {
            var info = new FileInfo(fullPath);
            FullPath = fullPath;
            FileName = info.Name;
            LastWriteTime = info.LastWriteTime;
            SizeText = FormatSize(info.Length);
            DetailText = $"{info.LastWriteTime:yyyy-MM-dd HH:mm:ss} · {fullPath}";
        }

        public string FullPath { get; }
        public string FileName { get; }
        public DateTime LastWriteTime { get; }
        public string SizeText { get; }
        public string DetailText { get; }

        private static string FormatSize(long bytes)
        {
            const double kb = 1024;
            const double mb = kb * 1024;
            if (bytes >= mb)
            {
                return $"{bytes / mb:F1} MB";
            }

            return $"{Math.Max(1, bytes / kb):F0} KB";
        }
    }
}

public enum DataSafetyAction
{
    None,
    CreateBackup,
    OpenBackupFolder,
    OpenDataFolder,
    RestoreDatabase,
    CheckUpdate
}
