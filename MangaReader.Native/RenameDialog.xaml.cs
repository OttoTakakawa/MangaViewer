using System.Windows;

namespace MangaReader.Native;

public partial class RenameDialog : Window
{
    private string _oldName = "";

    public string NewName { get; private set; } = "";

    public RenameDialog(string oldName)
        : this("重命名作者", "输入新的作者名称。", "旧名称", oldName, "新名称", oldName)
    {
    }

    public RenameDialog(string title, string description, string oldLabel, string oldValue, string newLabel, string initialValue)
    {
        InitializeComponent();
        _oldName = oldValue;
        Title = title;
        DialogTitleText.Text = title;
        DialogDescriptionText.Text = description;
        OldNameLabelText.Text = oldLabel;
        OldNameText.Text = oldValue;
        NewNameLabelText.Text = newLabel;
        NewNameBox.Text = initialValue;
        NewNameBox.Focus();
        NewNameBox.SelectAll();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        var trimmed = NewNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmed))
        {
            System.Windows.MessageBox.Show(@"新名称不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        NewName = trimmed;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
