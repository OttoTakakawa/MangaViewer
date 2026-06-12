using System.Windows;

namespace MangaReader.Native;

public partial class RenameDialog : Window
{
    private string _oldName = "";

    public string NewName { get; private set; } = "";

    public RenameDialog(string oldName)
    {
        InitializeComponent();
        _oldName = oldName;
        OldNameText.Text = oldName;
        NewNameBox.Text = oldName;
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
