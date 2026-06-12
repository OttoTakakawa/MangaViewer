using MangaReader.Native.Models;
using MangaReader.Native.Services;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace MangaReader.Native;

public partial class MainWindow : Window
{
    private const double WheelScrollMultiplier = 1.45;
    private static readonly TimeSpan SearchDebounceInterval = TimeSpan.FromMilliseconds(220);
    private static readonly TagPreset[] DefaultTagPresets = TagCatalog.BuiltInPresets;
    private readonly AppStorage _storage = new();
    private readonly LibraryScanner _scanner = new();
    private readonly BatchImportAnalyzer _batchImportAnalyzer = new();
    private readonly LibraryDatabase _database;
    private readonly CoverCache _coverCache;
    private readonly CoverThumbnailPipeline _coverPipeline;
    private MangaBook? _currentBook;
    private CancellationTokenSource? _scanCancellation;
    private List<Key> _nextKeys = [Key.Right, Key.D, Key.Space];
    private List<Key> _prevKeys = [Key.Left, Key.A];
    private bool _isEditMode;
    private ICollectionView? _booksView;
    private readonly HashSet<string> _activeTagFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _managedTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _managedTagCategories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _managedTagIsExclusive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _managedTagUpdatedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<MangaBook>> _tagBooksByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _suppressedTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _visibleCoverReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _bookSearchDebounceTimer = new() { Interval = SearchDebounceInterval };
    private readonly DispatcherTimer _tagSearchDebounceTimer = new() { Interval = SearchDebounceInterval };
    private readonly DispatcherTimer _tagManagerSearchDebounceTimer = new() { Interval = SearchDebounceInterval };
    private bool _isRefreshingAuthorFilters;
    private bool _libraryChromeCollapsed;
    private bool _isLogPanelVisible;
    private bool _isDetailDrawerCollapsed;
    private string _currentNavigationKey = "home";

    public ObservableCollection<MangaBook> Books { get; } = [];
    public ObservableCollection<TagChip> VisibleTags { get; } = [];
    public ObservableCollection<TagChip> ActiveTagFilters { get; } = [];
    public ObservableCollection<TagChip> TagManagerItems { get; } = [];
    public ObservableCollection<string> AuthorFilters { get; } = [];
    public ObservableCollection<MangaBook> ContinueReadingBooks { get; } = [];
    public ObservableCollection<MangaBook> RecentReadingBooks { get; } = [];
    public ObservableCollection<MangaBook> FavoriteShowcaseBooks { get; } = [];
    public ObservableCollection<MangaBook> RecentlyAddedBooks { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _booksView = CollectionViewSource.GetDefaultView(Books);
        _booksView.Filter = FilterBook;

        _storage.EnsureCreated();
        _database = new LibraryDatabase(_storage);
        _database.Initialize();
        LoadManagedTags();
        _coverCache = new CoverCache(_storage);
        _coverPipeline = new CoverThumbnailPipeline(_coverCache);
        LoadShortcuts();
        SetDetailVisible(false);
        ShowHomeView();
        UpdateStoragePathText();
        UpdateLogPanelVisibility();

        ConfigureSearchDebounceTimers();
        Loaded += MainWindow_Loaded;
        Closing += (_, _) =>
        {
            StopSearchDebounceTimers();
            SaveCurrentProgress();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var roots = _database.LoadLibraryRoots().Where(Directory.Exists).ToList();
            if (roots.Count == 0)
            {
                StatusText.Text = "请选择漫画库文件夹。漫画路径不会直接显示在界面里。";
                RefreshLibraryViews(sort: false, filter: false);
                RefreshShelfOverview();
                return;
            }

            await ScanRootsAsync(roots);
        }
        catch (Exception ex)
        {
            AppLogger.Error("startup-scan", ex, "Startup scan failed.");
            StatusText.Text = $"启动扫描失败：{ex.Message}";
        }
    }

    private async void ChooseLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ImportFolderDialog
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await TryImportSelectedFoldersAsync(dialog.FolderPaths.ToList(), "choose-library");
    }

    private async Task TryImportSelectedFoldersAsync(IReadOnlyList<string> folderPaths, string scope)
    {
        try
        {
            await ImportSelectedFoldersAsync(folderPaths);
        }
        catch (Exception ex)
        {
            AppLogger.Error(scope, ex, "Folder import failed.");
            HideImportProgress();
            HideImportDropFeedback();
            StatusText.Text = $"导入失败：{ex.Message}";
        }
    }

    private async Task ImportSelectedFoldersAsync(IReadOnlyList<string> folderPaths)
    {
        var folders = folderPaths
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folders.Count == 0)
        {
            StatusText.Text = "没有识别到可导入的文件夹。";
            return;
        }

        foreach (var folderPath in folders)
        {
            await ImportSelectedFolderAsync(folderPath);
        }

        if (folders.Count > 1)
        {
            StatusText.Text = $"多文件夹导入处理完成：{folders.Count} 个文件夹，当前书库 {Books.Count} 本漫画。";
        }
    }

    private async Task ImportSelectedFolderAsync(string folderPath)
    {
        var candidates = await Task.Run(() => _batchImportAnalyzer.AnalyzeAuthorFolder(folderPath));
        if (candidates.Count == 0)
        {
            _database.SaveLibraryRoot(folderPath);
            await ScanRootsAsync([folderPath]);
            return;
        }

        var authorName = Path.GetFileName(folderPath);
        var dialog = new AuthorBatchImportDialog(folderPath, authorName, candidates) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await ImportAuthorBatchAsync(folderPath, dialog.AuthorName, dialog.Candidates.ToList());
        }
    }

    private async Task ImportAuthorBatchAsync(string rootPath, string authorName, IReadOnlyList<BatchImportCandidate> candidates)
    {
        StatusText.Text = $"正在批量导入：{authorName}...";
        ShowImportProgress(authorName, 0, candidates.Count, "准备导入...");
        _database.SaveLibraryRoot(rootPath);
        var savedBooks = _database.LoadBooksByPath();
        var booksByPath = Books.ToDictionary(book => book.FolderPath, StringComparer.OrdinalIgnoreCase);
        var importedCount = 0;
        var failures = new List<string>();
        var booksToSave = new List<(MangaBook Book, bool IsAlreadyVisible)>();
        var processedCount = 0;

        foreach (var candidate in candidates)
        {
            processedCount++;
            ShowImportProgress(authorName, processedCount - 1, candidates.Count, $"正在处理：{candidate.Title}");
            await System.Windows.Threading.Dispatcher.Yield();
            try
            {
                var pages = candidate.Pages.Count > 0
                    ? candidate.Pages
                    : Directory.EnumerateFiles(candidate.FolderPath)
                        .Where(ImageLoader.IsSupportedImage)
                        .OrderBy(path => path, new NaturalPathComparer())
                        .ToList();
                if (pages.Count == 0)
                {
                    continue;
                }

                savedBooks.TryGetValue(candidate.FolderPath, out var saved);
                var isAlreadyVisible = booksByPath.TryGetValue(candidate.FolderPath, out var visibleBook);
                var book = visibleBook ?? saved ?? new MangaBook
                {
                    Id = BookId.FromFolderPath(candidate.FolderPath),
                    ImportedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd")
                };
                book.Id = BookId.FromFolderPath(candidate.FolderPath);
                book.Title = candidate.Title.Trim();
                book.Author = authorName.Trim();
                book.FolderPath = candidate.FolderPath;
                book.PageCount = pages.Count;
                book.CoverPageIndex = Math.Clamp(book.CoverPageIndex, 0, pages.Count - 1);
                book.LastReadPageIndex = Math.Clamp(book.LastReadPageIndex, 0, pages.Count - 1);
                book.IsMissing = false;
                if (saved is null && string.IsNullOrWhiteSpace(book.Tags))
                {
                    book.Tags = candidate.Tags;
                }
                book.Pages.Clear();
                foreach (var page in pages)
                {
                    book.Pages.Add(page);
                }

                booksToSave.Add((book, isAlreadyVisible));
                importedCount++;
                ShowImportProgress(authorName, processedCount, candidates.Count, $"已导入：{candidate.Title}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                AppLogger.Warn("author-import", $"{candidate.Title} failed: {ex}");
                failures.Add($"{candidate.Title}：{ex.Message}");
                ShowImportProgress(authorName, processedCount, candidates.Count, $"导入失败：{candidate.Title}");
            }
        }

        ShowImportProgress(authorName, processedCount, candidates.Count, "正在批量写入数据库...");
        await Task.Run(() => _database.UpsertBooksBatch(booksToSave.Select(item => item.Book).ToList()));
        foreach (var (book, isAlreadyVisible) in booksToSave)
        {
            book.NotifyAll();
            if (!isAlreadyVisible)
            {
                Books.Add(book);
                booksByPath[book.FolderPath] = book;
            }
        }

        HideImportProgress();
        RefreshLibraryViews(sort: true);
        EnsureLibraryViewCanShowBooks();
        StatusText.Text = failures.Count == 0
            ? $"批量导入完成：{authorName} · 新增/更新 {importedCount} 本，当前书库 {Books.Count} 本漫画。"
            : $"批量导入完成：{authorName} · 成功 {importedCount} 本，失败 {failures.Count} 本。";

        if (failures.Count > 0)
        {
            var detail = string.Join(Environment.NewLine, failures.Take(8));
            if (failures.Count > 8)
            {
                detail += $"{Environment.NewLine}……另有 {failures.Count - 8} 本失败。";
            }
            System.Windows.MessageBox.Show(this, detail, "部分漫画导入失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowImportProgress(string authorName, int completed, int total, string detail)
    {
        if (ImportProgressPanel is null)
        {
            return;
        }

        var safeTotal = Math.Max(1, total);
        if (ImportProgressPanel.Visibility != Visibility.Visible)
        {
            MotionService.ShowWithFade(ImportProgressPanel);
        }
        ImportProgressTitle.Text = $"正在导入：{authorName}";
        ImportProgressBar.Minimum = 0;
        ImportProgressBar.Maximum = safeTotal;
        ImportProgressBar.Value = Math.Clamp(completed, 0, safeTotal);
        ImportProgressText.Text = $"{Math.Clamp(completed, 0, safeTotal)} / {safeTotal} · {detail}";
        StatusText.Text = ImportProgressText.Text;
    }

    private void HideImportProgress()
    {
        if (ImportProgressPanel is not null)
        {
            MotionService.HideWithFade(ImportProgressPanel);
        }
    }

    private async void BookCoverHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MangaBook book })
        {
            return;
        }

        var key = GetCoverReferenceKey(book);
        _visibleCoverReferences[key] = _visibleCoverReferences.TryGetValue(key, out var count) ? count + 1 : 1;
        if (book.CoverImage is not null)
        {
            return;
        }

        try
        {
            var image = await _coverPipeline.LoadAsync(book);
            if (_visibleCoverReferences.ContainsKey(key) && image is not null)
            {
                book.CoverImage = image;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            AppLogger.Warn("cover-thumbnail", $"{book.Title} failed: {ex}");
            if (_visibleCoverReferences.ContainsKey(key))
            {
                StatusText.Text = $"封面缩略图加载失败：{book.Title}";
            }
        }
    }

    private void BookCoverHost_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MangaBook book })
        {
            return;
        }

        var key = GetCoverReferenceKey(book);
        if (!_visibleCoverReferences.TryGetValue(key, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            _visibleCoverReferences.Remove(key);
            return;
        }

        _visibleCoverReferences[key] = count - 1;
    }

    private static string GetCoverReferenceKey(MangaBook book)
    {
        return string.IsNullOrWhiteSpace(book.Id) ? book.FolderPath : book.Id;
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        var roots = _database.LoadLibraryRoots().Where(Directory.Exists).ToList();
        if (roots.Count == 0)
        {
            StatusText.Text = "没有可扫描的漫画库路径。";
            return;
        }
        await ScanRootsAsync(roots);
    }

    private async Task ScanRootsAsync(List<string> roots)
    {
        _scanCancellation?.Cancel();
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;

        StatusText.Text = "正在扫描漫画库...";
        _visibleCoverReferences.Clear();
        Books.Clear();
        _currentBook = null;
        SetDetailVisible(false);

        try
        {
            var savedBooks = _database.LoadBooksByPath();
            var scanned = await Task.Run(() =>
            {
                var all = new List<MangaBook>();
                foreach (var root in roots)
                {
                    token.ThrowIfCancellationRequested();
                    all.AddRange(_scanner.Scan(root, savedBooks));
                }
                return all;
            }, token);

            var scannedPaths = scanned.Select(book => book.FolderPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingBooks = savedBooks.Values
                .Where(book => !scannedPaths.Contains(book.FolderPath) && !Directory.Exists(book.FolderPath))
                .ToList();
            foreach (var missing in missingBooks)
            {
                token.ThrowIfCancellationRequested();
                missing.IsMissing = true;
                missing.Pages.Clear();
                missing.NotifyAll();
            }

            var booksToSave = scanned.Concat(missingBooks).ToList();
            await Task.Run(() => _database.UpsertBooksBatch(booksToSave), token);
            foreach (var book in scanned)
            {
                token.ThrowIfCancellationRequested();
                Books.Add(book);
            }

            foreach (var missing in missingBooks)
            {
                token.ThrowIfCancellationRequested();
                Books.Add(missing);
            }

            RefreshLibraryViews(sort: true);
            EnsureLibraryViewCanShowBooks();
            StatusText.Text = $"扫描完成：{Books.Count} 本漫画。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "扫描已取消。";
        }
        catch (Exception ex)
        {
            AppLogger.Error("library-scan", ex, "Library scan failed.");
            StatusText.Text = $"扫描失败：{ex.Message}";
        }
    }

    private void BooksList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveCurrentProgress();
        _currentBook = BooksList.SelectedItem as MangaBook;
        if (_currentBook is null)
        {
            SetDetailVisible(false);
            return;
        }

        _isDetailDrawerCollapsed = false;
        SetDetailVisible(true);
        FillMetadataEditors(_currentBook);
        SetEditMode(false);
    }

    private void BooksList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BooksList.SelectedItem is not MangaBook book || book.IsMissing || book.Pages.Count == 0)
        {
            StatusText.Text = "这本漫画路径失效或没有可阅读图片。";
            return;
        }

        OpenBook(book);
    }

    private void BookCard_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is UIElement element)
        {
            MotionService.ScaleTo(element, 1.025);
        }
    }

    private void BookCard_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is UIElement element)
        {
            MotionService.ScaleTo(element, 1.0);
        }
    }

    private void BookCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement element)
        {
            MotionService.PressBounce(element);
        }
    }

    private void BookCard_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement element)
        {
            MotionService.ScaleTo(element, element.IsMouseOver ? 1.025 : 1.0, MotionService.Fast);
        }
    }

    private void LibraryArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.ListBoxItem>((DependencyObject)e.OriginalSource) is not null)
        {
            return;
        }
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>((DependencyObject)e.OriginalSource) is not null
            || FindAncestor<System.Windows.Controls.TextBox>((DependencyObject)e.OriginalSource) is not null
            || FindAncestor<System.Windows.Controls.ComboBox>((DependencyObject)e.OriginalSource) is not null)
        {
            return;
        }

        BooksList.SelectedItem = null;
        _currentBook = null;
        SetDetailVisible(false);
    }

    private void SaveMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null) return;
        if (!_isEditMode)
        {
            StatusText.Text = "当前是只读模式，请先点击“编辑”。";
            return;
        }

        _currentBook.Author = AuthorBox.Text.Trim();
        _currentBook.CharacterName = CharacterNameBox.Text.Trim();
        _currentBook.ForeignName = ForeignNameBox.Text.Trim();
        _currentBook.ReadingStatus = GetSelectedReadingStatus();
        _currentBook.IsFavorite = FavoriteBox.IsChecked == true;
        if (!TryNormalizeDate(ProducedAtBox.Text, out var producedAt))
        {
            StatusText.Text = "出品时间格式不正确，请使用类似 2002-03-09 的标准格式。";
            return;
        }
        if (!TryNormalizeDate(ImportedAtBox.Text, out var importedAt))
        {
            StatusText.Text = "录入时间格式不正确，请使用类似 2002-03-09 的标准格式。";
            return;
        }

        _currentBook.ProducedAt = producedAt;
        _currentBook.ImportedAt = string.IsNullOrWhiteSpace(importedAt) ? DateTime.Today.ToString("yyyy-MM-dd") : importedAt;
        _currentBook.Summary = SummaryBox.Text.Trim();
        _currentBook.Tags = NormalizeTagsRespectingRules(TagService.ParseTags(TagsBox.Text.Trim()));
        TagsBox.Text = _currentBook.Tags;
        if (int.TryParse(CoverPageBox.Text.Trim(), out var coverPage))
        {
            _currentBook.CoverPageIndex = Math.Clamp(coverPage - 1, 0, Math.Max(_currentBook.PageCount - 1, 0));
        }
        if (!int.TryParse(ReadCountBox.Text.Trim(), out var readCount) || readCount < 0)
        {
            StatusText.Text = "阅读次数必须是大于等于 0 的数字。";
            return;
        }
        _currentBook.ReadCount = readCount;

        _database.SaveMetadata(_currentBook);
        _currentBook.NotifyAll();
        RefreshLibraryViews(tagManager: false, sort: true);
        SetEditMode(false);
        StatusText.Text = "书籍信息已保存。";
    }

    private void ImportedToday_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEditMode) return;
        ImportedAtBox.Text = DateTime.Today.ToString("yyyy-MM-dd");
    }

    private void SetCover_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null) return;
        if (!_isEditMode)
        {
            StatusText.Text = "当前是只读模式，请先点击“编辑”。";
            return;
        }
        if (!int.TryParse(CoverPageBox.Text.Trim(), out var coverPage))
        {
            StatusText.Text = "封面页必须是数字。";
            return;
        }

        _currentBook.CoverPageIndex = Math.Clamp(coverPage - 1, 0, Math.Max(_currentBook.PageCount - 1, 0));
        _database.SaveMetadata(_currentBook);
        _currentBook.CoverImage = _coverCache.LoadOrCreate(_currentBook);
        _currentBook.NotifyAll();
        StatusText.Text = $"封面已设置为第 {_currentBook.CoverPageIndex + 1} 页。";
    }

    private void CycleBookStyle_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null) return;

        _currentBook.CycleBookStyle();
        _database.SaveMetadata(_currentBook);
        _currentBook.NotifyAll();
        _booksView?.Refresh();
        StatusText.Text = $"已切换《{_currentBook.Title}》的卡片样式：样式 {_currentBook.BookStyleIndex + 1}。";
    }

    private void IncreaseReadCount_Click(object sender, RoutedEventArgs e)
    {
        ChangeReadCount(1);
    }

    private void DecreaseReadCount_Click(object sender, RoutedEventArgs e)
    {
        ChangeReadCount(-1);
    }

    private void ChangeReadCount(int delta)
    {
        if (_currentBook is null)
        {
            return;
        }

        _currentBook.ReadCount = Math.Max(0, _currentBook.ReadCount + delta);
        _database.SaveReadCount(_currentBook);
        _currentBook.NotifyAll();
        FillMetadataEditors(_currentBook);
        ApplyBookSort();
        StatusText.Text = $"《{_currentBook.Title}》已标记为读过 {_currentBook.ReadCount} 次。";
    }

    private void HideBook_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null) return;

        var book = _currentBook;
        book.IsHidden = !book.IsHidden;
        _database.SetHidden(book, book.IsHidden);
        book.NotifyAll();
        RefreshLibraryViews(tagManager: false, sort: false);

        if (book.IsHidden && ShowHiddenBox.IsChecked != true)
        {
            BooksList.SelectedItem = null;
            _currentBook = null;
            SetDetailVisible(false);
            StatusText.Text = $"《{book.Title}》已隐藏。勾选“显示隐藏作品”可以重新看到。";
            return;
        }

        FillMetadataEditors(book);
        StatusText.Text = book.IsHidden ? $"《{book.Title}》已隐藏。" : $"《{book.Title}》已恢复显示。";
    }

    private void DeleteBook_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null) return;

        var book = _currentBook;
        var result = System.Windows.MessageBox.Show(
            $"确定从书库中删除《{book.Title}》的记录吗？\n\n这不会删除硬盘里的漫画文件，只会删除软件内的作者、Tag、进度、封面页等记录。",
            "删除库记录",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _database.DeleteBook(book);
        Books.Remove(book);
        _currentBook = null;
        BooksList.SelectedItem = null;
        SetDetailVisible(false);
        RefreshLibraryViews(tagManager: false, authors: true);
        StatusText.Text = $"已删除《{book.Title}》的库记录，源文件未删除。";
    }

    private void ToggleEditMode_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null)
        {
            return;
        }

        SetEditMode(!_isEditMode);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null) return;
        if (!Directory.Exists(_currentBook.FolderPath))
        {
            StatusText.Text = "文件夹不存在，请先重新定位。";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_currentBook.FolderPath}\"",
            UseShellExecute = true
        });
    }

    private void OpenCurrentBook_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null)
        {
            StatusText.Text = "请先选择一本漫画。";
            return;
        }

        OpenBook(_currentBook);
    }

    private void ManualBackup_Click(object sender, RoutedEventArgs e)
    {
        var backupPath = _database.CreateManualBackup();
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            StatusText.Text = "当前还没有可备份的数据库。";
            return;
        }

        StatusText.Text = $"已创建数据库备份：{Path.GetFileName(backupPath)}";
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_storage.BackupPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_storage.BackupPath}\"",
            UseShellExecute = true
        });
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_storage.Root);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_storage.Root}\"",
            UseShellExecute = true
        });
    }

    private void ChooseDataFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择软件数据目录：数据库、封面缓存、备份、日志都会保存在这里",
            SelectedPath = Directory.Exists(_storage.Root) ? _storage.Root : AppStorage.DefaultRoot,
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        var selectedPath = Path.GetFullPath(dialog.SelectedPath);
        var currentPath = Path.GetFullPath(_storage.Root);
        if (selectedPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "当前已经在使用这个数据目录。";
            return;
        }

        try
        {
            AppStorage.SaveCustomRoot(selectedPath);
            StoragePathText.Text = $"下次启动将使用：{selectedPath}";
            StatusText.Text = "数据目录已指定，重启软件后生效。当前运行中的数据库不会热切换，避免写库过程中损坏数据。";
            AppLogger.Info("storage", $"Data root changed for next launch: {selectedPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLogger.Error("storage", ex, "Failed to update data root.");
            StatusText.Text = $"数据目录设置失败：{ex.Message}";
        }
    }

    private void Relocate_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null) return;

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择这本漫画移动后的文件夹",
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        var pages = Directory.EnumerateFiles(dialog.SelectedPath)
            .Where(ImageLoader.IsSupportedImage)
            .OrderBy(path => path, new NaturalPathComparer())
            .ToList();

        if (pages.Count == 0)
        {
            StatusText.Text = "重定位失败：目标文件夹内没有支持的图片。";
            return;
        }

        _currentBook.FolderPath = dialog.SelectedPath;
        _currentBook.PageCount = pages.Count;
        _currentBook.IsMissing = false;
        _currentBook.CoverPageIndex = Math.Clamp(_currentBook.CoverPageIndex, 0, pages.Count - 1);
        _currentBook.LastReadPageIndex = Math.Clamp(_currentBook.LastReadPageIndex, 0, pages.Count - 1);
        _currentBook.Pages.Clear();
        foreach (var page in pages)
        {
            _currentBook.Pages.Add(page);
        }

        _database.UpdateFolderPath(_currentBook);
        _database.SaveMetadata(_currentBook);
        _currentBook.CoverImage = _coverCache.LoadOrCreate(_currentBook);
        _currentBook.NotifyAll();
        FillMetadataEditors(_currentBook);
        StatusText.Text = "重定位完成。";
    }

    private void SaveShortcuts_Click(object sender, RoutedEventArgs e)
    {
        var next = ParseKeys(NextShortcutBox.Text);
        var prev = ParseKeys(PrevShortcutBox.Text);
        if (next.Count == 0 || prev.Count == 0)
        {
            StatusText.Text = "快捷键不能为空。示例：Right,D,Space";
            return;
        }

        if (next.Intersect(prev).Any())
        {
            StatusText.Text = "快捷键冲突：上一页和下一页不能使用相同按键。";
            return;
        }

        _nextKeys = next;
        _prevKeys = prev;
        _database.SaveShortcut("reader.next", NextShortcutBox.Text.Trim());
        _database.SaveShortcut("reader.previous", PrevShortcutBox.Text.Trim());
        StatusText.Text = "快捷键已保存。";
    }

    private void AddTag_Click(object sender, RoutedEventArgs e)
    {
        if (!TryResolveTagForCreate(TagSearchBox.Text.Trim(), out var tag, out var category, out var isExclusive))
        {
            return;
        }

        UpsertManagedTag(tag, category, isExclusive);
        if (_currentBook is not null)
        {
            AddTagToBookRespectingRules(_currentBook, tag);
            TagsBox.Text = _currentBook.Tags;
            _database.SaveMetadata(_currentBook);
            _currentBook.NotifyAll();
        }

        RefreshLibraryViews(authors: false, sort: false);
        StatusText.Text = _currentBook is null
            ? $"已创建独立标签：{tag}"
            : $"已添加 Tag：{tag}";
    }

    private void TagSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RestartDebounceTimer(_tagSearchDebounceTimer);
    }

    private void TagChip_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: TagChip chip } || chip.IsSelected)
        {
            return;
        }

        DragDrop.DoDragDrop((DependencyObject)sender, chip.Name, System.Windows.DragDropEffects.Copy);
    }

    private async void BooksList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        HideImportDropFeedback();
        var folders = GetDroppedFolders(e.Data);
        if (folders.Count > 0)
        {
            e.Handled = true;
            await TryImportSelectedFoldersAsync(folders, "books-list-drop-import");
            return;
        }

        if (!e.Data.GetDataPresent(typeof(string)))
        {
            return;
        }

        var tag = e.Data.GetData(typeof(string)) as string;
        var book = FindAncestor<System.Windows.Controls.ListBoxItem>((DependencyObject)e.OriginalSource)?.DataContext as MangaBook
            ?? BooksList.SelectedItem as MangaBook;
        if (string.IsNullOrWhiteSpace(tag) || book is null)
        {
            return;
        }

        UpsertManagedTag(tag);
        AddTagToBookRespectingRules(book, tag);
        _database.SaveMetadata(book);
        book.NotifyAll();
        if (ReferenceEquals(book, _currentBook))
        {
            TagsBox.Text = book.Tags;
        }
        RefreshLibraryViews(authors: false, sort: false);
        StatusText.Text = $"已给《{book.Title}》添加 Tag：{tag}";
    }

    private async void LibraryPagePanel_Drop(object sender, System.Windows.DragEventArgs e)
    {
        HideImportDropFeedback();
        var folders = GetDroppedFolders(e.Data);
        if (folders.Count > 0)
        {
            e.Handled = true;
            await TryImportSelectedFoldersAsync(folders, "library-drop-import");
        }
    }

    private void LibraryPagePanel_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        UpdateImportDragFeedback(e);
    }

    private void LibraryPagePanel_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        UpdateImportDragFeedback(e);
    }

    private void LibraryPagePanel_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (LibraryPagePanel is null)
        {
            return;
        }

        var point = e.GetPosition(LibraryPagePanel);
        if (point.X < 0 || point.Y < 0 || point.X > LibraryPagePanel.ActualWidth || point.Y > LibraryPagePanel.ActualHeight)
        {
            HideImportDropFeedback();
        }
    }

    private void UpdateImportDragFeedback(System.Windows.DragEventArgs e)
    {
        var folders = GetDroppedFolders(e.Data);
        if (folders.Count == 0)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            HideImportDropFeedback();
            e.Handled = true;
            return;
        }

        e.Effects = System.Windows.DragDropEffects.Copy;
        ShowImportDropFeedback(folders.Count);
        e.Handled = true;
    }

    private void ShowImportDropFeedback(int folderCount)
    {
        ImportDropOverlay.Visibility = Visibility.Visible;
        ImportDropTitle.Text = folderCount == 1
            ? "松开以导入这个文件夹"
            : $"松开以导入 {folderCount} 个文件夹";
        ImportDropHint.Text = folderCount == 1
            ? "会先识别该作者文件夹下的漫画目录，并弹出确认列表。"
            : "会逐个弹出作者导入确认，不会覆盖已经导入的漫画。";
        StatusText.Text = folderCount == 1
            ? "检测到文件夹：松开鼠标开始导入。"
            : $"检测到 {folderCount} 个文件夹：松开鼠标依次导入。";
    }

    private void HideImportDropFeedback()
    {
        if (ImportDropOverlay is not null)
        {
            ImportDropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private static List<string> GetDroppedFolders(System.Windows.IDataObject data)
    {
        if (!data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            || data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths)
        {
            return [];
        }

        return paths
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void TagManagerSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RestartDebounceTimer(_tagManagerSearchDebounceTimer);
    }

    private void CreateManagedTag_Click(object sender, RoutedEventArgs e)
    {
        if (!TryResolveTagForCreate(TagManagerSearchBox.Text.Trim(), out var tag, out var category, out var isExclusive))
        {
            return;
        }

        if (EnumerateKnownTags().Any(name => string.Equals(name, tag, StringComparison.OrdinalIgnoreCase)))
        {
            UpsertManagedTag(tag, category, isExclusive);
            TagManagerSearchBox.Clear();
            RefreshLibraryViews(authors: false, sort: false, filter: false);
            StatusText.Text = $"标签已存在：{tag}";
            return;
        }

        UpsertManagedTag(tag, category, isExclusive);
        TagManagerSearchBox.Clear();
        RefreshLibraryViews(authors: false, sort: false, filter: false);
        StatusText.Text = $"已创建候选标签：{tag}。它会出现在书库 Tag 池，可拖拽到漫画或添加到当前漫画。";
    }

    private void FillMetadataEditors(MangaBook book)
    {
        AuthorBox.Text = book.Author;
        CharacterNameBox.Text = book.CharacterName;
        ForeignNameBox.Text = book.ForeignName;
        ProducedAtBox.Text = book.ProducedAt;
        ImportedAtBox.Text = book.ImportedAt;
        TagsBox.Text = book.Tags;
        CoverPageBox.Text = (book.CoverPageIndex + 1).ToString();
        ReadCountBox.Text = book.ReadCount.ToString();
        SetSelectedReadingStatus(book.ReadingStatus);
        FavoriteBox.IsChecked = book.IsFavorite;
        SummaryBox.Text = book.Summary;
        ReadOnlyAuthorText.Text = EmptyAsPlaceholder(book.Author);
        ReadOnlyCharacterText.Text = EmptyAsPlaceholder(book.CharacterName);
        ReadOnlyForeignNameText.Text = EmptyAsPlaceholder(book.ForeignName);
        ReadOnlyStatusText.Text = book.ReadingStatusText;
        ReadOnlyFavoriteText.Text = book.IsFavorite ? "已收藏" : "未收藏";
        ReadOnlyProducedAtText.Text = EmptyAsPlaceholder(book.ProducedAt);
        ReadOnlyImportedAtText.Text = EmptyAsPlaceholder(book.ImportedAt);
        ReadOnlyTagsText.Text = EmptyAsPlaceholder(book.Tags);
        ReadOnlyCoverPageText.Text = (book.CoverPageIndex + 1).ToString();
        ReadOnlyReadCountText.Text = book.ReadCountText;
        ReadOnlySummaryText.Text = EmptyAsPlaceholder(book.Summary);
        HideBookButton.Content = book.IsHidden ? "恢复显示" : "隐藏作品";
        HideBookButtonEdit.Content = book.IsHidden ? "恢复显示" : "隐藏作品";
    }

    private void SetDetailVisible(bool visible)
    {
        if (DetailPanel is null || DetailShell is null || DetailColumn is null || DetailDrawerToggleButton is null)
        {
            return;
        }

        DetailColumn.Width = new GridLength(0);
        DetailDrawerToggleButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DetailDrawerToggleButton.Content = _isDetailDrawerCollapsed ? "‹" : "›";
        DetailDrawerToggleButton.ToolTip = _isDetailDrawerCollapsed ? "展开详情" : "收起详情";
        DetailDrawerToggleButton.Margin = _isDetailDrawerCollapsed
            ? new Thickness(0, 0, 26, 0)
            : new Thickness(0, 0, 404, 0);
        DetailPanel.Visibility = visible && !_isDetailDrawerCollapsed ? Visibility.Visible : Visibility.Collapsed;

        if (visible && !_isDetailDrawerCollapsed)
        {
            MotionService.ShowDrawer(DetailShell);
        }
        else if (DetailShell.Visibility == Visibility.Visible)
        {
            MotionService.HideDrawer(DetailShell);
        }
        else
        {
            DetailShell.Visibility = Visibility.Collapsed;
        }

        if (!visible)
        {
            _isDetailDrawerCollapsed = false;
            SetEditMode(false);
        }
    }

    private void ToggleDetailDrawer_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null || BooksList.SelectedItem is null)
        {
            return;
        }

        _isDetailDrawerCollapsed = !_isDetailDrawerCollapsed;
        SetDetailVisible(true);
    }

    private void SetEditMode(bool enabled)
    {
        _isEditMode = enabled;
        if (EditModeButton is null)
        {
            return;
        }

        EditModeButton.Content = enabled ? "取消编辑" : "编辑";
        EditModeHintText.Text = enabled ? "编辑模式：修改后点击“保存信息”" : "只读模式：点击“编辑”后修改信息";
        ReadOnlyInfoPanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        EditFormPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        SaveMetadataButton.IsEnabled = enabled;
        SetCoverButton.IsEnabled = enabled;
        ImportedTodayButton.IsEnabled = enabled;

        foreach (var box in new[] { AuthorBox, CharacterNameBox, ForeignNameBox, ProducedAtBox, ImportedAtBox, TagsBox, CoverPageBox, ReadCountBox, SummaryBox })
        {
            box.IsReadOnly = !enabled;
            box.Opacity = enabled ? 1.0 : 0.78;
        }
    }

    private static string EmptyAsPlaceholder(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "未填写" : value;
    }

    private void LoadShortcuts()
    {
        var shortcuts = _database.LoadShortcuts();
        if (shortcuts.TryGetValue("reader.next", out var next))
        {
            NextShortcutBox.Text = next;
            _nextKeys = ParseKeys(next);
        }
        if (shortcuts.TryGetValue("reader.previous", out var previous))
        {
            PrevShortcutBox.Text = previous;
            _prevKeys = ParseKeys(previous);
        }
    }

    private void RefreshVisibleTags()
    {
        if (TagSearchBox is null)
        {
            return;
        }

        var query = TagSearchBox.Text.Trim();
        var tagNames = EnumerateKnownTags()
            .Append(query)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var tags = tagNames
            .Select(tag => CreateTagChip(tag, _activeTagFilters.Contains(tag)))
            .Where(tag => string.IsNullOrWhiteSpace(query) || tag.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(tag => TagCategoryOrder(tag.Category))
            .ThenBy(tag => tag.Name)
            .ToList();

        VisibleTags.Clear();
        foreach (var tag in tags)
        {
            VisibleTags.Add(tag);
        }
    }

    private void RefreshAuthorFilters()
    {
        if (_isRefreshingAuthorFilters)
        {
            return;
        }

        _isRefreshingAuthorFilters = true;
        var selectedAuthor = AuthorFilterBox?.SelectedItem as string;
        try
        {
            var authors = Books.Where(book => ShowHiddenBox?.IsChecked == true || !book.IsHidden)
                .Select(book => book.Author)
                .Where(author => !string.IsNullOrWhiteSpace(author))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(author => author)
                .ToList();

            AuthorFilters.Clear();
            AuthorFilters.Add("全部作者");
            foreach (var author in authors)
            {
                AuthorFilters.Add(author);
            }

            if (AuthorFilterBox is not null)
            {
                AuthorFilterBox.SelectedItem = selectedAuthor is not null && AuthorFilters.Contains(selectedAuthor)
                    ? selectedAuthor
                    : "全部作者";
            }
        }
        finally
        {
            _isRefreshingAuthorFilters = false;
        }
    }

    private void BookSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RestartDebounceTimer(_bookSearchDebounceTimer);
    }

    private void ConfigureSearchDebounceTimers()
    {
        _bookSearchDebounceTimer.Tick += (_, _) =>
        {
            _bookSearchDebounceTimer.Stop();
            RefreshBookFilter();
        };
        _tagSearchDebounceTimer.Tick += (_, _) =>
        {
            _tagSearchDebounceTimer.Stop();
            RefreshVisibleTags();
        };
        _tagManagerSearchDebounceTimer.Tick += (_, _) =>
        {
            _tagManagerSearchDebounceTimer.Stop();
            RefreshTagManagementItems();
        };
    }

    private static void RestartDebounceTimer(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    private void StopSearchDebounceTimers()
    {
        _bookSearchDebounceTimer.Stop();
        _tagSearchDebounceTimer.Stop();
        _tagManagerSearchDebounceTimer.Stop();
    }

    private void BookFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingAuthorFilters)
        {
            return;
        }

        RefreshLibraryViews(tagManager: false, sort: false);
    }

    private void ToggleLibraryChrome_Click(object sender, RoutedEventArgs e)
    {
        SetLibraryChromeCollapsed(!_libraryChromeCollapsed);
    }

    private void ToggleStatusPanel_Click(object sender, RoutedEventArgs e)
    {
        _isLogPanelVisible = !_isLogPanelVisible;
        UpdateLogPanelVisibility();
    }

    private void BooksList_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (Books.Count < 80)
        {
            return;
        }

        if (e.VerticalOffset > 2 && !_libraryChromeCollapsed)
        {
            SetLibraryChromeCollapsed(true);
        }
    }

    private void SetLibraryChromeCollapsed(bool collapsed)
    {
        _libraryChromeCollapsed = collapsed;
        if (LibraryFilterControlsPanel is not null)
        {
            LibraryFilterControlsPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        }
        if (LibraryTagPanel is not null)
        {
            LibraryTagPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        }
        if (LibraryMetricPanel is not null)
        {
            LibraryMetricPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        }
        if (LibraryChromeToggleButton is not null)
        {
            LibraryChromeToggleButton.Content = collapsed ? "展开筛选" : "专注浏览";
        }
        StatusText.Text = collapsed
            ? "已进入专注浏览：筛选控件、Tag 池和统计卡片已收起，点击“展开筛选”可恢复。"
            : "已展开筛选区。";
    }

    private void UpdateLogPanelVisibility()
    {
        if (LogPanel is not null)
        {
            LogPanel.Visibility = _isLogPanelVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        if (LogPanelToggleButton is not null)
        {
            LogPanelToggleButton.Content = _isLogPanelVisible ? "收起日志" : "日志";
        }
    }

    private void UpdateStoragePathText()
    {
        var mode = _storage.UsesCustomRoot ? "自定义数据目录" : "默认数据目录";
        StoragePathText.Text = $"{mode}：{_storage.Root}";
    }

    private void EditSelectedAuthor_Click(object sender, RoutedEventArgs e)
    {
        var oldAuthor = AuthorFilterBox?.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(oldAuthor) || oldAuthor == "全部作者")
        {
            StatusText.Text = "请先在作者筛选中选择一个具体作者，再批量编辑。";
            return;
        }

        var affectedBooks = Books
            .Where(book => string.Equals(book.Author, oldAuthor, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (affectedBooks.Count == 0)
        {
            StatusText.Text = $"没有找到作者为“{oldAuthor}”的漫画。";
            return;
        }

        var dialog = new TagNameDialog(oldAuthor, $"批量编辑作者（{affectedBooks.Count} 本）") { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.TagName))
        {
            StatusText.Text = "没有修改作者。";
            return;
        }

        var newAuthor = dialog.TagName.Trim();
        if (string.Equals(oldAuthor, newAuthor, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "作者名没有变化。";
            return;
        }

        _database.SaveBookAuthorsBatch(
            affectedBooks.Select(book => (book.Id, newAuthor)).ToList(),
            "before-author-batch-rename");

        foreach (var book in affectedBooks)
        {
            book.Author = newAuthor;
            book.NotifyAll();
        }

        RefreshLibraryViews(sort: true);
        if (AuthorFilterBox is not null)
        {
            AuthorFilterBox.SelectedItem = newAuthor;
        }
        StatusText.Text = $"已将作者“{oldAuthor}”批量改为“{newAuthor}”，影响 {affectedBooks.Count} 本漫画。";
    }

    private void RefreshBookFilter()
    {
        _booksView?.Refresh();
        RefreshShelfOverview();
        RefreshHomeShelves();
    }

    private void RefreshLibraryViews(
        bool tags = true,
        bool tagManager = true,
        bool authors = true,
        bool sort = false,
        bool filter = true,
        bool activeTags = false)
    {
        if (tags || tagManager || activeTags)
        {
            RebuildTagIndex();
        }
        if (activeTags)
        {
            RefreshActiveTagFilters();
        }
        if (tags)
        {
            RefreshVisibleTags();
        }
        if (tagManager)
        {
            RefreshTagManagementItems();
        }
        if (authors)
        {
            RefreshAuthorFilters();
        }
        if (sort)
        {
            ApplyBookSort();
        }
        if (filter)
        {
            RefreshBookFilter();
        }
    }

    private void TagChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TagChip chip })
        {
            return;
        }

        if (chip.IsSelected)
        {
            return;
        }

        if (_currentBook is not null)
        {
            UpsertManagedTag(chip.Name, chip.Category, chip.IsExclusive);
            AddTagToBookRespectingRules(_currentBook, chip.Name);
            TagsBox.Text = _currentBook.Tags;
            _database.SaveMetadata(_currentBook);
            _currentBook.NotifyAll();
            RefreshLibraryViews(authors: false, sort: false);
            StatusText.Text = $"已给《{_currentBook.Title}》添加 Tag：{chip.Name}";
            return;
        }

        if (_activeTagFilters.Contains(chip.Name))
        {
            _activeTagFilters.Remove(chip.Name);
            StatusText.Text = $"已取消 Tag：{chip.Name}";
        }
        else
        {
            var category = TagCategory(chip.Name);
            if (IsExclusiveTag(chip.Name))
            {
                RemoveActiveTagsInExclusiveGroup(category);
            }
            _activeTagFilters.Add(chip.Name);
            StatusText.Text = $"已追加 Tag：{chip.Name}";
        }

        RefreshLibraryViews(tagManager: false, authors: false, sort: false, activeTags: true);
    }

    private void ActiveTagChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TagChip chip })
        {
            return;
        }

        if (_activeTagFilters.Remove(chip.Name))
        {
            RefreshLibraryViews(tagManager: false, authors: false, sort: false, activeTags: true);
            StatusText.Text = $"已移除 Tag：{chip.Name}";
        }
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        StopSearchDebounceTimers();
        BookSearchBox.Text = "";
        TagSearchBox.Text = "";
        _activeTagFilters.Clear();
        AuthorFilterBox.SelectedItem = "全部作者";
        StatusFilterBox.SelectedIndex = 0;
        FavoriteOnlyBox.IsChecked = false;
        ShowHiddenBox.IsChecked = false;
        RefreshLibraryViews(tagManager: false, authors: false, sort: false, activeTags: true);
        StatusText.Text = "已清空书架筛选。";
    }

    private void SortBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyBookSort();
    }

    private void FilterComboBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox comboBox || comboBox.IsDropDownOpen)
        {
            return;
        }

        comboBox.Focus();
        comboBox.IsDropDownOpen = true;
        e.Handled = true;
    }

    private void ApplyBookSort()
    {
        if (_booksView is null || SortBox is null)
        {
            return;
        }

        _booksView.SortDescriptions.Clear();
        switch (SortBox.SelectedIndex)
        {
            case 1:
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.Author), ListSortDirection.Ascending));
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.Title), ListSortDirection.Ascending));
                break;
            case 2:
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.LastReadPageIndex), ListSortDirection.Descending));
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.Title), ListSortDirection.Ascending));
                break;
            case 3:
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.ReadCount), ListSortDirection.Descending));
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.Title), ListSortDirection.Ascending));
                break;
            case 4:
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.ImportedAt), ListSortDirection.Descending));
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.Title), ListSortDirection.Ascending));
                break;
            case 5:
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.ProducedAt), ListSortDirection.Descending));
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.Title), ListSortDirection.Ascending));
                break;
            default:
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.Title), ListSortDirection.Ascending));
                break;
        }

        _booksView.Refresh();
    }

    private bool FilterBook(object item)
    {
        if (item is not MangaBook book)
        {
            return false;
        }

        if (book.IsHidden && ShowHiddenBox?.IsChecked != true)
        {
            return false;
        }

        if (FavoriteOnlyBox?.IsChecked == true && !book.IsFavorite)
        {
            return false;
        }

        var selectedStatus = GetSelectedStatusFilter();
        if (!string.IsNullOrWhiteSpace(selectedStatus)
            && !string.Equals(book.ReadingStatus, selectedStatus, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var selectedAuthor = AuthorFilterBox?.SelectedItem as string;
        if (!string.IsNullOrWhiteSpace(selectedAuthor)
            && selectedAuthor != "全部作者"
            && !string.Equals(book.Author, selectedAuthor, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_activeTagFilters.Count > 0
            && !_activeTagFilters.All(activeTag =>
                book.TagItems.Any(tag => string.Equals(tag.Name, activeTag, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        var query = BookSearchBox?.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return Contains(book.Title, query)
            || Contains(book.Author, query)
            || Contains(book.CharacterName, query)
            || Contains(book.ForeignName, query)
            || Contains(book.Tags, query)
            || Contains(book.Summary, query)
            || Contains(book.ProducedAt, query)
            || Contains(book.ImportedAt, query)
            || Contains(book.ReadingStatusText, query)
            || Contains(book.IsFavorite ? "收藏" : "", query)
            || Contains(book.ReadCountText, query);
    }

    private string GetSelectedReadingStatus()
    {
        if (ReadingStatusBox.SelectedItem is System.Windows.Controls.ComboBoxItem item
            && item.Tag is string status)
        {
            return status;
        }

        return "unread";
    }

    private void SetSelectedReadingStatus(string status)
    {
        foreach (var item in ReadingStatusBox.Items.OfType<System.Windows.Controls.ComboBoxItem>())
        {
            if (item.Tag is string value && string.Equals(value, status, StringComparison.OrdinalIgnoreCase))
            {
                ReadingStatusBox.SelectedItem = item;
                return;
            }
        }

        ReadingStatusBox.SelectedIndex = 0;
    }

    private string GetSelectedStatusFilter()
    {
        return StatusFilterBox?.SelectedIndex switch
        {
            1 => "unread",
            2 => "reading",
            3 => "finished",
            4 => "paused",
            _ => ""
        };
    }

    private static bool Contains(string source, string query)
    {
        return !string.IsNullOrWhiteSpace(source)
            && source.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshShelfOverview()
    {
        if (VisibleBookCountText is null
            || TotalBookCountText is null
            || FavoriteCountText is null
            || ReadingNowCountText is null
            || FinishedCountText is null
            || FilterSummaryText is null
            || ShelfEmptyState is null
            || ShelfEmptyHintText is null)
        {
            return;
        }

        var includeHidden = ShowHiddenBox?.IsChecked == true;
        var libraryBooks = Books.Where(book => includeHidden || !book.IsHidden).ToList();
        var visibleCount = _booksView?.Cast<object>().Count() ?? libraryBooks.Count;
        var favoriteCount = libraryBooks.Count(book => book.IsFavorite);
        var readingNowCount = libraryBooks.Count(book => book.ReadingStatus == "reading");
        var finishedCount = libraryBooks.Count(book => book.ReadingStatus == "finished");

        VisibleBookCountText.Text = $"{visibleCount} 本";
        TotalBookCountText.Text = includeHidden
            ? $"当前库共 {Books.Count} 本，含隐藏作品"
            : $"当前库共 {libraryBooks.Count} 本可见漫画";
        FavoriteCountText.Text = $"{favoriteCount} 本";
        ReadingNowCountText.Text = $"{readingNowCount} 本";
        FinishedCountText.Text = $"{finishedCount} 本";
        FilterSummaryText.Text = BuildFilterSummary(visibleCount, libraryBooks.Count);

        var isEmpty = visibleCount == 0;
        ShelfEmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        ShelfEmptyHintText.Text = BuildEmptyHint();
    }

    private string BuildFilterSummary(int visibleCount, int libraryCount)
    {
        var parts = new List<string>();
        var query = BookSearchBox?.Text.Trim();
        if (!string.IsNullOrWhiteSpace(query))
        {
            parts.Add($"搜索“{query}”");
        }

        var selectedAuthor = AuthorFilterBox?.SelectedItem as string;
        if (!string.IsNullOrWhiteSpace(selectedAuthor) && selectedAuthor != "全部作者")
        {
            parts.Add($"作者 {selectedAuthor}");
        }

        var status = GetSelectedStatusFilter();
        if (!string.IsNullOrWhiteSpace(status))
        {
            parts.Add($"状态 {MapStatusText(status)}");
        }

        if (_activeTagFilters.Count > 0)
        {
            parts.Add($"Tag {string.Join(" + ", _activeTagFilters.OrderBy(tag => tag))}");
        }

        if (FavoriteOnlyBox?.IsChecked == true)
        {
            parts.Add("只看收藏");
        }

        if (ShowHiddenBox?.IsChecked == true)
        {
            parts.Add("含隐藏作品");
        }

        if (parts.Count == 0)
        {
            return $"当前显示全部漫画，共 {visibleCount} / {libraryCount} 本。";
        }

        return $"当前命中 {visibleCount} / {libraryCount} 本，条件：{string.Join(" · ", parts)}";
    }

    private string BuildEmptyHint()
    {
        var reasons = new List<string>();
        if (!string.IsNullOrWhiteSpace(BookSearchBox?.Text.Trim()))
        {
            reasons.Add("搜索词");
        }
        if ((AuthorFilterBox?.SelectedItem as string) is string author && author != "全部作者")
        {
            reasons.Add("作者筛选");
        }
        if (!string.IsNullOrWhiteSpace(GetSelectedStatusFilter()))
        {
            reasons.Add("状态筛选");
        }
        if (_activeTagFilters.Count > 0)
        {
            reasons.Add("Tag 筛选");
        }
        if (FavoriteOnlyBox?.IsChecked == true)
        {
            reasons.Add("收藏筛选");
        }

        return reasons.Count == 0
            ? "这里还没有可显示的漫画，可以先导入新的漫画文件夹。"
            : $"当前筛选没有命中结果，先尝试清空{string.Join("、", reasons)}。";
    }

    private static string MapStatusText(string status)
    {
        return status switch
        {
            "reading" => "在读",
            "finished" => "已读",
            "paused" => "搁置",
            _ => "未读"
        };
    }

    private void RefreshHomeShelves()
    {
        if (HomeEmptyState is null
            || HomeSectionsPanel is null
            || HomeIntroText is null
            || ContinueReadingEmptyText is null
            || RecentReadingEmptyText is null
            || FavoriteShowcaseEmptyText is null
            || RecentlyAddedEmptyText is null)
        {
            return;
        }

        var homeBooks = Books
            .Where(book => !book.IsHidden && !book.IsMissing && book.Pages.Count > 0)
            .ToList();

        ReplaceBooks(ContinueReadingBooks, homeBooks
            .Where(book => book.ReadingStatus == "reading" || (book.LastReadPageIndex > 0 && book.ReadingStatus != "finished"))
            .OrderByDescending(book => book.ReadingStatus == "reading")
            .ThenByDescending(book => book.LastReadPageIndex)
            .ThenByDescending(book => book.ReadCount)
            .Take(8));

        ReplaceBooks(RecentReadingBooks, homeBooks
            .Where(book => book.LastReadPageIndex > 0 || book.ReadCount > 0 || book.ReadingStatus == "finished")
            .OrderByDescending(book => book.LastReadPageIndex)
            .ThenByDescending(book => book.ReadCount)
            .Take(12));

        ReplaceBooks(FavoriteShowcaseBooks, homeBooks
            .Where(book => book.IsFavorite)
            .OrderByDescending(book => book.ReadingStatus == "reading")
            .ThenByDescending(book => book.ReadCount)
            .ThenBy(book => book.Title)
            .Take(12));

        ReplaceBooks(RecentlyAddedBooks, homeBooks
            .OrderByDescending(book => book.ImportedAt)
            .ThenByDescending(book => book.ProducedAt)
            .Take(12));

        var isLibraryEmpty = homeBooks.Count == 0;
        HomeEmptyState.Visibility = isLibraryEmpty ? Visibility.Visible : Visibility.Collapsed;
        HomeSectionsPanel.Visibility = isLibraryEmpty ? Visibility.Collapsed : Visibility.Visible;

        HomeIntroText.Text = isLibraryEmpty
            ? "首页不该拿标题和空段落占位置。先导入一本漫画，后面才会出现继续阅读、收藏和最近加入。"
            : $"现在有 {homeBooks.Count} 本可浏览漫画，主页会优先展示正在读和收藏的内容。";

        ContinueReadingEmptyText.Visibility = ContinueReadingBooks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentReadingEmptyText.Visibility = RecentReadingBooks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FavoriteShowcaseEmptyText.Visibility = FavoriteShowcaseBooks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentlyAddedEmptyText.Visibility = RecentlyAddedBooks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ReplaceBooks(ObservableCollection<MangaBook> target, IEnumerable<MangaBook> source)
    {
        target.Clear();
        foreach (var book in source)
        {
            target.Add(book);
        }
    }

    private void OpenBook(MangaBook book)
    {
        if (book.IsMissing || book.Pages.Count == 0)
        {
            StatusText.Text = "这本漫画路径失效或没有可阅读图片。";
            return;
        }

        AppLogger.Info("reader-open", $"Opening reader: {book.Title}, pages={book.Pages.Count}, folder={book.FolderPath}");
        var reader = new ReaderWindow(book, _database, _nextKeys, _prevKeys)
        {
            Owner = this
        };
        reader.Show();
    }

    private void HomeBook_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MangaBook book })
        {
            OpenBook(book);
        }
    }

    private void NavHome_Click(object sender, RoutedEventArgs e)
    {
        ShowHomeView();
    }

    private void NavLibrary_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryView("library");
        ResetLibraryFilters();
    }

    private void NavTags_Click(object sender, RoutedEventArgs e)
    {
        ShowTagsView();
    }

    private void ShowHomeView()
    {
        _currentNavigationKey = "home";
        if (HomePagePanel is not null) MotionService.ShowWithFade(HomePagePanel);
        if (LibraryPagePanel is not null) MotionService.HideWithFade(LibraryPagePanel);
        if (TagsPagePanel is not null) MotionService.HideWithFade(TagsPagePanel);
        SetDetailVisible(false);
        RefreshHomeShelves();
        UpdateNavigationVisuals();
    }

    private void ShowLibraryView(string navigationKey)
    {
        _currentNavigationKey = navigationKey;
        if (HomePagePanel is not null) MotionService.HideWithFade(HomePagePanel);
        if (LibraryPagePanel is not null) MotionService.ShowWithFade(LibraryPagePanel);
        if (TagsPagePanel is not null) MotionService.HideWithFade(TagsPagePanel);
        SetDetailVisible(_currentBook is not null && BooksList.SelectedItem is not null);
        UpdateNavigationVisuals();
        RefreshBookFilter();
        EnsureLibraryViewCanShowBooks();
    }

    private void ShowTagsView()
    {
        _currentNavigationKey = "tags";
        if (HomePagePanel is not null) MotionService.HideWithFade(HomePagePanel);
        if (LibraryPagePanel is not null) MotionService.HideWithFade(LibraryPagePanel);
        if (TagsPagePanel is not null) MotionService.ShowWithFade(TagsPagePanel);
        SetDetailVisible(false);
        RefreshLibraryViews(tags: false, tagManager: true, authors: false, filter: false);
        UpdateNavigationVisuals();
    }

    private void ResetLibraryFilters()
    {
        StopSearchDebounceTimers();
        BookSearchBox.Text = "";
        TagSearchBox.Text = "";
        _activeTagFilters.Clear();
        AuthorFilterBox.SelectedItem = "全部作者";
        StatusFilterBox.SelectedIndex = 0;
        FavoriteOnlyBox.IsChecked = false;
        ShowHiddenBox.IsChecked = false;
        RefreshLibraryViews(tagManager: false, authors: false, sort: false, activeTags: true);
    }

    private void EnsureLibraryViewCanShowBooks()
    {
        if (Books.Count == 0 || _booksView is null)
        {
            return;
        }

        var visibleCount = _booksView.Cast<object>().Count();
        if (visibleCount > 0)
        {
            return;
        }

        if (HasActiveLibraryFilter())
        {
            ResetLibraryFilters();
            StatusText.Text = "书库中有漫画，但当前筛选没有命中，已自动回到全部漫画。";
            return;
        }

        ShelfEmptyHintText.Text = $"已识别 {Books.Count} 本漫画，但视图没有显示。请重新同步书架；如果仍为空，这是列表视图刷新问题。";
    }

    private bool HasActiveLibraryFilter()
    {
        var selectedAuthor = AuthorFilterBox?.SelectedItem as string;
        return !string.IsNullOrWhiteSpace(BookSearchBox?.Text)
            || !string.IsNullOrWhiteSpace(TagSearchBox?.Text)
            || _activeTagFilters.Count > 0
            || (!string.IsNullOrWhiteSpace(selectedAuthor) && selectedAuthor != "全部作者")
            || StatusFilterBox?.SelectedIndex > 0
            || FavoriteOnlyBox?.IsChecked == true
            || ShowHiddenBox?.IsChecked == true;
    }

    private void UpdateNavigationVisuals()
    {
        SetNavButtonState(HomeNavButton, _currentNavigationKey == "home");
        SetNavButtonState(LibraryNavButton, _currentNavigationKey == "library");
        SetNavButtonState(TagsNavButton, _currentNavigationKey == "tags");
    }

    private static void SetNavButtonState(System.Windows.Controls.Button? button, bool active)
    {
        if (button is null)
        {
            return;
        }

        button.Background = active
            ? new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#111827"))
            : System.Windows.Media.Brushes.Transparent;
        button.Foreground = active
            ? System.Windows.Media.Brushes.White
            : new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#374151"));
    }

    private void FastVerticalScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.ScrollViewer viewer || viewer.ScrollableHeight <= 0)
        {
            return;
        }

        viewer.ScrollToVerticalOffset(ClampOffset(viewer.VerticalOffset - e.Delta * WheelScrollMultiplier, viewer.ScrollableHeight));
        e.Handled = true;
    }

    private void HorizontalShelfScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not System.Windows.Controls.ScrollViewer viewer || viewer.ScrollableWidth <= 0)
        {
            return;
        }

        var nextOffset = ClampOffset(viewer.HorizontalOffset - e.Delta * WheelScrollMultiplier, viewer.ScrollableWidth);
        if (Math.Abs(nextOffset - viewer.HorizontalOffset) < 0.1)
        {
            return;
        }

        viewer.ScrollToHorizontalOffset(nextOffset);
        e.Handled = true;
    }

    private void FastItemsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindDescendant<System.Windows.Controls.ScrollViewer>((DependencyObject)sender) is not { ScrollableHeight: > 0 } viewer)
        {
            return;
        }

        viewer.ScrollToVerticalOffset(ClampOffset(viewer.VerticalOffset - e.Delta * WheelScrollMultiplier, viewer.ScrollableHeight));
        e.Handled = true;
    }

    private static double ClampOffset(double offset, double maxOffset)
    {
        return Math.Max(0, Math.Min(offset, maxOffset));
    }

    private void SaveCurrentProgress()
    {
        if (_currentBook is not null)
        {
            _database.SaveProgress(_currentBook);
        }
    }

    private static List<Key> ParseKeys(string text)
    {
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Enum.TryParse<Key>(value, true, out var key) ? key : (Key?)null)
            .Where(key => key is not null)
            .Select(key => key!.Value)
            .Distinct()
            .ToList();
    }

    private static bool TryNormalizeDate(string input, out string normalized)
    {
        var text = input.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            normalized = "";
            return true;
        }

        if (DateTime.TryParse(text, out var date))
        {
            normalized = date.ToString("yyyy-MM-dd");
            return true;
        }

        normalized = "";
        return false;
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject current) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
        {
            var child = VisualTreeHelper.GetChild(current, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private IEnumerable<string> EnumerateKnownTags()
    {
        return DefaultTagPresets.Select(tag => tag.Name)
            .Concat(_managedTags)
            .Concat(Books.SelectMany(book => book.TagItems.Select(tag => tag.Name)))
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Where(tag => !_suppressedTags.Contains(tag) || GetTagUsageCount(tag) > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private void RebuildTagIndex()
    {
        _tagBooksByName.Clear();
        foreach (var book in Books)
        {
            foreach (var item in book.TagItems)
            {
                if (string.IsNullOrWhiteSpace(item.Name))
                {
                    continue;
                }

                if (!_tagBooksByName.TryGetValue(item.Name, out var books))
                {
                    books = [];
                    _tagBooksByName[item.Name] = books;
                }

                books.Add(book);
            }
        }
    }

    private int GetTagUsageCount(string tag)
    {
        return _tagBooksByName.TryGetValue(tag, out var books) ? books.Count : 0;
    }

    private bool IsBuiltInTag(string tag)
    {
        return DefaultTagPresets.Any(item => string.Equals(item.Name, tag, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadManagedTags()
    {
        _managedTags.Clear();
        _managedTagCategories.Clear();
        _managedTagIsExclusive.Clear();
        _managedTagUpdatedAt.Clear();
        foreach (var tag in _database.LoadManagedTags().Where(tag => !string.IsNullOrWhiteSpace(tag.Name)))
        {
            _managedTags.Add(tag.Name);
            _managedTagCategories[tag.Name] = tag.Category;
            _managedTagIsExclusive[tag.Name] = tag.IsExclusive;
            _managedTagUpdatedAt[tag.Name] = tag.UpdatedAt;
        }

        _suppressedTags.Clear();
        foreach (var tag in _database.LoadSuppressedTags().Where(tag => !string.IsNullOrWhiteSpace(tag)))
        {
            _suppressedTags.Add(tag);
        }
    }

    private void UpsertManagedTag(string tag, string? category = null, bool? isExclusive = null)
    {
        var resolvedCategory = category ?? TagCategory(tag);
        var resolvedExclusive = isExclusive ?? IsExclusiveTag(tag);
        _suppressedTags.Remove(tag);
        _managedTags.Add(tag);
        _managedTagCategories[tag] = resolvedCategory;
        _managedTagIsExclusive[tag] = resolvedExclusive;
        _managedTagUpdatedAt[tag] = DateTimeOffset.Now.ToString("O");
        _database.SaveManagedTag(tag, resolvedCategory, resolvedExclusive);
    }

    private bool TryResolveTagForCreate(string initialValue, out string tag, out string category, out bool isExclusive)
    {
        tag = "";
        category = "自定义";
        isExclusive = false;

        if (EnumerateKnownTags().Any(name => string.Equals(name, initialValue, StringComparison.OrdinalIgnoreCase)))
        {
            tag = initialValue.Trim();
            category = TagCategory(tag);
            isExclusive = IsExclusiveTag(tag);
            return true;
        }

        var dialog = new TagCreateDialog(initialValue) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.TagName))
        {
            StatusText.Text = "没有创建标签。";
            return false;
        }

        tag = dialog.TagName;
        category = dialog.TagCategory;
        isExclusive = dialog.IsExclusive;
        return true;
    }

    private void AddTagToBookRespectingRules(MangaBook book, string tag)
    {
        var names = TagService.ParseTags(book.Tags).ToList();
        if (names.Any(name => string.Equals(name, tag, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (IsExclusiveTag(tag))
        {
            var category = TagCategory(tag);
            names = names
                .Where(name => !IsExclusiveTag(name) || !string.Equals(TagCategory(name), category, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        names.Add(tag);
        book.Tags = TagService.FormatTags(names);
    }

    private string NormalizeTagsRespectingRules(IEnumerable<string> tags)
    {
        var normalized = new List<string>();
        foreach (var tag in tags)
        {
            if (IsExclusiveTag(tag))
            {
                var category = TagCategory(tag);
                normalized.RemoveAll(name => IsExclusiveTag(name) && string.Equals(TagCategory(name), category, StringComparison.OrdinalIgnoreCase));
            }
            if (!normalized.Any(name => string.Equals(name, tag, StringComparison.OrdinalIgnoreCase)))
            {
                normalized.Add(tag);
            }
        }

        return TagService.FormatTags(normalized);
    }

    private static string TagColor(string tag)
    {
        return TagService.GetColor(tag);
    }

    private TagChip CreateTagChip(string tag, bool isSelected = false)
    {
        var preset = DefaultTagPresets.FirstOrDefault(item => string.Equals(item.Name, tag, StringComparison.OrdinalIgnoreCase));
        var isBuiltIn = preset is not null;
        var category = TagCategory(tag);
        var usageCount = GetTagUsageCount(tag);
        return preset is not null
            ? new TagChip
            {
                Name = preset.Name,
                Category = category,
                Color = preset.Color,
                IsExclusive = IsExclusiveTag(tag),
                IsSelected = isSelected,
                UsageCount = usageCount,
                IsBuiltIn = isBuiltIn,
                SourceText = "内置预设",
                UpdatedAt = ResolveTagUpdatedAt(tag),
                PreviewBooks = GetTagBooks(tag).Take(3).ToList()
            }
            : new TagChip
            {
                Name = tag,
                Category = category,
                Color = TagColor(tag),
                IsExclusive = IsExclusiveTag(tag),
                IsSelected = isSelected,
                UsageCount = usageCount,
                IsBuiltIn = false,
                SourceText = _managedTags.Contains(tag) ? "用户标签" : "书籍标签",
                UpdatedAt = ResolveTagUpdatedAt(tag),
                PreviewBooks = GetTagBooks(tag).Take(3).ToList()
            };
    }

    private static int TagCategoryOrder(string category)
    {
        return TagService.CategoryOrder(category);
    }

    private void RefreshActiveTagFilters()
    {
        if (ActiveTagSummaryText is null || ActiveTagFilterList is null)
        {
            return;
        }

        var chips = _activeTagFilters
            .OrderBy(tag => TagCategoryOrder(TagCategory(tag)))
            .ThenBy(tag => tag)
            .Select(tag => CreateTagChip(tag))
            .ToList();

        ActiveTagFilters.Clear();
        foreach (var chip in chips)
        {
            ActiveTagFilters.Add(chip);
        }

        ActiveTagSummaryText.Text = chips.Count == 0
            ? "已选 0 个 Tag"
            : $"已选 {chips.Count} 个 Tag";
        ActiveTagFilterList.Visibility = chips.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshTagManagementItems()
    {
        var query = TagManagerSearchBox?.Text.Trim() ?? "";
        var chips = EnumerateKnownTags()
            .Select(tag => CreateTagChip(tag))
            .Where(tag => string.IsNullOrWhiteSpace(query)
                || tag.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || tag.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(tag => TagCategoryOrder(tag.Category))
            .ThenByDescending(tag => tag.UsageCount)
            .ThenBy(tag => tag.Name)
            .ToList();

        TagManagerItems.Clear();
        foreach (var chip in chips)
        {
            TagManagerItems.Add(chip);
        }

        if (TagManagerTotalCountText is not null)
        {
            TagManagerTotalCountText.Text = $"{EnumerateKnownTags().Count()} 个";
        }
        if (TagManagerUsedCountText is not null)
        {
            TagManagerUsedCountText.Text = $"{EnumerateKnownTags().Count(tag => GetTagUsageCount(tag) > 0)} 个";
        }
        if (TagManagerStandaloneCountText is not null)
        {
            TagManagerStandaloneCountText.Text = $"{EnumerateKnownTags().Count(tag => GetTagUsageCount(tag) == 0)} 个";
        }
        if (TagManagerEmptyState is not null)
        {
            TagManagerEmptyState.Visibility = chips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private List<MangaBook> GetTagBooks(string tag)
    {
        return _tagBooksByName.TryGetValue(tag, out var books)
            ? books.Take(3).ToList()
            : [];
    }

    private string ResolveTagUpdatedAt(string tag)
    {
        if (_managedTagUpdatedAt.TryGetValue(tag, out var updatedAt) && DateTimeOffset.TryParse(updatedAt, out var managedTime))
        {
            return managedTime.ToString("yyyy-MM-dd HH:mm");
        }

        return "";
    }

    private string TagCategory(string tag)
    {
        if (_managedTagCategories.TryGetValue(tag, out var category) && !string.IsNullOrWhiteSpace(category))
        {
            return category;
        }

        return TagService.GetCategory(tag);
    }

    private bool IsExclusiveTag(string tag)
    {
        if (_managedTagIsExclusive.TryGetValue(tag, out var isExclusive))
        {
            return isExclusive;
        }

        var preset = DefaultTagPresets.FirstOrDefault(item => string.Equals(item.Name, tag, StringComparison.OrdinalIgnoreCase));
        if (preset is not null)
        {
            return preset.IsExclusive;
        }

        return false;
    }

    private bool IsMutuallyExclusiveTagCategory(string category)
    {
        return TagService.IsMutuallyExclusiveCategory(category);
    }

    private void RemoveActiveTagsInExclusiveGroup(string category)
    {
        foreach (var tag in _activeTagFilters
            .Where(tag => IsExclusiveTag(tag) && string.Equals(TagCategory(tag), category, StringComparison.OrdinalIgnoreCase))
            .ToList())
        {
            _activeTagFilters.Remove(tag);
        }
    }

    private void TagContextRename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagChip chip })
        {
            EditTagAcrossLibrary(chip);
        }
    }

    private void TagContextDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagChip chip })
        {
            DeleteTagAcrossLibrary(chip);
        }
    }

    private void TagContextOpenManager_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagChip chip })
        {
            OpenTagManagerForTag(chip.Name);
        }
    }

    private void RenameTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagChip chip })
        {
            EditTagAcrossLibrary(chip);
        }
    }

    private void DeleteTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagChip chip })
        {
            DeleteTagAcrossLibrary(chip);
        }
    }

    private void TagManagerFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TagChip chip })
        {
            return;
        }
        ShowLibraryView("library");
        if (chip.IsExclusive)
        {
            RemoveActiveTagsInExclusiveGroup(chip.Category);
        }
        _activeTagFilters.Add(chip.Name);
        RefreshLibraryViews(tagManager: false, authors: false, activeTags: true);
        StatusText.Text = chip.UsageCount == 0
            ? $"已按 Tag 筛选：{chip.Name}。当前没有关联漫画，所以结果为空。"
            : $"已在书库按 Tag 查看：{chip.Name}";
    }

    private void OpenTagManagerForTag(string tagName)
    {
        ShowTagsView();
        if (TagManagerSearchBox is not null)
        {
            TagManagerSearchBox.Text = tagName;
        }
    }

    private void EditTagAcrossLibrary(TagChip chip)
    {
        var relatedBooks = Books
            .Where(book => book.TagItems.Any(item => string.Equals(item.Name, chip.Name, StringComparison.OrdinalIgnoreCase)))
            .Take(3)
            .ToList();
        var dialog = new TagEditDialog(chip, relatedBooks) { Owner = this };
        var result = dialog.ShowDialog();
        if (dialog.OpenMoreRequested)
        {
            TagManagerFilter_Click(new FrameworkElement { DataContext = chip }, new RoutedEventArgs());
            return;
        }
        if (result != true)
        {
            return;
        }

        var newName = dialog.TagName;
        var newCategory = dialog.TagCategory;
        var newIsExclusive = dialog.IsExclusive;
        if (string.IsNullOrWhiteSpace(newName))
        {
            StatusText.Text = "标签名不能为空。";
            return;
        }

        if (string.Equals(chip.Name, newName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(chip.Category, newCategory, StringComparison.OrdinalIgnoreCase)
            && chip.IsExclusive == newIsExclusive)
        {
            StatusText.Text = "标签没有变化。";
            return;
        }

        var renamedTag = !string.Equals(chip.Name, newName, StringComparison.OrdinalIgnoreCase);
        var existing = renamedTag && EnumerateKnownTags().Any(tag => string.Equals(tag, newName, StringComparison.OrdinalIgnoreCase));
        if (existing)
        {
            var mergeResult = System.Windows.MessageBox.Show(
                $"标签“{newName}”已经存在，继续后会把“{chip.Name}”合并到它名下。是否继续？",
                "合并标签",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (mergeResult != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var affectedBooks = new List<(MangaBook Book, string Tags)>();
        if (renamedTag || newIsExclusive)
        {
            foreach (var book in Books)
            {
                var tags = book.TagItems.Select(item => item.Name).ToList();
                if (!tags.Any(tag => string.Equals(tag, chip.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var normalized = tags
                    .Select(tag => string.Equals(tag, chip.Name, StringComparison.OrdinalIgnoreCase) ? newName : tag)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (newIsExclusive)
                {
                    normalized = normalized
                        .Where(tag => string.Equals(tag, newName, StringComparison.OrdinalIgnoreCase)
                            || !IsExclusiveTag(tag)
                            || !string.Equals(TagCategory(tag), newCategory, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
                affectedBooks.Add((book, TagService.FormatTags(normalized)));
            }
        }

        _database.SaveBookTagsBatch(
            affectedBooks.Select(item => (item.Book.Id, item.Tags)).ToList(),
            renamedTag ? "before-tag-rename" : "before-tag-regroup");

        if (renamedTag && _managedTags.Remove(chip.Name))
        {
            _managedTagCategories.Remove(chip.Name);
            _managedTagIsExclusive.Remove(chip.Name);
            _managedTagUpdatedAt.Remove(chip.Name);
            _database.RenameManagedTag(chip.Name, newName, newCategory, newIsExclusive);
        }
        else
        {
            if (renamedTag && chip.IsBuiltIn)
            {
                _suppressedTags.Add(chip.Name);
                _database.SuppressTag(chip.Name);
            }
            _database.SaveManagedTag(newName, newCategory, newIsExclusive);
        }
        _managedTags.Add(newName);
        _managedTagCategories[newName] = newCategory;
        _managedTagIsExclusive[newName] = newIsExclusive;
        _managedTagUpdatedAt[newName] = DateTimeOffset.Now.ToString("O");

        foreach (var (book, tags) in affectedBooks)
        {
            book.Tags = tags;
            book.NotifyAll();
        }

        if (_activeTagFilters.Remove(chip.Name))
        {
            if (newIsExclusive)
            {
                RemoveActiveTagsInExclusiveGroup(newCategory);
            }
            _activeTagFilters.Add(newName);
        }

        if (_currentBook is not null)
        {
            FillMetadataEditors(_currentBook);
        }

        RefreshLibraryViews(authors: false, sort: false, activeTags: true);
        StatusText.Text = renamedTag
            ? existing
                ? $"已将标签“{chip.Name}”合并到“{newName}”，影响 {affectedBooks.Count} 本漫画。"
                : $"已将标签“{chip.Name}”重命名为“{newName}”，影响 {affectedBooks.Count} 本漫画。"
            : $"已将标签“{chip.Name}”更新为“{newCategory} / {(newIsExclusive ? "互斥" : "不互斥")}”。";
    }

    private void DeleteTagAcrossLibrary(TagChip chip)
    {
        var result = System.Windows.MessageBox.Show(
            $"确定删除标签“{chip.Name}”吗？\n\n这会把它从所有漫画记录中移除，并从独立标签库中删除。",
            "删除标签",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var affectedBooks = new List<(MangaBook Book, string Tags)>();
        foreach (var book in Books)
        {
            var tags = book.TagItems.Select(item => item.Name).ToList();
            if (!tags.Any(tag => string.Equals(tag, chip.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var remaining = tags.Where(tag => !string.Equals(tag, chip.Name, StringComparison.OrdinalIgnoreCase));
            affectedBooks.Add((book, string.Join(", ", remaining)));
        }

        _database.SaveBookTagsBatch(
            affectedBooks.Select(item => (item.Book.Id, item.Tags)).ToList(),
            "before-tag-delete");
        _managedTags.Remove(chip.Name);
        _managedTagCategories.Remove(chip.Name);
        _managedTagIsExclusive.Remove(chip.Name);
        _managedTagUpdatedAt.Remove(chip.Name);
        _database.DeleteManagedTag(chip.Name);
        if (chip.IsBuiltIn)
        {
            _suppressedTags.Add(chip.Name);
            _database.SuppressTag(chip.Name);
        }

        foreach (var (book, tags) in affectedBooks)
        {
            book.Tags = tags;
            book.NotifyAll();
        }

        _activeTagFilters.Remove(chip.Name);
        if (_currentBook is not null)
        {
            FillMetadataEditors(_currentBook);
        }

        RefreshLibraryViews(authors: false, sort: false, activeTags: true);
        StatusText.Text = $"已删除标签“{chip.Name}”，影响 {affectedBooks.Count} 本漫画。";
    }
}
