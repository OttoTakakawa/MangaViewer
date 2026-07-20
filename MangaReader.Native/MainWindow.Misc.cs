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

    private readonly ObservableCollection<MiscImage> _allMiscImages = new();
    private readonly ObservableCollection<MiscImage> _filteredMiscImages = new();
    private CancellationTokenSource? _miscThumbCts;
    private CancellationTokenSource? _miscLoadCts;
    private string _miscCategoryFilter = "";
    private bool _miscFavoriteOnly;

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
                        _allMiscImages.Add(r);
                    }
                    UpdateMiscCount();
                    RefreshMiscFilter();
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error("misc-load", ex, "加载杂图列表失败。");
            }
        }, cts.Token);
    }

    private void RefreshMiscTagCache()
    {
        try
        {
            var tags = _database.LoadMiscTags();
            MiscTagService.RefreshCache(tags);
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
            _filteredMiscImages.Add(img);
        }

        MiscList.ItemsSource = _filteredMiscImages;
        MiscEmptyState.Visibility = _filteredMiscImages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScheduleMiscThumbnailLoading();
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

    // ===== 缩略图加载 =====

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
                foreach (var img in _filteredMiscImages.ToList())
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    if (img.Thumbnail is not null)
                    {
                        continue;
                    }

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
                        return;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("misc-thumb", $"加载缩略图失败：{img.FilePath}。{ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void CancelMiscThumbnailLoading()
    {
        if (_miscThumbCts is not null)
        {
            _miscThumbCts.Cancel();
            _miscThumbCts = null;
        }
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
                var image = new MiscImage
                {
                    Id = GenerateMiscImageId(path),
                    FilePath = path,
                    FileName = Path.GetFileName(path),
                    Category = category ?? "",
                    FileSize = info.Length,
                    ImportedAt = DateTimeOffset.Now.ToString("O")
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

    // ===== 拖拽入库 =====

    private void MiscPagePanel_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
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

    private void MiscList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
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

        var window = new MiscImageViewerWindow(_database, playlist, index)
        {
            Owner = this
        };
        window.Show();
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

    private void RefreshMiscTagCacheAfterEdit()
    {
        try
        {
            var tags = _database.LoadMiscTags();
            MiscTagService.RefreshCache(tags);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("misc-tag-cache", $"刷新杂图 tag 缓存失败：{ex.Message}");
        }
    }
}
