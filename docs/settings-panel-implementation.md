# 设置面板 完整实现计划

> 本文档供 AI 直接执行，包含所有精确代码位置、方法签名、样式资源和实现步骤。

## 项目环境

- .NET 8 WPF，code-behind 模式（无 MVVM）
- 项目路径：`G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\`
- 编译命令（G: 盘有 obj 写入限制，中间路径必须指向 C: 盘）：
  ```
  dotnet build "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MangaReader.Native.csproj" -c Debug --nologo -p:IntermediateOutputPath="C:\Users\TAKAKAWA\AppData\Local\Temp\opencode\mv_obj\Debug\net8.0-windows\" -p:OutputPath="C:\Users\TAKAKAWA\AppData\Local\Temp\opencode\mv_bin\Debug\net8.0-windows\" -p:BaseIntermediateOutputPath="C:\Users\TAKAKAWA\AppData\Local\Temp\opencode\mv_baseint\"
  ```
- 打包命令：
  ```
  dotnet publish "...\MangaReader.Native.csproj" -c Release -o "...\_release" -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -p:IntermediateOutputPath="C:\...\mv_pub_obj\Release\net8.0-windows\" -p:BaseIntermediateOutputPath="C:\...\mv_pub_baseint\"
  ```

## 目标版本

`0.7.0`（新功能 + UI 重构）

---

## 一、新建文件

### 1. `SettingsDialog.xaml`

路径：`G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\SettingsDialog.xaml`

骨架参照 `DataSafetyDialog.xaml`（同目录），关键区别：
- 尺寸更大：`Width="820" Height="560"`
- 左侧导航 + 右侧内容布局
- 底部保存/取消按钮

```xml
<Window x:Class="MangaReader.Native.SettingsDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="设置"
        Width="820"
        Height="560"
        WindowStartupLocation="CenterOwner"
        ResizeMode="NoResize"
        WindowStyle="None"
        AllowsTransparency="True"
        Background="Transparent">
    <Grid Margin="22">
        <Border Style="{StaticResource AppDialogShadowBorder}"/>
        <Border CornerRadius="{StaticResource RadiusDialog}"
                Background="{StaticResource Brush.Surface}"
                BorderBrush="{StaticResource Brush.BorderSubtle}"
                BorderThickness="1"
                Padding="26"
                SnapsToDevicePixels="True">
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>

                <!-- 标题栏 -->
                <DockPanel>
                    <Button DockPanel.Dock="Right" Content="×" Click="Cancel_Click" Style="{StaticResource AppDialogCloseButton}"/>
                    <TextBlock Text="设置" Style="{StaticResource AppDialogTitleText}"/>
                </DockPanel>

                <!-- 左导航 + 右内容 -->
                <Grid Grid.Row="1" Margin="0,20,0,0">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="160"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>

                    <!-- 左侧导航 -->
                    <ListBox x:Name="NavList"
                             Background="Transparent"
                             BorderThickness="0"
                             SelectedIndex="0"
                             SelectionChanged="NavList_SelectionChanged">
                        <ListBoxItem Content="通用"/>
                        <ListBoxItem Content="阅读"/>
                        <ListBoxItem Content="数据"/>
                        <ListBoxItem Content="标签"/>
                        <ListBoxItem Content="危险"/>
                    </ListBox>

                    <!-- 右侧内容区：5 个 Grid 用 Visibility 切换 -->
                    <ScrollViewer Grid.Column="1" Margin="20,0,0,0" VerticalScrollBarVisibility="Auto">
                        <Grid>
                            <!-- 通用分区 -->
                            <StackPanel x:Name="SectionGeneral">
                                <!-- 隐私模式 -->
                                <!-- 删除源文件密码 -->
                                <!-- 目录中显示删除源文件 -->
                            </StackPanel>

                            <!-- 阅读分区 -->
                            <StackPanel x:Name="SectionReading" Visibility="Collapsed">
                                <!-- 下一页快捷键 -->
                                <!-- 上一页快捷键 -->
                                <!-- 默认滚轮模式 -->
                                <!-- 默认渲染质量 -->
                                <!-- 默认双页间距 -->
                            </StackPanel>

                            <!-- 数据分区 -->
                            <StackPanel x:Name="SectionData" Visibility="Collapsed">
                                <!-- 数据根目录 -->
                                <!-- 打开备份文件夹 -->
                                <!-- 手动备份 -->
                                <!-- 恢复备份 -->
                                <!-- 检查更新 -->
                            </StackPanel>

                            <!-- 标签分区 -->
                            <StackPanel x:Name="SectionTags" Visibility="Collapsed">
                                <!-- 自定义标签颜色 -->
                            </StackPanel>

                            <!-- 危险分区 -->
                            <StackPanel x:Name="SectionDanger" Visibility="Collapsed">
                                <!-- 清除所有书签 -->
                                <!-- 重置所有设置为默认 -->
                            </StackPanel>
                        </Grid>
                    </ScrollViewer>
                </Grid>

                <!-- 底部按钮 -->
                <DockPanel Grid.Row="2" Margin="0,20,0,0">
                    <Button DockPanel.Dock="Right" Content="保存" Click="Save_Click" Style="{StaticResource AppDialogButton}" Margin="8,0,0,0"/>
                    <Button DockPanel.Dock="Right" Content="取消" Click="Cancel_Click" Style="{StaticResource AppDialogGhostButton}"/>
                </DockPanel>
            </Grid>
        </Border>
    </Grid>
</Window>
```

### 2. `SettingsDialog.xaml.cs`

路径：`G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\SettingsDialog.xaml.cs`

```csharp
using MangaReader.Native.Services;
using System.Windows;
using System.Windows.Controls;

namespace MangaReader.Native;

public partial class SettingsDialog : Window
{
    private readonly AppStorage _storage;
    private readonly LibraryDatabase _database;

    // 回传：保存后需要主窗口执行的后续动作
    public bool NeedsRestart { get; private set; }
    public bool PrivacyModeChanged { get; private set; }
    public bool ShortcutsChanged { get; private set; }

    public SettingsDialog(AppStorage storage, LibraryDatabase database)
    {
        InitializeComponent();
        _storage = storage;
        _database = database;
        LoadCurrentSettings();
    }

    private void LoadCurrentSettings()
    {
        // 通用分区
        // 隐私模式
        var privacy = _database.LoadSetting("app.privacy_mode");
        // 设置 CheckBox/ToggleButton 状态

        // 删除源文件密码：不显示明文，只提供"修改"入口

        // 目录删除源文件开关
        var catalogDelete = _database.LoadSetting("app.catalog_delete_source_enabled", "1");

        // 阅读分区
        var shortcuts = _database.LoadShortcuts();
        // reader.next, reader.previous, reader.wheelmode, reader.qualitymode, reader.doublepage.gap

        // 数据分区
        // 显示 _storage.Root（只读）

        // 标签分区
        // 读取 tag.custom_colors

        // 危险分区：无需预加载
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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // 收集所有变更，批量保存
        // 隐私模式 → _database.SaveSetting("app.privacy_mode", ...)
        // 快捷键 → _database.SaveShortcut("reader.next", ...) 等
        // 标签颜色 → _database.SaveSetting("tag.custom_colors", ...)
        // 数据根目录 → AppStorage.SaveCustomRoot(...) → NeedsRestart = true

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
```

---

## 二、各分区详细实现

### 分区 1：通用

#### 1.1 隐私模式 ToggleButton

| 项目 | 值 |
|------|-----|
| 控件 | `CheckBox x:Name="PrivacyModeCheckBox"` |
| 存储 Key | `app.privacy_mode` |
| 存储表 | `app_settings` |
| 读取 | `_database.LoadSetting("app.privacy_mode")` → `"1"` 为开 |
| 保存 | `_database.SaveSetting("app.privacy_mode", value ? "1" : "0")` |
| 回传 | `PrivacyModeChanged = true` |

XAML：
```xml
<CheckBox x:Name="PrivacyModeCheckBox" Content="隐私模式（隐藏所有作品封面）" FontSize="14" Margin="0,0,0,16"/>
```

LoadCurrentSettings 中：
```csharp
PrivacyModeCheckBox.IsChecked = _database.LoadSetting("app.privacy_mode") == "1";
```

Save_Click 中：
```csharp
var newPrivacy = PrivacyModeCheckBox.IsChecked == true;
var oldPrivacy = _database.LoadSetting("app.privacy_mode") == "1";
if (newPrivacy != oldPrivacy)
{
    _database.SaveSetting("app.privacy_mode", newPrivacy ? "1" : "0");
    PrivacyModeChanged = true;
}
```

#### 1.2 删除源文件密码

| 项目 | 值 |
|------|-----|
| 控件 | `Button Content="修改密码"` + 弹出 InputBox |
| 存储 Key | `app.delete_source_password` |
| 默认值 | `"0309"` |

XAML：
```xml
<StackPanel Orientation="Horizontal" Margin="0,0,0,16">
    <TextBlock Text="删除源文件密码" Style="{StaticResource AppDialogLabelText}" VerticalAlignment="Center" Margin="0,0,12,0"/>
    <Button x:Name="ChangePasswordButton" Content="修改密码" Click="ChangePassword_Click" Style="{StaticResource AppDialogGhostButton}"/>
</StackPanel>
```

C#（需 `using Microsoft.VisualBasic;`）：
```csharp
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
```

#### 1.3 目录中显示删除源文件

| 项目 | 值 |
|------|-----|
| 控件 | `CheckBox x:Name="CatalogDeleteCheckBox"` |
| 存储 Key | `app.catalog_delete_source_enabled` |
| 默认值 | `"1"`（开启） |

XAML：
```xml
<CheckBox x:Name="CatalogDeleteCheckBox" Content="在详情页目录右键中显示「删除源文件」" FontSize="14"/>
```

LoadCurrentSettings 中：
```csharp
CatalogDeleteCheckBox.IsChecked = _database.LoadSetting("app.catalog_delete_source_enabled", "1") == "1";
```

Save_Click 中：
```csharp
_database.SaveSetting("app.catalog_delete_source_enabled", CatalogDeleteCheckBox.IsChecked == true ? "1" : "0");
```

---

### 分区 2：阅读

所有设置存入 `shortcuts` 表，用 `_database.SaveShortcut(key, value)` / `_database.LoadShortcuts()` 读写。

> 注意：阅读器顶栏保留即时调节入口，设置面板只管"默认值"。

#### 2.1 下一页快捷键

| 项目 | 值 |
|------|-----|
| 控件 | `TextBox x:Name="NextShortcutTextBox"` |
| 存储 Key | `reader.next` |
| 默认值 | `"Right,Space"` |

#### 2.2 上一页快捷键

| 项目 | 值 |
|------|-----|
| 控件 | `TextBox x:Name="PrevShortcutTextBox"` |
| 存储 Key | `reader.previous` |
| 默认值 | `"Left"` |

XAML（两个快捷键）：
```xml
<StackPanel Margin="0,0,0,16">
    <TextBlock Text="下一页快捷键" Style="{StaticResource AppDialogLabelText}" Margin="0,0,0,6"/>
    <TextBox x:Name="NextShortcutTextBox" Style="{StaticResource AppDialogTextBox}" Text="Right,Space"/>
    <TextBlock Text="多个键用英文逗号分隔，如 Right,Space" Foreground="{StaticResource Brush.TextMuted}" FontSize="11" Margin="0,6,0,16"/>
    <TextBlock Text="上一页快捷键" Style="{StaticResource AppDialogLabelText}" Margin="0,0,0,6"/>
    <TextBox x:Name="PrevShortcutTextBox" Style="{StaticResource AppDialogTextBox}" Text="Left"/>
</StackPanel>
```

LoadCurrentSettings 中：
```csharp
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
```

Save_Click 中：
```csharp
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
```

#### 2.3 默认滚轮模式

```xml
<StackPanel Margin="0,0,0,16">
    <TextBlock Text="默认滚轮模式" Style="{StaticResource AppDialogLabelText}" Margin="0,0,0,6"/>
    <ComboBox x:Name="WheelModeComboBox" Height="40">
        <ComboBoxItem Content="滚轮翻页"/>
        <ComboBoxItem Content="滚轮缩放"/>
        <ComboBoxItem Content="滚轮滚动"/>
    </ComboBox>
</StackPanel>
```

#### 2.4 默认渲染质量

```xml
<StackPanel Margin="0,0,0,16">
    <TextBlock Text="默认渲染质量" Style="{StaticResource AppDialogLabelText}" Margin="0,0,0,6"/>
    <ComboBox x:Name="QualityModeComboBox" Height="40">
        <ComboBoxItem Content="质量"/>
        <ComboBoxItem Content="性能"/>
    </ComboBox>
</StackPanel>
```

#### 2.5 默认双页间距

```xml
<StackPanel Margin="0,0,0,16">
    <TextBlock Text="默认双页间距" Style="{StaticResource AppDialogLabelText}" Margin="0,0,0,6"/>
    <Slider x:Name="DoublePageGapSlider" Minimum="0" Maximum="80" Value="8" TickPlacement="None"/>
    <TextBlock x:Name="DoublePageGapLabel" Text="8" Foreground="{StaticResource Brush.TextMuted}" FontSize="12" Margin="0,6,0,0"/>
</StackPanel>
```

C# 中处理 Slider.ValueChanged 更新 label。

---

### 分区 3：数据

#### 3.1 数据根目录（只读 + 更改按钮）

```xml
<StackPanel Margin="0,0,0,16">
    <TextBlock Text="数据根目录" Style="{StaticResource AppDialogLabelText}" Margin="0,0,0,6"/>
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>
        <TextBox x:Name="DataRootTextBox" IsReadOnly="True" Style="{StaticResource AppDialogTextBox}" TextWrapping="Wrap"/>
        <Button Grid.Column="1" Content="更改" Click="ChangeDataRoot_Click" Style="{StaticResource AppDialogGhostButton}" Margin="8,0,0,0"/>
    </Grid>
    <TextBlock Text="更改后需要重启软件生效" Foreground="{StaticResource Brush.TextMuted}" FontSize="11" Margin="0,6,0,0"/>
</StackPanel>
```

C#：
```csharp
private void ChangeDataRoot_Click(object sender, RoutedEventArgs e)
{
    using var dialog = new System.Windows.Forms.FolderBrowserDialog
    {
        Description = "选择软件数据目录",
        SelectedPath = Directory.Exists(_storage.Root) ? _storage.Root : AppStorage.DefaultRoot
    };
    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
    var selected = Path.GetFullPath(dialog.SelectedPath);
    DataRootTextBox.Text = selected;
    _pendingDataRoot = selected;
    NeedsRestart = true;
}

private string? _pendingDataRoot;
```

Save_Click 中：
```csharp
if (_pendingDataRoot is not null)
{
    AppStorage.SaveCustomRoot(_pendingDataRoot);
}
```

需要 `using WinForms = System.Windows.Forms;`（已在 MainWindow 中使用此别名模式）。

#### 3.2 数据操作按钮组

```xml
<WrapPanel Margin="0,0,0,8">
    <Button Content="打开备份目录" Click="OpenBackupFolder_Click" Style="{StaticResource AppDialogGhostButton}" Margin="0,0,8,8"/>
    <Button Content="打开数据目录" Click="OpenDataFolder_Click" Style="{StaticResource AppDialogGhostButton}" Margin="0,0,8,8"/>
    <Button Content="手动备份" Click="CreateBackup_Click" Style="{StaticResource AppDialogGhostButton}" Margin="0,0,8,8"/>
    <Button Content="恢复备份" Click="OpenDataSafety_Click" Style="{StaticResource AppDialogGhostButton}" Margin="0,0,8,8"/>
    <Button Content="检查更新" Click="CheckUpdate_Click" Style="{StaticResource AppDialogGhostButton}" Margin="0,0,8,8"/>
</WrapPanel>
```

这些按钮可以通过回传 `RequestedAction` 让主窗口执行，或者直接在 SettingsDialog 内部操作（因为 `_storage` 已注入）。推荐回传模式（与 DataSafetyDialog 一致）：

```csharp
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

public SettingsAction RequestedAction { get; private set; } = SettingsAction.None;
```

---

### 分区 4：标签

#### 4.1 自定义标签颜色

当前存储格式：`tag.custom_colors` = `";"` 分隔的 `#RRGGBB` 字符串。

简化方案：显示当前颜色列表 + 添加/删除按钮。

```xml
<StackPanel Margin="0,0,0,16">
    <TextBlock Text="自定义标签颜色" Style="{StaticResource AppDialogLabelText}" Margin="0,0,0,6"/>
    <ItemsControl x:Name="ColorList">
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <StackPanel Orientation="Horizontal" Margin="0,0,0,8">
                    <Border Width="28" Height="28" CornerRadius="6" Background="{Binding}" Margin="0,0,12,0"/>
                    <TextBlock Text="{Binding}" VerticalAlignment="Center" Foreground="{StaticResource Brush.TextMuted}" Margin="0,0,12,0"/>
                    <Button Content="删除" Tag="{Binding}" Click="RemoveColor_Click" Style="{StaticResource AppDialogGhostButton}"/>
                </StackPanel>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
    <Button Content="+ 添加颜色" Click="AddColor_Click" Style="{StaticResource AppDialogGhostButton}" Margin="0,8,0,0"/>
</StackPanel>
```

C#：
```csharp
private readonly List<string> _colors = new();

// LoadCurrentSettings 中：
_colors.Clear();
foreach (var c in _database.LoadSetting("tag.custom_colors")
    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
{
    _colors.Add(c);
}
ColorList.ItemsSource = _colors.ToList();

private void AddColor_Click(object sender, RoutedEventArgs e)
{
    var input = Microsoft.VisualBasic.Interaction.InputBox("输入颜色值（如 #FF6B6B）：", "添加颜色", "#");
    if (string.IsNullOrWhiteSpace(input)) return;
    input = input.Trim();
    if (!input.StartsWith("#") || input.Length != 7) return;
    _colors.Add(input);
    ColorList.ItemsSource = _colors.ToList();
}

private void RemoveColor_Click(object sender, RoutedEventArgs e)
{
    if (sender is Button btn && btn.Tag is string color)
    {
        _colors.Remove(color);
        ColorList.ItemsSource = _colors.ToList();
    }
}
```

Save_Click 中：
```csharp
var colorValue = string.Join(";", _colors.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
_database.SaveSetting("tag.custom_colors", colorValue);
```

---

### 分区 5：危险

#### 5.1 清除所有书签

```xml
<Button Content="清除所有书签" Click="ClearAllBookmarks_Click" Style="{StaticResource AppDialogDangerButton}" Margin="0,0,0,12"/>
```

C#：
```csharp
private void ClearAllBookmarks_Click(object sender, RoutedEventArgs e)
{
    var result = System.Windows.MessageBox.Show(
        "确定清除所有书籍的书签标记吗？此操作不可恢复。",
        "清除书签",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning);
    if (result != MessageBoxResult.Yes) return;

    var input = Microsoft.VisualBasic.Interaction.InputBox("请输入密码以确认：", "密码确认", "");
    var password = _database.LoadSetting("app.delete_source_password", "0309");
    if (input != password)
    {
        System.Windows.MessageBox.Show("密码不正确。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        return;
    }

    // 需要在 LibraryDatabase 新增方法：
    // public void ClearAllBookmarks() { DELETE FROM book_bookmarks; }
    RequestedAction = SettingsAction.ClearAllBookmarks;
    DialogResult = true;
    Close();
}
```

> **需要在 `LibraryDatabase.cs` 新增方法**：
> ```csharp
> public void ClearAllBookmarks()
> {
>     using var connection = Open();
>     using var command = connection.CreateCommand();
>     command.CommandText = "DELETE FROM book_bookmarks;";
>     command.ExecuteNonQuery();
> }
> ```

#### 5.2 重置所有设置为默认

```xml
<Button Content="重置所有设置为默认" Click="ResetSettings_Click" Style="{StaticResource AppDialogDangerButton}"/>
```

C#：
```csharp
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
```

---

## 三、MainWindow 改动

### 3.1 添加"设置"按钮

文件：`MainWindow.xaml`，在侧边栏"工具"组（约第 1125-1132 行）的 `<StackPanel>` 内添加：

```xml
<Button Content="设置" Click="Settings_Click" Style="{StaticResource SidebarActionButton}"/>
```

### 3.2 添加 Settings_Click 方法

文件：`MainWindow.xaml.cs`，放在 `DataSafety_Click` 附近（约第 1644 行）：

```csharp
private void Settings_Click(object sender, RoutedEventArgs e)
{
    var dialog = new SettingsDialog(_storage, _database) { Owner = this };
    if (dialog.ShowDialog() != true)
    {
        return;
    }

    // 应用隐私模式
    if (dialog.PrivacyModeChanged)
    {
        LoadPrivacyMode();
    }

    // 重新加载快捷键
    if (dialog.ShortcutsChanged)
    {
        LoadShortcuts();
    }

    // 处理回传动作
    switch (dialog.RequestedAction)
    {
        case SettingsAction.OpenBackupFolder:
            OpenBackupFolder();
            break;
        case SettingsAction.OpenDataFolder:
            OpenDataFolder();
            break;
        case SettingsAction.CreateBackup:
            _ = CreateManualBackupAsync();
            break;
        case SettingsAction.OpenDataSafety:
            DataSafety_Click(sender, e);
            break;
        case SettingsAction.CheckUpdate:
            _ = CheckUpdateAsync(null);
            break;
        case SettingsAction.ClearAllBookmarks:
            _ = Task.Run(() => _database.ClearAllBookmarks());
            StatusText.Text = "已清除所有书签。";
            break;
    }

    // 数据根目录变更 → 提示重启
    if (dialog.NeedsRestart)
    {
        var result = System.Windows.MessageBox.Show(
            "数据目录已更改，需要重启软件生效，是否现在重启？",
            "重启软件",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        if (result == MessageBoxResult.Yes)
        {
            RestartCurrentProcess();
        }
    }
}
```

### 3.3 从侧边栏移除已收纳的按钮

迁移后从"工具"组移除：
- `指定数据`（`ChooseDataFolder_Click`）→ 已在设置→数据
- `备份数据`（`DataSafety_Click`）→ 已在设置→数据
- `隐私模式`（`TogglePrivacyMode_Click`）→ 已在设置→通用

"工具"组将只保留 `设置` 一个按钮。

### 3.4 清理隐藏的快捷键 UI

文件：`MainWindow.xaml`，删除第 2527-2539 行的 `<Border Visibility="Collapsed">` 整块。

但注意：`NextShortcutBox`、`PrevShortcutBox`、`CoverPageBox`、`ReadCountBox`、`ReadingStatusBox` 被多处引用。

**必须改为从代码中创建或移除引用**：

1. `LoadShortcuts()`（第 3140-3153 行）— 删除 `NextShortcutBox.Text = next` 和 `PrevShortcutBox.Text = previous`，改为只加载到内存字段：
   ```csharp
   private void LoadShortcuts()
   {
       var shortcuts = _database.LoadShortcuts();
       if (shortcuts.TryGetValue("reader.next", out var next))
           _nextKeys = ParseKeys(next);
       if (shortcuts.TryGetValue("reader.previous", out var previous))
           _prevKeys = ParseKeys(previous);
   }
   ```

2. `SaveShortcuts_Click`（第 1974-2000 行）— 整个方法可以删除（不再有按钮调用它），或者保留但改为从设置面板触发。推荐**删除**。

3. `CoverPageBox`、`ReadCountBox`、`ReadingStatusBox` — 这些控件也被其他代码引用（编辑模式的 `FillMetadataEditors` 等）。**不能删除**，需要把它们从隐藏 Border 中移出到其他地方（或保留在 XAML 中但不隐藏）。

> **安全方案**：只从隐藏 Border 中移除 `NextShortcutBox` 和 `PrevShortcutBox` 两个 TextBox 和 `保存快捷键` Button，保留 `CoverPageBox`/`ReadCountBox`/`ReadingStatusBox` 在隐藏 Border 中（它们仍被代码引用但不需可见）。同时修改 `LoadShortcuts` 移除对 `NextShortcutBox`/`PrevShortcutBox` 的引用。

### 3.5 版本号

文件：`MangaReader.Native.csproj`，改为 `0.7.0`：
```xml
<Version>0.7.0</Version>
<AssemblyVersion>0.7.0</AssemblyVersion>
<FileVersion>0.7.0</FileVersion>
<InformationalVersion>0.7.0</InformationalVersion>
```

---

## 四、LibraryDatabase 新增方法

文件：`G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\Services\LibraryDatabase.cs`

在 `RemoveAllBookmarks` 方法之后添加：

```csharp
public void ClearAllBookmarks()
{
    using var connection = Open();
    using var command = connection.CreateCommand();
    command.CommandText = "DELETE FROM book_bookmarks;";
    command.ExecuteNonQuery();
}
```

---

## 五、现有方法签名参考（供直接调用）

| 方法 | 位置 | 签名 |
|------|------|------|
| `LoadSetting` | `LibraryDatabase.cs:626` | `string LoadSetting(string key, string defaultValue = "")` |
| `SaveSetting` | `LibraryDatabase.cs:851` | `void SaveSetting(string key, string value)` |
| `LoadShortcuts` | `LibraryDatabase.cs:603` | `Dictionary<string, string> LoadShortcuts()` |
| `SaveShortcut` | `LibraryDatabase.cs:833` | `void SaveShortcut(string actionId, string keybinding)` |
| `LoadBookmarks` | `LibraryDatabase.cs:650` | `HashSet<int> LoadBookmarks(string bookId)` |
| `AddBookmark` | `LibraryDatabase.cs:662` | `void AddBookmark(string bookId, int pageIndex)` |
| `RemoveBookmark` | `LibraryDatabase.cs:674` | `void RemoveBookmark(string bookId, int pageIndex)` |
| `RemoveAllBookmarks` | `LibraryDatabase.cs:686` | `void RemoveAllBookmarks(string bookId)` |
| `CreateManualBackup` | `LibraryDatabase.cs:919` | `string CreateManualBackup()` |
| `ParseKeys` | `MainWindow.xaml.cs:4627` | `static List<Key> ParseKeys(string text)` |
| `OpenBackupFolder` | `MainWindow.xaml.cs` | `void OpenBackupFolder()` |
| `OpenDataFolder` | `MainWindow.xaml.cs` | `void OpenDataFolder()` |
| `RestartCurrentProcess` | `MainWindow.xaml.cs:1902` | `bool RestartCurrentProcess()` |
| `LoadPrivacyMode` | `MainWindow.xaml.cs:3155` | `void LoadPrivacyMode()` |
| `LoadShortcuts` | `MainWindow.xaml.cs:3140` | `void LoadShortcuts()` |
| `AppStorage.SaveCustomRoot` | `AppStorage.cs:125` | `static void SaveCustomRoot(string root)` |
| `AppStorage.DefaultRoot` | `AppStorage.cs:16` | `static string DefaultRoot { get; }` |
| `AppStorage.Root` | `AppStorage.cs:8` | `string Root { get; }` |

---

## 六、可用样式资源清单

定义在 `Themes\MangaTheme.xaml`：

| 样式 Key | 用途 |
|----------|------|
| `AppDialogShadowBorder` | 对话框阴影 |
| `RadiusDialog` | 对话框圆角 32 |
| `AppDialogTitleText` | 标题文字（28px Black） |
| `AppDialogSubtitleText` | 副标题（TextMuted） |
| `AppDialogLabelText` | 标签文字（12px SemiBold TextMuted） |
| `AppDialogTextBox` | 输入框（44px 高） |
| `AppDialogReadOnlyText` | 只读文本（44px 高） |
| `AppDialogButton` | 主按钮（40px 高，104px 最小宽） |
| `AppDialogGhostButton` | 次要按钮 |
| `AppDialogDangerButton` | 危险按钮（红色） |
| `AppDialogCloseButton` | 关闭按钮（×） |

定义在 `MainWindow.xaml`：

| 样式 Key | 用途 |
|----------|------|
| `SidebarActionButton` | 侧边栏按钮（32px 高） |
| `SidebarToggleButton` | 侧边栏开关按钮（带 active 态） |

---

## 七、实现顺序

1. **新建 `SettingsDialog.xaml` + `.xaml.cs`** — 完整 5 个分区
2. **`LibraryDatabase.cs` 新增 `ClearAllBookmarks`**
3. **`MainWindow.xaml` 侧边栏添加"设置"按钮**
4. **`MainWindow.xaml.cs` 添加 `Settings_Click` 方法**
5. **`MainWindow.xaml.cs` 修改 `LoadShortcuts`** — 移除 `NextShortcutBox`/`PrevShortcutBox` 引用
6. **`MainWindow.xaml` 删除隐藏的快捷键 TextBox** — 只删 `NextShortcutBox`、`PrevShortcutBox` 和"保存快捷键"按钮
7. **`MainWindow.xaml` 移除侧边栏"指定数据"/"备份数据"/"隐私模式"按钮**
8. **`MainWindow.xaml.cs` 删除 `SaveShortcuts_Click`** — 不再被调用
9. **`MangaReader.Native.csproj` 版本号改为 `0.7.0`**
10. **编译验证**
11. **打包**

---

## 八、注意事项

1. **G: 盘编译问题**：`obj` 目录必须指向 C: 盘临时目录，否则 WPF 临时项目写 DLL 失败。每次编译前清理所有 obj/bin。

2. **`CoverPageBox`/`ReadCountBox`/`ReadingStatusBox` 不能删**：这些控件被 `FillMetadataEditors` 等方法引用。从隐藏 Border 中只删 `NextShortcutBox`、`PrevShortcutBox` 和"保存快捷键"按钮。

3. **隐私模式即时生效**：`PrivacyModeChanged` 回传后，主窗口调用 `LoadPrivacyMode()` 重新加载。

4. **快捷键即时生效**：`ShortcutsChanged` 回传后，主窗口调用 `LoadShortcuts()` 重新加载到 `_nextKeys`/`_prevKeys`。下次打开阅读器即生效。

5. **数据根目录需重启**：`NeedsRestart` 回传后弹出重启确认。

6. **SettingsDialog 中的按钮操作**（打开备份目录等）：推荐通过 `RequestedAction` 枚举回传给主窗口执行，不在对话框内直接操作（保持与 DataSafetyDialog 一致的模式）。

7. **`using` 声明**：SettingsDialog.xaml.cs 需要：
   ```csharp
   using MangaReader.Native.Services;
   using Microsoft.VisualBasic;  // Interaction.InputBox
   using System.Windows;
   using System.Windows.Controls;
   using WinForms = System.Windows.Forms;  // FolderBrowserDialog
   ```

8. **SettingsAction 枚举**：定义在 `SettingsDialog.xaml.cs` 末尾（与 `DataSafetyAction` 模式一致）。

---

## 九、验收标准

- [ ] 侧边栏"工具"组只有"设置"按钮
- [ ] 点击"设置"打开对话框，左侧 5 个导航项可切换
- [ ] 通用分区：隐私模式开关、修改密码（需验证旧密码）、目录删除开关
- [ ] 阅读分区：快捷键输入、滚轮模式/渲染质量/双页间距下拉
- [ ] 数据分区：数据根目录显示+更改、备份/恢复/检查更新按钮
- [ ] 标签分区：颜色列表增删
- [ ] 危险分区：清除书签（需密码）、重置设置
- [ ] 保存后隐私模式/快捷键即时生效
- [ ] 数据根目录更改后提示重启
- [ ] 隐藏的快捷键 TextBox 已删除，`LoadShortcuts` 不引用它们
- [ ] 编译 0 错误 0 警告
- [ ] 版本号 `0.7.0`
