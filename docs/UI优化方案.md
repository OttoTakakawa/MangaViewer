# MangaView UI 全量优化方案

> 日期：2026-06-19
> 状态：大部分完成（P0 全部 + P1 四项 + P2 全部）
> 范围：MangaView（MangaReader.Native）UI 性能、Token 体系、美观度全量优化
> 参考排查数据：366 个硬编码颜色、39 个 ControlTemplate、35 个无样式按钮、47 处同步 DB 调用、2 个目录列表无虚拟化
>
> ## 进度记录（2026-06-20）
> **已完成：**
> P0-1 ✅ 目录列表虚拟化 | P0-2 ✅ 启动并行DB读取 | P0-3 ✅ BookmarkBrush缓存 | P0-4 ✅ AddRange优化 | P0-5 ✅ 封面缓存清理
> P1-3 ✅ 间距Token | P1-4 ✅ 字体Token | P1-5 ✅ ReaderTheme | P1-7 ✅ TagChip统一
> P2-1 ✅ ContextMenu | P2-2 ✅ ToolTip | P2-3 ✅ 对话框统一 | P2-4 ✅ ReaderWindow按钮 | P2-5 ✅ 滑块修复 | P2-6 ✅ RadioButton | P2-7 ✅ 卡片静止态 | P2-8 ✅ 评分徽章去重
>
> **待完成（需新 session）：**
> P1-1 主题拆分（672行→5文件）| P1-2 颜色Token替换（366个硬编码）| P1-6 ControlTemplate去重（39个模板）

---

## 0. 执行须知

### 0.1 编译命令

G 盘有文件系统级 WPF obj 写入限制，编译/打包时中间路径必须指向 C 盘临时目录。

**编译前清理**：

```powershell
cmd /c "rmdir /s /q G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\obj 2>nul & rmdir /s /q G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\bin 2>nul & rmdir /s /q C:\Users\TAKAKAWA\AppData\Local\Temp\opencode\mv_obj 2>nul & rmdir /s /q C:\Users\TAKAKAWA\AppData\Local\Temp\opencode\mv_bin 2>nul & rmdir /s /q C:\Users\TAKAKAWA\AppData\Local\Temp\opencode\mv_baseint 2>nul"
```

**Debug 编译**：

```powershell
dotnet build "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MangaReader.Native.csproj" -c Debug --nologo -p:IntermediateOutputPath="C:\Users\TAKAKAWA\AppData\Local\Temp\opencode\mv_obj\Debug\net8.0-windows\" -p:OutputPath="C:\Users\TAKAKAWA\AppData\Local\Temp\opencode\mv_bin\Debug\net8.0-windows\" -p:BaseIntermediateOutputPath="C:\Users\TAKAKAWA\AppData\Local\Temp\opencode\mv_baseint\"
```

### 0.2 红线

- **不许改** `PlayerWindow`（VideoTapes 才有，MangaView 是 `ReaderWindow`，播放逻辑不动）
- **不许改** `Controls\VirtualizingWrapPanel.cs`（虚拟化面板性能敏感）
- **不许改** `Services\UpdateService.cs`（更新服务稳定）
- **不许改** `Services\MotionService.cs`（动画服务稳定）
- **不许在 XAML 中写死** `CornerRadius="数字"`（必须引用 Token）
- **不许新增** `RadiusPill`（已禁止）
- **不许用系统** `MessageBox` 做阅读器内确认层
- **不许在 UI 线程做 DB 写入**（用 `await Task.Run(() => ...)`）
- **不许用** `ObservableCollection.Add()` 循环添加大量项（用 `ReplaceRange` / `AddRange`）

### 0.3 执行顺序

```
P0（性能）→ P1（Token 体系）→ P2（美观度）
```

每个 P 完成后编译验证，通过后再进入下一个 P。

### 0.4 参考文件位置

| 文件 | 行数 | 说明 |
|------|------|------|
| `Themes\MangaTheme.xaml` | 672 | 主题文件（当前唯一，含所有 Token 和样式） |
| `MainWindow.xaml` | 2689 | 主窗口 UI（250 个硬编码颜色，23 个模板） |
| `MainWindow.xaml.cs` | ~5980 | 主窗口逻辑（47 处同步 DB 调用） |
| `ReaderWindow.xaml` | 598 | 阅读器 UI（81 个硬编码颜色，12 个无样式按钮） |
| `ReaderWindow.xaml.cs` | ~2243 | 阅读器逻辑 |
| `Models\MangaBook.cs` | 448 | 漫画模型（3 处 ObservableCollection.Add 循环） |
| `Models\PageCatalogItem.cs` | 37 | 目录项模型（BookmarkBrush 未缓存） |
| `Services\CoverCache.cs` | ~143 | 封面缓存（无清理逻辑） |
| `Controls\VirtualizingWrapPanel.cs` | — | 虚拟化面板（已有，复用） |

---

## P0：性能修复

### P0-1：目录列表虚拟化

**问题**：`ReaderWindow.xaml:424` 和 `MainWindow.xaml:2563` 的页面目录用 `<WrapPanel/>` 无虚拟化，200 页漫画打开目录会卡死。

**改动文件**：

1. `ReaderWindow.xaml:415-426`（`PageCatalogList`）
2. `MainWindow.xaml:2555-2565`（`DetailCatalogList`）

**改法**：

```xml
<!-- 旧 -->
<ItemsPanelTemplate>
    <WrapPanel/>
</ItemsPanelTemplate>

<!-- 新 -->
<ItemsPanelTemplate>
    <controls:VirtualizingWrapPanel 
        IsVirtualizing="True" 
        VirtualizationMode="Recycling" 
        CacheLength="2"/>
</ItemsPanelTemplate>
```

同时在 ListBox 上加 `ScrollViewer.CanContentScroll="True"`。

`controls` 命名空间已在 MainWindow.xaml 和 ReaderWindow.xaml 中声明（`xmlns:controls="clr-namespace:..."`），确认引用正确。

### P0-2：同步 DB 调用改异步

**问题**：47 处 `_database.*` 调用在 UI 线程同步执行。

**重点改动**：

| 位置 | 问题 | 改法 |
|------|------|------|
| `MainWindow.xaml.cs:196-200` | 启动时 5 次顺序 DB 读取 | 改为 `await Task.WhenAll(...)` 并行读取 |
| `ReaderWindow.xaml.cs:1875` | 构造函数同步读 shortcuts | 延迟到 `Loaded` 事件中异步加载 |
| `ReaderWindow.xaml.cs:2119` | 打开目录时同步读 bookmarks | 异步加载，先显示空目录再填充 |
| `ReaderWindow.xaml.cs:2171` | 每次 toggle bookmark 读 setting | 启动时加载一次，缓存到字段 |
| `MainWindow.xaml.cs:5161` | SaveCategoryColor 循环内逐条写 DB | 改为批量方法 `SaveManagedTagsBatch` |
| `MainWindow.xaml.cs:1492` | 打开详情目录时同步读 bookmarks | 异步加载 |

**启动并行读取示例**：

```csharp
// MainWindow.xaml.cs:196-200 旧：
LoadManagedTags();
LoadCustomTagColors();
LoadManagedAuthors();
LoadShortcuts();
LoadPrivacyMode();

// 新：
await Task.WhenAll(
    Task.Run(() => LoadManagedTags()),
    Task.Run(() => LoadCustomTagColors()),
    Task.Run(() => LoadManagedAuthors()),
    Task.Run(() => LoadShortcuts()),
    Task.Run(() => LoadPrivacyMode())
);
```

注意：这些方法内部如果有 UI 线程操作（如设置控件属性），需要用 `Dispatcher.Invoke` 包裹 UI 部分，或者把 DB 读取和 UI 更新分离。

**ReaderWindow 延迟加载示例**：

```csharp
// ReaderWindow.xaml.cs 构造函数中移除 LoadViewerPreferences()
// 改为在 Loaded 事件中：
private async void ReaderWindow_Loaded(object sender, RoutedEventArgs e)
{
    // ... 现有逻辑
    await Task.Run(() => LoadViewerPreferences());
    // 应用偏好到 UI
}
```

**SaveCategoryColor 批量化示例**：

```csharp
// MainWindow.xaml.cs:5161 旧：foreach 循环内 _database.SaveManagedTag(...)
// 新：收集所有变更，一次性批量写入
var updates = new List<(string name, string category, string color)>();
foreach (var tag in tagsInCategory)
{
    updates.Add((tag.Name, category, newColor));
}
await Task.Run(() => _database.SaveManagedTagsBatch(updates));
```

需要在 `LibraryDatabase.cs` 新增 `SaveManagedTagsBatch` 方法。

### P0-3：BookmarkBrush 缓存

**问题**：`PageCatalogItem.cs:55` 每次 getter 调用 `new SolidColorBrush` + `ColorConverter.ConvertFromString`，数据绑定反复触发，分配大量未冻结 Brush。

**改动文件**：`Models\PageCatalogItem.cs`

**改法**：

```csharp
// 旧（:55-56）：
public Brush BookmarkBrush
{
    get => new SolidColorBrush((Color)ColorConverter.ConvertFromString(_bookmarkColor));
}

// 新：
private SolidColorBrush? _bookmarkBrushCache;

public Brush BookmarkBrush
{
    get
    {
        if (_bookmarkBrushCache is null || _bookmarkBrushCache.Color.ToString() != _bookmarkColor)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(_bookmarkColor);
                _bookmarkBrushCache = new SolidColorBrush(color);
                _bookmarkBrushCache.Freeze();
            }
            catch
            {
                _bookmarkBrushCache = Brushes.Transparent;
            }
        }
        return _bookmarkBrushCache;
    }
}
```

同时修改 `BookmarkColor` 的 setter，在颜色变化时清空缓存：

```csharp
public string BookmarkColor
{
    get => _bookmarkColor;
    set
    {
        _bookmarkColor = value;
        _bookmarkBrushCache = null;  // 清空缓存
        OnPropertyChanged(nameof(BookmarkBrush));
    }
}
```

### P0-4：ObservableCollection.Add 改 AddRange

**问题**：3 处循环 `.Add()` 触发 N 次布局刷新。

**改动文件和位置**：

| 文件 | 行号 | 集合 | 改法 |
|------|------|------|------|
| `ReaderWindow.xaml.cs` | 2121-2127 | `PageCatalogItems` | 改为 `RangeObservableCollection`，用 `AddRange` |
| `Models\MangaBook.cs` | 294-301 | `TagItems` | 同上 |
| `Models\MangaBook.cs` | 342-355 | `CardTagItems` | 同上 |

**改法示例**：

```csharp
// 旧（ReaderWindow.xaml.cs:2121-2127）：
for (var i = 0; i < _book.Pages.Count; i++)
{
    PageCatalogItems.Add(new PageCatalogItem(i, _book.Pages[i]) { ... });
}

// 新：
var items = new List<PageCatalogItem>(_book.Pages.Count);
for (var i = 0; i < _book.Pages.Count; i++)
{
    items.Add(new PageCatalogItem(i, _book.Pages[i]) { ... });
}
PageCatalogItems.AddRange(items);  // 一次通知
```

需要把 `PageCatalogItems`、`TagItems`、`CardTagItems` 的类型从 `ObservableCollection<T>` 改为 `RangeObservableCollection<T>`。项目已有 `RangeObservableCollection<T>` 类（`Models\RangeObservableCollection.cs`），支持 `AddRange`。

### P0-5：磁盘封面缓存清理

**问题**：`CoverCache.cs` 旧 PNG 永不删除，缓存目录无限增长。

**改动文件**：`Services\CoverCache.cs`

**新增方法**：

```csharp
/// <summary>
/// 清理不再被任何书籍引用的旧封面缓存文件。
/// 在每次扫描完成后调用。
/// </summary>
public void SweepStaleCovers(IEnumerable<string> validBookIds)
{
    var validSet = new HashSet<string>(validBookIds, StringComparer.OrdinalIgnoreCase);
    try
    {
        foreach (var file in Directory.EnumerateFiles(_storage.CoverCachePath, "*.jpg"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            // 文件名格式：{bookId}_{ticks}
            var bookId = fileName.Split('_')[0];
            if (!validSet.Contains(bookId))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }
    catch { }
}
```

**调用点**：在 `MainWindow.xaml.cs` 的扫描完成回调中调用：

```csharp
// 扫描完成后
_coverCache.SweepStaleCovers(Books.Select(b => b.Id));
```

### P0 验收

- [ ] 编译通过，0 错误 0 警告
- [ ] 打开 200+ 页漫画的目录，无卡顿
- [ ] 应用启动速度明显改善（5 次 DB 并行读）
- [ ] 阅读器打开速度明显改善（延迟加载）
- [ ] 切换标记颜色时无卡顿（缓存 Brush）
- [ ] 封面缓存目录不再无限增长

---

## P1：Token 体系建设

### P1-1：主题系统拆分（支持多主题）

**目标**：将 `MangaTheme.xaml` 拆分为可切换的主题结构。

**新文件结构**：

```
Themes/
├── ThemeBase.xaml          ← 圆角 Token、间距 Token、字体 Token、控件样式（不含颜色定义）
├── ThemeLight.xaml         ← 浅色主题（Color.* + Brush.* 定义）
├── ThemeWarm.xaml          ← 暖纸主题（当前暖色系，作为默认）
├── ThemeDark.xaml          ← 暗色主题
└── ReaderTheme.xaml        ← ReaderWindow 暗色 Token（独立于主主题）
```

**拆分规则**：

| 内容 | 放入 | 说明 |
|------|------|------|
| `Color.*` 和 `Brush.*` 定义 | ThemeWarm.xaml / ThemeLight.xaml / ThemeDark.xaml | 每个主题文件各自定义颜色值，Key 相同 |
| `CornerRadius` Token | ThemeBase.xaml | 圆角不随主题变 |
| `Thickness` Token（间距） | ThemeBase.xaml | 间距不随主题变 |
| `FontSize` Token（新增） | ThemeBase.xaml | 字体大小不随主题变 |
| 所有 `Style` 和 `ControlTemplate` | ThemeBase.xaml | 样式引用 Token，不写死颜色 |
| `DropShadowEffect` | ThemeBase.xaml | 阴影不随主题变（或阴影颜色用 Token） |
| Reader 专用 `Color.Reader.*` 和 `Brush.Reader.*` | ReaderTheme.xaml | 独立于主主题 |

**App.xaml 改造**：

```xml
<!-- 旧 -->
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary Source="Themes/MangaTheme.xaml"/>
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>

<!-- 新 -->
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary Source="Themes/ThemeBase.xaml"/>
            <ResourceDictionary Source="Themes/ThemeWarm.xaml"/>
            <ResourceDictionary Source="Themes/ReaderTheme.xaml"/>
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

**主题切换逻辑**（`App.xaml.cs`）：

```csharp
public static void ApplyTheme(string themeName)
{
    var current = Current.Resources.MergedDictionaries;
    // 找到颜色字典（第 2 个，索引 1）并替换
    var newColorDict = new ResourceDictionary
    {
        Source = new Uri($"Themes/Theme{themeName}.xaml", UriKind.Relative)
    };
    
    // 替换颜色字典
    for (int i = 0; i < current.Count; i++)
    {
        if (current[i].Source?.OriginalString.Contains("Theme") == true
            && !current[i].Source.OriginalString.Contains("ThemeBase")
            && !current[i].Source.OriginalString.Contains("ReaderTheme"))
        {
            current[i] = newColorDict;
            break;
        }
    }
}
```

**主题选择持久化**：

存 `app_settings` key=`ui.theme`，值 `Warm`/`Light`/`Dark`。
在设置面板（SettingsDialog）新增主题切换入口。
切换后调 `App.ApplyTheme(themeName)` + 存 `app_settings`。

**三个主题文件的颜色定义**：

ThemeWarm.xaml（当前暖色系，默认）：

```xml
<Color x:Key="Color.AppBackground">#F6F7F9</Color>
<Color x:Key="Color.Surface">#FFFFFF</Color>
<Color x:Key="Color.SurfaceMuted">#F8F2EC</Color>
<Color x:Key="Color.TextPrimary">#25211E</Color>
<Color x:Key="Color.TextMuted">#8D8177</Color>
<Color x:Key="Color.BorderSubtle">#E5E0D8</Color>
<Color x:Key="Color.BorderStrong">#D1CCC2</Color>
<Color x:Key="Color.Accent">#B45309</Color>
<!-- ... -->
```

ThemeLight.xaml（冷蓝灰系）：

```xml
<Color x:Key="Color.AppBackground">#F6F7F9</Color>
<Color x:Key="Color.Surface">#FFFFFF</Color>
<Color x:Key="Color.SurfaceMuted">#F8FAFC</Color>
<Color x:Key="Color.TextPrimary">#111827</Color>
<Color x:Key="Color.TextMuted">#6B7280</Color>
<Color x:Key="Color.BorderSubtle">#E5E7EB</Color>
<Color x:Key="Color.BorderStrong">#D1D5DB</Color>
<Color x:Key="Color.Accent">#B45309</Color>
<!-- ... -->
```

ThemeDark.xaml（暗色系）：

```xml
<Color x:Key="Color.AppBackground">#1A1A1A</Color>
<Color x:Key="Color.Surface">#2A2A2A</Color>
<Color x:Key="Color.SurfaceMuted">#333333</Color>
<Color x:Key="Color.TextPrimary">#F5F5F5</Color>
<Color x:Key="Color.TextMuted">#A0A0A0</Color>
<Color x:Key="Color.BorderSubtle">#3A3A3A</Color>
<Color x:Key="Color.BorderStrong">#4A4A4A</Color>
<Color x:Key="Color.Accent">#D97706</Color>
<!-- ... -->
```

**关键**：统一 Token Key 名，三个主题文件用相同的 Key，只改 Value。这样所有 Style 引用 `{StaticResource Brush.*}` 时自动跟随主题。

**合并暖冷 Token**：

当前主题有 `TextPrimary`(#111827 冷) 和 `TextWarm`(#25211E 暖) 两个"主文字色" Token。合并为一个 `TextPrimary`，每个主题文件各自定义值：

- ThemeWarm: `TextPrimary = #25211E`
- ThemeLight: `TextPrimary = #111827`
- ThemeDark: `TextPrimary = #F5F5F5`

删除 `TextWarm`、`SurfaceWarm`、`TextSubtle`（合并到 `TextMuted`），减少 Token 混乱。

### P1-2：颜色 Token 补全

**目标**：把 XAML 中 366 个硬编码颜色替换为 Token 引用。

**新增 Token**：

| Token Key | 值（Warm 主题） | 用途 |
|-----------|-----------------|------|
| `Color.StatusMissing` | `#B44A36` | 缺失状态 |
| `Color.StatusHidden` | `#9A6F25` | 隐藏状态 |
| `Color.StatusReading` | `#447154` | 在读状态 |
| `Color.StatusFinished` | `#3B5A7C` | 读完状态 |
| `Color.StatusUnread` | `#9CA3AF` | 未读状态 |
| `Color.BorderFocus` | `#B45309` | 输入框焦点边框 |
| `Color.SurfaceHover` | `#F0EDE8` | 卡片 hover 背景 |
| `Color.SurfaceSelected` | `#E8E2D8` | 选中态背景 |
| `Color.TagBg` | `#F8F2EC` | 标签背景 |
| `Color.TagBorder` | `#E5E0D8` | 标签边框 |
| `Color.Favorite` | `#E4B95F` | 收藏星标 |
| `Color.Rating` | `#E4B95F` | 评分星标 |
| `Color.Dim` | `#9CA3AF` | 次要/禁用 |

**执行策略**：分文件逐个替换。

**MainWindow.xaml（250 个颜色）**：

第一步：把 131 个已和现有 Token 重复的硬编码替换为 Token 引用：

| 硬编码 | 替换为 |
|--------|--------|
| `#25211E` | `{StaticResource Brush.TextPrimary}` |
| `#6B7280` | `{StaticResource Brush.TextMuted}` |
| `#FFFFFF` | `{StaticResource Brush.Surface}` |
| `#E5E7EB` | `{StaticResource Brush.BorderSubtle}` |
| `#111827` | `{StaticResource Brush.TextPrimary}` |
| `#B45309` | `{StaticResource Brush.Accent}` |
| `#D1D5DB` | `{StaticResource Brush.BorderStrong}` |
| `#F8FAFC` | `{StaticResource Brush.SurfaceMuted}` |
| `#8D8177` | `{StaticResource Brush.TextMuted}` |

第二步：把剩余 ~119 个无 Token 的颜色替换为新 Token（见上表）。

第三步：少量真正独特的一次性颜色（如特定的卡片样式装饰色），如果无法归入任何 Token，在对应主题文件中新增专用 Token。

**ReaderWindow.xaml（81 个颜色）**：

全部归入 ReaderTheme.xaml 的 `Reader.*` Token（见 P1-5）。

**对话框文件（~13 个颜色）**：

少量硬编码，替换为现有 Token。

### P1-3：间距 Token 体系

**目标**：95 种 Margin、39 种 Padding 替换为间距 Token。

**新增 Token**（在 ThemeBase.xaml）：

```xml
<!-- 4px 基准间距尺度 -->
<Thickness x:Key="Space2">4</Thickness>
<Thickness x:Key="Space3">8</Thickness>
<Thickness x:Key="Space4">12</Thickness>
<Thickness x:Key="Space5">16</Thickness>
<Thickness x:Key="Space6">20</Thickness>
<Thickness x:Key="Space7">24</Thickness>
<Thickness x:Key="Space8">28</Thickness>
<Thickness x:Key="Space10">36</Thickness>

<!-- 方向间距（常用组合） -->
<Thickness x:Key="SpaceBottom3">0,0,0,8</Thickness>
<Thickness x:Key="SpaceBottom4">0,0,0,12</Thickness>
<Thickness x:Key="SpaceBottom5">0,0,0,16</Thickness>
<Thickness x:Key="SpaceTop3">0,8,0,0</Thickness>
<Thickness x:Key="SpaceTop4">0,12,0,0</Thickness>
<Thickness x:Key="SpaceTop6">0,20,0,0</Thickness>
<Thickness x:Key="SpaceRight3">0,0,8,0</Thickness>
<Thickness x:Key="SpaceRight4">0,0,12,0</Thickness>
```

保留现有 `SpacePanel`(16) 和 `SpaceDialog`(24)，它们等于 `Space5` 和 `Space7`。

**替换规则**：

| 当前值 | 替换为 |
|--------|--------|
| `Margin="0,8,0,0"` (28处) | `Margin="{StaticResource SpaceTop3}"` |
| `Margin="0,0,10,0"` (23处) | `Margin="{StaticResource SpaceRight3}"`（10→8 归入） |
| `Margin="0,0,8,0"` (23处) | `Margin="{StaticResource SpaceRight3}"` |
| `Margin="0,6,0,0"` (14处) | `Margin="{StaticResource SpaceTop3}"`（6→8 归入） |
| `Margin="0,0,0,10"` (13处) | `Margin="{StaticResource SpaceBottom3}"`（10→8 归入） |
| `Margin="0,0,0,12"` (10处) | `Margin="{StaticResource SpaceBottom4}"` |
| `Margin="22"` (8处) | `Margin="{StaticResource Space6}"`（22→20 归入） |
| `Margin="0,24,0,0"` (7处) | `Margin="{StaticResource SpaceTop7}"` |
| `Padding="26"` (10处) | `Padding="{StaticResource Space7}"`（26→24 归入） |
| `Padding="16"` (8处) | `Padding="{StaticResource Space5}"` |

**原则**：把 6/7/10/14/18/22/26 等不规则值归入最近的 4px 基准值（4/8/12/16/20/24/28/36）。

### P1-4：字体 Token 体系

**目标**：15 种 FontSize 替换为 7 级字体 Token。

**新增 Token**（在 ThemeBase.xaml）：

```xml
<!-- 字体大小 Token -->
<x:Double x:Key="Font.Display">30</x:Double>
<x:Double x:Key="Font.Title">28</x:Double>
<x:Double x:Key="Font.TitleSm">22</x:Double>
<x:Double x:Key="Font.Section">16</x:Double>
<x:Double x:Key="Font.Body">14</x:Double>
<x:Double x:Key="Font.Label">12</x:Double>
<x:Double x:Key="Font.Caption">11</x:Double>
```

**替换规则**：

| 当前 FontSize | 替换 Token | 说明 |
|---------------|-----------|------|
| 30 | `Font.Display` | 空状态大标题 |
| 28 | `Font.Title` | 页面/对话框标题 |
| 26 | `Font.Title` | 归入 28 |
| 24 | `Font.Title` | 归入 28（空状态标题） |
| 22 | `Font.TitleSm` | 卡片标题 |
| 21 | `Font.TitleSm` | 归入 22（侧边栏 logo） |
| 20 | `Font.Section` | 归入 16（子标题） |
| 18 | `Font.Section` | 归入 16（编辑表单标题） |
| 16 | `Font.Section` | 分区标题 |
| 15 | `Font.Section` | 归入 16（SettingsDialog SectionHeader） |
| 14 | `Font.Body` | 正文 |
| 13 | `Font.Body` | 归入 14（元信息） |
| 12 | `Font.Label` | 标签 |
| 11 | `Font.Caption` | 小字 |
| 10 | `Font.Caption` | 归入 11 |

**FontWeight 体系**：

| 用途 | FontWeight |
|------|------------|
| Display/Title/TitleSm | Black |
| Section | SemiBold |
| Body | Regular（不显式设置） |
| Label | SemiBold |
| Caption | Regular（不显式设置） |

当前问题：全项目用 `SemiBold`/`Black`，没有 `Regular`。正文应回归 Regular，让 bold 有区分度。

### P1-5：ReaderWindow 暗色 Token 化

**目标**：ReaderWindow 的 ~20 个暗色硬编码提取为 Token，不改外观。

**新增文件**：`Themes\ReaderTheme.xaml`

```xml
<ResourceDictionary xmlns="..." xmlns:x="...">
    <!-- Reader 基础色 -->
    <Color x:Key="Color.Reader.Bg">#0F1115</Color>
    <Color x:Key="Color.Reader.Surface">#080A0E</Color>
    <Color x:Key="Color.Reader.Text">#F9FAFB</Color>
    <Color x:Key="Color.Reader.TextMuted">#273244</Color>
    <Color x:Key="Color.Reader.Border">#22FFFFFF</Color>
    
    <!-- Reader 工具栏（不同透明度） -->
    <Color x:Key="Color.Reader.ToolbarTop">#A6080A0E</Color>
    <Color x:Key="Color.Reader.ToolbarBottom">#B0080A0E</Color>
    <Color x:Key="Color.Reader.Overlay">#F0080A0E</Color>
    <Color x:Key="Color.Reader.Confirm">#EA080A0E</Color>
    <Color x:Key="Color.Reader.Hint">#99080A0E</Color>
    
    <!-- Reader 控件 -->
    <Color x:Key="Color.Reader.SliderTrack">#30FFFFFF</Color>
    <Color x:Key="Color.Reader.ButtonBg">#1AFFFFFF</Color>
    <Color x:Key="Color.Reader.ButtonBorder">#22FFFFFF</Color>
    <Color x:Key="Color.Reader.MessageBg">#EAFDFBF7</Color>
    <Color x:Key="Color.Reader.MessageText">#111827</Color>
    
    <!-- Reader Brush -->
    <SolidColorBrush x:Key="Brush.Reader.Bg" Color="{StaticResource Color.Reader.Bg}"/>
    <SolidColorBrush x:Key="Brush.Reader.Surface" Color="{StaticResource Color.Reader.Surface}"/>
    <SolidColorBrush x:Key="Brush.Reader.Text" Color="{StaticResource Color.Reader.Text}"/>
    <SolidColorBrush x:Key="Brush.Reader.TextMuted" Color="{StaticResource Color.Reader.TextMuted}"/>
    <SolidColorBrush x:Key="Brush.Reader.Border" Color="{StaticResource Color.Reader.Border}"/>
    <SolidColorBrush x:Key="Brush.Reader.ToolbarTop" Color="{StaticResource Color.Reader.ToolbarTop}"/>
    <SolidColorBrush x:Key="Brush.Reader.ToolbarBottom" Color="{StaticResource Color.Reader.ToolbarBottom}"/>
    <SolidColorBrush x:Key="Brush.Reader.Overlay" Color="{StaticResource Color.Reader.Overlay}"/>
    <SolidColorBrush x:Key="Brush.Reader.Confirm" Color="{StaticResource Color.Reader.Confirm}"/>
    <SolidColorBrush x:Key="Brush.Reader.Hint" Color="{StaticResource Color.Reader.Hint}"/>
    <SolidColorBrush x:Key="Brush.Reader.SliderTrack" Color="{StaticResource Color.Reader.SliderTrack}"/>
    <SolidColorBrush x:Key="Brush.Reader.ButtonBg" Color="{StaticResource Color.Reader.ButtonBg}"/>
    <SolidColorBrush x:Key="Brush.Reader.ButtonBorder" Color="{StaticResource Color.Reader.ButtonBorder}"/>
    <SolidColorBrush x:Key="Brush.Reader.MessageBg" Color="{StaticResource Color.Reader.MessageBg}"/>
    <SolidColorBrush x:Key="Brush.Reader.MessageText" Color="{StaticResource Color.Reader.MessageText}"/>
</ResourceDictionary>
```

**ReaderWindow.xaml 替换映射**：

| 行号 | 旧值 | 新值 |
|------|------|------|
| `:5` | `#0F1115` | `{StaticResource Brush.Reader.Bg}` |
| `:108-109` | `#111827`/`#273244` | `Brush.Reader.Text`/`Brush.Reader.TextMuted` |
| `:172` | `#E5E7EB`（亮灰滑块轨道） | `{StaticResource Brush.Reader.SliderTrack}` |
| `:275` | `#EA080A0E` | `{StaticResource Brush.Reader.Confirm}` |
| `:385` | `#F0080A0E` | `{StaticResource Brush.Reader.Overlay}` |
| `:424` | WrapPanel | VirtualizingWrapPanel（见 P0-1） |
| `:505` | `#A6080A0E` | `{StaticResource Brush.Reader.ToolbarTop}` |
| `:523` | `#B0080A0E` | `{StaticResource Brush.Reader.ToolbarBottom}` |
| `:587` | `#99080A0E` | `{StaticResource Brush.Reader.Hint}` |
| `:244` | `#EAFDFBF7` / `#111827` | `Brush.Reader.MessageBg` / `Brush.Reader.MessageText` |

所有 `#22FFFFFF`、`#1AFFFFFF`、`#30FFFFFF` 等半透明白色替换为对应 Reader Token。

### P1-6：ControlTemplate 去重

**目标**：39 个 ControlTemplate 中 ~15 个重复的合并。

**合并计划**：

| 模板 | 当前重复位置 | 合并到 |
|------|-------------|--------|
| ListBoxItem | MainWindow:631, 1743, 1872, 2575 + ReaderWindow:436 + SettingsDialog:39 + DataSafetyDialog:22 | ThemeBase: `AppListBoxItemStyle` |
| ComboBox | MainWindow:286, 359 + ReaderWindow:63 | ThemeBase: `AppComboBox`（区别于 AppDialogComboBox，用于主窗口） |
| TextBox | MainWindow:220, 252 | ThemeBase: `AppTextBox`（区别于 AppDialogTextBox） |
| CheckBox | MainWindow:483, 538 | ThemeBase: `AppCheckBox` |
| ComboBoxItem | MainWindow:445 + ReaderWindow:137 | ThemeBase: `AppComboBoxItem`（区别于 AppDialogComboBoxItem） |
| Button | MainWindow:84 + ReaderWindow:26 | MainWindow 的删除用 `AppButton`；ReaderWindow 的改为 `ReaderButton`（见 P2-4） |

**执行方式**：

1. 在 ThemeBase.xaml 新增上述统一样式
2. MainWindow.xaml 和 ReaderWindow.xaml 中删除重复的局部模板
3. 控件引用改为 `Style="{StaticResource AppXXX}"`

### P1-7：TagChip 统一

**目标**：4 套 TagChip 统一为 1 个基础样式 + 3 个尺寸变体。

**新增样式**（ThemeBase.xaml）：

```xml
<!-- 基础 TagChip -->
<Style x:Key="TagChip" TargetType="Border">
    <Setter Property="CornerRadius" Value="{StaticResource RadiusControl}"/>
    <Setter Property="Background" Value="{StaticResource Brush.SurfaceMuted}"/>
    <Setter Property="BorderBrush" Value="{StaticResource Brush.BorderSubtle}"/>
    <Setter Property="BorderThickness" Value="1"/>
</Style>

<!-- 小尺寸（卡片标签） -->
<Style x:Key="TagChipSm" TargetType="Border" BasedOn="{StaticResource TagChip}">
    <Setter Property="Height" Value="18"/>
    <Setter Property="Padding" Value="6,0"/>
    <Setter Property="Margin" Value="0,0,5,4"/>
</Style>

<!-- 中尺寸（卡片标签） -->
<Style x:Key="TagChipMd" TargetType="Border" BasedOn="{StaticResource TagChip}">
    <Setter Property="Padding" Value="10,5"/>
    <Setter Property="Margin" Value="0,0,7,7"/>
</Style>

<!-- 大尺寸（详情页标签） -->
<Style x:Key="TagChipLg" TargetType="Border" BasedOn="{StaticResource TagChip}">
    <Setter Property="MinHeight" Value="24"/>
    <Setter Property="Padding" Value="10,4"/>
    <Setter Property="Margin" Value="0,0,8,8"/>
</Style>
```

**删除**：
- `MainWindow.xaml:140-155` `CardTagChipBorder` → 用 `TagChipSm`
- `MainWindow.xaml:125-139` `AppTagChipBorder` → 用 `TagChipMd`
- `MainWindow.xaml:162-167` `DetailHeroTagChipBorder` → 用 `TagChipLg`
- `MangaTheme.xaml:652-659` `DetailTagChipBorder`（`CornerRadius=999`）→ 用 `TagChipLg`（圆角从 999 改为 RadiusControl）

### P1 验收

- [ ] 编译通过
- [ ] 主题切换功能正常（Warm/Light/Dark 三种主题）
- [ ] 主题切换后所有颜色正确变化
- [ ] XAML 中无硬编码颜色（`rg -n '#[0-9A-Fa-f]{6}' --glob '*.xaml'` 应只在主题文件中命中）
- [ ] 间距/字体统一为 Token 引用
- [ ] ReaderWindow 外观不变（暗色 Token 化不改外观）
- [ ] ControlTemplate 无重复
- [ ] TagChip 统一为 4 个样式

---

## P2：美观度提升

### P2-1：ContextMenu/MenuItem 样式化

**问题**：0 个自定义菜单样式，用 Windows 默认灰色矩形，和自定义 UI 风格严重不搭。

**新增样式**（ThemeBase.xaml）：

```xml
<Style x:Key="AppContextMenu" TargetType="ContextMenu">
    <Setter Property="Background" Value="{StaticResource Brush.Surface}"/>
    <Setter Property="BorderBrush" Value="{StaticResource Brush.BorderSubtle}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Padding" Value="4"/>
    <Setter Property="Foreground" Value="{StaticResource Brush.TextPrimary}"/>
    <Setter Property="FontFamily" Value="Microsoft YaHei UI"/>
    <Setter Property="FontSize" Value="{StaticResource Font.Label}"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="ContextMenu">
                <Border Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}"
                        CornerRadius="{StaticResource RadiusPanel}"
                        Padding="{TemplateBinding Padding}">
                    <StackPanel>
                        <ContentPresenter/>
                    </StackPanel>
                </Border>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>

<Style x:Key="AppMenuItem" TargetType="MenuItem">
    <Setter Property="Padding" Value="14,8"/>
    <Setter Property="Foreground" Value="{StaticResource Brush.TextPrimary}"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="MenuItem">
                <Border x:Name="Root"
                        Background="Transparent"
                        CornerRadius="{StaticResource RadiusControl}"
                        Padding="{TemplateBinding Padding}">
                    <ContentPresenter ContentSource="Header"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Root" Property="Background" Value="{StaticResource Brush.SurfaceMuted}"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

设为全局默认样式（无 x:Key 的 `TargetType="ContextMenu"` 和 `TargetType="MenuItem"`），让所有右键菜单自动应用。

### P2-2：ToolTip 样式化

```xml
<Style TargetType="ToolTip">
    <Setter Property="Background" Value="{StaticResource Brush.Surface}"/>
    <Setter Property="BorderBrush" Value="{StaticResource Brush.BorderSubtle}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="CornerRadius" Value="{StaticResource RadiusControl}"/>
    <Setter Property="Padding" Value="10,6"/>
    <Setter Property="FontSize" Value="{StaticResource Font.Label}"/>
    <Setter Property="Foreground" Value="{StaticResource Brush.TextMuted}"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="ToolTip">
                <Border Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}"
                        CornerRadius="{TemplateBinding CornerRadius}"
                        Padding="{TemplateBinding Padding}">
                    <ContentPresenter/>
                </Border>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

设为全局默认样式（无 x:Key）。

### P2-3：对话框统一

**问题**：4/8 对话框背景边框写反，3 种底部按钮布局，DataSafetyDialog 标题不用样式。

**改动**：

1. **统一背景边框**：

| 文件 | 旧 | 新 |
|------|-----|-----|
| `ImportFolderDialog.xaml:18-21` | `Background=AppBackground, BorderBrush=Surface` | `Background=Surface, BorderBrush=BorderSubtle` |
| `RenameDialog.xaml:14-17` | 同上 | 同上 |
| `TagNameDialog.xaml:14-17` | 同上 | 同上 |
| `AuthorBatchImportDialog.xaml:15-18` | 同上 | 同上 |

2. **统一底部布局**：全部改为 DockPanel 模式：

```xml
<DockPanel Margin="0,20,0,0">
    <!-- 左侧可选 hint -->
    <TextBlock DockPanel.Dock="Left" .../>
    <!-- 右侧按钮 -->
    <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
        <Button Content="取消" Style="{StaticResource AppDialogGhostButton}"/>
        <Button Content="确认" Style="{StaticResource AppDialogButton}" Margin="8,0,0,0"/>
    </StackPanel>
</DockPanel>
```

3. **DataSafetyDialog 标题**：

`DataSafetyDialog.xaml:66` 的 `FontSize="30"` 改为 `Style="{StaticResource AppDialogTitleText}"`。

4. **按钮宽度统一**：删除 `Width="124"`，统一用 `MinWidth="104"`。

### P2-4：ReaderWindow 按钮样式化

**问题**：12/12 按钮无 Style（100%）。

**新增样式**（ReaderTheme.xaml）：

```xml
<Style x:Key="ReaderButton" TargetType="Button" BasedOn="{StaticResource AppButton}">
    <Setter Property="Background" Value="{StaticResource Brush.Reader.ButtonBg}"/>
    <Setter Property="BorderBrush" Value="{StaticResource Brush.Reader.ButtonBorder}"/>
    <Setter Property="Foreground" Value="{StaticResource Brush.Reader.Text}"/>
</Style>

<Style x:Key="ReaderGhostButton" TargetType="Button" BasedOn="{StaticResource ReaderButton}">
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="BorderBrush" Value="{StaticResource Brush.Reader.Border}"/>
</Style>
```

**改动**：`ReaderWindow.xaml` 中 12 个按钮全部加 `Style="{StaticResource ReaderButton}"` 或 `ReaderGhostButton`。

位置（行号参考）：357, 361, 366, 410, 543, 544, 545, 553, 555, 556, 574, 575。

### P2-5：ReaderWindow 滑块修复

**问题**：`ReaderWindow.xaml:172` 滑块 decrease 轨道用 `#E5E7EB`（亮灰），暗色工具栏上刺眼。

**改法**：替换为 `{StaticResource Brush.Reader.SliderTrack}`（`#30FFFFFF`）。

### P2-6：RadioButton 样式化

**问题**：SettingsDialog 的 RadioButton 用默认小圆圈，和自定义 pill CheckBox 风格冲突。

**新增样式**（ThemeBase.xaml）：

```xml
<Style x:Key="AppRadioButton" TargetType="RadioButton">
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="FontSize" Value="{StaticResource Font.Body}"/>
    <Setter Property="Foreground" Value="{StaticResource Brush.TextPrimary}"/>
    <Setter Property="VerticalAlignment" Value="Center"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="RadioButton">
                <Border x:Name="Root"
                        Background="{StaticResource Brush.SurfaceMuted}"
                        BorderBrush="{StaticResource Brush.BorderSubtle}"
                        BorderThickness="1"
                        CornerRadius="{StaticResource RadiusControl}"
                        Padding="14,8">
                    <ContentPresenter VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Root" Property="BorderBrush" Value="{StaticResource Brush.BorderStrong}"/>
                    </Trigger>
                    <Trigger Property="IsChecked" Value="True">
                        <Setter TargetName="Root" Property="Background" Value="{StaticResource Brush.TextPrimary}"/>
                        <Setter TargetName="Root" Property="BorderBrush" Value="{StaticResource Brush.TextPrimary}"/>
                        <Setter TargetName="Root" Property="TextBlock.Foreground" Value="{StaticResource Brush.Surface}"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

`SettingsDialog.xaml:207-210` 的 4 个 RadioButton 加 `Style="{StaticResource AppRadioButton}"`。

### P2-7：库卡片静止态

**问题**：库卡片透明背景+透明边框，白底上像浮动文字。

**改法**：`MainWindow.xaml` 的 `LibraryBookTemplate`（`:654-663`）：

```xml
<!-- 旧 -->
<Border Background="Transparent" BorderBrush="Transparent" BorderThickness="0">

<!-- 新 -->
<Border Background="{StaticResource Brush.SurfaceMuted}" 
        BorderBrush="{StaticResource Brush.BorderSubtle}" 
        BorderThickness="1"
        CornerRadius="{StaticResource RadiusPanel}">
```

在 `LibraryListItemStyle` 的 hover/selected 触发器中调整背景色为 `SurfaceHover`/`SurfaceSelected`。

### P2-8：评分徽章去重

**问题**：评分星标 XAML 在 MainWindow 重复 3 次（各 ~58 行）。

**改法**：提取为 DataTemplate 放 ThemeBase.xaml：

```xml
<DataTemplate x:Key="RatingBadgeTemplate">
    <Grid>
        <!-- 评分星标 + 数字 -->
        <!-- 从 MainWindow.xaml:756-813 提取 -->
    </Grid>
</DataTemplate>
```

3 处引用改为 `ContentControl Content="{Binding}" ContentTemplate="{StaticResource RatingBadgeTemplate}"`。

### P2 验收

- [ ] 编译通过
- [ ] 右键菜单显示圆角+主题色（非 Windows 默认）
- [ ] ToolTip 显示圆角+主题色
- [ ] 8 个对话框背景边框统一
- [ ] ReaderWindow 按钮有统一样式
- [ ] ReaderWindow 滑块轨道为暗色（非亮灰）
- [ ] SettingsDialog RadioButton 为自定义样式
- [ ] 库卡片有微妙静止态背景和边框
- [ ] 评分徽章无重复 XAML

---

## 最终验收

### 编译验证

```powershell
cmd /c "rmdir /s /q G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\obj 2>nul & rmdir /s /q G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\bin 2>nul & rmdir /s /q C:\Users\TAKAKAWA\AppData\Local\Temp\opencode\mv_obj 2>nul & rmdir /s /q C:\Users\TAKAKAWA\AppData\Local\Temp\opencode\mv_bin 2>nul & rmdir /s /q C:\Users\TAKAKAWA\AppData\Local\Temp\opencode\mv_baseint 2>nul"

dotnet build "G:\Lanweilig\Heimlich\Karikatur\MangaView\MangaReader.Native\MangaReader.Native.csproj" -c Debug --nologo -p:IntermediateOutputPath="C:\Users\TAKAKAWA\AppData\Local\Temp\opencode\mv_obj\Debug\net8.0-windows\" -p:OutputPath="C:\Users\TAKAKAWA\AppData\Local\Temp\opencode\mv_bin\Debug\net8.0-windows\" -p:BaseIntermediateOutputPath="C:\Users\TAKAKAWA\AppData\Local\Temp\opencode\mv_baseint\"
```

期望：0 错误 0 警告。

### 功能验证

| 验证点 | 方法 |
|--------|------|
| 目录列表流畅 | 打开 200+ 页漫画的目录，无卡顿 |
| 启动速度 | 启动时间明显缩短 |
| 阅读器打开 | 打开阅读器无明显卡顿 |
| 主题切换 | 设置面板切换 Warm/Light/Dark，所有颜色正确 |
| ReaderWindow 外观 | 暗色外观不变（Token 化不改外观） |
| 右键菜单 | 圆角+主题色，非 Windows 默认 |
| ToolTip | 圆角+主题色 |
| 对话框 | 8 个对话框背景边框统一 |
| 库卡片 | 有静止态背景和边框 |

### 残留检查

```powershell
# 检查 XAML 中是否还有硬编码颜色（应在主题文件中无）
rg -n '#[0-9A-Fa-f]{6}' --glob '*.xaml' .
```

期望：硬编码颜色只在 ThemeWarm.xaml/ThemeLight.xaml/ThemeDark.xaml/ReaderTheme.xaml 中出现。

---

## 工作量估算

| 阶段 | 项 | 估算 |
|------|-----|------|
| P0 | 5 项性能修复 | 4 小时 |
| P1 | Token 体系 + 主题拆分 + 去重 | 8-12 小时 |
| P2 | 美观度 8 项 | 4-6 小时 |
| **总计** | | **16-22 小时** |

---

*本文档供 AI 或开发者执行。按 P0 → P1 → P2 顺序，每个 P 完成后编译验证。*
