using MangaReader.Native.Models;
using MangaReader.Native.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace MangaReader.Native;

public partial class TagEditDialog : Window
{
    private static readonly string[] PresetColors = TagCatalog.PresetColors;

    public ObservableCollection<string> CustomColors { get; } = new();
    public ObservableCollection<string> AvailableColors { get; } = new();
    private readonly IReadOnlyDictionary<string, string> _categoryColors;
    private bool _suppressCategoryColorSync;
    private string _selectedColor;

    public string TagName => TagNameBox.Text.Trim();
    public string TagCategory => GetSelectedCategory();
    public bool IsExclusive => ((TagTypeBox.SelectedItem as ComboBoxItem)?.Content as string) == "互斥";
    public string SelectedColor => _selectedColor;
    public bool OpenMoreRequested { get; private set; }

    public TagEditDialog(
        TagChip tag,
        IReadOnlyList<MangaBook> relatedBooks,
        IEnumerable<string>? existingCategories = null,
        IReadOnlyDictionary<string, string>? categoryColors = null,
        IEnumerable<string>? customColors = null)
    {
        InitializeComponent();
        _categoryColors = categoryColors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        TagNameBox.Text = string.IsNullOrWhiteSpace(tag.RawName) ? tag.Name : tag.RawName;
        UpdatedAtText.Text = tag.UpdatedAtText;
        UsageCountText.Text = $"已关联 {tag.UsageCount} 本漫画";
        var previews = relatedBooks.Take(3).ToList();
        PreviewBooksList.ItemsSource = previews;
        EmptyPreviewText.Visibility = previews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _suppressCategoryColorSync = true;
        PopulateCategories(existingCategories);
        SelectCategory(tag.Category);
        TagTypeBox.SelectedIndex = tag.IsExclusive ? 0 : 1;

        PopulateColors(customColors);
        PresetColorPicker.ItemsSource = AvailableColors;

        if (!PresetColors.Contains(tag.Color, StringComparer.OrdinalIgnoreCase))
        {
            if (!CustomColors.Contains(tag.Color, StringComparer.OrdinalIgnoreCase))
            {
                CustomColors.Add(tag.Color);
            }
            if (!AvailableColors.Contains(tag.Color, StringComparer.OrdinalIgnoreCase))
            {
                AvailableColors.Add(tag.Color);
            }
        }

        _selectedColor = tag.Color;
        SelectColor(_selectedColor);
        _suppressCategoryColorSync = false;

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
            if (!AvailableColors.Contains(color, StringComparer.OrdinalIgnoreCase))
            {
                AvailableColors.Add(color);
            }
            SelectColor(color);
        }
    }

    private void TagCategoryBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateColorFromCategory();
    }

    private void TagCategoryBox_LostFocus(object sender, RoutedEventArgs e)
    {
        UpdateColorFromCategory();
    }

    private void UpdateColorFromCategory()
    {
        if (_suppressCategoryColorSync)
        {
            return;
        }

        var category = GetSelectedCategory();
        if (_categoryColors.TryGetValue(category, out var color) && !string.IsNullOrWhiteSpace(color))
        {
            SelectColor(color);
        }
    }

    private void PopulateColors(IEnumerable<string>? customColors)
    {
        foreach (var color in PresetColors)
        {
            AvailableColors.Add(color);
        }

        if (customColors is null)
        {
            return;
        }

        foreach (var color in customColors.Where(IsValidHexColor).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!PresetColors.Contains(color, StringComparer.OrdinalIgnoreCase))
            {
                CustomColors.Add(color);
                AvailableColors.Add(color);
            }
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
    }

    private string GetSelectedCategory()
    {
        var text = TagCategoryBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        if (TagCategoryBox.SelectedItem is ComboBoxItem item)
        {
            return item.Content as string ?? "自定义";
        }

        return "自定义";
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
        if (!IsValidHexColor(color))
        {
            return;
        }

        _selectedColor = color;
        SelectedColorPreview.Background = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        SelectedColorText.Text = $"已选颜色：{color}";
    }

    private static bool IsValidHexColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 7 || value[0] != '#')
        {
            return false;
        }

        return value.Skip(1).All(Uri.IsHexDigit);
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
