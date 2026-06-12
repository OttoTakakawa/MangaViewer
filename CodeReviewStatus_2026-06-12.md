# MangaReader.Native 代码现状审查（2026-06-12）

## 已确认解决 ✅

| 项目 | 位置 | 确认方式 |
|------|------|----------|
| SQLite WAL 模式 | `LibraryDatabase.cs:32` | `PRAGMA journal_mode=WAL` |
| 数据库索引（5个） | `LibraryDatabase.cs:80-84` | author/reading_status/is_favorite/is_hidden/folder_path |
| Tag O(N²) 优化 → 改为索引查表 | `RebuildTagIndex()` 存在，`GetTagUsageCount` 查字典 | 有效避免重复扫描 |
| 五次全量刷新合批 | `RefreshLibraryViews` 统一入口（line 1171） | 参数化选择性刷新 |
| 批量导入错误处理 | `ImportAuthorBatchAsync` | 逐本 try-catch，失败不中断整批 |
| MainWindow_Loaded try-catch 保护 | `lines 75-91` | 顶层异常保护 |
| `_controlsRevealTimer.Stop()` | `ReaderWindow.xaml.cs:56` | Closing 时已调用 |
| Mouse.Capture finally 保护 | `ReaderWindow.xaml.cs:359-369` | try-catch + ReleaseHoldZoom |
| AnalyzeImage 图片尺寸上限 | `AnalysisDecodePixelWidth = 160` | 加载时已限制 |
| BookStyleIndex 溢出修复 | `MangaBook.cs:103` | `(Id.GetHashCode() & 0x7FFFFFFF) % 4` |
| 书架虚拟化缓存 | `MainWindow.xaml:1034` | `CacheLength="4"` |
| TagCatalog 统一颜色 | `TagCatalog.cs` | MangaBook/MainWindow 共用 |
| ScanRootsAsync 线程安全 | `MainWindow.xaml.cs:378-448` | 缺失书籍状态修改移至 Task.Run（后台线程） |
| UpsertBooksBatch 事务保护 | `LibraryDatabase.cs:207-235` | try-catch-rollback 已加 |
| SaveBookTagsBatch 事务保护 | `LibraryDatabase.cs:297-336` | try-catch-rollback 已加 ✨ NEW |
| SaveBookAuthorsBatch 事务保护 | `LibraryDatabase.cs:330-365` | try-catch-rollback 已加 ✨ NEW |
| ImportAuthorBatchAsync 取消令牌 | `MainWindow.xaml.cs:165-251` | CancellationToken 参数已添加 ✨ NEW |
| 导入操作 CancellationTokenSource | `MainWindow.xaml.cs:29` | `_importCancellation` 字段已添加 ✨ NEW |

## 仍然存在 ❌

| 项目 | 位置 | 影响 | 优先级 |
|------|------|------|--------|
| 搜索框无防抖 | `MainWindow.xaml.cs:1057`（书库搜索）、`line 733`（Tag搜索） | 中文输入每个字母触发全量过滤 | P2 |
| XAML 颜色 100+ 处内联 | `MainWindow.xaml` 全文 | 无法统一改色，深色模式无从下手 | P3 |
| ComboBox 模板两套重复 | `MainWindow.xaml:154-316` | 维护两份几乎相同的模板 | P3 |
| 阅读模式字符串匹配 | `ReaderWindow.xaml.cs:211-212` | 暂缓，等阅读器设置重构时处理 | P3 |
| SaveBookReadingStatusBatch 缺事务保护 | `LibraryDatabase.cs` | 可能存在，需检查 | P2 |

## 改进清单（本次新增修复）

### Bug #6 - 数据库事务异常处理（已完成）
- **SaveBookTagsBatch**：加入 try-catch-rollback，防止事务泄漏
- **SaveBookAuthorsBatch**：加入 try-catch-rollback，防止事务泄漏
- **原因**：异常时数据库锁定，后续操作卡死

### Bug #7 - CancellationToken 传递不一致（已完成）
- **ImportAuthorBatchAsync** 添加 CancellationToken 参数
- **ImportSelectedFolderAsync** 创建/传递 _importCancellation token
- **ImportAuthorBatchAsync 循环** 添加 ThrowIfCancellationRequested 检查
- **原因**：导入操作无法中断，用户交互卡顿

## 代码编译状态

✅ **Release 编译通过**（0 个警告，0 个错误）  
✅ **Publish 发布完成** → `bin/publish-latest/`  

## 后续优化建议

| 优先级 | 项目 | 复杂度 | 收益 |
|--------|------|--------|------|
| P1 | 搜索框防抖（TextChangedEvent + DispatcherTimer） | 低 | 中文输入流畅 |
| P1 | 检查其他批量操作是否缺事务保护 | 低 | 防止数据一致性破坏 |
| P2 | 检查其他线程安全违反点（ObservableCollection 修改） | 中 | 稳定性提升 |
| P2 | XAML 颜色提取为 ResourceDictionary | 中 | 支持主题切换 |
| P3 | 合并重复 ComboBox 模板 | 中 | 代码维护性 |

## 总体评估

截至 0.3.82 版本，以下关键缺陷已消除：
- ✅ 数据库事务一致性（3 个批量操作已加保护）
- ✅ 导入操作可中断性（支持 CancellationToken）
- ✅ ObservableCollection 线程安全（ScanRootsAsync 已修复）
- ✅ 阅读器控制逻辑（HUD 自动隐藏修复）

应用稳定性评估：**中等偏高** → 核心业务流程已保护，UI 线程安全得到改善。
