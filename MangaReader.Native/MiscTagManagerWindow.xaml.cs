using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using MangaReader.Native.Models;
using MangaReader.Native.Services;

namespace MangaReader.Native;

// 杂图 Tag 管理窗口。
// 与 MainWindow 主 Tag 管理完全独立，操作 misc_image_tags 表。
// 编辑/删除后通过回调通知 MainWindow 刷新 MiscTagService 缓存与已显示杂图的 TagChip 颜色。
// 单个 Tag 编辑复用 TagEditDialog（含名称/分类/颜色选择 UI），但 IsExclusive 字段被忽略。
public partial class MiscTagManagerWindow : Window
{
    private readonly LibraryDatabase _database;
    private readonly Action _onChanged;
    private readonly ObservableCollection<MiscTagRow> _rows = new();
    private List<MiscTagRow> _allRows = new();

    public MiscTagManagerWindow(LibraryDatabase database, Action onChanged)
    {
        InitializeComponent();
        _database = database;
        _onChanged = onChanged;
        TagList.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            Reload();
        };
    }

    // ===== 数据加载 =====

    private void Reload()
    {
        try
        {
            var records = _database.LoadMiscTags();
            _allRows = records
                .Select(r => new MiscTagRow
                {
                    Name = r.Name,
                    Category = string.IsNullOrEmpty(r.Category) ? "未分类" : r.Category,
                    RawCategory = r.Category ?? "",
                    Color = string.IsNullOrEmpty(r.Color) ? MiscTagService.GetColor(r.Name) : r.Color,
                    RawColor = r.Color ?? "",
                    UsageCount = _database.CountMiscTagUsage(r.Name)
                })
                .OrderBy(r => r.RawCategory, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            RefreshGroupFilter();
            ApplyFilter();
            UpdateStats();
        }
        catch (Exception ex)
        {
            AppLogger.Error("misc-tag-mgr-load", ex, "加载杂图 Tag 列表失败。");
            System.Windows.MessageBox.Show($"加载失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshGroupFilter()
    {
        var groups = _allRows
            .Select(r => r.RawCategory)
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var previous = GroupFilterBox.SelectedIndex > 0
            ? (GroupFilterBox.SelectedItem as ComboBoxItem)?.Tag as string
            : null;

        GroupFilterBox.Items.Clear();
        var allItem = new ComboBoxItem { Content = "全部分组", Tag = "" };
        GroupFilterBox.Items.Add(allItem);
        foreach (var g in groups)
        {
            GroupFilterBox.Items.Add(new ComboBoxItem { Content = g, Tag = g });
        }

        if (!string.IsNullOrEmpty(previous))
        {
            var match = GroupFilterBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals(i.Tag as string, previous, StringComparison.OrdinalIgnoreCase));
            GroupFilterBox.SelectedItem = match ?? allItem;
        }
        else
        {
            GroupFilterBox.SelectedItem = allItem;
        }
    }

    private void UpdateStats()
    {
        TotalCountText.Text = $"{_allRows.Count} 个";
        var groups = _allRows
            .Select(r => r.RawCategory)
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        GroupCountText.Text = $"{groups} 组";
    }

    private void ApplyFilter()
    {
        var keyword = (SearchBox.Text ?? "").Trim();
        var groupFilter = (GroupFilterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        _rows.Clear();
        foreach (var row in _allRows)
        {
            if (!string.IsNullOrEmpty(groupFilter) && !string.Equals(row.RawCategory, groupFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.IsNullOrEmpty(keyword) &&
                !row.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                !row.RawCategory.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            _rows.Add(row);
        }

        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===== 事件 =====

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void GroupFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
    }

    private void CreateTag_Click(object sender, RoutedEventArgs e)
    {
        // 走杂图专属对话框，与漫画 TagEditDialog 完全解耦：独立色板、无互斥、无关联漫画。
        var (existingCategories, categoryColors) = LoadMiscCategoryContext();
        var dialog = new MiscTagEditDialog(
            initialName: "",
            initialCategory: "未分类",
            initialColor: MiscTagService.PresetColors[0],
            isNew: true,
            existingCategories: existingCategories,
            categoryColors: categoryColors)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var name = dialog.TagName.Trim();
        var category = dialog.TagCategory.Trim();
        var color = dialog.SelectedColor;
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        try
        {
            _database.UpsertMiscTag(name, category, color);
            MiscTagService.UpsertLocal(name, category, color);
            _onChanged?.Invoke();
            Reload();
        }
        catch (Exception ex)
        {
            AppLogger.Error("misc-tag-create", ex, $"创建杂图 Tag 失败：{name}");
            System.Windows.MessageBox.Show($"创建失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MiscTagRow row }) return;

        var (existingCategories, categoryColors) = LoadMiscCategoryContext();
        var dialog = new MiscTagEditDialog(
            initialName: row.Name,
            initialCategory: string.IsNullOrEmpty(row.RawCategory) ? "未分类" : row.RawCategory,
            initialColor: row.Color,
            isNew: false,
            existingCategories: existingCategories,
            categoryColors: categoryColors,
            usageCount: row.UsageCount)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var newName = dialog.TagName.Trim();
        var newCategory = dialog.TagCategory.Trim();
        var newColor = dialog.SelectedColor;
        if (string.IsNullOrEmpty(newName))
        {
            return;
        }

        try
        {
            if (!string.Equals(newName, row.Name, StringComparison.Ordinal))
            {
                _database.RenameMiscTag(row.Name, newName);
                MiscTagService.RemoveLocal(row.Name);
                MiscTagService.UpsertLocal(newName, newCategory, newColor);
            }
            else
            {
                _database.UpsertMiscTag(newName, newCategory, newColor);
                MiscTagService.UpsertLocal(newName, newCategory, newColor);
            }

            _onChanged?.Invoke();
            Reload();
        }
        catch (Exception ex)
        {
            AppLogger.Error("misc-tag-edit", ex, $"编辑杂图 Tag 失败：{row.Name}");
            System.Windows.MessageBox.Show($"编辑失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // 从 misc_image_tags 表加载已有分类及其颜色，用于：
    // 1. 编辑对话框的分组下拉候选项
    // 2. 切换分组时自动套用同组颜色（与漫画 TagEditDialog 行为对齐，但数据源完全独立）
    private (IReadOnlyList<string> Categories, IReadOnlyDictionary<string, string> CategoryColors) LoadMiscCategoryContext()
    {
        try
        {
            var records = _database.LoadMiscTags();
            var categories = records
                .Select(r => r.Category ?? "")
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in records)
            {
                var cat = r.Category ?? "";
                if (string.IsNullOrWhiteSpace(cat))
                {
                    continue;
                }
                // 同组多个 tag 颜色可能不一致，取第一条非空作为代表色
                if (!colors.ContainsKey(cat) && !string.IsNullOrWhiteSpace(r.Color))
                {
                    colors[cat] = r.Color;
                }
            }
            return (categories, colors);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("misc-tag-ctx", $"加载杂图分类上下文失败：{ex.Message}");
            return (Array.Empty<string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MiscTagRow row }) return;

        var confirm = System.Windows.MessageBox.Show(
            $"确定要删除杂图 Tag「{row.Name}」吗？\n当前已被 {row.UsageCount} 张杂图引用，删除后这些杂图的 tags 字段中对应名称将保留为文本（不会被自动清理）。",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            _database.DeleteMiscTag(row.Name);
            MiscTagService.RemoveLocal(row.Name);
            _onChanged?.Invoke();
            Reload();
        }
        catch (Exception ex)
        {
            AppLogger.Error("misc-tag-delete", ex, $"删除杂图 Tag 失败：{row.Name}");
            System.Windows.MessageBox.Show($"删除失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

// 列表项数据模型
public sealed class MiscTagRow : INotifyPropertyChanged
{
    private string _name = "";
    private string _category = "未分类";
    private string _rawCategory = "";
    private string _color = "#C7B8EA";
    private string _rawColor = "";
    private int _usageCount;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public string Category
    {
        get => _category;
        set { _category = value; OnPropertyChanged(); }
    }

    public string RawCategory
    {
        get => _rawCategory;
        set { _rawCategory = value; OnPropertyChanged(); }
    }

    public string Color
    {
        get => _color;
        set { _color = value; OnPropertyChanged(); }
    }

    public string RawColor
    {
        get => _rawColor;
        set { _rawColor = value; OnPropertyChanged(); }
    }

    public int UsageCount
    {
        get => _usageCount;
        set { _usageCount = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
