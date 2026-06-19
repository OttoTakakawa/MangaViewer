using MangaReader.Native.Services;
using Microsoft.VisualBasic;
using System.Windows;
using System.Windows.Controls;
using WinForms = System.Windows.Forms;

namespace MangaReader.Native;

public partial class SettingsDialog : Window
{
    private readonly AppStorage _storage;
    private readonly LibraryDatabase _database;

    public bool NeedsRestart { get; private set; }
    public bool PrivacyModeChanged { get; private set; }
    public bool ShortcutsChanged { get; private set; }
    public SettingsAction RequestedAction { get; private set; } = SettingsAction.None;

    private readonly List<string> _colors = new();
    private string? _pendingDataRoot;

    public SettingsDialog(AppStorage storage, LibraryDatabase database)
    {
        InitializeComponent();
        _storage = storage;
        _database = database;
        LoadCurrentSettings();
        DoublePageGapSlider.ValueChanged += DoublePageGapSlider_ValueChanged;
    }

    private void LoadCurrentSettings()
    {
        // 通用
        PrivacyModeCheckBox.IsChecked = _database.LoadSetting("app.privacy_mode") == "1";
        CatalogDeleteCheckBox.IsChecked = _database.LoadSetting("app.catalog_delete_source_enabled", "1") == "1";

        // 阅读
        var shortcuts = _database.LoadShortcuts();
        if (shortcuts.TryGetValue("reader.next", out var next))
            NextShortcutTextBox.Text = next;
        if (shortcuts.TryGetValue("reader.previous", out var prev))
            PrevShortcutTextBox.Text = prev;
        if (shortcuts.TryGetValue("reader.wheelmode", out var wheel) && int.TryParse(wheel, out var wheelIdx))
            WheelModeComboBox.SelectedIndex = Math.Clamp(wheelIdx, 0, 2);
        if (shortcuts.TryGetValue("reader.qualitymode", out var quality))
            QualityModeComboBox.SelectedIndex = quality == "Performance" ? 1 : 0;
        if (shortcuts.TryGetValue("reader.doublepage.gap", out var gapStr) &&
            double.TryParse(gapStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gap))
            DoublePageGapSlider.Value = Math.Clamp(gap, 0, 80);

        // 数据
        DataRootTextBox.Text = _storage.Root;

        // 标签
        _colors.Clear();
        foreach (var c in _database.LoadSetting("tag.custom_colors")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _colors.Add(c);
        }
        ColorList.ItemsSource = _colors.ToList();
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedIndex < 0) return;
        SectionGeneral.Visibility = NavList.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        SectionReading.Visibility = NavList.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        SectionData.Visibility = NavList.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        SectionTags.Visibility = NavList.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        SectionDanger.Visibility = NavList.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DoublePageGapSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        DoublePageGapLabel.Text = ((int)e.NewValue).ToString();
    }

    // --- 通用分区 ---

    private void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var current = _database.LoadSetting("app.delete_source_password", "0309");
        var oldInput = Interaction.InputBox("请输入当前密码：", "验证旧密码", "");
        if (oldInput != current)
        {
            System.Windows.MessageBox.Show("密码不正确。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var newInput = Interaction.InputBox("请输入新密码：", "设置新密码", "");
        if (string.IsNullOrEmpty(newInput))
        {
            return;
        }
        _database.SaveSetting("app.delete_source_password", newInput);
        System.Windows.MessageBox.Show("密码已更新。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // --- 数据分区 ---

    private void ChangeDataRoot_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择软件数据目录",
            SelectedPath = Directory.Exists(_storage.Root) ? _storage.Root : AppStorage.DefaultRoot
        };
        if (dialog.ShowDialog() != WinForms.DialogResult.OK) return;
        var selected = Path.GetFullPath(dialog.SelectedPath);
        DataRootTextBox.Text = selected;
        _pendingDataRoot = selected;
        NeedsRestart = true;
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = SettingsAction.OpenBackupFolder;
        DialogResult = true;
        Close();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = SettingsAction.OpenDataFolder;
        DialogResult = true;
        Close();
    }

    private void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = SettingsAction.CreateBackup;
        DialogResult = true;
        Close();
    }

    private void OpenDataSafety_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = SettingsAction.OpenDataSafety;
        DialogResult = true;
        Close();
    }

    private void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = SettingsAction.CheckUpdate;
        DialogResult = true;
        Close();
    }

    // --- 标签分区 ---

    private void AddColor_Click(object sender, RoutedEventArgs e)
    {
        var input = Interaction.InputBox("输入颜色值（如 #FF6B6B）：", "添加颜色", "#");
        if (string.IsNullOrWhiteSpace(input)) return;
        input = input.Trim();
        if (!input.StartsWith("#") || input.Length != 7) return;
        _colors.Add(input);
        ColorList.ItemsSource = _colors.ToList();
    }

    private void RemoveColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string color)
        {
            _colors.Remove(color);
            ColorList.ItemsSource = _colors.ToList();
        }
    }

    // --- 危险分区 ---

    private void ClearAllBookmarks_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "确定清除所有书籍的书签标记吗？此操作不可恢复。",
            "清除书签",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var input = Interaction.InputBox("请输入密码以确认：", "密码确认", "");
        var password = _database.LoadSetting("app.delete_source_password", "0309");
        if (input != password)
        {
            System.Windows.MessageBox.Show("密码不正确。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RequestedAction = SettingsAction.ClearAllBookmarks;
        DialogResult = true;
        Close();
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "确定重置所有设置为默认值吗？\n\n将重置：隐私模式、快捷键、阅读器偏好、标签颜色、密码。\n不会影响数据根目录和数据库。",
            "重置设置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _database.SaveSetting("app.privacy_mode", "0");
        _database.SaveSetting("app.delete_source_password", "0309");
        _database.SaveSetting("app.catalog_delete_source_enabled", "1");
        _database.SaveSetting("tag.custom_colors", "");
        _database.SaveShortcut("reader.next", "Right,Space");
        _database.SaveShortcut("reader.previous", "Left");
        _database.SaveShortcut("reader.wheelmode", "0");
        _database.SaveShortcut("reader.qualitymode", "Quality");
        _database.SaveShortcut("reader.doublepage.gap", "8");

        PrivacyModeChanged = true;
        ShortcutsChanged = true;

        System.Windows.MessageBox.Show("所有设置已重置为默认值。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        LoadCurrentSettings();
    }

    // --- 保存/取消 ---

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // 隐私模式
        var newPrivacy = PrivacyModeCheckBox.IsChecked == true;
        var oldPrivacy = _database.LoadSetting("app.privacy_mode") == "1";
        if (newPrivacy != oldPrivacy)
        {
            _database.SaveSetting("app.privacy_mode", newPrivacy ? "1" : "0");
            PrivacyModeChanged = true;
        }

        // 目录删除开关
        _database.SaveSetting("app.catalog_delete_source_enabled", CatalogDeleteCheckBox.IsChecked == true ? "1" : "0");

        // 快捷键
        var nextText = NextShortcutTextBox.Text.Trim();
        var prevText = PrevShortcutTextBox.Text.Trim();
        if (!string.IsNullOrEmpty(nextText) && !string.IsNullOrEmpty(prevText))
        {
            _database.SaveShortcut("reader.next", nextText);
            _database.SaveShortcut("reader.previous", prevText);
            ShortcutsChanged = true;
        }
        _database.SaveShortcut("reader.wheelmode", WheelModeComboBox.SelectedIndex.ToString());
        _database.SaveShortcut("reader.qualitymode", QualityModeComboBox.SelectedIndex == 1 ? "Performance" : "Quality");
        _database.SaveShortcut("reader.doublepage.gap", DoublePageGapSlider.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));

        // 标签颜色
        var colorValue = string.Join(";", _colors.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
        _database.SaveSetting("tag.custom_colors", colorValue);

        // 数据根目录
        if (_pendingDataRoot is not null)
        {
            AppStorage.SaveCustomRoot(_pendingDataRoot);
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

public enum SettingsAction
{
    None,
    OpenBackupFolder,
    OpenDataFolder,
    CreateBackup,
    OpenDataSafety,
    CheckUpdate,
    ClearAllBookmarks
}
