using System.Windows;

namespace MangaReader.Native;

// 杂图反向重命名对话框。
// 仅返回新文件名（不含扩展名），由调用方执行 File.Move + LibraryDatabase.RenameMiscImageFile。
// 若文件已存在或无权限，调用方负责 try-catch 并提示。
public partial class MiscRenameDialog : Window
{
    public string NewFileName { get; private set; } = "";

    private readonly string _directory;
    private readonly string _extension;

    public MiscRenameDialog(string oldFilePath)
    {
        InitializeComponent();
        OldNameText.Text = Path.GetFileName(oldFilePath);
        _directory = Path.GetDirectoryName(oldFilePath) ?? "";
        _extension = Path.GetExtension(oldFilePath);
        var baseName = Path.GetFileNameWithoutExtension(oldFilePath);
        NewNameBox.Text = baseName;
        NewNameBox.Focus();
        NewNameBox.SelectAll();
        UpdatePreview(baseName);
        NewNameBox.TextChanged += (_, args) => UpdatePreview(NewNameBox.Text);
    }

    private void UpdatePreview(string baseName)
    {
        var trimmed = baseName?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmed))
        {
            PreviewPathText.Text = "";
            return;
        }
        PreviewPathText.Text = $"预览路径：{Path.Combine(_directory, trimmed + _extension)}";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var trimmed = NewNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmed))
        {
            System.Windows.MessageBox.Show("新文件名不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var invalid = Path.GetInvalidFileNameChars();
        if (trimmed.IndexOfAny(invalid) >= 0)
        {
            System.Windows.MessageBox.Show("新文件名包含非法字符。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.Equals(trimmed + _extension, Path.GetFileName(OldNameText.Text), StringComparison.OrdinalIgnoreCase))
        {
            // 名字没变，直接取消以避免无意义 IO。
            DialogResult = false;
            Close();
            return;
        }

        NewFileName = trimmed + _extension;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
