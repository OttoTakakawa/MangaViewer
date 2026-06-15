# macOS 版本策略

## 目标

在不破坏现有 WPF Windows 版本的前提下，建立可持续的 macOS 版本路线。

核心结论：不尝试把当前 `MangaReader.Native` 直接迁移到 macOS。当前项目是 `net8.0-windows` + WPF + WinForms，对 macOS 不可编译。推荐路线是先抽离跨平台业务核心 `MangaReader.Core`，再新增 Avalonia 桌面壳 `MangaReader.Avalonia`。

## 当前状态

### Windows 壳

文件：`MangaReader.Native/MangaReader.Native.csproj`

现状：

- `TargetFramework` 是 `net8.0-windows`
- `UseWPF=true`
- `UseWindowsForms=true`
- 发布目标写死 `win-x64`
- 更新器路径是 `Updater\MangaReader.Updater.exe`

结论：该项目必须继续定位为 Windows Native 壳，不承担 macOS 兼容目标。

### 可复用业务逻辑

优先抽离到 Core 的模块：

- `Services/LibraryDatabase.cs`：SQLite 书库、索引、读写逻辑
- `Services/AppStorage.cs`：数据目录、缓存、日志、备份路径
- `Services/LibraryScanner.cs`：目录扫描
- `Services/NaturalPathComparer.cs`：自然排序
- `Services/TagService.cs`、`Services/TagCatalog.cs`：Tag 标准化与颜色
- `Services/BookId.cs`：书籍 ID 生成
- `Models/AuthorItem.cs`
- `Models/BatchImportCandidate.cs`
- `Models/RangeObservableCollection.cs`
- `Models/TagChip.cs`

需要轻度改造后再抽离：

- `Models/MangaBook.cs`：当前耦合 `BitmapSource`，应把封面图像从模型中移除，模型只保留 `CoverPageIndex`、`FolderPath`、`PageCount` 等纯数据。
- `Services/BatchImportAnalyzer.cs`：当前使用 WPF 图像类型，应拆成“文件识别逻辑”和“封面/图片分析逻辑”两层。

### 不可直接复用的 WPF 逻辑

必须留在 Windows 壳或重写为 Avalonia 等价实现：

- `MainWindow.xaml` / `MainWindow.xaml.cs`
- `ReaderWindow.xaml` / `ReaderWindow.xaml.cs`
- 所有 WPF Dialog XAML
- `Controls/VirtualizingWrapPanel.cs`
- `Services/MotionService.cs`
- `Services/ImageLoader.cs`
- `Services/CoverCache.cs`
- `Services/CoverThumbnailPipeline.cs`

原因：这些模块强依赖 `System.Windows`、`System.Windows.Media.Imaging.BitmapSource`、WPF 动画、WPF 虚拟化、WinForms 文件对话框。

## 推荐架构

```text
Manga/
├── MangaReader.Core/
│   ├── Models/
│   ├── Services/
│   ├── Abstractions/
│   └── MangaReader.Core.csproj        # net8.0
│
├── MangaReader.Native/
│   └── WPF Windows 壳                  # net8.0-windows，继续维护
│
├── MangaReader.Avalonia/
│   └── macOS / Windows / Linux UI 壳    # net8.0 + Avalonia
│
└── MangaReader.Updater/
    └── Windows 更新器                  # 首阶段不迁移到 macOS
```

## Core 抽离原则

### Core 不允许引用 UI

`MangaReader.Core` 必须保持：

- 不引用 `System.Windows`
- 不引用 `System.Windows.Forms`
- 不引用 Avalonia
- 不返回 `BitmapSource`、`BitmapImage`、`ImageSource`
- 不弹窗、不启动进程、不直接打开文件夹

Core 只负责：

- 数据库
- 文件扫描
- Tag 解析
- 书籍元数据
- 业务规则
- 路径和缓存策略

### UI 通过接口接入平台能力

建议新增抽象：

```csharp
public interface IAppStorageProvider
{
    string Root { get; }
    string DatabasePath { get; }
    string CoverCachePath { get; }
    string LogsPath { get; }
    string BackupPath { get; }
}

public interface IImageThumbnailService<TImage>
{
    Task<TImage?> LoadCoverAsync(MangaBook book, CancellationToken cancellationToken);
}

public interface IFileDialogService
{
    Task<string?> PickFolderAsync();
    Task<string?> PickDatabaseAsync();
}

public interface IExternalLaunchService
{
    Task OpenFolderAsync(string path);
    Task OpenUrlAsync(string url);
}
```

Windows WPF 壳实现 WPF 版本；Avalonia 壳实现 Avalonia/macOS 版本。

## macOS 数据目录策略

当前 `AppStorage.DefaultRoot` 使用 `AppContext.BaseDirectory/MangaReader_Data`。这适合便携 Windows 版，但不适合 macOS `.app` 包，因为应用包目录不应写入用户数据。

macOS 建议：

```text
~/Library/Application Support/MangaReader/
├── app.db
├── cache/covers/
├── logs/
└── backups/
```

Windows 现有便携模式可以保留，不强制改变。

策略：

- Core 只定义目录结构，不决定平台根目录。
- Windows WPF 壳继续使用当前便携目录。
- macOS Avalonia 壳使用 `Environment.SpecialFolder.ApplicationData` 或显式拼接 `~/Library/Application Support/MangaReader`。

## 图片管线策略

当前 Windows 图片管线依赖 WPF：

- `BitmapImage`
- `BitmapSource`
- `DecodePixelWidth`
- `Freeze()`

macOS 不可复用这些类型。

推荐做法：

1. Core 只保留图片文件枚举、排序、大小统计、封面页索引。
2. Windows 壳保留 WPF `ImageLoader`。
3. Avalonia 壳新增 `AvaloniaImageLoader`，返回 Avalonia 可绑定的 `Bitmap` 或 `IImage`。
4. 缩略图缓存保留“固定尺寸缓存 + LRU + 邻页预解码”的业务策略，但实现类型由平台壳决定。

首版 macOS 不做复杂图像增强，只保证：

- jpg/jpeg/png/webp/bmp/gif/tif/tiff 基础加载
- 书库封面缩略图缓存
- 阅读器单页/双页显示
- 内存压力保护策略可后续迁移

## UI 策略

### 不做 XAML 逐行翻译

WPF XAML 和 Avalonia XAML 语法相近但不等价。当前 `MainWindow.xaml` 体量大，直接翻译会把历史复杂度带到新壳。

macOS 壳采用“功能分层重建”：

1. 书库页
2. 详情页
3. 阅读器
4. Tag 管理
5. 数据安全 / 备份恢复
6. 更新检查

### 视觉规范复用

保留现有视觉方向：

- 极简浅色背景
- 侧边栏固定宽度
- 详情页封面主体化
- 展示态 / 编辑态分离
- 18px 滚动触发区与 6px 轨道
- 单字母快捷键优先级

Avalonia 主题应新建 `MangaTheme.axaml`，不要复制 WPF Template。

## 更新策略

当前 `UpdateService` 偏 Windows：

- 选择 `win-x64`
- 调用 `Updater\MangaReader.Updater.exe`
- 本地发布命令写死 `-r win-x64`

macOS 首版建议：

- 不内置自动更新器
- 在“检查更新”中打开 GitHub Releases
- 发布 `osx-arm64` 和 `osx-x64` 两个包
- 后续再评估 Sparkle 或自研 zip 替换器

理由：macOS 自动更新涉及签名、权限、隔离属性、应用包替换，比 Windows 复杂，不应阻塞首版。

## 打包策略

首阶段目标：

```powershell
dotnet publish MangaReader.Avalonia -c Release -r osx-arm64 --self-contained true
dotnet publish MangaReader.Avalonia -c Release -r osx-x64 --self-contained true
```

后续目标：

- 生成 `.app` bundle
- 添加图标 `.icns`
- `codesign`
- notarization
- zip / dmg 发布

优先级：

1. 先确保本地可运行
2. 再做 `.app`
3. 最后做签名和公证

## 实施阶段

### P0：策略分支

分支：`feature/macos-strategy`

产物：

- 本文档
- 不改动业务代码

验收：

- 文档说明路线、模块边界、风险和阶段
- 分支可推送到 GitHub

### P1：Core 抽离

新增：

- `MangaReader.Core/MangaReader.Core.csproj`
- `MangaReader.Core/Models`
- `MangaReader.Core/Services`
- `MangaReader.Core/Abstractions`

迁移：

- `LibraryDatabase`
- `AppStorage` 的跨平台结构部分
- `TagService`
- `TagCatalog`
- `BookId`
- `NaturalPathComparer`
- `LibraryScanner`
- 纯数据模型

改造：

- `MangaBook` 移除 `BitmapSource? CoverImage`
- Windows 壳用 ViewModel 或绑定适配层保存封面图像

验收：

```powershell
dotnet build MangaReader.Core -c Release
dotnet build MangaReader.Native -c Release
```

### P2：Avalonia 壳 MVP

新增：

- `MangaReader.Avalonia`

首版功能：

- 打开/扫描漫画目录
- 展示书库列表
- 展示详情页
- 打开阅读器
- SQLite 数据读写复用 Core

暂缓：

- 自动更新
- 完整 Tag 管理
- 高级拖拽细节
- 全量动画复刻

验收：

```powershell
dotnet build MangaReader.Avalonia -c Release
dotnet publish MangaReader.Avalonia -c Release -r osx-arm64 --self-contained true
```

### P3：阅读器迁移

重点：

- 单页 / 双页
- 从右到左
- 适配宽度 / 适配高度 / 原始尺寸
- 单字母快捷键优先级
- LRU 页图缓存
- 邻页预解码

验收：

- 大目录打开不卡 UI
- 翻页无明显闪烁
- 快捷键行为与 Windows 版一致

### P4：macOS 发布链路

新增：

- `pack-macos.ps1`
- `pack-macos.sh`
- GitHub Actions macOS job

发布产物：

- `MangaReader-osx-arm64.zip`
- `MangaReader-osx-x64.zip`

后续增强：

- `.dmg`
- 签名
- 公证
- Sparkle 更新

## 风险清单

### 高风险

- `MangaBook` 当前耦合 WPF 图像类型，是 Core 抽离最大阻塞点。
- `ReaderWindow.xaml.cs` 包含大量 WPF 手势、滚动、缩放、预解码逻辑，不能简单复制。
- 自动更新器完全 Windows 化，macOS 首版必须降级为手动更新。

### 中风险

- Avalonia 图片解码与 WPF `DecodePixelWidth` 行为不同，需要重新验证内存占用。
- macOS 文件系统大小写、权限、应用沙盒习惯与 Windows 不同。
- `.app` 包内路径不适合作为数据目录。

### 低风险

- SQLite 可跨平台复用。
- Tag、作者、筛选、排序等业务规则大多可直接迁移。
- 现有 UI 设计规范可以迁移为 Avalonia 主题 token。

## 首版 macOS 范围

必须包含：

- 书库扫描
- 书库列表
- 详情页展示
- 开始阅读
- 收藏
- Tag 展示
- 简介/作品信息展示
- SQLite 持久化

可以延后：

- 自动更新
- 批量作者导入完整体验
- Tag 管理高级编辑
- 数据安全弹窗完整移植
- Windows 风格的托管发布器

## 决策

1. Windows WPF 版继续作为稳定主线。
2. macOS 不基于 WPF，不引入 MAUI。
3. 跨平台 UI 采用 Avalonia。
4. 业务逻辑先抽到 `MangaReader.Core`，Avalonia 壳只负责界面和平台能力。
5. macOS 首版先追求可用与稳定，不追求与 Windows 版 100% 功能同步。
6. 自动更新在首版降级为 GitHub Releases 手动更新。

## 执行记录

### 2026-06-15：P1 Core 抽离第一切片

已完成：

- 新增 `MangaReader.Core`，目标框架为 `net8.0`。
- 迁入第一批跨平台模型：`MangaBook`、`AuthorItem`、`BatchImportCandidate`、`RangeObservableCollection`、`TagChip`。
- 迁入第一批跨平台服务：`LibraryDatabase`、`AppStorage`、`LibraryScanner`、`BookId`、`NaturalPathComparer`、`TagService`、`TagCatalog`。
- `MangaReader.Core.Models.MangaBook` 已移除 `BitmapSource? CoverImage`，Core 不再依赖 WPF 图像类型。
- 新增 `ImageFileService`，只保留图片扩展名识别与文件大小统计，图片解码继续留给平台壳。
- 新增 `IAppStorageProvider`、`IImageThumbnailService<TImage>`、`IFileDialogService`、`IExternalLaunchService`，为后续 Avalonia 壳接入平台能力预留边界。
- `MangaReader.Native` 已添加 `ProjectReference` 指向 `MangaReader.Core`，但暂不替换现有 WPF 运行路径，避免一次性迁移影响 Windows 稳定性。

验证：

```powershell
dotnet build MangaReader.Core\MangaReader.Core.csproj -c Release -nologo
dotnet build MangaReader.Native\MangaReader.Native.csproj -c Release -nologo
```

结果：

- `MangaReader.Core`：0 警告，0 错误。
- `MangaReader.Native`：0 警告，0 错误。
- `MangaReader.Core` 中未发现 `System.Windows`、`System.Windows.Forms`、`BitmapSource`、`BitmapImage`、`MangaReader.Native` 残留引用。

### 2026-06-15：P2/P4 macOS MVP 与发布链路

已完成：

- 新增 `MangaReader.Avalonia`，目标框架为 `net8.0`。
- 使用 Avalonia 11.3.2 搭建跨平台桌面壳，不依赖 WPF/WinForms。
- MVP 功能闭环：
  - 选择漫画根目录；
  - 使用 Core 的 `LibraryScanner` 扫描图片目录；
  - 使用 Core 的 `LibraryDatabase` 持久化 SQLite 书库；
  - 书库列表、搜索、详情页展示；
  - 封面预览、Tag 胶囊、简介、作品信息、文件路径；
  - 收藏状态保存；
  - 阅读器窗口、上一页/下一页、A/D/方向键/空格快捷翻页；
  - 点击“开始阅读”自动增加阅读次数并保存进度。
- macOS 数据目录切到 `~/Library/Application Support/MangaReader`，避免向 `.app` 包内写入用户数据。
- 新增 `pack-macos.ps1` 与 `pack-macos.sh`：
  - 支持 `osx-arm64`；
  - 支持 `osx-x64`；
  - 生成 `.app` bundle；
  - 生成 `MangaReader-osx-arm64.zip` 与 `MangaReader-osx-x64.zip`。
- `.gitignore` 已忽略 `_release_macos/`。

验证：

```powershell
dotnet build MangaReader.Core\MangaReader.Core.csproj -c Release -nologo
dotnet build MangaReader.Native\MangaReader.Native.csproj -c Release -nologo
dotnet build MangaReader.Avalonia\MangaReader.Avalonia.csproj -c Release -nologo
powershell -ExecutionPolicy Bypass -File .\pack-macos.ps1
```

结果：

- `MangaReader.Core`：0 警告，0 错误。
- `MangaReader.Native`：0 警告，0 错误。
- `MangaReader.Avalonia`：0 警告，0 错误。
- 已产出 `_release_macos/MangaReader-osx-arm64.zip`，约 41.21 MB。
- 已产出 `_release_macos/MangaReader-osx-x64.zip`，约 42.79 MB。

限制：

- 当前 macOS 包未做 Apple Developer ID 签名与 notarization，首次运行可能需要用户在系统安全设置中确认。
- 自动更新仍按策略降级为打开 GitHub Releases。

### 2026-06-16：阅读器能力补齐与签名链路预留

已完成：

- Avalonia 阅读器新增单页/双页切换。
- 新增适宽、适高、原始尺寸三种显示模式。
- 新增缩放滑条，支持 0.1x 到 3x。
- 新增快捷键：
  - `A` / 左方向：上一页；
  - `D` / 右方向 / 空格：下一页；
  - `Q`：单页/双页切换；
  - `W`：适高；
  - `E`：适宽；
  - `Esc`：关闭阅读器。
- 新增 5 项 LRU 页图缓存。
- 新增 90ms 延迟邻页预加载。
- 阅读器翻页后保存 `LastReadPageIndex`。
- `pack-macos.ps1` 与 `pack-macos.sh` 已支持可选签名与公证参数：
  - `SignIdentity` / `SIGN_IDENTITY`；
  - `Notarize` / `NOTARIZE=1`；
  - `AppleId` / `APPLE_ID`；
  - `AppleTeamId` / `APPLE_TEAM_ID`；
  - `AppleAppPassword` / `APPLE_APP_PASSWORD`。

验证：

```powershell
dotnet build MangaReader.Core\MangaReader.Core.csproj -c Release -nologo
dotnet build MangaReader.Native\MangaReader.Native.csproj -c Release -nologo
dotnet build MangaReader.Avalonia\MangaReader.Avalonia.csproj -c Release -nologo
powershell -ExecutionPolicy Bypass -File .\pack-macos.ps1
```

结果：

- `MangaReader.Core`：0 警告，0 错误。
- `MangaReader.Native`：0 警告，0 错误。
- `MangaReader.Avalonia`：0 警告，0 错误。
- 已重新产出 `_release_macos/MangaReader-osx-arm64.zip`，约 41.21 MB。
- 已重新产出 `_release_macos/MangaReader-osx-x64.zip`，约 42.79 MB。

外部阻塞：

- Apple Developer ID 签名与 notarization 需要 macOS 环境、Apple Developer 账号、Developer ID Application 证书、App-specific password 或 App Store Connect API Key。当前 Windows 环境只能把脚本链路补齐，不能实际完成 Apple 公证。

### 2026-06-16：PC 样式一致性对齐

决策调整：

- macOS/Avalonia 壳不追求 macOS 原生视觉。
- 目标改为与 PC/WPF 版样式一致：同一主题 token、同一侧边栏规格、同一书库信息架构、同一阅读器控制层级。

已完成：

- 新增 `MangaReader.Avalonia/Styles/MangaTheme.axaml`，同步 PC 版 `MangaTheme.xaml` 的颜色、Brush、控件高度、圆角和滚动条触发区基础规格。
- Avalonia 主窗口标题从 `MangaReader macOS MVP` 改为 `MangaReader`，去掉平台割裂感。
- Avalonia 主壳改为 PC 版同构结构：
  - 左侧固定 `132px` 侧边栏；
  - `主页 / 书库 / 标签 / 作者` 导航；
  - 底部 `指定数据 / 检查更新` 操作；
  - 书库页保留搜索、收藏筛选、排序；
  - 主页增加书库数量、收藏数量、数据目录信息；
  - 标签页和作者页使用与 PC 版一致的管理入口结构。
- Avalonia 书库详情区继续沿用 PC 版“作品详情页”层级：封面、主标题、作者行、页数/状态、收藏、开始阅读、标签胶囊、简介、作品信息、文件区。

验证：

```powershell
dotnet build MangaReader.Core\MangaReader.Core.csproj -c Release -nologo
dotnet build MangaReader.Native\MangaReader.Native.csproj -c Release -nologo
dotnet build MangaReader.Avalonia\MangaReader.Avalonia.csproj -c Release -nologo
powershell -ExecutionPolicy Bypass -File .\pack-macos.ps1
```

结果：

- `MangaReader.Core`：0 警告，0 错误。
- `MangaReader.Native`：0 警告，0 错误。
- `MangaReader.Avalonia`：0 警告，0 错误。
- 已重新产出 `_release_macos/MangaReader-osx-arm64.zip`，约 41.22 MB。
- 已重新产出 `_release_macos/MangaReader-osx-x64.zip`，约 42.80 MB。

### 2026-06-16：PC 等价操作补齐

继续收口：

- Avalonia 详情页补齐 PC 版常用管理动作：
  - 收藏 / 取消收藏；
  - 编辑 / 取消 / 保存；
  - 打开所在文件夹；
  - 重新定位目录；
  - 隐藏 / 恢复作品；
  - 删除库记录；
  - 封面页与阅读次数编辑。
- 书库筛选补齐隐藏作品开关，默认不显示隐藏作品。
- 日志面板补齐 PC 版约束：
  - 默认高度 `160px`；
  - 扩大高度 `420px`；
  - 最多保留 `300` 行；
  - 单行最多 `900` 字符。
- 主页统计会跟随收藏、删除、扫描和编辑操作刷新。

边界：

- 删除操作只删除 SQLite 库记录，不删除用户磁盘文件。
- macOS/Avalonia 仍然使用独立前端实现，但视觉规格、页面层级、关键动作和约束继续向 PC/WPF 版对齐。

### 2026-06-16：PC 完整复刻继续收口

继续补齐：

- 书库页对齐 PC 版筛选密度：
  - 标题/作者/标签全文搜索；
  - Tag 快速筛选；
  - 作者下拉筛选；
  - 阅读状态筛选；
  - 收藏筛选；
  - 隐藏作品开关；
  - 活动 Tag 筛选摘要与清除筛选。
- 详情页补齐 PC 版作品信息字段：
  - 角色名；
  - 出品时间；
  - 阅读次数；
  - 文件体积；
  - 阅读状态编辑。
- 标签管理页从只读列表升级为可操作页面：
  - 新增标签；
  - 标签搜索；
  - 按标签筛选书库；
  - 重命名标签；
  - 修改分组与颜色；
  - 删除标签并同步移除书籍上的该标签。
- 作者管理页从只读列表升级为可操作页面：
  - 新增作者；
  - 作者搜索；
  - 按作者筛选书库；
  - 重命名作者并同步更新关联书籍。
- 阅读器继续对齐 PC 视觉 token，移除硬编码深色，改用 `Brush.TextPrimary` / `Brush.Surface` / `Brush.BorderStrong` 等主题资源。

说明：

- “完全复刻 PC”在 macOS 端仍受技术栈差异影响：WPF 与 Avalonia 控件模板不同，不能逐像素复用 XAML；当前策略是视觉 token、布局规格、页面层级、功能动作、快捷键与数据行为对齐。
