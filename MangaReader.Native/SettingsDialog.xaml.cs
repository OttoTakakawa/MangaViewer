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

    private string? _pendingDataRoot;

    // 快捷键捕获
    private List<System.Windows.Input.Key> _nextKeys = new();
    private List<System.Windows.Input.Key> _prevKeys = new();
    private bool _capturingNext;
    private bool _capturingPrev;

    // 标记颜色预设
    private static readonly string[] PresetGroupA =
    [
        "#EF4444", "#F97316", "#EAB308", "#22C55E",
        "#14B8A6", "#3B82F6", "#6366F1", "#A855F7",
        "#EC4899", "#F43F5E", "#84CC16", "#06B6D4"
    ];
    private static readonly string[] PresetGroupB =
    [
        "#FB923C", "#FBBF24", "#4ADE80", "#2DD4BF",
        "#60A5FA", "#818CF8", "#C084FC", "#F472B6",
        "#FB7185", "#A3E635", "#22D3EE", "#E879F9"
    ];

    public SettingsDialog(AppStorage storage, LibraryDatabase database)
    {
        InitializeComponent();
        _storage = storage;
        _database = database;
        LoadCurrentSettings();
        DoublePageGapSlider.ValueChanged += DoublePageGapSlider_ValueChanged;
        PreviewKeyDown += SettingsDialog_PreviewKeyDown;
    }

    private void LoadCurrentSettings()
    {
        // 通用
        PrivacyModeCheckBox.IsChecked = _database.LoadSetting("app.privacy_mode") == "1";
        CatalogDeleteCheckBox.IsChecked = _database.LoadSetting("app.catalog_delete_source_enabled", "1") == "1";

        // 阅读
        var shortcuts = _database.LoadShortcuts();
        if (shortcuts.TryGetValue("reader.next", out var next))
            _nextKeys = ParseKeys(next);
        if (shortcuts.TryGetValue("reader.previous", out var prev))
            _prevKeys = ParseKeys(prev);
        UpdateShortcutButtons();
        if (shortcuts.TryGetValue("reader.wheelmode", out var wheel) && int.TryParse(wheel, out var wheelIdx))
            WheelModeComboBox.SelectedIndex = Math.Clamp(wheelIdx, 0, 2);
        if (shortcuts.TryGetValue("reader.qualitymode", out var quality))
            QualityModeComboBox.SelectedIndex = quality == "Performance" ? 1 : 0;
        if (shortcuts.TryGetValue("reader.doublepage.gap", out var gapStr) &&
            double.TryParse(gapStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gap))
            DoublePageGapSlider.Value = Math.Clamp(gap, 0, 80);

        // 数据
        DataRootTextBox.Text = _storage.Root;

        // 标记颜色
        BuildColorSwatches();
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedIndex < 0 || SectionGeneral is null) return;
        SectionGeneral.Visibility = NavList.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        SectionReading.Visibility = NavList.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        SectionData.Visibility = NavList.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        SectionTags.Visibility = NavList.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        SectionDanger.Visibility = NavList.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DoublePageGapSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DoublePageGapLabel is not null)
            DoublePageGapLabel.Text = ((int)e.NewValue).ToString();
    }

    // --- 快捷键捕获 ---

    private void UpdateShortcutButtons()
    {
        NextShortcutButton.Content = _nextKeys.Count > 0 ? string.Join(" + ", _nextKeys) : "点击设置";
        PrevShortcutButton.Content = _prevKeys.Count > 0 ? string.Join(" + ", _prevKeys) : "点击设置";
        CheckShortcutConflict();
    }

    private void CheckShortcutConflict()
    {
        var conflict = _nextKeys.Count > 0 && _prevKeys.Count > 0 && _nextKeys.Intersect(_prevKeys).Any();
        NextShortcutConflict.Visibility = conflict ? Visibility.Visible : Visibility.Collapsed;
        PrevShortcutConflict.Visibility = conflict ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NextShortcutCapture_Click(object sender, RoutedEventArgs e)
    {
        _capturingNext = true;
        _capturingPrev = false;
        NextShortcutButton.Content = "按下按键...";
        NextShortcutButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDB, 0xEA, 0xFE));
        PrevShortcutButton.ClearValue(BackgroundProperty);
        Focus();
    }

    private void PrevShortcutCapture_Click(object sender, RoutedEventArgs e)
    {
        _capturingPrev = true;
        _capturingNext = false;
        PrevShortcutButton.Content = "按下按键...";
        PrevShortcutButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDB, 0xEA, 0xFE));
        NextShortcutButton.ClearValue(BackgroundProperty);
        Focus();
    }

    private void SettingsDialog_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_capturingNext && !_capturingPrev) return;

        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin
            or System.Windows.Input.Key.ImeProcessed or System.Windows.Input.Key.ImeAccept
            or System.Windows.Input.Key.ImeConvert or System.Windows.Input.Key.ImeNonConvert
            or System.Windows.Input.Key.ImeModeChange)
            return;

        var target = _capturingNext ? _nextKeys : _prevKeys;

        if (target.Count >= 3)
            target.Clear();

        target.Add(key);

        if (_capturingNext)
        {
            _capturingNext = false;
            NextShortcutButton.ClearValue(BackgroundProperty);
        }
        else
        {
            _capturingPrev = false;
            PrevShortcutButton.ClearValue(BackgroundProperty);
        }

        UpdateShortcutButtons();
        ShortcutsChanged = true;
        e.Handled = true;
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

    // --- 标记分区 ---

    private void BuildColorSwatches()
    {
        BuildSwatchRow(ColorGroupA, PresetGroupA);
        BuildSwatchRow(ColorGroupB, PresetGroupB);
    }

    private static void BuildSwatchRow(System.Windows.Controls.Panel container, string[] colors)
    {
        container.Children.Clear();
        foreach (var color in colors)
        {
            var border = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(6),
                Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)),
                Margin = new Thickness(0, 0, 8, 8),
                ToolTip = color
            };
            container.Children.Add(border);
        }
    }

    // --- 危险分区 ---

    private void ClearAllBookmarks_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "确定清除所有书籍的页标记吗？此操作不可恢复。",
            "清除标记",
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
            "确定重置所有设置为默认值吗？\n\n将重置：隐私模式、快捷键、阅读器偏好、密码。\n不会影响数据根目录和数据库。",
            "重置设置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _database.SaveSetting("app.privacy_mode", "0");
        _database.SaveSetting("app.delete_source_password", "0309");
        _database.SaveSetting("app.catalog_delete_source_enabled", "1");
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
        if (_nextKeys.Count > 0 && _prevKeys.Count > 0)
        {
            _database.SaveShortcut("reader.next", FormatKeys(_nextKeys));
            _database.SaveShortcut("reader.previous", FormatKeys(_prevKeys));
            ShortcutsChanged = true;
        }
        _database.SaveShortcut("reader.wheelmode", WheelModeComboBox.SelectedIndex.ToString());
        _database.SaveShortcut("reader.qualitymode", QualityModeComboBox.SelectedIndex == 1 ? "Performance" : "Quality");
        _database.SaveShortcut("reader.doublepage.gap", DoublePageGapSlider.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));

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

    // --- 工具方法 ---

    private static List<System.Windows.Input.Key> ParseKeys(string text)
    {
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => Enum.TryParse<System.Windows.Input.Key>(k, true, out var key) ? key : System.Windows.Input.Key.None)
            .Where(k => k != System.Windows.Input.Key.None)
            .ToList();
    }

    private static string FormatKeys(List<System.Windows.Input.Key> keys)
    {
        return string.Join(",", keys);
    }
}

public enum SettingsAction
{
    None,
    OpenBackupFolder,
    OpenDataFolder,
    CreateBackup,
    OpenDataSafety,
    ClearAllBookmarks
}
