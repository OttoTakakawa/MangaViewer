using MangaReader.Native.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MangaReader.Native;

public partial class TagEditDialog : Window
{
    private static readonly string[] AvailableColors =
    [
        "#F4B6C2", "#B7D7A8", "#A9CCE3", "#F7DC6F",
        "#D7BDE2", "#F5CBA7", "#AED6F1", "#A3E4D7",
        "#E8F1FF", "#FFF2D6", "#EAF7E8", "#EFE5DA"
    ];

    private string _selectedColor;

    public string TagName => TagNameBox.Text.Trim();
    public string TagCategory => GetSelectedCategory();
    public bool IsExclusive => ((TagTypeBox.SelectedItem as ComboBoxItem)?.Content as string) == "互斥";
    public string SelectedColor => _selectedColor;
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
        TagTypeBox.SelectedIndex = tag.IsExclusive ? 0 : 1;
        ColorPicker.ItemsSource = AvailableColors;
        _selectedColor = tag.Color;
        SelectColor(_selectedColor);

        Loaded += (_, _) =>
        {
            TagNameBox.Focus();
            TagNameBox.SelectAll();
        };
    }

    private string GetSelectedCategory()
    {
        if (TagCategoryBox.SelectedItem is ComboBoxItem item)
        {
            return item.Content as string ?? "自定义";
        }
        var text = TagCategoryBox.Text?.Trim();
        return string.IsNullOrWhiteSpace(text) ? "自定义" : text;
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

        TagCategoryBox.Text = category;
    }

    private void SelectColor(string color)
    {
        _selectedColor = color;
        SelectedColorPreview.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        SelectedColorText.Text = $"已选颜色：{color}";
    }

    private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.Border border && border.Background is SolidColorBrush brush)
        {
            SelectColor(brush.Color.ToString());
        }
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
