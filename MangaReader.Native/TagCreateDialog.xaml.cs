using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace MangaReader.Native;

public partial class TagCreateDialog : Window
{
    private static readonly string[] PresetColors =
    [
        "#F4B6C2", "#B7D7A8", "#A9CCE3", "#F7DC6F",
        "#D7BDE2", "#F5CBA7", "#AED6F1"
    ];

    public ObservableCollection<string> CustomColors { get; } = new();
    private string _selectedColor = "#F4B6C2";

    public string TagName => TagNameBox.Text.Trim();
    public string TagCategory => GetSelectedCategory();
    public bool IsExclusive => ((TagTypeBox.SelectedItem as ComboBoxItem)?.Content as string) == "互斥";
    public string SelectedColor => _selectedColor;

    public TagCreateDialog(string initialValue, IEnumerable<string>? existingCategories = null)
    {
        InitializeComponent();
        TagNameBox.Text = initialValue;
        PopulateCategories(existingCategories);
        TagTypeBox.SelectedIndex = 1;
        PresetColorPicker.ItemsSource = PresetColors;
        CustomColorPicker.ItemsSource = CustomColors;
        SelectColor(_selectedColor);
        Loaded += (_, _) =>
        {
            TagNameBox.Focus();
            TagNameBox.SelectAll();
        };
    }

    private void AddCustomColor_Click(object sender, MouseButtonEventArgs e)
    {
        if (CustomColors.Count >= 8) return;

        using var dialog = new WinForms.ColorDialog { FullOpen = true };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            var color = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
            if (!CustomColors.Contains(color, StringComparer.OrdinalIgnoreCase))
            {
                CustomColors.Add(color);
            }
            SelectColor(color);
        }
    }

    private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is string color)
        {
            SelectColor(color);
        }
    }

    private void PopulateCategories(IEnumerable<string>? existingCategories)
    {
        var builtIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "内容形态", "色彩规格", "画质规格", "自定义"
        };
        TagCategoryBox.Items.Add(new ComboBoxItem { Content = "内容形态" });
        TagCategoryBox.Items.Add(new ComboBoxItem { Content = "色彩规格" });
        TagCategoryBox.Items.Add(new ComboBoxItem { Content = "画质规格" });
        TagCategoryBox.Items.Add(new ComboBoxItem { Content = "自定义" });
        if (existingCategories is not null)
        {
            foreach (var cat in existingCategories.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c))
            {
                if (!builtIn.Contains(cat))
                {
                    TagCategoryBox.Items.Add(new ComboBoxItem { Content = cat });
                }
            }
        }
        TagCategoryBox.SelectedIndex = 3;
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
        SelectedColorPreview.Background = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        SelectedColorText.Text = $"已选颜色：{color}";
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
