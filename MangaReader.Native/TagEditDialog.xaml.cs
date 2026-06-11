using MangaReader.Native.Models;
using System.Windows;
using System.Windows.Controls;

namespace MangaReader.Native;

public partial class TagEditDialog : Window
{
    public string TagName => TagNameBox.Text.Trim();
    public string TagCategory => (TagCategoryBox.SelectedItem as ComboBoxItem)?.Content as string ?? "自定义";
    public bool OpenMoreRequested { get; private set; }

    public TagEditDialog(TagChip tag, IReadOnlyList<MangaBook> relatedBooks)
    {
        InitializeComponent();
        TagNameBox.Text = tag.Name;
        UpdatedAtText.Text = tag.UpdatedAtText;
        UsageCountText.Text = $"已关联 {tag.UsageCount} 本漫画";
        var previews = relatedBooks.Take(3).ToList();
        PreviewBooksList.ItemsSource = previews;
        EmptyPreviewText.Visibility = previews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectCategory(tag.Category);

        Loaded += (_, _) =>
        {
            TagNameBox.Focus();
            TagNameBox.SelectAll();
        };
    }

    private void SelectCategory(string category)
    {
        foreach (var item in TagCategoryBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content as string, category, StringComparison.OrdinalIgnoreCase))
            {
                TagCategoryBox.SelectedItem = item;
                return;
            }
        }

        TagCategoryBox.SelectedIndex = 3;
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        OpenMoreRequested = true;
        DialogResult = false;
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
