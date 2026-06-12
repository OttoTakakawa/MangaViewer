using System.Windows;
using System.Windows.Controls;

namespace MangaReader.Native;

public partial class TagCreateDialog : Window
{
    public string TagName => TagNameBox.Text.Trim();
    public string TagCategory => (TagCategoryBox.SelectedItem as ComboBoxItem)?.Content as string ?? "自定义";
    public bool IsExclusive => ((TagTypeBox.SelectedItem as ComboBoxItem)?.Content as string) == "互斥";

    public TagCreateDialog(string initialValue)
    {
        InitializeComponent();
        TagNameBox.Text = initialValue;
        TagCategoryBox.SelectedIndex = 3;
        TagTypeBox.SelectedIndex = 1;
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
