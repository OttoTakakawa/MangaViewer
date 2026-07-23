# MangaViewer 项目指令

## 基本信息

- 用户叫 **苏博子**，所有交流使用中文。
- 项目路径：`G:\Lanweilig\Heimlich\Karikatur\MangaView`
- 技术栈：WPF / .NET 8 + SQLite（Microsoft.Data.Sqlite）+ Windows x64
- GitHub：`OttoTakakawa/MangaViewer`
- 当前主线：`MangaReader.Native/`（WPF 应用）
- 自动更新器：`MangaReader.Updater/`（独立进程）

## 每次改动必须执行

1. **更新版本号**：修改 `MangaReader.Native/MangaReader.Native.csproj` 中的四个字段：
   - `<Version>` — 如 `0.3.97`
   - `<AssemblyVersion>` — 如 `0.3.97.0`
   - `<FileVersion>` — 同 AssemblyVersion
   - `<InformationalVersion>` — 同 Version

2. **更新开发文档**：在 `漫画阅读器开发文档.md` 末尾追加版本记录，格式：
   ```
   ### YYYY-MM-DD 原生软件 0.3.xx

   **主题：简短描述**

   - 具体改动条目
   - 已通过 `dotnet build`，0 警告 0 错误。
   ```

3. **编译验证**：`dotnet build` 必须 0 警告 0 错误。

4. **检查清单**（改动后自检）：
   - XAML 能编译通过
   - 阅读器快捷键仍然有效
   - 书库筛选区不会自动收起
   - 首页继续阅读仍三张一行
   - 大书库列表仍使用虚拟化面板

## 项目结构

```
MangaView/
├── MangaReader.Native/              # 主线 WPF 应用
│   ├── App.xaml(.cs)                # 全局异常处理、日志初始化
│   ├── MainWindow.xaml(.cs)         # 主窗口：书库管理、导航、详情（~3200 行 God Class）
│   ├── ReaderWindow.xaml(.cs)       # 阅读器：翻页、缩放、双页、快捷键
│   ├── ImportFolderDialog.xaml(.cs) # 文件夹导入窗口
│   ├── AuthorBatchImportDialog.xaml(.cs) # 作者批量导入确认
│   ├── TagNameDialog / TagCreateDialog / TagEditDialog / RenameDialog
│   ├── Models/
│   │   ├── MangaBook.cs             # 核心书籍模型（~30 属性，INotifyPropertyChanged）
│   │   ├── TagChip.cs               # 标签展示模型
│   │   ├── AuthorItem.cs            # 作者列表项
│   │   ├── BatchImportCandidate.cs  # 批量导入候选
│   │   └── RangeObservableCollection.cs # 批量通知集合（性能关键）
│   ├── Services/
│   │   ├── LibraryDatabase.cs       # SQLite 持久化（books, tags, shortcuts, backups）
│   │   ├── LibraryScanner.cs        # 文件系统扫描 → MangaBook
│   │   ├── CoverCache.cs            # 磁盘缩略图缓存（PNG）
│   │   ├── CoverThumbnailPipeline.cs # 异步 LRU 内存缓存 + 并发限流
│   │   ├── TagService.cs            # Tag 解析/格式化/分类/互斥
│   │   ├── TagCatalog.cs            # 内置 Tag 预设 + 颜色分配
│   │   ├── MotionService.cs         # WPF 动画统一调度
│   │   ├── UpdateService.cs         # 多源更新检查与下载
│   │   ├── AppLogger.cs             # 日志系统（每日日志 + crash 快照）
│   │   ├── AppStorage.cs            # 数据目录解析（默认/自定义路径）
│   │   ├── BookId.cs                # SHA256 稳定书籍 ID
│   │   ├── ImageLoader.cs           # WPF BitmapImage 安全加载
│   │   ├── NaturalPathComparer.cs   # 自然排序比较器
│   │   └── BatchImportAnalyzer.cs   # 智能 Tag 推断（图像分析）
│   └── Controls/
│       └── VirtualizingWrapPanel.cs # 虚拟化多列面板（IScrollInfo）
├── MangaReader.Updater/             # 自动更新器（独立进程）
│   └── Program.cs                   # 等待主进程 → 替换文件 → 重启
├── .github/workflows/release.yml    # GitHub Actions CI/CD
├── pack.bat / pack.ps1              # 本地打包脚本
├── Data/                            # 运行时数据（.gitignore）
│   ├── app.db                       # SQLite 数据库
│   ├── backups/                     # 数据库备份（保留 40 份）
│   ├── cache/covers/                # 封面缩略图
│   ├── logs/                        # 日志文件
│   └── updates/                     # 本地更新包
├── _release/                        # 发布产物（.gitignore）
└── 漫画阅读器开发文档.md             # 开发记录（必须同步更新）
```

## 性能红线

- 大集合（`Books`、`VisibleTags`、`TagManagerItems`、`AuthorManagerItems`、`AuthorFilters`）使用 `RangeObservableCollection<T>` + `ReplaceRange()`/`AddRange()`，**禁止逐项 `Add()`**。
- `RefreshBookFilter()` **不得**自动触发 `RefreshHomeShelves()`。书库筛选和首页书架刷新必须解耦。
- 耗时操作（扫描、数据库读取、图片解码）**不放在 UI 线程**，使用 `Task.Run()`。
- 书库列表使用 `VirtualizingWrapPanel`，**禁止替换为普通 `WrapPanel`**。
- 搜索框使用 220ms 防抖（`DispatcherTimer`），不即时触发全量过滤。
- 筛选条件缓存到 `_cachedSearchQuery` 等字段，`FilterBook()` 内不反复访问控件。

## 阅读器约束

- 下一本必须遵循当前书库筛选和排序状态（通过 `MainWindow.ResolveNextBookInCurrentView`）。
- **不要恢复**左键双击隐藏 UI。
- 翻页**不加淡入动画**（用户认为不跟手）。
- 正常翻页不弹中心提示，只在图片读取失败时显示错误。
- 新增阅读器控件必须 `Focusable="False"`，避免抢走快捷键焦点。
- 左键点击按位置翻页（左侧 ~36% 上一页，右侧下一页）。
- 右键长按临时放大，松开恢复。
- 确认层是阅读器内置深色 UI，不用系统 `MessageBox`。
- 快捷键：W 全屏、E 适高、Q 适宽、D 显隐 UI、S 单双页、Z 滚轮模式、X 退出、C 左右顺序、A 背景色。

## UI 规范

- 圆角按语义 Token 使用，不写无语义零散值：
  | Token | 值 | 用途 |
  |---|---:|---|
  | `RadiusClip` | 4 | 封面裁切、小图层 |
  | `RadiusProgress` | 5 | 进度条 |
  | `RadiusControl` | 9 | 按钮、列表项 |
  | `RadiusPanel` | 10 | 信息卡、筛选区 |
  | `RadiusField` | 11 | 输入框、下拉框 |
  | `RadiusTag` | 16 | Tag 芯片 |
  | `RadiusDialog` | 32 | 弹窗、大遮罩 |
  | `RadiusPill` | 999 | 胶囊按钮 |
- 书库统计**不恢复**椭圆底色，保持透明纯文本。
- 首页继续阅读三张卡片保持一行（宽度 360，间距 12）。
- **不要恢复**作者排序。
- 书库卡片元信息一行：`状态 · 页数 · 容量`。
- 用户排斥系统默认感、突兀块状感和不统一配色。

## 数据安全

- 扫描/重导/再次导入**不覆盖**用户手工 Tag。智能推断只对新书首次创建生效。
- 批量操作（Tag 重命名/删除、元数据批量修改）前**必须备份**。
- `books.tags` 是每本漫画 Tag 的真实保存位置；`managed_tags` 只是候选池和管理视图。
- 删除库记录只删除软件记录，**不删除**硬盘漫画源文件。
- 数据库迁移前自动备份；备份保留最近 40 份，节流 10 分钟。

## 数据库改动检查清单

新增 `books` 字段时必须同步改：
1. `CREATE TABLE IF NOT EXISTS books`
2. `EnsureColumn()` 迁移
3. `LoadBooksByPath()` 的 SELECT 与 reader 映射
4. `UpsertBookSql`
5. `AddBookParameters()`
6. 相关 UPDATE 方法（重定位、批量保存等）
7. `MangaBook.NotifyAll()` 和派生显示属性

## 发布流程

### 本地发布
```powershell
dotnet publish .\MangaReader.Native\MangaReader.Native.csproj -c Release -r win-x64 --self-contained true -o .\_release\0.3.xx
```
发布目录会自动包含 `Updater/MangaReader.Updater.exe`。

### 正式发版
```powershell
git tag v0.3.xx
git push origin v0.3.xx
```
GitHub Actions 自动构建 `MangaReader-win-x64-v*.zip` 并上传 Release。

### 本地打包脚本
```powershell
.\pack.bat        # 交互式选择 standalone / runtime-dep
.\pack.ps1 -Mode standalone  # 直接发布
```

## 更新策略（本地优先）

1. **本地 `_release/` 目录**：查找更高版本的发布目录或 zip。
2. **本地源码编译**：如果 `.csproj` 版本更高，自动 `dotnet publish` 到 `Data/updates/local-build-*`。
3. **GitHub Release（兜底）**：下载 `MangaReader-win-x64-v*.zip`。

更新器 `MangaReader.Updater.exe` 替换文件时跳过 `MangaReader_Data/` 和 `MangaReader_DataLocation.txt`。

## 数据目录

- 默认：`.exe` 同级 `MangaReader_Data/`
- 自定义：`.exe` 同级 `MangaReader_DataLocation.txt` 写入自定义路径
- 子目录：`app.db`、`backups/`、`cache/covers/`、`logs/`、`updates/`
- 切换策略：下次启动生效，不在运行时热切换

## 架构注意事项

- `MainWindow.xaml.cs` 是 ~3200 行的 God Class，后续按功能域渐进拆分 Service。
- 不做一次性 MVVM 大重构。
- Tag 颜色由 `TagCatalog` 统一管理，不在各处硬编码。
- 封面缩略图走 `CoverThumbnailPipeline`（LRU 320 张 + 并发 2），不在 UI 线程解码原图。
- 动画统一走 `MotionService`，不在窗口层散写 `Storyboard`。
- 日志走 `AppLogger`（`Data/logs/`），不用 `Console.WriteLine`。
