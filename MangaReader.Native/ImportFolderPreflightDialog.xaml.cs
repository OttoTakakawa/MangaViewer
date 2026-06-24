using MangaReader.Native.Services;
using System.Diagnostics;
using System.Windows;

namespace MangaReader.Native;

public enum ImportPreflightAction
{
    Cancel,
    ImportSingleBook,
    ImportAuthorFolder
}

public partial class ImportFolderPreflightDialog : Window
{
    private readonly ImportFolderClassification _classification;

    public ImportPreflightAction SelectedAction { get; private set; } = ImportPreflightAction.Cancel;

    public ImportFolderPreflightDialog(ImportFolderClassification classification)
    {
        InitializeComponent();
        _classification = classification;
        RootPathText.Text = classification.RootPath;
        RootPathText.ToolTip = classification.RootPath;
        DirectImageCountText.Text = $"{classification.DirectImageCount} 张";
        ChildBookCountText.Text = $"{classification.ChildBookFolders.Count} 本";
        ChildFolderList.ItemsSource = classification.ChildBookFolders;
        StandardAuthorBox.Text = Path.GetFileName(classification.RootPath);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _classification.RootPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            HintText.Text = $"打开文件夹失败：{ex.Message}";
        }
    }

    private void CreateStandardFolder_Click(object sender, RoutedEventArgs e)
    {
        var authorName = StandardAuthorBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(authorName))
        {
            HintText.Text = "作者文件夹名称不能为空。";
            return;
        }

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择标准作者文件夹的目标根目录",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        try
        {
            var authorFolder = Path.Combine(dialog.SelectedPath, SanitizeFolderName(authorName));
            Directory.CreateDirectory(authorFolder);
            if (CreateInboxFolderBox.IsChecked == true)
            {
                Directory.CreateDirectory(Path.Combine(authorFolder, "待整理"));
            }

            HintText.Text = $"已创建：{authorFolder}";
            Process.Start(new ProcessStartInfo
            {
                FileName = authorFolder,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            HintText.Text = $"创建失败：{ex.Message}";
        }
    }

    private static string SanitizeFolderName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "未命名作者" : cleaned;
    }

    private void ImportSingle_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = ImportPreflightAction.ImportSingleBook;
        DialogResult = true;
    }

    private void ImportAuthor_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = ImportPreflightAction.ImportAuthorFolder;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = ImportPreflightAction.Cancel;
        DialogResult = false;
    }
}
