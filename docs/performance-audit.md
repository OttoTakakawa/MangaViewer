# 性能优化审计报告 v0.7.3.3

> 审计日期：2026-06-20
> 基线版本：v0.7.3.3

## 一、P0 — 立即可修复，效果显著

### P0-1 缺少 `last_opened_at` 索引
- **位置**: `LibraryDatabase.cs:127-132`（Initialize 方法中的 CREATE INDEX 序列）
- **现状**: 已有 author、reading_status、is_favorite、is_hidden、folder_path、book_bookmarks(book_id) 共 6 个索引，但缺少 `last_opened_at`
- **影响**: 首页"继续阅读"按 `LastOpenedAt` 排序（MainWindow.xaml.cs:4331），无索引导致全表排序
- **修复**: 在 Initialize 中添加 `CREATE INDEX IF NOT EXISTS idx_books_last_opened_at ON books(last_opened_at);`

### P0-2 多处同步 DB 调用在 UI 线程
- **位置**:
  - `MainWindow.xaml.cs:219` — `LoadLibraryRoots()`
  - `MainWindow.xaml.cs:5045` — `LoadManagedTags()`
  - `MainWindow.xaml.cs:5058` — `LoadSuppressedTags()`
  - `MainWindow.xaml.cs:806` — `LoadSetting()`
  - `MainWindow.xaml.cs:1513` — `LoadBookmarks()`
  - `ReaderWindow.xaml.cs:1878` — `LoadShortcuts()`
- **影响**: UI 线程阻塞，用户感知卡顿
- **修复**: 用 `Task.Run` 包装或改为异步方法

### P0-3 RebuildTagIndex 频繁全量重建
- **位置**: `MainWindow.xaml.cs:5003-5025`
- **现状**: `RefreshLibraryViews` 中 tags=true 时全量重建 `_tagBooksByName`，而 `RefreshLibraryViews` 被调用 51 次
- **影响**: 操作后 UI 延迟
- **修复**: 实现增量更新，仅在书籍 tag 实际变更时更新索引

## 二、P1 — 中等优先级

### P1-1 LoadBooksByPath 查询全部 23 列
- **位置**: `LibraryDatabase.cs:183-227`
- **现状**: `SELECT ... FROM books` 全表扫描，包含 summary 等大字段
- **影响**: 启动慢、内存浪费
- **修复**: 拆分为 `LoadBooksByPathForList`（仅列表字段）+ `LoadBookDetail`（全部字段）

### P1-2 GetCachePath 每次访问文件系统
- **位置**: `CoverCache.cs:46-51`
- **现状**: `File.GetLastWriteTimeUtc(coverPage).Ticks` 每次获取缓存路径都访问磁盘
- **影响**: 封面加载延迟
- **修复**: 在 MangaBook 上缓存封面文件修改时间戳

### P1-3 Reader 页面缓存无字节追踪
- **位置**: `ReaderWindow.xaml.cs:1051-1070`
- **现状**: `MaxQualityReaderPageCacheEntries=3`，不追踪实际内存占用。高质量模式下一页 3000×4000 BGRA ≈ 48MB，3 页 144MB
- **影响**: 大图内存溢出风险
- **修复**: 增加字节大小追踪，类似 `_qualityFitCacheBytes` 机制

### P1-4 锐化处理 CPU 密集
- **位置**: `ReaderWindow.xaml.cs:1373-1401`
- **现状**: `SharpenBgraPixels` 对每个像素执行 4 邻域拉普拉斯锐化 + `(byte[])pixels.Clone()` 全量拷贝
- **影响**: 3000×4000 图片处理 48MB 数据，翻页延迟 >100ms
- **修复**: 使用 `Span<byte>` 优化；考虑 SIMD 加速；大图降低锐化精度

## 三、P2 — 可优化

### P2-1 每次操作创建新 DB 连接
- **位置**: `LibraryDatabase.cs:1019-1027`
- **现状**: 每个操作 `new SqliteConnection` + PRAGMA 设置 + 操作 + 关闭
- **修复**: 连接池或长期连接复用，PRAGMA 移到初始化阶段

### P2-2 封面并发限制仅 2
- **位置**: `CoverThumbnailPipeline.cs:10` — `SemaphoreSlim(2)`
- **现状**: 最多 2 个并发封面加载
- **修复**: 提高到 4-6，或根据 CPU 核心数动态调整

### P2-3 Tag 过滤 O(N×M×K)
- **位置**: `MainWindow.xaml.cs:3978-3983`
- **现状**: 对每本书的每个 activeTag 执行 `TagItems.Any()` 线性扫描
- **修复**: 预构建 `HashSet<string>`，用 `IsSupersetOf` 替代

### P2-4 RefreshHomeShelves 4 次遍历
- **位置**: `MainWindow.xaml.cs:4312-4365`
- **现状**: 4 个 LINQ 查询分别遍历 homeBooks
- **修复**: 合并为单次遍历按条件分类

## 四、P3 — 长期改进

### P3-1 SaveMetadata 每次触发备份检查
- **位置**: `LibraryDatabase.cs:292-339`
- **现状**: `BackupDatabase("before-metadata-save", force: ShouldCreateMetadataBackup())` 涉及文件复制
- **修复**: 备份移到后台线程异步执行

### P3-2 封面缓存无字节大小限制
- **位置**: `CoverThumbnailPipeline.cs:8` — `MaxMemoryCovers=320`
- **现状**: LRU 按条目数淘汰（320 个），不追踪字节大小，约 110MB
- **修复**: 增加字节大小淘汰阈值（如 64MB）

### P3-3 页面目录缩略图逐个加载
- **位置**: `ReaderWindow.xaml.cs:2238-2272`
- **现状**: 200 页漫画需要 200 次独立 `ImageLoader.LoadBitmap`
- **修复**: 批量预解码或仅加载可见范围

### P3-4 EnumerateKnownTags 每次全量扫描
- **位置**: `MainWindow.xaml.cs:4993-5001`
- **现状**: 每次遍历所有书籍所有 tag 再去重
- **修复**: 直接使用 `_tagBooksByName.Keys`

### P3-5 _allBooks 使用 List 而非索引
- **位置**: `MainWindow.xaml.cs:106`
- **现状**: `List.Remove` 是 O(N)，按 ID 查找也是 O(N)
- **修复**: 维护并行 `Dictionary<string, MangaBook>` 索引

## 五、已修复问题

| 版本 | 问题 | 修复方式 |
|------|------|----------|
| v0.7.3.1 | 目录间距过大 | VirtualizingWrapPanel → WrapPanel + Margin |
| v0.7.3.1 | CheckBox 换色 | IsChecked 触发器去掉整条变色 |
| v0.7.3.2 | 主页评分徽章被裁剪 | 徽章移到整个横向容器右上角 |
| v0.7.3.3 | 点击标题复制无反馈 | 添加 Toast 提示 |
| v0.7.3.3 | 复制后显示"失败" | SafeSetClipboard 重试异常不再冒泡 |
| v0.7.3.3 | 复制时 UI 卡顿 | Thread.Sleep → Dispatcher.BeginInvoke 异步 |
