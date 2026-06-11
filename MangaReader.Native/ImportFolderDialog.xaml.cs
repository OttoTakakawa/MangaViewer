using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace MangaReader.Native;

public partial class ImportFolderDialog : Window
{
    public ObservableCollection<string> FolderPaths { get; } = [];

    public ImportFolderDialog()
    {
        InitializeComponent();
        FolderList.ItemsSource = FolderPaths;
        RefreshState();
    }

    private void DialogRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void AddPath_Click(object sender, RoutedEventArgs e)
    {
        AddFolder(FolderPathBox.Text.Trim());
        FolderPathBox.Clear();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (FolderPaths.Count == 0)
        {
            HintText.Text = "先拖入文件夹，或粘贴一个有效的本地文件夹路径。";
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        UpdateDragEffect(e);
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        UpdateDragEffect(e);
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!TryGetDroppedFolders(e, out var folders))
        {
            HintText.Text = "只能导入文件夹。";
            return;
        }

        foreach (var folder in folders)
        {
            AddFolder(folder, showInvalid: false);
        }
        HintText.Text = $"已加入 {folders.Count} 个文件夹。";
    }

    private void UpdateDragEffect(System.Windows.DragEventArgs e)
    {
        e.Effects = TryGetDroppedFolders(e, out _) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private bool TryGetDroppedFolders(System.Windows.DragEventArgs e, out List<string> folders)
    {
        folders = [];
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            || e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths)
        {
            return false;
        }

        folders = paths
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return folders.Count > 0;
    }

    private void AddFolder(string folderPath, bool showInvalid = true)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            if (showInvalid)
            {
                HintText.Text = "路径无效，请粘贴一个存在的文件夹路径。";
            }
            return;
        }

        if (FolderPaths.Contains(folderPath, StringComparer.OrdinalIgnoreCase))
        {
            HintText.Text = "这个文件夹已经在导入列表里。";
            return;
        }

        FolderPaths.Add(folderPath);
        HintText.Text = $"已加入：{Path.GetFileName(folderPath)}";
        RefreshState();
    }

    private void RefreshState()
    {
        EmptyText.Visibility = FolderPaths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
