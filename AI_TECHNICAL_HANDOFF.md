# MangaViewer AI 技术交接文档

本文面向后续接手本项目的 AI / 开发者。目标不是介绍产品，而是说明代码怎么改、哪里容易踩坑、哪些约束必须遵守。

## 1. 当前项目定位

- 主线是 WPF / .NET 8 桌面应用：`MangaReader.Native/`。
- SQLite 本地数据库：默认位于运行目录旁的 `MangaReader_Data/app.db`。
- 自动更新器是独立进程：`MangaReader.Updater/`。
- 仓库里仍保留早期前端/Vite 文件，但当前功能开发默认不要改 `src/`、`dist/`、`package.json`，除非用户明确要求。

## 2. 入口与关键文件

- `MangaReader.Native/MainWindow.xaml`  
  主窗口 UI：侧边栏、首页、书库、标签管理、作者管理、详情面板、批量管理。
- `MangaReader.Native/MainWindow.xaml.cs`  
  主窗口逻辑：扫描书库、筛选排序、首页书架、详情编辑、批量操作、导入、更新检查。
- `MangaReader.Native/ReaderWindow.xaml`  
  阅读器 UI：图片区域、顶部标题栏、底部工具栏、隐藏 UI 提示、下一本确认层。
- `MangaReader.Native/ReaderWindow.xaml.cs`  
  阅读器逻辑：翻页、双页、适宽/适高、滚轮模式、右键长按放大、页码显示、下一本跳转。
- `MangaReader.Native/Models/MangaBook.cs`  
  书籍模型：标题、作者、标签、页数、容量、状态、收藏、UI 绑定文本。
- `MangaReader.Native/Services/LibraryDatabase.cs`  
  SQLite 表结构、迁移、读写元数据、进度、标签、快捷键、备份。
- `MangaReader.Native/Services/LibraryScanner.cs`  
  书库扫描。扫描图片页、推断作者、计算容量。
- `MangaReader.Native/Services/CoverCache.cs` 和 `CoverThumbnailPipeline.cs`  
  封面缓存与缩略图加载。
- `MangaReader.Native/Controls/VirtualizingWrapPanel.cs`  
  书库瀑布流虚拟化面板。性能敏感，不要随意替换成普通 `WrapPanel`。
- `漫画阅读器开发文档.md`  
  开发记录与 UI/交互规范。功能改动必须同步更新。

## 3. 数据模型与数据库

核心表是 `books`，由 `LibraryDatabase.Initialize()` 创建并通过 `EnsureColumn()` 做轻量迁移。新增字段时必须同时改：

1. `CREATE TABLE IF NOT EXISTS books`。
2. `EnsureColumn(connection, "books", "...", "...")`。
3. `LoadBooksByPath()` 的 SELECT 与 reader 映射。
4. `UpsertBookSql`。
5. `AddBookParameters()`。
6. 如果字段会在重定位或特殊保存路径更新，还要改对应 UPDATE 方法。
7. `MangaBook.NotifyAll()` 和相关派生显示属性。

当前重要字段：

- `page_count`：页数。
- `total_bytes`：作品图片总容量，单位是原始字节。显示时在 `MangaBook.SizeText` 转成 MB/G，排序必须按 `TotalBytes` 排，不要按显示字符串排。
- `last_read_page_index`：阅读进度。
- `reading_status`：`unread / reading / finished / paused`。
- `is_favorite`：收藏。
- `rating`：评分（REAL，0~5，步长 0.5）。**只通过 `SaveMetadata` 写入**，扫描器路径不会覆盖。
- `is_hidden`：隐藏作品。
- `book_style`：封面样式。
- `tags`：格式化后的标签字符串。

**"半托管"字段模式**：`is_favorite` 和 `rating` 都有意**不出现**在 `UpsertBookSql` 和 `AddBookParameters` 里。原因是扫描器使用 `UpsertBooksBatch` 写入，会触发 `ON CONFLICT(id) DO UPDATE`；如果把这些字段加进去，每次重新扫描会把用户手设的值覆盖掉。新增"用户私有字段"（不应被扫描器覆盖）时，**故意不写进 UpsertBookSql / AddBookParameters**，只在 `SaveMetadata` 的 UPDATE 列表里出现。新增其他类型字段则需要同步全部 7 处。

数据库写入注意：

- `LibraryDatabase` 里大量写操作会触发备份，尤其元数据编辑会调用 `BackupDatabase("before-metadata-save", ...)`。
- 大批量导入必须用批量事务，例如 `UpsertBooksBatch()`。
- 不要在 UI 线程里做长时间数据库读取或扫描。

## 4. 书库刷新与性能红线

大书库性能是项目核心约束。不要轻易改回逐项刷新。

- `Books`、`VisibleTags`、`ActiveTagFilters`、`TagManagerItems`、`AuthorManagerItems`、`AuthorFilters` 使用 `RangeObservableCollection<T>`。
- 大集合替换用 `ReplaceRange()` / `AddRange()`，不要循环 `ObservableCollection.Add()`。
- 书库瀑布流现在是 UI 层分页：完整筛选/排序结果缓存在 `_pagedSourceBooks`，`Books` 只承载当前页。不要把 `Books` 当成完整筛选结果；需要完整当前视图顺序时用 `_pagedSourceBooks`。
- 分页页大小固定选项为 `25 / 50 / 100`，默认 `50`，保存到 `app_settings` 的 `library.page_size`。筛选、搜索、排序变化回到第一页；单纯元数据刷新尽量保留当前页。
- 批量“全选”和 Shift 范围选择只面向当前页 `Books`，不要无提示扩展到全部筛选结果。
- `RefreshBookFilter()` 不得自动触发 `RefreshHomeShelves()`。书库筛选和首页书架刷新必须解耦。
- 书库列表使用 `VirtualizingWrapPanel`，配置在 `MainWindow.xaml` 的 `BooksList.ItemsPanel`。
- 扫描书库时耗时部分必须放在后台线程，例如现有扫描通过 `Task.Run()`。
- 首页书架更新用序列相等检查避免无意义重绘，相关逻辑在 `RefreshHomeShelves()` / `ReplaceBooks()` 附近。
- 书库顶部筛选区不能因为滚动自动收起，只能由“专注浏览 / 展开筛选”按钮手动切换。

如果新增筛选或排序：

- 筛选条件统一缓存到 `_cachedSearchQuery`、`_cachedAuthorFilter`、`_cachedStatusFilter` 等字段，避免 `FilterBook()` 内反复访问控件。
- 排序在 `ApplyBookSort()`。当前排序项包含标题、进度、页数、容量、阅读次数、录入时间、出品时间；不要恢复“作者排序”，用户已明确认为没必要。
- 新增排序项时同步改 `MainWindow.xaml` 的 `SortBox` 和 `ApplyBookSort()` 的 `SelectedIndex` 映射。

## 5. 阅读器交互约束

阅读器优先级：跟手、沉浸、单字母快捷键、明确激活态。

当前快捷键：

- `W`：全屏。
- `E`：适高。
- `Q`：适宽。
- `D`：显示/隐藏 UI。
- `S`：单页/双页。
- `Z`：切换滚轮模式。
- `X`：退出阅读器。
- `C`：切换左右顺序。
- `A`：背景色循环，黑 / 白 / 纸色。

当前鼠标逻辑：

- 左键点击：按点击位置翻页。左侧约 36% 上一页，右侧下一页。
- 左键双击：不再切换 UI，避免快速翻页误触。
- 中键按下：切换 HUD 显示/隐藏（`SetControlsHidden(!_controlsHidden)`）。**不要恢复**之前的"三态循环窗口/全屏+HUD/全屏-HUD"，用户明确认为过重。全屏入口走 `W` 快捷键。
- 右键长按：临时放大，松开恢复到原来的缩放滑块值。
- 缩放滑块继续保留，右键长按以当前滑块倍率为基准临时放大。
- 滚轮模式：翻页 / 缩放 / 滚动，由 `WheelModeBox` 控制。

翻页与滚动：

- 翻页统一入口 `LoadPageCore`。**所有翻页路径**（左键点击 / 滚轮 / 键盘 / 目录跳页）末尾都会汇聚到这里。
- `LoadPageCore` 在 `ApplyDoublePageGap()` 之后必须 `ReaderScrollViewer.ScrollToVerticalOffset(0)`，避免滚动模式下从第 N 页底部翻到第 N+1 页时垂直位置跨页保留。
- `EnterFullscreen` / `ExitFullscreen` 末尾必须 `ScheduleFitModeApply()`，否则窗口模式设置的适宽/适高在全屏切换后不会按新视口重算（80ms 防抖足够 WindowState 更新到位）。

阅读器页码显示：

- 顶部标题栏 `PageText`。
- 底部工具栏 `BottomPageText`，放在缩放比例前。
- 隐藏 UI 时 `HiddenPageText` 显示在“显示 UI”下方。
- `UpdateNavigationState()` 是页码唯一更新入口之一，不要在 `LoadPage()` 后把 `PageText` 清空。

双页逻辑：

- 双页由 `ReadingModeBox` 控制。
- `_displayedPageCount` 决定下一页步进。
- 双页间距由 `DoublePageGapSlider` 控制，范围 `0-80`，保存到 `shortcuts` 表的 `reader.doublepage.gap`。
- 保存有 `260ms` 防抖，避免拖动滑块时频繁写 SQLite。
- 适宽计算必须读取左右页真实 `Margin`，避免间距影响比例计算。

下一本逻辑：

- 读到最后一页后调用 `TryGoToNextBook()`。
- 下一本必须通过 `MainWindow` 提供的 resolver，遵循当前书库筛选和排序状态。
- 确认层是阅读器内置深色 UI，不要用系统 `MessageBox`。
- `_isNextBookPromptOpen` 是防重入，避免滚轮快速触发多个确认层。

## 6. UI 规范

用户强烈排斥系统默认感、突兀块状感和不统一配色。改 UI 时优先保持极简、紧凑、统一。

圆角语义 Token 在 `MainWindow.xaml` 资源层：

- `RadiusClip`
- `RadiusProgress`
- `RadiusControl`
- `RadiusPanel`
- `RadiusField`
- `RadiusTag`
- `RadiusDialog`
- `RadiusPill`

不要继续增加无语义的零散圆角值。确实需要新半径时先判断是否属于已有语义。

顶部统计区：

- 书库顶部统计已经去掉椭圆浅色底，保持透明纯文本。
- 只保留文字颜色和数字加粗，不要恢复胶囊背景。

首页继续阅读：

- 三张作品必须保持同一行。
- 当前 `HomeWideBookTemplate` 宽度收敛为 `360`，间距 `12`。
- 按钮固定在卡片底部行，避免被标题、作者、进度挤压成圆形或裁切。

详情与书库：

- 信息展示要紧凑，避免冗余字段。
- 书库卡片元信息为一行：`状态 · 页数 · 容量`。
- 容量小于 1G 显示 MB，超过 1G 显示 G。

瀑布流胶囊（评分 + 收藏 合一）：

- 卡片右上角是单一圆角 Border，内含 `RatingText` + `★`，**不要**再恢复成两个独立胶囊。
- 配色由 `IsFavorite` 切换：收藏 → 亮金 `#FFEEAA / #E4B95F / #B45309`；未收藏 → 银灰 `#F3F4F6 / #D1D5DB / #6B7280`。
- `RatingText` 仅在 `HasRating=True` 时显示；`★` 仅在 `IsFavorite=True` 时显示；两者都为 false 时容器折叠。
- "未评" 文案 / 独立 `RatingCapsuleText` 派生属性已废弃，**不要恢复**。

详情页 5 颗星评分编辑器：

- 用 `System.Windows.Shapes.Path` + `Geometry.Parse(StarGeometry)` 标准 5 角星矢量，**不要**用 `★` 字符 TextBlock 双层叠加（会有金灰抗锯齿错位重影）。
- `StrokeLineJoin=PenLineJoin.Round` + 同色 1.5px Stroke 实现倒角圆润。
- 半显示由 Border ClipToBounds + Width 控制；左右半透明 Rectangle 命中区调用 `RatingStar_Click`。
- 保存路径：`book.Rating = newRating; await Task.Run(() => _database.SaveMetadata(book));`——`SaveMetadata` 内部已触发 10 分钟节流备份。

侧栏 Toggle 按钮（隐私模式 / 展开日志）：

- 用 `SidebarToggleButton` 样式，激活态通过 `Tag="active"` 触发深色（`#1F2937 / #FFFFFF`）。
- 隐私模式 / 展开日志按钮的 `Content` **固定不变**，**不要恢复**"隐私模式：开 / 关"切字逻辑。
- 启动时由 `LoadPrivacyMode()` / `UpdateLogPanelVisibility()` 初始化 Tag。

瀑布流批量多选 CheckBox（`BatchSelectCheckBox` 样式）：

- 尺寸 22×22，圆角 6，边框 `#CBD5E1` → 选中 `#1F2937` + 白勾。**不要恢复 30×30**，会让收藏/评分胶囊视觉孤立。

焦点约束：

- 阅读器交互控件尽量 `Focusable="False"`，避免抢走快捷键焦点。
- 新增阅读器控件时尤其注意这个问题。

## 7. 标签、作者与批量管理

标签体系：

- `TagService`：标签解析、格式化、颜色。
- `TagCatalog`：内置标签目录 + **唯一颜色调色板来源** `TagCatalog.PresetColors`（8 色）。两个 Dialog（`TagCreateDialog` / `TagEditDialog`）的 `private static readonly string[] PresetColors` 都引用这一份，不要再在 Dialog 里定义独立色集。
- `managed_tags`：用户管理的标签，`color` 列存调色板颜色。
- `suppressed_tags`：被隐藏的候选标签。
- Tag 颜色属于“分组”，不是单个 Tag。编辑任意 Tag 的颜色会统一该 Tag 所在分组颜色；保存前必须用 `NormalizeManagedTagCategory()` 归一分组，禁止把 Tag 名写成 category。
- 旧数据中如果 `managed_tags.category == tag.Name`，或 category 命中另一个 Tag 名但不是合法预设分组，显示和再次保存时归一为 `自定义`。

Tag 新建对话框（`TagCreateDialog`）规则：

- 创建按钮 `ConfirmButton.IsEnabled` 联动 `TagNameBox.Text` 非空（`TagNameBox_TextChanged` → `UpdateConfirmEnabled`），不允许空名提交。
- `TagCategoryBox` 选已有组 → `ApplyCategorySelection` 强制锁定颜色为该组色、隐藏自定义色按钮、`CategoryHintText` 提示「已选分组 X 下已有 N 个标签，颜色锁定。如需改色请到左侧标签 → 编辑标签」。
- 输入新组名 → 允许选色 + 提示「将创建新分组 X，颜色可自定义」。
- **组色只能从「左侧标签 → 编辑标签」（`TagEditDialog`）修改**，新建对话框是消费方而非编辑面。
- 构造函数 5 个参数：`initialValue, existingCategories, categoryColors, categoryTagCounts, customColors`。修改签名时同步 `MainWindow.xaml.cs:TryResolveTagForCreate` 调用点。

批量管理：

- 多选状态绑定 `MangaBook.IsSelectedForBatch`。
- 批量操作包括去前缀、应用封面样式、批量增删标签。
- 批量改元数据时必须使用数据库批量方法，并考虑备份。

批量模式下卡片交互（`IsBatchSelectionMode=true`）：

- **普通点击卡片**：toggle 单本，并更新 `_batchAnchorBook` 为锚点。
- **Shift+点击**：从 `_batchAnchorBook` 到当前卡片在 `Books` 集合中的范围**全部置 true**。
- **Ctrl+点击**：强制移出选择（`IsSelectedForBatch=false`），更新锚点。
- **空白处拖动框选**：`BooksList_PreviewMouseLeftButtonDown` 检测起点不在 `ListBoxItem` 上时启动；`RubberBandRect` Canvas overlay 跟随鼠标；`PreviewMouseLeftButtonUp` 时遍历 `BooksList.ItemContainerGenerator` 已实例化的 `ListBoxItem` 求交命中，命中的 `IsSelectedForBatch = !_rubberSubtract`。Ctrl+拖 = 减选；Shift+拖 = 加选（不清空既有）；普通拖 = 清空既有再选中框内。
- 框选受 `VirtualizingWrapPanel` 虚拟化限制：**只能命中已实例化的可见项**，框选窗口外滚动区域的卡片**不会**被选中——这是当前实现的已知约束，非 bug。
- `BookCard_PreviewMouseLeftButtonDown` 在 batch 模式下设 `e.Handled = true` 阻止 `BooksList.SelectionChanged` 触发详情面板。CheckBox 子元素仍走自身 toggle，不被卡片点击吃掉。

作者管理：

- 作者筛选仍保留，但排序不按作者。
- 作者管理页复用标签管理页的搜索/列表思路。

## 8. 图片、封面与容量

图片页识别由 `ImageLoader.IsSupportedImage()` 决定。

扫描路径：

- `LibraryScanner.Scan()` 遍历目录，按自然排序收集图片页。
- `BookId.FromFolderPath(folder)` 生成稳定 ID。
- `TryGetAuthorName(rootPath, folder)` 从根目录下第一段推断作者。
- `SumFileBytes(pages)` 计算作品容量。

批量作者导入：

- `BatchImportAnalyzer` 负责候选分析、标签推断、质量标签推断。
- 导入落地在 `MainWindow.xaml.cs` 的作者导入流程，也会计算 `TotalBytes`。

封面：

- `CoverCache` 根据 `CoverPageIndex` 生成或读取缓存。
- 大量封面加载要走缓存和缩略图管线，不要直接在 UI 线程解码原图。

## 9. 自动更新与发布

版本号位置：

- `MangaReader.Native/MangaReader.Native.csproj` 的 `Version`、`AssemblyVersion`、`FileVersion`、`InformationalVersion`。

本地发布（推荐用打包脚本）：

```powershell
# Standalone 自包含（~60MB，不需要 .NET 8 Runtime）
.\pack.ps1 -Mode standalone
# Runtime-dep 轻量版（需要 .NET 8 Runtime）
.\pack.ps1 -Mode runtime-dep
```

脚本会自动从 `icon.png` 生成 `AppIcon.ico`，然后 `dotnet publish` 到 `_release/`。

也可以手动发布：

```powershell
dotnet publish .\MangaReader.Native\MangaReader.Native.csproj -c Release -r win-x64 --self-contained true -o .\_release\0.3.xx
```

发布目录会自动包含 `Updater/MangaReader.Updater.exe`。

更新策略：

1. 优先检查 `_release/更高版本/` 发布目录。
2. 再检查 `_release/` 或 `updates/` 下更高版本 zip。
3. 再检查本地源码版本，如果源码版本更高则自动 `dotnet publish`。
4. 最后才访问 GitHub latest Release。

正式发版：

```powershell
git tag v0.3.xx
git push origin v0.3.xx
```

GitHub Actions 会构建 `MangaReader-win-x64-v*.zip`。

注意：

- `_release/` 不提交。
- `MangaReader_Data/` 不提交。
- 发布新版本必须同步开发文档。

## 10. 调试与验证

最小验证命令：

```powershell
dotnet build .\MangaReader.Native\MangaReader.Native.csproj
```

常用运行：

```powershell
dotnet run --project .\MangaReader.Native\MangaReader.Native.csproj
```

改 UI 后至少检查：

- XAML 是否能编译。
- 阅读器快捷键是否仍有效。
- 书库筛选区是否不会自动收起。
- 首页继续阅读是否仍三张一行。
- 大书库列表是否仍使用虚拟化。

改数据库后至少检查：

- 新装数据库能创建。
- 旧数据库能通过 `EnsureColumn()` 迁移。
- `LoadBooksByPath()` 能正确读旧数据默认值。
- 扫描、重定位、批量导入是否写入新字段。

## 11. 当前重要约束清单

- 功能实现必须同步更新 `漫画阅读器开发文档.md`。
- 不要把耗时扫描、DB 大量读取放到 UI 线程。
- 大集合 UI 刷新不要逐项 Add。
- `RefreshBookFilter()` 不得自动触发 `RefreshHomeShelves()`。
- 阅读器”下一本”必须严格遵循当前书库筛选和排序。
- 阅读器正常翻页不弹中心提示，只在图片读取失败时显示错误。
- 阅读器页切换不要加淡入动画，用户认为不跟手。
- 书库顶部统计不要恢复椭圆底色。
- 首页继续阅读三张卡片保持一行。
- 不要恢复左键双击隐藏 UI。
- 不要恢复作者排序。
- 所有 `_database.*` 写操作必须包裹 `await Task.Run(() => ...)`，不得在 UI 线程直接调用。
- `CoverCache.LoadOrCreate` 必须包裹 `Task.Run`，不得在 UI 线程直接调用。
- `MangaBook.NotifyAll()` 已简化为单次 `PropertyChangedEventArgs(“”)`，不要恢复 38 次逐项通知。
- `SolidColorBrush` 必须缓存为 `static readonly` 并 `Freeze()`，不要每次 new 或调用 `ColorConverter.ConvertFromString`。
- 标签管理页 / 作者管理页使用 Grid 三行布局 + ListBox 内置虚拟化，不要用 ScrollViewer 包裹 ListBox（会导致虚拟化失效）。
- 对话框使用双层 Border 方案（外层承载阴影，内层承载内容），不要在单层 Border 上直接挂 DropShadowEffect。
- 对话框风格统一为冷色浅调（白底 + 浅灰输入框），不要使用暖色奶油调。
- **评分（`rating`）字段**只通过 `SaveMetadata` 写入，扫描器路径（`UpsertBookSql` / `AddBookParameters`）故意不带 `rating`，避免重新扫描覆盖用户评分。新增类似"半托管"字段同样处理。
- 阅读器**翻页时必须 `ReaderScrollViewer.ScrollToVerticalOffset(0)`**，禁止跨页保留垂直滚动位置。
- 阅读器**全屏切换（EnterFullscreen / ExitFullscreen）末尾必须 `ScheduleFitModeApply()`**，否则适宽/适高不会按新视口重算。
- 阅读器**中键只切 HUD**，不要恢复"三态循环窗口/全屏+HUD/全屏-HUD"。
- **瀑布流胶囊保持单容器 + 金银双态**：不要恢复"评分胶囊 + 收藏胶囊"两个独立 Border、不要恢复内部分隔线 + 双 ★ 重复。
- **详情页评分用 Path 矢量 + StrokeLineJoin=Round**，不要恢复 TextBlock ★ 字符双层叠加（金灰重影问题）。
- **`TagCatalog.PresetColors` 是 Tag 颜色调色板唯一来源**，两个 Tag Dialog 都引用它，不要在 Dialog 私有 PresetColors 数组里维护副本。
- **Tag 新建对话框选已有组时颜色锁定**，组色只能在「编辑标签」对话框改。新建按钮 IsEnabled 联动名称非空。
- **批量多选交互**：普通=toggle，Shift=范围加选，Ctrl=强制减选，空白拖=框选；框选受虚拟化限制只对可见 ListBoxItem 生效。
- `BatchSelectCheckBox` 样式 22×22 圆角 6，不要恢复 30×30。
- 全局 `FilterComboBox` 下拉 ScrollViewer 必须 `HorizontalScrollBarVisibility="Disabled"`；长项目通过 ItemTemplate 的 `TextTrimming="CharacterEllipsis"` 截断（AuthorFilterBox 单独加 ItemTemplate）。

## 12. 异步 I/O 模式

`LibraryDatabase` 的所有方法保持同步（`void` / `T` 返回），不做 async 改造。在调用点用 `await Task.Run(() => ...)` 包裹：

```csharp
// 正确：
private async void SaveMetadata_Click(object sender, RoutedEventArgs e)
{
    var book = _currentBook;
    await Task.Run(() => _database.SaveMetadata(book));
    _currentBook.NotifyAll();
    RefreshLibraryViews(tagManager: false, sort: true);
}

// 错误：直接在 UI 线程调用
private void SaveMetadata_Click(object sender, RoutedEventArgs e)
{
    _database.SaveMetadata(_currentBook);  // 阻塞 UI
}
```

关键原则：
- DB 操作在 `Task.Run` 中。
- UI 更新（`NotifyAll`、`RefreshLibraryViews`、`FillMetadataEditors`）在 `await` 之后的 UI 线程。
- Closing 事件中无法 await，用 `_ = Task.Run(...)` fire-and-forget。
- 捕获局部变量（`var book = _currentBook`）传入 `Task.Run`，避免闭包捕获 `this`。

## 13. 建议的改动流程

1. 先读本文件和 `漫画阅读器开发文档.md` 最近版本记录。
2. 用 `rg` 找目标控件、方法或字段。
3. 小范围修改对应 XAML / C#。
4. 如果涉及模型或数据库，按第 3 节同步所有读写路径。
5. 跑 `dotnet build`。
6. 更新 `漫画阅读器开发文档.md`。
7. 提交前检查 `git diff --stat` 和 `git status --short --branch`。

## 14. 未来迭代方向（接手 AI 参考）

本节是基于 0.5.9.18 现状对项目演进给的一个**带优先级**的建议清单。**不是承诺**，用户随时可调整。每条都标了「收益 / 代价 / 风险」三要素。

### P0 — 值得近期做（低风险高价值）

**1. 阅读器内存预读取（Adjacent Page Preload）**
- 当前 `_pageCache` 是被动 LRU（读过才存）；改为：翻到第 N 页后，后台 `Task.Run` 异步预解码 N+1（双页时再 N+2）放入 LRU，扩 LRU 到 8~10 张。
- 入口：`ReaderWindow.xaml.cs` 的 `LoadPageCore` 末尾。当前已有 `ScheduleAdjacentPagePreload` 调用（line 819 附近），评估是否已部分实现 / 需要补强。
- **收益**：HDD/USB 漫画顺翻几乎瞬时响应；对 SSD 也有效。
- **代价**：内存涨几十 MB，无磁盘代价。
- **风险**：0。之前用户已与我讨论过此方案 A 并认可，但选择"暂不动手"，可作为下一轮性能优化的首选。

**2. 升级前自动 schema-upgrade backup**
- 当前 `LibraryDatabase.Initialize()` 直接跑 `EnsureColumn`，老库升级前**无自动备份**——虽然 SQLite ADD COLUMN 原子安全，仍是数据安全的薄弱环节。
- 实现：在 `Initialize()` 检测到任何 `EnsureColumn` 实际执行（返回 true 表示加了列）前，先 `BackupDatabase("before-schema-upgrade", force: true)`。
- **收益**：用户首次升级新版时心理压力 ↓；万一未来加错字段也能回滚。
- **代价**：仅在 schema 变动时多一次拷贝（极少触发）。
- **风险**：0。

**3. 评分系统延伸**
- 按评分筛选（"只看 ≥4 分" / "未评"），加进 `FilterAndSortBooks` 过滤链。
- 批量评分（批量管理面板加"统一设为 X 分"按钮），复用 `SaveMetadata` 批量路径。
- 评分分布统计放进首页或新增"统计"视图。
- **收益**：评分系统真正闭环成用户决策工具。
- **代价**：3~5 个 PR 规模的中等改动。
- **风险**：低。涉及 UI 和过滤逻辑，不动 schema。

### P1 — 中期体验深化

**4. 阅读统计仪表板**
- 利用 `read_count` / `imported_at` / `last_read_page_index` / `reading_status` 现有字段做月度/周度阅读量、完读率、平均评分。
- 入口：首页加 tab 或独立"统计"视图。
- **收益**：让用户更了解自己的阅读习惯，自然增加打开软件频次。
- **代价**：纯只读 UI 工作。
- **风险**：0。
- **注意**：用户偏好极简紧凑，不要堆图表，2~4 个关键数字即可。

**5. 章节书签 / 一本多书签**
- 当前每本书只有 `last_read_page_index`，相当于 1 个隐式书签；用户读漫画时常想"记住某个名场面"。
- 实现：新增表 `book_bookmarks (book_id, page_index, label, created_at)`；阅读器加 `M` 快捷键加书签 / `Shift+M` 列出。
- **收益**：阅读器深度体验提升。
- **代价**：新增表 + 阅读器 UI，约 1 周工作量。
- **风险**：低。新表对老库无影响。

**6. 标签别名 / 合并**
- 用户长期使用后会出现 "CG" / "cg" / "C.G." 等同义标签，需要合并机制。
- 实现：`TagEditDialog` 加"合并到…"按钮；调用 `SaveBookTagsBatch` 批量替换；删除源 tag。
- **收益**：管理效率显著提升。
- **代价**：需要确认弹窗 + 备份保护。
- **风险**：中。批量改 tag 是高破坏性操作，必须**强制备份**+预览影响书籍列表。

**7. 自定义快捷键 UI 完整化**
- 项目已有 `shortcuts` 表 + 部分阅读器快捷键可改；让整套快捷键系统在"设置"里可视化配置。
- **收益**：进阶用户体验。
- **代价**：中等 UI 工作。
- **风险**：0。

**8. 英文界面完整度**
- 双语显示是 §2.1 硬约束，但代码里大量中文字面量（`"未读"` `"已收藏"` `"未评"` 等）散落 XAML / C#。
- 实现：抽取到资源字典 `Strings.zh-CN.xaml` / `Strings.en-US.xaml`，App 启动时按 setting 加载。
- **收益**：兑现双语承诺；为未来日韩用户预留路径。
- **代价**：大量字符串迁移，2~3 周。
- **风险**：低，但回归测试面广。

### P2 — 工程化 / 长期

**9. 单元测试覆盖核心 Service**
- 当前无测试目录。优先级最高的覆盖目标：
  - `LibraryDatabase`：迁移、SaveMetadata 字段完整性、备份触发
  - `TagService`：解析、格式化、颜色推断
  - `BookId.FromFolderPath`：稳定 ID 生成
- **收益**：未来重构 / 加字段时的安全网。
- **代价**：初次搭建框架 + 关键路径覆盖约 1 周。
- **风险**：0。

**10. 崩溃日志收集**
- 当前 `MainWindow_Loaded` 已加 try-catch，但生产崩溃没有写盘。建议捕获 `App.DispatcherUnhandledException` 写入 `MangaReader_Data/logs/crash-{date}.log`。
- **收益**：远程支持 / 自查能力。
- **代价**：约 1 天。
- **风险**：0。

**11. 大书库性能 audit（10000+ 本场景）**
- 当前 1000~3000 本流畅，但用户书库可能持续增长。
- 关注点：扫描时间、首次封面解码总耗时、过滤排序响应、DB 大小。
- 工具：dotnet trace / PerfView 跑一次 profile。
- **收益**：扩展性边界明确。
- **代价**：1~2 周分析 + 针对性优化。
- **风险**：0。

### 明确**不在路线图**的方向（避免后续 AI 误判用户意图）

- ❌ **云同步 / 在线漫画源 / OPDS**：用户硬约束「离线优先」（§2.1 / §3 调研结论）。
- ❌ **压缩包支持（zip / cbz / rar / cbr）**：用户硬约束「文件夹优先」。
- ❌ **抓取远程元数据**：用户硬约束。
- ❌ **移动端 / Web 端**：项目定位是 Windows 桌面，仓库里 `MangaReader.Avalonia/` 是早期试验，**不要**误以为是活跃跨平台分支。
- ❌ **AI 图像增强 / 自动打 tag**：用户没要求；与"管理优先 + 阅读舒适"主线偏离。
- ❌ **"作者排序"**：用户已两次明确拒绝（见 §4 与 §11）。
- ❌ **左键双击切 HUD / 中键三态循环**：见 §5。

### 接手 AI 的几条心法

1. **看到「为什么这么写」的注释为零，就翻 git log 找上下文**——项目刻意不堆 what 注释（用户偏好），但 commit message 记录了 why。
2. **改 UI 前先看 `漫画阅读器开发文档.md` 最近 3~5 个版本记录**——最近的偏好往往覆盖早期文档。
3. **碰到性能问题先问：是 DB？是封面解码？是 UI 线程阻塞？还是用户磁盘 I/O？** 不同根因解法天差地别（如 G 盘 HDD 加缓存到 G 盘本身=无效）。
4. **数据安全永远优先**：评分这种"用户付出心智的字段"必须半托管、必须有备份触发、必须不在扫描器路径里被覆盖。新增类似字段也要遵循。
5. **用户偏好极简紧凑**：宁可少一个按钮，不要多一个按钮；宁可一行信息，不要两行；宁可去掉装饰，不要加。
6. **发布是有节奏的**：用户习惯"改完直接发"+ tag 触发 Actions 出包。中间过程不要 push 半成品 commit。


