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
    public bool WaterfallRightClickChanged { get; private set; }
    public bool LibraryExitConfirmChanged { get; private set; }
    public bool ThemeChanged { get; private set; }
    public bool CoverQualityChanged { get; private set; }
    public SettingsAction RequestedAction { get; private set; } = SettingsAction.None;

    private string? _pendingDataRoot;
    private bool _hasChanges;
    private bool _forceClose;
    private bool _loading;

    // 快捷键：5个功能 × 3个槽位
    private readonly System.Windows.Input.Key[,] _keySlots = new System.Windows.Input.Key[5, 3];
    private bool _capturing;
    private int _captureRow;
    private int _captureCol;
    private System.Windows.Controls.Button? _captureButton;

    // 快捷键功能名
    private static readonly string[] KeyFunctionNames = ["next", "prev", "fullscreen", "hideui", "pagination"];
    private static readonly string[] KeySettingKeys = [
        "reader.next", "reader.previous", "reader.key.fullscreen",
        "reader.key.hideui", "reader.key.pagination"
    ];
    private static readonly string[] KeyDefaultValues = [
        "Right,Space", "Left", "W", "D", "S"
    ];

    // 标记颜色预设
    private static readonly string[] PresetGroupA =
    [
        "#EF4444", "#F97316", "#EAB308", "#22C55E",
        "#14B8A6", "#3B82F6", "#6366F1", "#A855F7",
        "#EC4899", "#F43F5E", "#84CC16", "#06B6D4"
    ];
    private static readonly string[] PresetGroupB =
    [
        "#991B1B", "#9A3412", "#854D0E", "#166534",
        "#115E59", "#1E3A8A", "#3730A3", "#581C87",
        "#831843", "#9F1239", "#365314", "#164E63"
    ];
    private static readonly string[] PresetGroupC =
    [
        "#E879A0", "#F0946C", "#E6B84A", "#7FB884",
        "#5BB5A2", "#6A9FD8", "#8B8EC9", "#B08DC2",
        "#D98CB8", "#E07A6A", "#A3C565", "#54B8C4"
    ];
    private static readonly string[] PresetGroupD =
    [
        "#C0392B", "#D35400", "#D4AC0D", "#1E8449",
        "#1A5276", "#2E4085", "#6C3483", "#A93276",
        "#C0395A", "#D4553A", "#27AE60", "#16A085"
    ];

    public SettingsDialog(AppStorage storage, LibraryDatabase database)
    {
        _loading = true;
        InitializeComponent();
        _storage = storage;
        _database = database;
        LoadCurrentSettings();
        DoublePageGapSlider.ValueChanged += DoublePageGapSlider_ValueChanged;
        PreviewKeyDown += SettingsDialog_PreviewKeyDown;
        _loading = false;
    }

    private void LoadCurrentSettings()
    {
        // 通用
        PrivacyModeCheckBox.IsChecked = _database.LoadSetting("app.privacy_mode") == "1";
        CatalogDeleteCheckBox.IsChecked = _database.LoadSetting("app.catalog_delete_source_enabled", "1") == "1";
        WaterfallRightClickCheckBox.IsChecked = _database.LoadSetting("app.waterfall_right_click", "0") == "1";
        TagClickFilterCheckBox.IsChecked = _database.LoadSetting("app.tag_click_filter_enabled", "1") == "1";
        TagDragAssignCheckBox.IsChecked = _database.LoadSetting("app.tag_drag_assign_enabled", "1") == "1";
        LibraryExitConfirmCheckBox.IsChecked = _database.LoadSetting("app.library_exit_confirm", "1") == "1";
        CoverQualityComboBox.SelectedIndex = _database.LoadSetting(CoverQualitySettings.SettingKey, CoverQualitySettings.DefaultValue) switch
        {
            "low" => 0,
            "high" => 2,
            _ => 1
        };

        // 主题
        var theme = _database.LoadSetting("app.theme", "Warm");
        switch (theme)
        {
            case "Light": ThemeLightRadio.IsChecked = true; break;
            case "Dark": ThemeDarkRadio.IsChecked = true; break;
            default: ThemeWarmRadio.IsChecked = true; break;
        }

        // 快捷键
        for (var i = 0; i < 5; i++)
        {
            var saved = _database.LoadSetting(KeySettingKeys[i], KeyDefaultValues[i]);
            var keys = ParseKeys(saved);
            for (var j = 0; j < 3; j++)
                _keySlots[i, j] = j < keys.Count ? keys[j] : System.Windows.Input.Key.None;
        }
        RefreshAllKeyButtons();

        // 阅读偏好
        var shortcuts = _database.LoadShortcuts();
        if (shortcuts.TryGetValue("reader.wheelmode", out var wheel) && int.TryParse(wheel, out var wheelIdx))
            WheelModeComboBox.SelectedIndex = Math.Clamp(wheelIdx, 0, 2);
        if (shortcuts.TryGetValue("reader.qualitymode", out var quality))
            QualityModeComboBox.SelectedIndex = quality == "Performance" ? 1 : 0;
        if (shortcuts.TryGetValue("reader.doublepage.gap", out var gapStr) &&
            double.TryParse(gapStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gap))
            DoublePageGapSlider.Value = Math.Clamp(gap, 0, 80);

        // 数据
        DataRootTextBox.Text = _storage.Root;

        // 标记颜色组
        var savedGroup = _database.LoadSetting("mark.color_group", "A");
        ColorGroupARadio.IsChecked = savedGroup == "A" || savedGroup != "B" && savedGroup != "C" && savedGroup != "D";
        ColorGroupBRadio.IsChecked = savedGroup == "B";
        ColorGroupCRadio.IsChecked = savedGroup == "C";
        ColorGroupDRadio.IsChecked = savedGroup == "D";
        RefreshColorSwatches();

        _hasChanges = false;
        UnsavedHint.Visibility = Visibility.Collapsed;
    }

    private void MarkChanged()
    {
        _hasChanges = true;
        UnsavedHint.Visibility = Visibility.Visible;
    }

    // --- 关闭守卫 ---

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose || !_hasChanges) return;
        var result = System.Windows.MessageBox.Show(
            "有未保存的更改，确定放弃吗？", "放弃更改",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    // --- 导航 ---

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedIndex < 0 || SectionGeneral is null) return;
        SectionGeneral.Visibility = NavList.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        SectionReading.Visibility = NavList.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        SectionData.Visibility = NavList.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        SectionTags.Visibility = NavList.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        SectionDanger.Visibility = NavList.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || ThemeWarmRadio is null) return;
        var theme = ThemeLightRadio.IsChecked == true ? "Light"
            : ThemeDarkRadio.IsChecked == true ? "Dark"
            : "Warm";
        App.ApplyTheme(theme);
        _hasChanges = true;
    }

    // --- 快捷键捕获 ---

    private void RefreshAllKeyButtons()
    {
        for (var i = 0; i < 5; i++)
        {
            var buttons = GetKeyButtons(i);
            for (var j = 0; j < 3; j++)
            {
                var key = _keySlots[i, j];
                buttons[j].Content = key == System.Windows.Input.Key.None ? "—" : FormatKeyName(key);
            }
        }
        CheckKeyConflicts();
    }

    private System.Windows.Controls.Button[] GetKeyButtons(int row) => row switch
    {
        0 => [NextKey1, NextKey2, NextKey3],
        1 => [PrevKey1, PrevKey2, PrevKey3],
        2 => [FsKey1, FsKey2, FsKey3],
        3 => [HideKey1, HideKey2, HideKey3],
        4 => [PagKey1, PagKey2, PagKey3],
        _ => []
    };

    private void KeySlot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string tag) return;
        var parts = tag.Split(',');
        var row = Array.IndexOf(KeyFunctionNames, parts[0]);
        var col = int.Parse(parts[1]);
        if (row < 0) return;

        // 如果正在捕获同一个按钮，清除它
        if (_capturing && _captureRow == row && _captureCol == col)
        {
            _keySlots[row, col] = System.Windows.Input.Key.None;
            _capturing = false;
            _captureButton?.ClearValue(BackgroundProperty);
            _captureButton = null;
            RefreshAllKeyButtons();
            MarkChanged();
            return;
        }

        // 清除之前的捕获状态
        _captureButton?.ClearValue(BackgroundProperty);

        _capturing = true;
        _captureRow = row;
        _captureCol = col;
        _captureButton = btn;
        btn.Content = "按下按键...";
        btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDB, 0xEA, 0xFE));
    }

    private void SettingsDialog_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_capturing) return;

        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin
            or System.Windows.Input.Key.ImeProcessed or System.Windows.Input.Key.ImeAccept
            or System.Windows.Input.Key.ImeConvert or System.Windows.Input.Key.ImeNonConvert
            or System.Windows.Input.Key.ImeModeChange)
            return;

        // Escape 取消捕获
        if (key == System.Windows.Input.Key.Escape)
        {
            _capturing = false;
            _captureButton?.ClearValue(BackgroundProperty);
            _captureButton = null;
            RefreshAllKeyButtons();
            e.Handled = true;
            return;
        }

        _keySlots[_captureRow, _captureCol] = key;
        _capturing = false;
        _captureButton?.ClearValue(BackgroundProperty);
        _captureButton = null;
        RefreshAllKeyButtons();
        MarkChanged();
        e.Handled = true;
    }

    private void CheckKeyConflicts()
    {
        var allKeys = new List<(System.Windows.Input.Key key, int row, int col)>();
        for (var i = 0; i < 5; i++)
            for (var j = 0; j < 3; j++)
                if (_keySlots[i, j] != System.Windows.Input.Key.None)
                    allKeys.Add((_keySlots[i, j], i, j));

        var hasConflict = false;
        for (var a = 0; a < allKeys.Count; a++)
            for (var b = a + 1; b < allKeys.Count; b++)
                if (allKeys[a].key == allKeys[b].key && allKeys[a].row != allKeys[b].row)
                    hasConflict = true;

        KeyConflictWarning.Visibility = hasConflict ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- 标记颜色 ---

    private void ColorGroup_Checked(object sender, RoutedEventArgs e)
    {
        if (ColorSwatchPanel is null) return;
        RefreshColorSwatches();
        MarkChanged();
    }

    private void RefreshColorSwatches()
    {
        if (ColorSwatchPanel is null) return;
        var colors = ColorGroupBRadio.IsChecked == true ? PresetGroupB
            : ColorGroupCRadio.IsChecked == true ? PresetGroupC
            : ColorGroupDRadio.IsChecked == true ? PresetGroupD
            : PresetGroupA;
        ColorSwatchPanel.Children.Clear();
        foreach (var color in colors)
        {
            var border = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(6),
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)),
                Margin = new Thickness(0, 0, 8, 8),
                ToolTip = color
            };
            ColorSwatchPanel.Children.Add(border);
        }
    }

    // --- 通用分区 ---

    private void PrivacyModeCheckBox_Changed(object sender, RoutedEventArgs e) { if (UnsavedHint is not null) MarkChanged(); }
    private void CatalogDeleteCheckBox_Changed(object sender, RoutedEventArgs e) { if (UnsavedHint is not null) MarkChanged(); }
    private void WaterfallRightClickCheckBox_Changed(object sender, RoutedEventArgs e) { if (UnsavedHint is not null) MarkChanged(); }
    private void TagClickFilterCheckBox_Changed(object sender, RoutedEventArgs e) { if (UnsavedHint is not null) MarkChanged(); }
    private void TagDragAssignCheckBox_Changed(object sender, RoutedEventArgs e) { if (UnsavedHint is not null) MarkChanged(); }
    private void LibraryExitConfirmCheckBox_Changed(object sender, RoutedEventArgs e) { if (UnsavedHint is not null) MarkChanged(); }
    private void CoverQualityComboBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || UnsavedHint is null)
        {
            return;
        }

        MarkChanged();
    }

    private void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        var current = _database.LoadSetting("app.permission_password", "0309");
        var oldInput = Interaction.InputBox("请输入当前密码：", "验证旧密码", "");
        if (oldInput != current)
        {
            System.Windows.MessageBox.Show("密码不正确。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var newInput = Interaction.InputBox("请输入新密码：", "设置新密码", "");
        if (string.IsNullOrEmpty(newInput)) return;
        _database.SaveSetting("app.permission_password", newInput);
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
        MarkChanged();
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = SettingsAction.OpenBackupFolder;
        _forceClose = true;
        DialogResult = true;
        Close();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = SettingsAction.OpenDataFolder;
        _forceClose = true;
        DialogResult = true;
        Close();
    }

    private void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = SettingsAction.CreateBackup;
        _forceClose = true;
        DialogResult = true;
        Close();
    }

    private void OpenDataSafety_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = SettingsAction.OpenDataSafety;
        _forceClose = true;
        DialogResult = true;
        Close();
    }

    private void ViewActivityHistory_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = SettingsAction.ViewActivityHistory;
        _forceClose = true;
        DialogResult = true;
        Close();
    }

    private void RunLibraryHealthCheck_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = SettingsAction.RunLibraryHealthCheck;
        _forceClose = true;
        DialogResult = true;
        Close();
    }

    private void RunDuplicateCheck_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = SettingsAction.RunDuplicateCheck;
        _forceClose = true;
        DialogResult = true;
        Close();
    }

    private void OpenReverseOrganize_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = SettingsAction.OpenReverseOrganize;
        _forceClose = true;
        DialogResult = true;
        Close();
    }

    private void ClearCoverCache_Click(object sender, RoutedEventArgs e)
    {
        RequestedAction = SettingsAction.ClearCoverCache;
        _forceClose = true;
        DialogResult = true;
        Close();
    }

    // --- 危险分区 ---

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "确定重置所有设置为默认值吗？\n\n将重置：隐私模式、快捷键、阅读器偏好、密码。\n不会影响数据根目录和数据库。",
            "重置设置", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _database.SaveSetting("app.privacy_mode", "0");
        _database.SaveSetting("app.permission_password", "0309");
        _database.SaveSetting("app.catalog_delete_source_enabled", "1");
        _database.SaveSetting("app.waterfall_right_click", "0");
        _database.SaveSetting("app.tag_click_filter_enabled", "1");
        _database.SaveSetting("app.tag_drag_assign_enabled", "1");
        _database.SaveSetting("app.library_exit_confirm", "1");
        _database.SaveSetting(CoverQualitySettings.SettingKey, CoverQualitySettings.DefaultValue);
        _database.SaveSetting("mark.color_group", "A");
        for (var i = 0; i < 5; i++)
            _database.SaveSetting(KeySettingKeys[i], KeyDefaultValues[i]);
        _database.SaveShortcut("reader.wheelmode", "0");
        _database.SaveShortcut("reader.qualitymode", "Quality");
        _database.SaveShortcut("reader.doublepage.gap", "8");

        PrivacyModeChanged = true;
        ShortcutsChanged = true;
        WaterfallRightClickChanged = true;
        CoverQualityChanged = true;

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

        // 瀑布流右键
        var newWrc = WaterfallRightClickCheckBox.IsChecked == true;
        var oldWrc = _database.LoadSetting("app.waterfall_right_click", "0") == "1";
        if (newWrc != oldWrc)
        {
            _database.SaveSetting("app.waterfall_right_click", newWrc ? "1" : "0");
            WaterfallRightClickChanged = true;
        }

        // Tag 交互
        _database.SaveSetting("app.tag_click_filter_enabled", TagClickFilterCheckBox.IsChecked == true ? "1" : "0");
        _database.SaveSetting("app.tag_drag_assign_enabled", TagDragAssignCheckBox.IsChecked == true ? "1" : "0");

        // 关闭漫画库确认提示
        var newExitConfirm = LibraryExitConfirmCheckBox.IsChecked == true;
        var oldExitConfirm = _database.LoadSetting("app.library_exit_confirm", "1") == "1";
        if (newExitConfirm != oldExitConfirm)
        {
            _database.SaveSetting("app.library_exit_confirm", newExitConfirm ? "1" : "0");
            LibraryExitConfirmChanged = true;
        }

        // 封面质量
        var newCoverQuality = CoverQualityComboBox.SelectedIndex switch
        {
            0 => "low",
            2 => "high",
            _ => "standard"
        };
        var oldCoverQuality = _database.LoadSetting(CoverQualitySettings.SettingKey, CoverQualitySettings.DefaultValue);
        if (!string.Equals(newCoverQuality, oldCoverQuality, StringComparison.OrdinalIgnoreCase))
        {
            _database.SaveSetting(CoverQualitySettings.SettingKey, newCoverQuality);
            CoverQualityChanged = true;
        }

        // 快捷键
        for (var i = 0; i < 5; i++)
        {
            var keys = new List<System.Windows.Input.Key>();
            for (var j = 0; j < 3; j++)
                if (_keySlots[i, j] != System.Windows.Input.Key.None)
                    keys.Add(_keySlots[i, j]);
            _database.SaveShortcut(KeySettingKeys[i], FormatKeys(keys));
        }
        ShortcutsChanged = true;

        // 阅读偏好
        _database.SaveShortcut("reader.wheelmode", WheelModeComboBox.SelectedIndex.ToString());
        _database.SaveShortcut("reader.qualitymode", QualityModeComboBox.SelectedIndex == 1 ? "Performance" : "Quality");
        _database.SaveShortcut("reader.doublepage.gap", DoublePageGapSlider.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));

        // 标记颜色组
        var markColorGroup = ColorGroupBRadio.IsChecked == true ? "B"
            : ColorGroupCRadio.IsChecked == true ? "C"
            : ColorGroupDRadio.IsChecked == true ? "D"
            : "A";
        _database.SaveSetting("mark.color_group", markColorGroup);

        // 数据根目录
        if (_pendingDataRoot is not null)
            AppStorage.SaveCustomRoot(_pendingDataRoot);

        // 主题
        var newTheme = ThemeLightRadio.IsChecked == true ? "Light"
            : ThemeDarkRadio.IsChecked == true ? "Dark"
            : "Warm";
        var oldTheme = _database.LoadSetting("app.theme", "Warm");
        if (newTheme != oldTheme)
        {
            _database.SaveSetting("app.theme", newTheme);
            App.ApplyTheme(newTheme);
            ThemeChanged = true;
        }

        _hasChanges = false;
        _forceClose = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _forceClose = true;
        DialogResult = false;
        Close();
    }

    // --- 工具方法 ---

    private void DoublePageGapSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DoublePageGapLabel is not null)
            DoublePageGapLabel.Text = ((int)e.NewValue).ToString();
    }

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

    private static string FormatKeyName(System.Windows.Input.Key key) => key switch
    {
        System.Windows.Input.Key.Space => "Space",
        System.Windows.Input.Key.Left => "←",
        System.Windows.Input.Key.Right => "→",
        System.Windows.Input.Key.Up => "↑",
        System.Windows.Input.Key.Down => "↓",
        System.Windows.Input.Key.OemComma => ",",
        System.Windows.Input.Key.OemPeriod => ".",
        _ => key.ToString()
    };
}

public enum SettingsAction
{
    None,
    OpenBackupFolder,
    OpenDataFolder,
    CreateBackup,
    OpenDataSafety,
    ViewActivityHistory,
    RunLibraryHealthCheck,
    RunDuplicateCheck,
    OpenReverseOrganize,
    ClearCoverCache
}
