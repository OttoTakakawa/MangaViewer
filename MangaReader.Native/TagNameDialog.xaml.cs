using System.Windows;

namespace MangaReader.Native;

public partial class TagNameDialog : Window
{
    public string TagName => TagNameBox.Text.Trim();

    public TagNameDialog(string initialValue, string title)
    {
        InitializeComponent();
        Title = title;
        TagNameBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            TagNameBox.Focus();
            TagNameBox.SelectAll();
        };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
