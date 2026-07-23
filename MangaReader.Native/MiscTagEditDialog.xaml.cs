using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MangaReader.Native.Services;
using WinForms = System.Windows.Forms;

namespace MangaReader.Native;

// 杂图 Tag 编辑/新建对话框。
// 与漫画 TagEditDialog 完全解耦：
// - 色板走 MiscTagService.PresetColors（与漫画 TagCatalog.PresetColors 独立）
// - 不显示互斥规则（杂图 tag 体系无此概念）
// - 不显示关联漫画预览（杂图没有书籍）
// - 分组候选项从 misc_image_tags 表动态加载（不写死漫画分类）
// - 用法统计显示"已关联 X 张杂图"，不是"本漫画"
public partial class MiscTagEditDialog : Window
{
    // 杂图专用预设色板。与漫画 TagCatalog.PresetColors 独立，避免视觉混用。
    private static readonly string[] PresetColors = MiscTagService.PresetColors;

    public ObservableCollection<string> CustomColors { get; } = new();
    public ObservableCollection<string> AvailableColors { get; } = new();

    private string _selectedColor = PresetColors[0];
    private readonly IReadOnlyDictionary<string, string> _categoryColors;
    private bool _suppressCategoryColorSync;

    public string TagName => TagNameBox.Text.Trim();
    public string TagCategory => GetSelectedCategory();
    public string SelectedColor => _selectedColor;

    // isNew=true 显示"新建杂图标签"，false 显示"编辑杂图标签"。
    // existingCategories / categoryColors 由 MiscTagManagerWindow 从 misc_image_tags 表加载后传入。
    // usageCount > 0 时显示"已关联 N 张杂图"，否则隐藏。
    public MiscTagEditDialog(
        string initialName,
        string initialCategory,
        string initialColor,
        bool isNew,
        IEnumerable<string>? existingCategories = null,
        IReadOnlyDictionary<string, string>? categoryColors = null,
        int usageCount = 0)
    {
        InitializeComponent();

        DialogTitle.Text = isNew ? "新建杂图标签" : "编辑杂图标签";
        ConfirmButton.Content = isNew ? "创建" : "保存";

        _categoryColors = categoryColors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        TagNameBox.Text = initialName ?? "";

        if (usageCount > 0)
        {
            UsageCountText.Text = $"已关联 {usageCount} 张杂图";
            UsageCountText.Visibility = Visibility.Visible;
        }

        _suppressCategoryColorSync = true;
        PopulateCategories(existingCategories);
        SelectCategory(string.IsNullOrWhiteSpace(initialCategory) ? "未分类" : initialCategory);
        _suppressCategoryColorSync = false;

        PopulateColors();
        PresetColorPicker.ItemsSource = AvailableColors;

        var startColor = string.IsNullOrWhiteSpace(initialColor) ? PresetColors[0] : initialColor;
        if (!PresetColors.Contains(startColor, StringComparer.OrdinalIgnoreCase))
        {
            if (!CustomColors.Contains(startColor, StringComparer.OrdinalIgnoreCase))
            {
                CustomColors.Add(startColor);
            }
            if (!AvailableColors.Contains(startColor, StringComparer.OrdinalIgnoreCase))
            {
                AvailableColors.Add(startColor);
            }
        }

        _selectedColor = startColor;
        SelectColor(_selectedColor);

        Loaded += (_, _) =>
        {
            TagNameBox.Focus();
            TagNameBox.SelectAll();
        };
    }

    private void PopulateCategories(IEnumerable<string>? existingCategories)
    {
        // 杂图不写死内置分类，只从 misc_image_tags 已有分类中加载，给用户纯粹的"我的杂图分类"。
        // 始终提供"未分类"作为默认项，便于快速归类。
        var builtIn = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "未分类" };
        TagCategoryBox.Items.Add(new ComboBoxItem { Content = "未分类" });
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
        return TagDialogSupport.GetSelectedCategory(TagCategoryBox, "未分类");
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

    private void PopulateColors()
    {
        foreach (var color in PresetColors)
        {
            AvailableColors.Add(color);
        }
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

    private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is string color)
        {
            SelectColor(color);
        }
    }

    private void SelectColor(string color)
    {
        if (!TagDialogSupport.IsValidHexColor(color))
        {
            return;
        }

        _selectedColor = color;
        SelectedColorPreview.Background = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        SelectedColorText.Text = $"已选颜色：{color}";
    }

    // 分类变更时自动套用同组已有颜色（如果该分类在 misc_image_tags 中已有颜色记录）
    private void TagCategoryBox_Changed(object sender, SelectionChangedEventArgs e)
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

    private void TagCategoryBox_LostFocus(object sender, RoutedEventArgs e)
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

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
