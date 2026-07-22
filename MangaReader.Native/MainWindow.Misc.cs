using MangaReader.Native.Models;
using MangaReader.Native.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MangaReader.Native;

// MainWindow 分部类：杂图总集（misc_images）相关逻辑。
// 独立于 MainWindow.BookCards.cs / MainWindow.Import.cs。
// 不参与反向规整 / 批量整理 / MetaFetcher 流程。
public partial class MainWindow
{
    private const string MiscCategoryAll = "";
    private const string MiscCategoryReal = "真人";
    private const string MiscCategoryArt = "绘画";

    // 杂图 Tag 拖拽使用独立 data format，避免与漫画 Tag 拖拽串台。
    // 漫画 Tag 池拖到杂图卡片不会被识别；反之亦然。两套体系完全隔离。
    private const string MiscTagDragDataFormat = "MangaReader.MiscTagName";

    private readonly ObservableCollection<MiscImage> _allMiscImages = new();
    private readonly ObservableCollection<MiscImage> _filteredMiscImages = new();
    private CancellationTokenSource? _miscThumbCts;
    private CancellationTokenSource? _miscLoadCts;
    private string _miscCategoryFilter = "";
    private string? _miscTagFilter; // null = 不限制，非空 = 必须包含该 tag
    private bool _miscFavoriteOnly;

    // 杂图独立 Tag 池：与漫画 TagPool 完全隔离的数据源，绑定到 MiscPagePanel 顶部的 Tag 池 UI。
    // chip 可拖拽到任意杂图卡片完成 tag 分配，也可点击作为筛选条件（与 MiscTagFilterBox 同步）。
    public RangeObservableCollection<TagChip> MiscTagPoolItems { get; } = [];

    // 杂图 chip 拖拽轨迹（参考漫画 TagChip 拖拽实现，独立字段避免共享状态）。
    private System.Windows.Point? _miscTagDragStartPoint;
    private FrameworkElement? _miscTagPressedElement;
    private bool _miscTagDragTriggered;

    // ===== 导航入口 =====

    private void NavMisc_Click(object sender, RoutedEventArgs e)
    {
        ShowMiscView();
    }

    private void ShowMiscView()
    {
        _currentNavigationKey = "misc";

        if (HomePagePanel is not null) MotionService.HideWithFade(HomePagePanel);
        if (LibraryPagePanel is not null) MotionService.HideWithFade(LibraryPagePanel);
        if (TagsPagePanel is not null) MotionService.HideWithFade(TagsPagePanel);
        if (AuthorsPagePanel is not null) MotionService.HideWithFade(AuthorsPagePanel);
        if (MiscPagePanel is not null) MotionService.ShowWithFade(MiscPagePanel);
        SetDetailVisible(false);
        UpdateNavigationVisuals();
        RefreshMiscTagCache();
        EnsureMiscImagesLoaded();
        BuildMiscTagPool();
        RefreshMiscFilter();
    }

    private void LeaveMiscView()
    {
        CancelMiscThumbnailLoading();
    }

    // ===== 数据加载 =====

    private void EnsureMiscImagesLoaded()
    {
        if (_allMiscImages.Count > 0)
        {
            return;
        }

        _miscLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _miscLoadCts = cts;

        Task.Run(() =>
        {
            try
            {
                var records = _database.LoadAllMiscImages();
                var tags = _database.LoadMiscTags();
                Dispatcher.Invoke(() =>
                {
                    MiscTagService.RefreshCache(tags);
                    _allMiscImages.Clear();
                    foreach (var r in records)
                    {
                        r.PropertyChanged += MiscImage_PropertyChanged;
                        _allMiscImages.Add(r);
                    }
                    UpdateMiscCount();
                    RefreshMiscFilter();
                });
                // 后台回填 width/height 缺失的记录，完成后通知 UI 重新布局
                _ = BackfillMiscImageDimensionsAsync(cts.Token);
            }
            catch (Exception ex)
            {
                AppLogger.Error("misc-load", ex, "加载杂图列表失败。");
            }
        }, cts.Token);
    }

    // 后台并发读取尺寸并回填 width/height=0 的记录。
    // 已回填的记录通过 SQL WHERE 自动跳过，幂等无副作用。
    private async Task BackfillMiscImageDimensionsAsync(CancellationToken cancellationToken)
    {
        List<(string Id, string FilePath)> pending;
        try
        {
            pending = _database.LoadMiscImagesWithoutDimensions();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("misc-migrate", $"查询待回填杂图尺寸失败：{ex.Message}");
            return;
        }

        if (pending.Count == 0)
        {
            return;
        }

        AppLogger.Info("misc-migrate", $"开始回填 {pending.Count} 张杂图的尺寸。");
        var gate = new SemaphoreSlim(8);
        var updated = 0;
        var failed = 0;
        var snapshot = pending.ToList();
        var progressBatch = new System.Collections.Concurrent.ConcurrentQueue<(string Id, int W, int H)>();

        await Task.WhenAll(snapshot.Select(async entry =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var (w, h) = LibraryDatabase.ReadImageDimensions(entry.FilePath);
                if (w > 0 && h > 0)
                {
                    _database.UpdateMiscImageDimensions(entry.Id, w, h);
                    progressBatch.Enqueue((entry.Id, w, h));
                    Interlocked.Increment(ref updated);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("misc-migrate", $"回填杂图尺寸失败：{entry.FilePath}。{ex.Message}");
                Interlocked.Increment(ref failed);
            }
            finally
            {
                gate.Release();
            }
        }).ToList()).ConfigureAwait(false);

        AppLogger.Info("misc-migrate", $"回填完成：成功 {updated}，失败 {failed}，共 {snapshot.Count}。");

        // 通知 UI 重新布局：更新内存对象并触发 Panel 重新 measure
        if (updated > 0)
        {
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    var byId = _allMiscImages.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
                    foreach (var (id, w, h) in progressBatch)
                    {
                        if (byId.TryGetValue(id, out var img))
                        {
                            img.PixelWidth = w;
                            img.PixelHeight = h;
                        }
                    }
                    // 通知 Panel 缓存的 aspect 数据已失效，需重新 measure
                    var panel = FindMiscVirtualizingPanel();
                    if (panel is not null)
                    {
                        panel.InvalidateLayout();
                        AppLogger.Info("misc-panel", $"回填完成，已调用 InvalidateLayout，共 {progressBatch.Count} 张图片尺寸更新");
                    }
                    else
                    {
                        MiscList?.InvalidateMeasure();
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn("misc-migrate", $"回填后刷新 UI 失败：{ex.Message}");
            }
        }
    }

    private void RefreshMiscTagCache()
    {
        try
        {
            var tags = _database.LoadMiscTags();
            MiscTagService.RefreshCache(tags);
            RefreshMiscTagFilterBox();
            BuildMiscTagPool();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("misc-tag-cache", $"刷新杂图 tag 缓存失败：{ex.Message}");
        }
    }

    private void UpdateMiscCount()
    {
        MiscCountText.Text = _allMiscImages.Count > 0
            ? $"共 {_allMiscImages.Count} 张"
            : "";
    }

    // ===== 筛选 =====

    private void RefreshMiscFilter()
    {
        // 守卫：InitializeComponent 期间 ComboBox 的 SelectedIndex=0 会触发 SelectionChanged，
        // 此时 MiscList / MiscEmptyState 等控件可能尚未在 IComponentConnector.Connect 中赋值。
        if (MiscList == null || MiscEmptyState == null)
        {
            return;
        }
        _filteredMiscImages.Clear();
        foreach (var img in _allMiscImages)
        {
            if (_miscFavoriteOnly && !img.IsFavorite)
            {
                continue;
            }
            if (!string.IsNullOrEmpty(_miscCategoryFilter) && !string.Equals(img.Category, _miscCategoryFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.IsNullOrEmpty(_miscTagFilter) && !MiscImageContainsTag(img.Tags, _miscTagFilter))
            {
                continue;
            }
            _filteredMiscImages.Add(img);
        }

        MiscList.ItemsSource = _filteredMiscImages;
        MiscEmptyState.Visibility = _filteredMiscImages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleMiscThumbnailLoading();
    }

    // 杂图 tags 字段以 ", " 分隔存储，需要精确匹配（避免同名子串误匹配）
    private static bool MiscImageContainsTag(string tags, string target)
    {
        if (string.IsNullOrEmpty(tags) || string.IsNullOrEmpty(target))
        {
            return false;
        }
        var separators = new[] { ',', '，', ';', '；' };
        var parts = tags.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            if (string.Equals(p, target, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    // 重新加载 Tag 过滤下拉项（保留当前选中）
    public void RefreshMiscTagFilterBox()
    {
        if (MiscTagFilterBox is null)
        {
            return;
        }
        var current = _miscTagFilter;
        var tags = MiscTagService.GetAllTags();
        MiscTagFilterBox.ItemsSource = null;
        var items = new List<object> { new ComboBoxItem { Content = "全部 Tag", Tag = "" } };
        items.AddRange(tags.OrderBy(t => t).Select(t => new ComboBoxItem { Content = t, Tag = t }));
        MiscTagFilterBox.ItemsSource = items;
        if (current is null || current == "")
        {
            MiscTagFilterBox.SelectedIndex = 0;
        }
        else
        {
            var idx = -1;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] is ComboBoxItem ci && string.Equals(ci.Tag as string, current, StringComparison.Ordinal))
                {
                    idx = i;
                    break;
                }
            }
            MiscTagFilterBox.SelectedIndex = idx >= 0 ? idx : 0;
        }
    }

    private void MiscTagFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 守卫：ItemsSource 还没填充前不处理
        if (MiscTagFilterBox?.ItemsSource is null)
        {
            return;
        }
        if (MiscTagFilterBox?.SelectedItem is ComboBoxItem item)
        {
            _miscTagFilter = string.IsNullOrEmpty(item.Tag as string) ? null : item.Tag as string;
        }
        RefreshMiscFilter();
    }

    private void MiscCategoryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MiscCategoryFilter?.SelectedItem is ComboBoxItem item)
        {
            _miscCategoryFilter = item.Tag as string ?? "";
        }
        RefreshMiscFilter();
    }

    private void MiscFavoriteOnlyToggle_Click(object sender, RoutedEventArgs e)
    {
        _miscFavoriteOnly = !_miscFavoriteOnly;
        if (MiscFavoriteOnlyToggle is not null)
        {
            MiscFavoriteOnlyToggle.Content = _miscFavoriteOnly ? "✓ 仅看收藏" : "仅看收藏";
        }
        RefreshMiscFilter();
    }

    // 一键清空所有筛选条件：分类、Tag、收藏
    private void MiscClearFilters_Click(object sender, RoutedEventArgs e)
    {
        _miscCategoryFilter = "";
        _miscTagFilter = null;
        _miscFavoriteOnly = false;

        if (MiscCategoryFilter is not null) MiscCategoryFilter.SelectedIndex = 0;
        if (MiscTagFilterBox is not null) MiscTagFilterBox.SelectedIndex = 0;
        if (MiscFavoriteOnlyToggle is not null) MiscFavoriteOnlyToggle.Content = "仅看收藏";

        RefreshMiscFilter();
    }

    // 造型功能已在 v0.8.0.28 移除（默认直角卡片，MiscCardCornerRadius 默认值=0）。
    // 如需恢复，参考 git history 中 MiscCardShape_SelectionChanged / ApplySavedMiscCardShape 的实现。

    // ===== 缩略图加载 =====

    // 缩略图加载策略（v0.8.0.28 重构）：
    // - 先过滤已加载的（Thumbnail != null），只对 pending 排序
    // - 不再 Take(256) 截断：旧版在过滤前截断，前 256 张已加载后后面的永远没机会加载
    // - 内存安全：Pipeline 内部 LRU(480) 自动回收，全量调度不会爆内存
    private void ScheduleMiscThumbnailLoading()
    {
        CancelMiscThumbnailLoading();
        var cts = new CancellationTokenSource();
        _miscThumbCts = cts;
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // 1. 先过滤已加载的，只对 pending 排序（不再 Take(256) 截断）
                var pending = new List<MiscImage>(_filteredMiscImages.Count);
                foreach (var img in _filteredMiscImages)
                {
                    if (img.Thumbnail is null)
                    {
                        pending.Add(img);
                    }
                }
                if (pending.Count == 0)
                {
                    return;
                }

                // 2. 按距视口远近排序（可见区优先）
                var ordered = OrderMiscImagesByVisibility(pending);

                // 3. 分批全量加载，batch size 提高到 48 减少任务数
                var batch = new List<MiscImage>(48);
                var tasks = new List<Task>();
                foreach (var img in ordered)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }
                    batch.Add(img);
                    if (batch.Count >= 48)
                    {
                        var batchSnapshot = batch.ToList();
                        batch.Clear();
                        tasks.Add(LoadMiscThumbnailBatchAsync(batchSnapshot, token));
                    }
                }
                if (batch.Count > 0)
                {
                    tasks.Add(LoadMiscThumbnailBatchAsync(batch, token));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLogger.Warn("misc-thumb", $"加载缩略图批次失败：{ex.Message}");
            }
        }, token);
    }

    // 按 ScrollViewer 当前视口位置排序：可见/即将可见的图片优先加载，其他延后。
    // v0.8.0.29：直接从 VirtualizingWrapPanel._pinterestLayout 获取精确 Y 坐标，
    // 不再估算行高（旧版估算严重失准，导致滚到底部仍从头加载）。
    private List<MiscImage> OrderMiscImagesByVisibility(IList<MiscImage> source)
    {
        if (source.Count <= 64 || MiscList is null)
        {
            return source.ToList();
        }

        try
        {
            var scrollViewer = FindVisualChild<System.Windows.Controls.ScrollViewer>(MiscList);
            if (scrollViewer is null || scrollViewer.ViewportHeight <= 0)
            {
                return source.ToList();
            }

            var viewportTop = scrollViewer.VerticalOffset;
            var viewportBottom = scrollViewer.VerticalOffset + scrollViewer.ViewportHeight;
            var overscan = scrollViewer.ViewportHeight * 2.0;

            // 尝试用 Panel 的精确 layout 获取可见索引
            var panel = FindMiscVirtualizingPanel();
            if (panel is not null && panel.HasPinterestLayout)
            {
                // 注意：source 是 _filteredMiscImages 的子集（已过滤 Thumbnail!=null 的 pending），
                // 但 panel 的 layout 是对完整 _filteredMiscImages 的。
                // 所以需要把 panel 的索引映射回 source（source 是按 _filteredMiscImages 原始顺序过滤的）。
                // 简化：用 source 所在的 _filteredMiscImages 来查 panel，然后筛选出在 source 中的。
                var visibleIndices = panel.GetVisibleIndices(viewportTop, viewportBottom, overscan);
                var visibleSet = new HashSet<int>(visibleIndices);

                var result = new List<MiscImage>(source.Count);
                // 先收集可见区中且在 source（pending）里的
                // source 是 pending 列表，需要知道每个 pending 在 _filteredMiscImages 中的原始索引
                // 建立 pending → 原始索引的映射
                var pendingIndexMap = new Dictionary<MiscImage, int>();
                for (var i = 0; i < _filteredMiscImages.Count; i++)
                {
                    if (_filteredMiscImages[i].Thumbnail is null)
                    {
                        pendingIndexMap[_filteredMiscImages[i]] = i;
                    }
                }

                // 可见区优先
                foreach (var idx in visibleIndices)
                {
                    if (idx < _filteredMiscImages.Count
                        && pendingIndexMap.TryGetValue(_filteredMiscImages[idx], out _))
                    {
                        result.Add(_filteredMiscImages[idx]);
                    }
                }
                // 其余 pending 按原始顺序补（可见区之前的先补，然后可见区之后的）
                foreach (var img in source)
                {
                    if (!result.Contains(img))
                    {
                        result.Add(img);
                    }
                }
                return result;
            }

            // Panel layout 不可用时回退：按原始顺序（不排序）
            return source.ToList();
        }
        catch
        {
            return source.ToList();
        }
    }

    // 批量并发加载：每张缩略图独立 await，互不阻塞
    private async Task LoadMiscThumbnailBatchAsync(IReadOnlyList<MiscImage> batch, CancellationToken token)
    {
        var tasks = batch.Select(async img =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var thumb = await _miscPipeline.LoadAsync(img, token).ConfigureAwait(true);
                if (thumb is not null)
                {
                    img.Thumbnail = thumb;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AppLogger.Warn("misc-thumb", $"加载缩略图失败：{img.FilePath}。{ex.Message}");
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private void CancelMiscThumbnailLoading()
    {
        if (_miscThumbCts is not null)
        {
            _miscThumbCts.Cancel();
            _miscThumbCts = null;
        }
    }

    // 滚动时取消当前缩略图加载，重新按可见区域优先调度。
    // 防抖：500ms（旧版 200ms 太短，快速滚动时频繁取消+重调度，加载永远完不成）
    private System.Windows.Threading.DispatcherTimer? _miscScrollDebounce;
    private void MiscList_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0)
        {
            return;
        }
        _miscScrollDebounce?.Stop();
        _miscScrollDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _miscScrollDebounce.Tick += (_, _) =>
        {
            _miscScrollDebounce?.Stop();
            _miscScrollDebounce = null;
            ScheduleMiscThumbnailLoading();
        };
        _miscScrollDebounce.Start();
    }

    // ===== 导入 =====

    private void MiscImportFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择图片文件",
            Multiselect = true,
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var files = dialog.FileNames.Where(File.Exists).ToArray();
        if (files.Length == 0)
        {
            return;
        }

        var category = PromptMiscCategory();
        ImportMiscFiles(files, category);
    }

    private void MiscImportFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择要扫描的文件夹",
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        var folder = dialog.SelectedPath;
        if (!Directory.Exists(folder))
        {
            return;
        }

        var category = PromptMiscCategory();
        var files = EnumerateImageFiles(folder);
        if (files.Count == 0)
        {
            System.Windows.MessageBox.Show("该文件夹下没有找到支持的图片文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ImportMiscFiles(files, category);
    }

    private static List<string> EnumerateImageFiles(string folder)
    {
        var result = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                if (ImageLoader.IsSupportedImage(file))
                {
                    result.Add(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn("misc-scan", $"扫描文件夹失败：{folder}。{ex.Message}");
        }
        return result;
    }

    private static string PromptMiscCategory()
    {
        // 不强制分类；拖拽/扫描入库默认为空，用户后续右键设置。
        return "";
    }

    private void ImportMiscFiles(IReadOnlyList<string> filePaths, string category)
    {
        var toInsert = new List<MiscImage>();
        var existingPaths = new HashSet<string>(_allMiscImages.Select(i => i.FilePath), StringComparer.OrdinalIgnoreCase);

        foreach (var path in filePaths)
        {
            if (existingPaths.Contains(path))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                var (pixelWidth, pixelHeight) = LibraryDatabase.ReadImageDimensions(path);
                var image = new MiscImage
                {
                    Id = GenerateMiscImageId(path),
                    FilePath = path,
                    FileName = Path.GetFileName(path),
                    Category = category ?? "",
                    FileSize = info.Length,
                    ImportedAt = DateTimeOffset.Now.ToString("O"),
                    PixelWidth = pixelWidth,
                    PixelHeight = pixelHeight
                };
                toInsert.Add(image);
                existingPaths.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Warn("misc-import", $"跳过文件：{path}。{ex.Message}");
            }
        }

        if (toInsert.Count == 0)
        {
            System.Windows.MessageBox.Show("没有新增图片（可能已存在）。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _database.UpsertMiscImagesBatch(toInsert);
            foreach (var img in toInsert)
            {
                img.PropertyChanged += MiscImage_PropertyChanged;
                _allMiscImages.Insert(0, img);
            }
            UpdateMiscCount();
            RefreshMiscFilter();
            StatusText.Text = $"已导入 {toInsert.Count} 张杂图。";
        }
        catch (Exception ex)
        {
            AppLogger.Error("misc-import", ex, $"批量写入失败。");
            System.Windows.MessageBox.Show($"导入失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string GenerateMiscImageId(string absolutePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(absolutePath));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..20];
    }

    // ===== 拖拽入库 / 拖拽分配 Tag =====

    private void MiscPagePanel_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(MiscTagDragDataFormat)
            || e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void MiscPagePanel_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        MiscPagePanel_DragEnter(sender, e);
    }

    private void MiscPagePanel_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        e.Handled = true;
    }

    private void MiscPagePanel_Drop(object sender, System.Windows.DragEventArgs e)
    {
        // 优先处理杂图 Tag 拖拽分配（来自 MiscPagePanel 顶部 Tag 池）
        if (e.Data.GetDataPresent(MiscTagDragDataFormat))
        {
            var tag = e.Data.GetData(MiscTagDragDataFormat) as string;
            var img = FindAncestor<System.Windows.Controls.ListBoxItem>((DependencyObject)e.OriginalSource)?.DataContext as MiscImage;
            if (!string.IsNullOrWhiteSpace(tag) && img is not null)
            {
                ApplyMiscTagFromDrop(img, tag);
                e.Handled = true;
                return;
            }
            // tag 拖到空白处：忽略，不进入文件导入分支
            e.Handled = true;
            return;
        }

        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            return;
        }

        var data = e.Data.GetData(System.Windows.DataFormats.FileDrop);
        if (data is not string[] entries)
        {
            return;
        }

        var files = new List<string>();
        foreach (var entry in entries)
        {
            if (File.Exists(entry) && ImageLoader.IsSupportedImage(entry))
            {
                files.Add(entry);
            }
            else if (Directory.Exists(entry))
            {
                files.AddRange(EnumerateImageFiles(entry));
            }
        }

        if (files.Count == 0)
        {
            return;
        }

        ImportMiscFiles(files, "");
    }

    // ===== 卡片操作 =====

    private void MiscList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 单击卡片即进入图库（与漫画卡片行为一致）
        if (MiscList.SelectedItem is MiscImage img)
        {
            OpenMiscViewer(img);
        }
    }

    private void MiscOpenViewer_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetMiscImageFromMenu(sender, out var img))
        {
            OpenMiscViewer(img);
        }
    }

    private void OpenMiscViewer(MiscImage image)
    {
        var playlist = _filteredMiscImages.ToList();
        var index = playlist.FindIndex(i => string.Equals(i.Id, image.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            playlist = new List<MiscImage> { image };
            index = 0;
        }

        try
        {
            _database.UpdateMiscImageLastOpenedAt(image.Id, DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("misc-viewer", $"更新 LastOpenedAt 失败：{image.Id}。{ex.Message}");
        }

        // 构造临时 MangaBook：Pages = 杂图文件路径列表，复用 ReaderWindow 全部 UI 与交互
        // Id 用 "misc-" 前缀避免与真实书籍冲突；FolderPath 用 "misc://" 协议避免被 SaveMetadata 污染 books 表
        var miscBook = new MangaBook
        {
            Id = "misc-" + Guid.NewGuid().ToString("N"),
            Title = "杂图浏览",
            FolderPath = "misc://" + Guid.NewGuid().ToString("N"),
            PageCount = playlist.Count,
            LastReadPageIndex = index,
            Rating = playlist[index].Rating,
            IsFavorite = playlist[index].IsFavorite,
            ReadingStatus = "reading"
        };
        foreach (var img in playlist)
        {
            miscBook.Pages.Add(img.FilePath);
        }

        var session = new ReaderWindow.MiscSession { Images = playlist };

        var reader = new ReaderWindow(
            miscBook,
            _database,
            _nextKeys,
            _prevKeys,
            miscSession: session)
        {
            Owner = this
        };
        reader.Show();
    }

    private void MiscEditComment_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMiscImageFromMenu(sender, out var img)) return;
        var dialog = new TagNameDialog(img.Comment, "编辑评语")
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            img.Comment = dialog.TagName;
            try { _database.UpdateMiscImageComment(img.Id, img.Comment); }
            catch (Exception ex) { AppLogger.Warn("misc-comment", $"保存评语失败：{ex.Message}"); }
        }
    }

    private void MiscEditTags_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMiscImageFromMenu(sender, out var img)) return;
        var dialog = new TagNameDialog(img.Tags, "编辑 Tag（逗号分隔）")
        {
            Owner = this
        };
        if (dialog.ShowDialog() == true)
        {
            img.Tags = MiscTagService.FormatTags(MiscTagService.ParseTags(dialog.TagName));
            try { _database.UpdateMiscImageTags(img.Id, img.Tags); }
            catch (Exception ex) { AppLogger.Warn("misc-tags", $"保存 Tag 失败：{ex.Message}"); }
        }
    }

    private void MiscToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMiscImageFromMenu(sender, out var img)) return;
        img.IsFavorite = !img.IsFavorite;
        try { _database.UpdateMiscImageFavorite(img.Id, img.IsFavorite); }
        catch (Exception ex) { AppLogger.Warn("misc-fav", $"保存收藏失败：{ex.Message}"); }
        if (_miscFavoriteOnly)
        {
            RefreshMiscFilter();
        }
    }

    private void MiscSetCategoryReal_Click(object sender, RoutedEventArgs e)
    {
        SetMiscCategoryFromMenu(sender, MiscCategoryReal);
    }

    private void MiscSetCategoryArt_Click(object sender, RoutedEventArgs e)
    {
        SetMiscCategoryFromMenu(sender, MiscCategoryArt);
    }

    private void MiscSetCategoryNone_Click(object sender, RoutedEventArgs e)
    {
        SetMiscCategoryFromMenu(sender, "");
    }

    private void SetMiscCategoryFromMenu(object sender, string category)
    {
        if (!TryGetMiscImageFromMenu(sender, out var img)) return;
        img.Category = category;
        try { _database.UpdateMiscImageCategory(img.Id, category); }
        catch (Exception ex) { AppLogger.Warn("misc-category", $"保存分类失败：{ex.Message}"); }
        RefreshMiscFilter();
    }

    private void MiscRename_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMiscImageFromMenu(sender, out var img)) return;
        if (!File.Exists(img.FilePath))
        {
            System.Windows.MessageBox.Show("找不到原文件，无法重命名。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new MiscRenameDialog(img.FilePath) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var newFileName = dialog.NewFileName;
        var directory = Path.GetDirectoryName(img.FilePath) ?? "";
        var newPath = Path.Combine(directory, newFileName);

        if (File.Exists(newPath))
        {
            System.Windows.MessageBox.Show($"目标文件已存在：{newFileName}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            File.Move(img.FilePath, newPath);
            _database.RenameMiscImageFile(img.Id, newPath, newFileName);
            img.FilePath = newPath;
            img.FileName = newFileName;
            StatusText.Text = $"已重命名为：{newFileName}";
        }
        catch (Exception ex)
        {
            AppLogger.Error("misc-rename", ex, $"反向重命名失败：{img.FilePath} → {newPath}");
            System.Windows.MessageBox.Show($"反向重命名失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MiscOpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMiscImageFromMenu(sender, out var img)) return;
        if (!File.Exists(img.FilePath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{img.FilePath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("misc-open-location", $"打开文件位置失败：{ex.Message}");
        }
    }

    private void MiscDeleteRecord_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMiscImageFromMenu(sender, out var img)) return;
        var confirm = System.Windows.MessageBox.Show(
            $"确定删除记录「{img.FileName}」？\n（原文件保留，可再次导入恢复）",
            "确认",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            _database.DeleteMiscImage(img.Id);
            _allMiscImages.Remove(img);
            _filteredMiscImages.Remove(img);
            UpdateMiscCount();
            RefreshMiscFilter();
        }
        catch (Exception ex)
        {
            AppLogger.Error("misc-delete", ex, $"删除杂图记录失败：{img.Id}");
        }
    }

    private bool TryGetMiscImageFromMenu(object sender, out MiscImage image)
    {
        image = null!;
        if (sender is not MenuItem menuItem) return false;
        // ContextMenu 不在视觉树中，需通过 Parent 链找到 ContextMenu，再取 PlacementTarget。
        var menu = menuItem.Parent as ContextMenu;
        if (menu?.PlacementTarget is not FrameworkElement fe) return false;
        image = fe.DataContext as MiscImage ?? null!;
        return image is not null;
    }

    // ===== Tag 管理（独立窗口） =====

    private void MiscManageTags_Click(object sender, RoutedEventArgs e)
    {
        var window = new MiscTagManagerWindow(_database, RefreshMiscTagCacheAfterEdit)
        {
            Owner = this
        };
        window.ShowDialog();

        // 编辑后刷新已加载杂图的 TagChip 颜色（颜色可能已变更）。
        foreach (var img in _allMiscImages)
        {
            img.NotifyAll();
        }
    }

    // ===== 杂图 Tag 池：构建 / 拖拽分配 =====

    // 从 MiscTagService 缓存构建 Tag 池 chip 列表。
    // chip 颜色和分类完全来自 misc_image_tags，不读取 managed_tags。
    // 排序：按分类字母序，再按名称字母序，便于用户在池中快速定位。
    private void BuildMiscTagPool()
    {
        if (MiscTagPoolItems is null)
        {
            return;
        }

        try
        {
            var records = _database.LoadMiscTags();
            var chips = records
                .OrderBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new TagChip
                {
                    Name = r.Name,
                    RawName = r.Name,
                    Category = string.IsNullOrEmpty(r.Category) ? "未分类" : r.Category,
                    Color = string.IsNullOrEmpty(r.Color) ? MiscTagService.GetColor(r.Name) : r.Color,
                    Foreground = MiscTagService.GetTextColorForBackground(
                        string.IsNullOrEmpty(r.Color) ? MiscTagService.GetColor(r.Name) : r.Color)
                })
                .ToList();

            MiscTagPoolItems.Clear();
            MiscTagPoolItems.AddRange(chips);

            if (MiscTagPoolEmptyHint is not null)
            {
                MiscTagPoolEmptyHint.Visibility = chips.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("misc-tag-pool", $"构建杂图 Tag 池失败：{ex.Message}");
        }
    }

    // chip 按下：记录起点，准备拖拽或点击筛选。
    private void MiscTagChip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TagChip chip } element)
        {
            _miscTagDragStartPoint = null;
            _miscTagPressedElement = null;
            _miscTagDragTriggered = false;
            return;
        }

        // 已选中（当前筛选态）的 chip 仍允许拖拽分配，但不参与点击切换。
        _miscTagDragStartPoint = e.GetPosition(this);
        _miscTagPressedElement = element;
        _miscTagDragTriggered = false;
        MotionService.PressBounce(element);
        e.Handled = true;
    }

    private void MiscTagChip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement element)
        {
            MotionService.ScaleTo(element, element.IsMouseOver ? 1.04 : 1.0, MotionService.Fast);
        }

        var wasDrag = _miscTagDragTriggered;
        var pressed = _miscTagPressedElement;
        _miscTagDragStartPoint = null;
        _miscTagPressedElement = null;
        _miscTagDragTriggered = false;

        if (wasDrag)
        {
            e.Handled = true;
            return;
        }

        // 单击 chip：作为筛选条件（与 MiscTagFilterBox 同步）
        if (pressed is FrameworkElement { DataContext: TagChip chip })
        {
            _miscTagFilter = string.IsNullOrWhiteSpace(chip.Name) ? null : chip.Name;
            if (MiscTagFilterBox is not null)
            {
                // 同步下拉框选中项，避免 UI 状态分裂
                var target = chip.Name ?? "";
                var idx = -1;
                for (var i = 0; i < MiscTagFilterBox.Items.Count; i++)
                {
                    if (MiscTagFilterBox.Items[i] is ComboBoxItem ci
                        && string.Equals(ci.Tag as string, target, StringComparison.Ordinal))
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx >= 0 && MiscTagFilterBox.SelectedIndex != idx)
                {
                    MiscTagFilterBox.SelectedIndex = idx;
                }
            }
            RefreshMiscFilter();
            e.Handled = true;
        }
    }

    // chip 鼠标移动：超过系统拖拽阈值则发起 DoDragDrop，data format 走 MiscTagDragDataFormat。
    private void MiscTagChip_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || sender is not FrameworkElement { DataContext: TagChip chip } element
            || !ReferenceEquals(_miscTagPressedElement, element))
        {
            return;
        }

        if (_miscTagDragStartPoint is { } start
            && Math.Abs(e.GetPosition(this).X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(e.GetPosition(this).Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _miscTagDragTriggered = true;
        var tagName = chip.RawName;
        if (string.IsNullOrWhiteSpace(tagName))
        {
            tagName = chip.Name;
        }

        var data = new System.Windows.DataObject();
        data.SetData(MiscTagDragDataFormat, tagName);
        // 同时塞一个 string 格式，方便未来与外部程序交互（不影响隔离，因为 Drop 端会优先识别 MiscTagDragDataFormat）
        data.SetData(typeof(string), tagName);
        try
        {
            System.Windows.DragDrop.DoDragDrop(element, data, System.Windows.DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("misc-tag-drag", $"杂图 Tag 拖拽失败：{tagName}。{ex.Message}");
        }
        e.Handled = true;
    }

    // 拖拽落到某张杂图：写入 misc_images.tags，刷新 UI 颜色缓存。
    // 幂等：如果杂图已包含该 tag，直接返回不写库。
    private void ApplyMiscTagFromDrop(MiscImage img, string tag)
    {
        if (img is null || string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        var previousTags = img.Tags;
        try
        {
            // 若 misc_image_tags 中没有该 tag，自动补一条默认记录，避免颜色查询 miss
            // 用"未分类"代替空字符串，避免分组显示为空
            try
            {
                _database.UpsertMiscTag(tag, "未分类", MiscTagService.GetColor(tag));
                MiscTagService.UpsertLocal(tag, "未分类", MiscTagService.GetColor(tag));
            }
            catch (Exception ex)
            {
                AppLogger.Warn("misc-tag-drop", $"补齐 misc_image_tags 失败（忽略）：{tag}。{ex.Message}");
            }

            img.AddTag(tag);
            _database.UpdateMiscImageTags(img.Id, img.Tags);
            img.NotifyAll();

            StatusText.Text = $"已给「{img.FileName}」添加 Tag：{tag}";
        }
        catch (Exception ex)
        {
            img.Tags = previousTags;
            img.NotifyAll();
            AppLogger.Error("misc-tag-drop", ex, $"拖拽分配杂图 Tag 失败：img={img.FileName}, tag={tag}");
            StatusText.Text = $"分配 Tag 失败：{ex.Message}";
        }
    }

    private void RefreshMiscTagCacheAfterEdit()
    {
        try
        {
            var tags = _database.LoadMiscTags();
            MiscTagService.RefreshCache(tags);
            BuildMiscTagPool();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("misc-tag-cache", $"刷新杂图 tag 缓存失败：{ex.Message}");
        }
    }

    // 在 MiscList 的可视化树中查找 VirtualizingWrapPanel 实例（用于回填后强制失效布局缓存）
    private Controls.VirtualizingWrapPanel? FindMiscVirtualizingPanel()
    {
        if (MiscList is null)
        {
            return null;
        }
        return FindVisualChild<Controls.VirtualizingWrapPanel>(MiscList);
    }

    private static T? FindVisualChild<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                return typed;
            }
            var result = FindVisualChild<T>(child);
            if (result is not null)
            {
                return result;
            }
        }
        return null;
    }

    // ===== 多选模式 =====

    private void MiscToggleBatchMode_Click(object sender, RoutedEventArgs e)
    {
        IsMiscBatchMode = !IsMiscBatchMode;
        if (!IsMiscBatchMode)
        {
            MiscClearBatchSelection();
        }
        if (MiscBatchToggleButton is not null)
        {
            MiscBatchToggleButton.Content = IsMiscBatchMode ? "退出多选" : "多选";
        }
        UpdateMiscBatchCount();
    }

    private void MiscClearBatchSelection()
    {
        foreach (var img in _allMiscImages)
        {
            img.IsSelectedForBatch = false;
        }
        UpdateMiscBatchCount();
    }

    private void MiscBatchSelectAll_Click(object sender, RoutedEventArgs e)
    {
        // 全选所有杂图（不只是可见的），让用户能批量操作全部图片
        foreach (var img in _allMiscImages)
        {
            img.IsSelectedForBatch = true;
        }
        UpdateMiscBatchCount();
    }

    private void MiscBatchClear_Click(object sender, RoutedEventArgs e)
    {
        MiscClearBatchSelection();
    }

    // 点击卡片上的圆形选择标记：切换该图的选中状态
    private void MiscBatchToggleSingle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MiscImage img }) return;
        img.IsSelectedForBatch = !img.IsSelectedForBatch;
        e.Handled = true; // 阻止事件冒泡到卡片（避免触发单击进入浏览）
    }

    private void UpdateMiscBatchCount()
    {
        var count = _allMiscImages.Count(i => i.IsSelectedForBatch);
        if (MiscBatchCountText is not null)
        {
            MiscBatchCountText.Text = count.ToString();
        }
    }

    // 监听 IsSelectedForBatch 变化（通过 PropertyChanged 事件转发）
    // 在 EnsureMiscImagesLoaded 中订阅每个 MiscImage 的 PropertyChanged
    private void MiscImage_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MiscImage.IsSelectedForBatch) || string.IsNullOrEmpty(e.PropertyName))
        {
            UpdateMiscBatchCount();
        }
    }

    private void MiscBatchAddTag_Click(object sender, RoutedEventArgs e)
    {
        var selected = _allMiscImages.Where(i => i.IsSelectedForBatch).ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show("请先选择图片。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new TagNameDialog("", "批量添加 Tag（逗号分隔）") { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var newTags = MiscTagService.ParseTags(dialog.TagName).ToList();
        if (newTags.Count == 0) return;

        try
        {
            foreach (var img in selected)
            {
                foreach (var tag in newTags)
                {
                    img.AddTag(tag);
                    // 自动补齐 misc_image_tags 记录（用"未分类"避免分组为空）
                    _database.UpsertMiscTag(tag, "未分类", MiscTagService.GetColor(tag));
                    MiscTagService.UpsertLocal(tag, "未分类", MiscTagService.GetColor(tag));
                }
                _database.UpdateMiscImageTags(img.Id, img.Tags);
                img.NotifyAll();
            }
            RefreshMiscTagCache();
            BuildMiscTagPool();
            StatusText.Text = $"已给 {selected.Count} 张图片添加 {newTags.Count} 个 Tag。";
        }
        catch (Exception ex)
        {
            AppLogger.Error("misc-batch-tag", ex, "批量添加 Tag 失败");
            System.Windows.MessageBox.Show($"批量添加失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MiscBatchRemoveTag_Click(object sender, RoutedEventArgs e)
    {
        var selected = _allMiscImages.Where(i => i.IsSelectedForBatch).ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show("请先选择图片。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new TagNameDialog("", "批量移除 Tag（逗号分隔）") { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var removeTags = MiscTagService.ParseTags(dialog.TagName).ToList();
        if (removeTags.Count == 0) return;

        try
        {
            foreach (var img in selected)
            {
                foreach (var tag in removeTags)
                {
                    img.RemoveTag(tag);
                }
                _database.UpdateMiscImageTags(img.Id, img.Tags);
                img.NotifyAll();
            }
            StatusText.Text = $"已从 {selected.Count} 张图片移除 {removeTags.Count} 个 Tag。";
        }
        catch (Exception ex)
        {
            AppLogger.Error("misc-batch-tag", ex, "批量移除 Tag 失败");
            System.Windows.MessageBox.Show($"批量移除失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void MiscBatchToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        var selected = _allMiscImages.Where(i => i.IsSelectedForBatch).ToList();
        if (selected.Count == 0) return;

        // 如果全部已收藏则取消，否则全部收藏
        var targetState = !selected.All(i => i.IsFavorite);
        try
        {
            foreach (var img in selected)
            {
                img.IsFavorite = targetState;
                _database.UpdateMiscImageFavorite(img.Id, img.IsFavorite);
            }
            StatusText.Text = $"已{(targetState ? "收藏" : "取消收藏")} {selected.Count} 张图片。";
        }
        catch (Exception ex)
        {
            AppLogger.Error("misc-batch-fav", ex, "批量切换收藏失败");
        }
    }

    private void MiscBatchDelete_Click(object sender, RoutedEventArgs e)
    {
        var selected = _allMiscImages.Where(i => i.IsSelectedForBatch).ToList();
        if (selected.Count == 0) return;

        var confirm = System.Windows.MessageBox.Show(
            $"确定删除 {selected.Count} 张杂图记录？\n（原文件保留，可再次导入恢复）",
            "确认批量删除",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        try
        {
            foreach (var img in selected)
            {
                _database.DeleteMiscImage(img.Id);
                _allMiscImages.Remove(img);
                _filteredMiscImages.Remove(img);
            }
            UpdateMiscCount();
            RefreshMiscFilter();
            UpdateMiscBatchCount();
            StatusText.Text = $"已删除 {selected.Count} 张杂图记录。";
        }
        catch (Exception ex)
        {
            AppLogger.Error("misc-batch-delete", ex, $"批量删除杂图失败");
            System.Windows.MessageBox.Show($"批量删除失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
