using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MangaReader.Native;

public partial class TagCreateDialog : Window
{
    private static readonly string[] AvailableColors =
    [
        "#F4B6C2", "#B7D7A8", "#A9CCE3", "#F7DC6F",
        "#D7BDE2", "#F5CBA7", "#AED6F1", "#A3E4D7",
        "#E8F1FF", "#FFF2D6", "#EAF7E8", "#EFE5DA"
    ];

    private string _selectedColor = "#EFE5DA";

    public string TagName => TagNameBox.Text.Trim();
    public string TagCategory => GetSelectedCategory();
    public bool IsExclusive => ((TagTypeBox.SelectedItem as ComboBoxItem)?.Content as string) == "互斥";
    public string SelectedColor => _selectedColor;

    public TagCreateDialog(string initialValue)
    {
        InitializeComponent();
        TagNameBox.Text = initialValue;
        TagCategoryBox.SelectedIndex = 3;
        TagTypeBox.SelectedIndex = 1;
        ColorPicker.ItemsSource = AvailableColors;
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

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
