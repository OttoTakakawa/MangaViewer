using MangaReader.Native.Models;
using MangaReader.Native.Services;
using Microsoft.VisualBasic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace MangaReader.Native;

public sealed record NextBookRecommendations(MangaBook? NextInView, MangaBook? SameAuthor, MangaBook? SimilarTags);

public partial class MainWindow : Window
{
    public static readonly DependencyProperty IsBatchSelectionModeProperty =
        DependencyProperty.Register(
            nameof(IsBatchSelectionMode),
            typeof(bool),
            typeof(MainWindow),
            new PropertyMetadata(false, OnBatchSelectionModeChanged));

    public static readonly DependencyProperty IsBatchSelectionUiVisibleProperty =
        DependencyProperty.Register(
            nameof(IsBatchSelectionUiVisible),
            typeof(bool),
            typeof(MainWindow),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsPrivacyModeProperty =
        DependencyProperty.Register(
            nameof(IsPrivacyMode),
            typeof(bool),
            typeof(MainWindow),
            new PropertyMetadata(false));

    private const double WheelScrollMultiplier = 1.45;
    private const double NormalLogPanelHeight = 160;
    private const double ExpandedLogPanelHeight = 420;
    private const int MaxLiveLogLines = 300;
    private const int MaxLiveLogLineLength = 900;
    private const int InitialDetailCatalogThumbnailLimit = 96;
    private const string PrivacyModeSettingKey = "app.privacy_mode";
    private const string CustomTagColorsSettingKey = "tag.custom_colors";
    private const string DeleteSourcePasswordKey = "app.permission_password";
    private const string DefaultDeleteSourcePassword = "0309";
    private const string CatalogDeleteSourceEnabledKey = "app.catalog_delete_source_enabled";
    private const string TagDragDataFormat = "MangaReader.TagName";
    private static readonly TimeSpan SearchDebounceInterval = TimeSpan.FromMilliseconds(160);
    private static readonly TagPreset[] DefaultTagPresets = TagCatalog.BuiltInPresets;

    private static SolidColorBrush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush GetThemeBrush(string key)
    {
        if (System.Windows.Application.Current.TryFindResource(key) is System.Windows.Media.SolidColorBrush brush)
            return brush;
        return System.Windows.Media.Brushes.Transparent;
    }

    private static readonly SolidColorBrush TransparentBrush = FrozenBrush("#00FFFFFF");

    private readonly AppStorage _storage = new();
    private readonly LibraryScanner _scanner = new();
    private readonly BatchImportAnalyzer _batchImportAnalyzer = new();
    private readonly LibraryDatabase _database;
    private readonly CoverCache _coverCache;
    private readonly CoverThumbnailPipeline _coverPipeline;
    private readonly UpdateService _updateService;
    private MangaBook? _currentBook;
    private MangaBook? _detailCatalogBook;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _importCancellation;
    private CancellationTokenSource? _detailCatalogLoadCancellation;
    private int _scanRunId;
    private int _detailCatalogPreviewRunId;
    private List<Key> _nextKeys = [Key.Right, Key.Space];
    private List<Key> _prevKeys = [Key.Left];
    private bool _isEditMode;
    private readonly HashSet<string> _activeTagFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _managedTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _managedAuthors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _managedTagCategories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _managedTagIsExclusive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _managedTagUpdatedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _managedTagColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _customTagColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<MangaBook>> _tagBooksByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _suppressedTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _visibleCoverReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _coverLoadCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _bookSearchDebounceTimer = new() { Interval = SearchDebounceInterval };
    private readonly DispatcherTimer _tagSearchDebounceTimer = new() { Interval = SearchDebounceInterval };
    private readonly DispatcherTimer _tagManagerSearchDebounceTimer = new() { Interval = SearchDebounceInterval };
    private readonly DispatcherTimer _authorSearchDebounceTimer = new() { Interval = SearchDebounceInterval };
    private readonly DispatcherTimer _statusLogTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private DispatcherTimer? _toastTimer;
    private readonly Queue<string> _liveLogLines = new();
    private List<MangaBook> _allBooks = [];
    private CancellationTokenSource _filterCts = new();
    private bool _refreshShelfOverviewAfterBookFilter;
    private bool _ensureLibraryViewAfterBookFilter;
    private bool _isRefreshingAuthorFilters;
    private bool _tagGroupFilterOptionsDirty = true;
    private bool _libraryChromeCollapsed;
    private bool _isLogPanelVisible;
    private bool _isLogPanelExpanded;
    private bool _isCheckingForUpdates;
    private string _currentNavigationKey = "home";
    private string _cachedSearchQuery = "";
    private string _cachedStatusFilter = "";
    private string _cachedAuthorFilter = "";
    private string _lastStatusLogText = "";
    private bool _cachedFavoriteOnly;
    private bool _cachedShowHidden;
    private bool _cachedOnlyHidden;
    private bool _sortDescending;
    private System.Windows.Point? _tagDragStartPoint;
    private string[] _cachedActiveTagFilters = [];

    public RangeObservableCollection<MangaBook> Books { get; } = [];
    public RangeObservableCollection<TagChip> VisibleTags { get; } = [];
    public RangeObservableCollection<TagChip> ActiveTagFilters { get; } = [];
    public RangeObservableCollection<TagChip> TagManagerItems { get; } = [];
    public RangeObservableCollection<TagChip> EditSelectedTagItems { get; } = [];
    public RangeObservableCollection<TagChip> EditTagOptions { get; } = [];
    public RangeObservableCollection<AuthorItem> EditAuthorOptions { get; } = [];
    public RangeObservableCollection<AuthorItem> AuthorManagerItems { get; } = [];
    public RangeObservableCollection<string> AuthorFilters { get; } = [];

    public bool IsBatchSelectionMode
    {
        get => (bool)GetValue(IsBatchSelectionModeProperty);
        set => SetValue(IsBatchSelectionModeProperty, value);
    }

    public bool IsBatchSelectionUiVisible
    {
        get => (bool)GetValue(IsBatchSelectionUiVisibleProperty);
        set => SetValue(IsBatchSelectionUiVisibleProperty, value);
    }

    public bool IsPrivacyMode
    {
        get => (bool)GetValue(IsPrivacyModeProperty);
        set => SetValue(IsPrivacyModeProperty, value);
    }

    public RangeObservableCollection<MangaBook> ContinueReadingBooks { get; } = [];
    public RangeObservableCollection<MangaBook> RecentReadingBooks { get; } = [];
    public RangeObservableCollection<MangaBook> FavoriteShowcaseBooks { get; } = [];
    public RangeObservableCollection<MangaBook> RecentlyAddedBooks { get; } = [];
    public RangeObservableCollection<PageCatalogItem> DetailPageCatalogItems { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _storage.EnsureCreated();
        _database = new LibraryDatabase(_storage);
        _updateService = new UpdateService(_storage);
        _coverCache = new CoverCache(_storage);
        _coverPipeline = new CoverThumbnailPipeline(_coverCache);
        SetDetailVisible(false);
        ShowHomeView();
        UpdateLogPanelVisibility();
        AppLogger.LineWritten += AppLogger_LineWritten;

        ConfigureSearchDebounceTimers();
        _statusLogTimer.Tick += StatusLogTimer_Tick;
        _statusLogTimer.Start();
        VersionText.Text = $"v{UpdateService.CurrentVersionText}";
        Loaded += MainWindow_Loaded;
        Closing += (_, _) =>
        {
            AppLogger.LineWritten -= AppLogger_LineWritten;
            _statusLogTimer.Stop();
            StopSearchDebounceTimers();
            _filterCts.Cancel();
            _filterCts.Dispose();
            SaveCurrentProgress();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Run(() => _database.Initialize());

            // 应用保存的主题
            var savedTheme = _database.LoadSetting("app.theme", "Warm");
            if (savedTheme is "Light" or "Dark")
                App.ApplyTheme(savedTheme);

            var managedTagsTask = Task.Run(() => _database.LoadManagedTags());
            var suppressedTagsTask = Task.Run(() => _database.LoadSuppressedTags());
            var customTagColorsTask = Task.Run(() => _database.LoadSetting(CustomTagColorsSettingKey));
            var managedAuthorsTask = Task.Run(() => _database.LoadManagedAuthors());
            var shortcutsTask = Task.Run(() => _database.LoadShortcuts());
            var privacyModeTask = Task.Run(() => _database.LoadSetting(PrivacyModeSettingKey));

            await Task.WhenAll(managedTagsTask, suppressedTagsTask, customTagColorsTask, managedAuthorsTask, shortcutsTask, privacyModeTask);

            ApplyManagedTags(managedTagsTask.Result, suppressedTagsTask.Result);
            ApplyCustomTagColors(customTagColorsTask.Result);
            ApplyManagedAuthors(managedAuthorsTask.Result);
            ApplyShortcuts(shortcutsTask.Result);
            IsPrivacyMode = string.Equals(privacyModeTask.Result, "1", StringComparison.Ordinal);

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
            StatusText.Text = $"多文件夹导入处理完成：{folders.Count} 个文件夹，当前书库 {_allBooks.Count} 本漫画。";
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
            _importCancellation?.Cancel();
            _importCancellation = new CancellationTokenSource();
            await ImportAuthorBatchAsync(folderPath, dialog.AuthorName, dialog.Candidates.ToList(), _importCancellation.Token);
        }
    }

    private async Task ImportAuthorBatchAsync(string rootPath, string authorName, IReadOnlyList<BatchImportCandidate> candidates, CancellationToken token)
    {
        StatusText.Text = $"正在批量导入：{authorName}...";
        ShowImportProgress(authorName, 0, candidates.Count, "准备导入...");
        _database.SaveLibraryRoot(rootPath);
        var savedBooks = await Task.Run(() => _database.LoadBooksByPath(), token);
        var booksByPath = _allBooks.ToDictionary(book => book.FolderPath, StringComparer.OrdinalIgnoreCase);
        var importedCount = 0;
        var failures = new List<string>();
        var booksToSave = new List<(MangaBook Book, bool IsAlreadyVisible)>();
        var processedCount = 0;

        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
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
                book.TotalBytes = ImageLoader.SumFileBytes(pages);
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
        await Task.Run(() => _database.UpsertBooksBatch(booksToSave.Select(item => item.Book).ToList()), token);
        var newBooks = new List<MangaBook>();
        foreach (var (book, isAlreadyVisible) in booksToSave)
        {
            token.ThrowIfCancellationRequested();
            book.NotifyAll();
            if (!isAlreadyVisible)
            {
                newBooks.Add(book);
                booksByPath[book.FolderPath] = book;
            }
        }
        _allBooks.AddRange(newBooks);

        HideImportProgress();
        RefreshLibraryViews(sort: true, ensureLibraryView: true);
        RefreshHomeShelves();
        StatusText.Text = failures.Count == 0
            ? $"批量导入完成：{authorName} · 新增/更新 {importedCount} 本，当前书库 {_allBooks.Count} 本漫画。"
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

    private void ShowLibraryLoadProgress(string title, int completed, int total, int booksFound, string detail)
    {
        if (LibraryLoadProgressPanel is null)
        {
            return;
        }

        var safeTotal = Math.Max(1, total);
        var safeCompleted = Math.Clamp(completed, 0, safeTotal);
        if (LibraryLoadProgressPanel.Visibility != Visibility.Visible)
        {
            MotionService.ShowWithFade(LibraryLoadProgressPanel);
        }

        LibraryLoadProgressTitle.Text = title;
        LibraryLoadProgressBar.Minimum = 0;
        LibraryLoadProgressBar.Maximum = safeTotal;
        LibraryLoadProgressBar.Value = safeCompleted;
        LibraryLoadProgressText.Text = $"{safeCompleted} / {safeTotal} · 已发现 {booksFound} 本 · {detail}";
        StatusText.Text = LibraryLoadProgressText.Text;
    }

    private void HideLibraryLoadProgress()
    {
        if (LibraryLoadProgressPanel is not null)
        {
            MotionService.HideWithFade(LibraryLoadProgressPanel);
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

        if (!_coverLoadCancellations.TryGetValue(key, out var loadCancellation))
        {
            loadCancellation = new CancellationTokenSource();
            _coverLoadCancellations[key] = loadCancellation;
        }

        try
        {
            var image = await _coverPipeline.LoadAsync(book, loadCancellation.Token);
            if (_visibleCoverReferences.ContainsKey(key) && image is not null)
            {
                book.CoverImage = image;
            }
        }
        catch (OperationCanceledException)
        {
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
            if (_coverLoadCancellations.Remove(key, out var loadCancellation))
            {
                loadCancellation.Cancel();
                loadCancellation.Dispose();
            }
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
        var scanCancellation = new CancellationTokenSource();
        _scanCancellation = scanCancellation;
        var scanRunId = ++_scanRunId;
        var token = scanCancellation.Token;
        bool IsActiveScan() => ReferenceEquals(_scanCancellation, scanCancellation) && scanRunId == _scanRunId && !token.IsCancellationRequested;

        StatusText.Text = "正在扫描漫画库...";
        foreach (var cancellation in _coverLoadCancellations.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        _coverLoadCancellations.Clear();
        _visibleCoverReferences.Clear();
        _filterCts.Cancel();
        Books.Clear();
        _allBooks = [];
        _currentBook = null;
        SetDetailVisible(false);
        ShowLibraryView("library");
        ShowLibraryLoadProgress("正在加载漫画库", 0, 1, 0, "准备扫描...");

        try
        {
            var rootIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < roots.Count; index++)
            {
                rootIndexes.TryAdd(roots[index], index);
            }

            var lastProgressUpdate = DateTimeOffset.MinValue;
            var scanProgress = new Progress<LibraryScanProgress>(update =>
            {
                if (!IsActiveScan())
                {
                    return;
                }

                var now = DateTimeOffset.Now;
                var isComplete = update.CompletedFolders >= update.TotalFolders;
                if (!isComplete && now - lastProgressUpdate < TimeSpan.FromMilliseconds(90))
                {
                    return;
                }

                lastProgressUpdate = now;
                rootIndexes.TryGetValue(update.RootPath, out var rootIndex);
                var title = roots.Count > 1
                    ? $"正在加载漫画库（路径 {rootIndex + 1}/{roots.Count}）"
                    : "正在加载漫画库";
                var folderName = Path.GetFileName(update.CurrentFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(folderName))
                {
                    folderName = update.CurrentFolder;
                }

                ShowLibraryLoadProgress(
                    title,
                    update.CompletedFolders,
                    update.TotalFolders,
                    update.BooksFound,
                    $"正在扫描：{folderName}");
            });

            var scanned = await Task.Run(() =>
            {
                var savedBooks = _database.LoadBooksByPath();
                var all = new List<MangaBook>();
                foreach (var root in roots)
                {
                    token.ThrowIfCancellationRequested();
                    all.AddRange(_scanner.Scan(root, savedBooks, scanProgress, token));
                }

                var scannedPaths = all.Select(book => book.FolderPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missing = savedBooks.Values
                    .Where(book => !scannedPaths.Contains(book.FolderPath) && !Directory.Exists(book.FolderPath))
                    .ToList();
                foreach (var book in missing)
                {
                    token.ThrowIfCancellationRequested();
                    book.IsMissing = true;
                    book.Pages.Clear();
                    book.NotifyAll();
                }
                return (Scanned: all, MissingBooks: missing);
            }, token);

            token.ThrowIfCancellationRequested();
            if (!IsActiveScan())
            {
                return;
            }

            var missingBooks = scanned.MissingBooks;
            var visibleBooks = scanned.Scanned.Concat(missingBooks).ToList();
            ShowLibraryLoadProgress("正在保存书库索引", 1, 1, visibleBooks.Count, "正在写入数据库...");
            await Task.Run(() => _database.UpsertBooksBatch(visibleBooks), token);
            token.ThrowIfCancellationRequested();
            if (!IsActiveScan())
            {
                return;
            }

            _allBooks = visibleBooks;

            RefreshLibraryViews(sort: true, ensureLibraryView: true);
            RefreshHomeShelves();
            _coverCache.SweepStaleCovers(_allBooks.Select(b => b.Id));
            StatusText.Text = $"扫描完成：{_allBooks.Count} 本漫画。";
        }
        catch (OperationCanceledException)
        {
            if (IsActiveScan())
            {
                StatusText.Text = "扫描已取消。";
            }
        }
        catch (Exception ex)
        {
            if (!IsActiveScan())
            {
                return;
            }

            AppLogger.Error("library-scan", ex, "Library scan failed.");
            StatusText.Text = $"扫描失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, scanCancellation))
            {
                _scanCancellation = null;
                HideLibraryLoadProgress();
            }

            scanCancellation.Dispose();
        }
    }

    private void BooksList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveCurrentProgress();
        HideDetailCatalog();
        _currentBook = BooksList.SelectedItem as MangaBook;
        if (_currentBook is null)
        {
            SetDetailVisible(false);
            return;
        }

        SetDetailVisible(true);
        FillMetadataEditors(_currentBook);
        SetEditMode(false);
    }

    private void BooksList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsTagChipEventSource(e.OriginalSource))
        {
            e.Handled = true;
            return;
        }

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

    private MangaBook? _batchAnchorBook;

    private void BookCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement element)
        {
            MotionService.PressBounce(element);
        }

        if (!IsBatchSelectionMode) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not MangaBook book) return;
        if (e.OriginalSource is DependencyObject dep
            && FindAncestor<System.Windows.Controls.CheckBox>(dep) is not null)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var currentIndex = Books.IndexOf(book);
        if (currentIndex < 0) return;

        if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift && _batchAnchorBook is not null)
        {
            var anchorIndex = Books.IndexOf(_batchAnchorBook);
            if (anchorIndex >= 0)
            {
                var lo = Math.Min(anchorIndex, currentIndex);
                var hi = Math.Max(anchorIndex, currentIndex);
                for (int i = lo; i <= hi; i++) Books[i].IsSelectedForBatch = true;
            }
        }
        else if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            book.IsSelectedForBatch = false;
            _batchAnchorBook = book;
        }
        else
        {
            book.IsSelectedForBatch = !book.IsSelectedForBatch;
            _batchAnchorBook = book;
        }

        UpdateBatchSelectionState();
        e.Handled = true;
    }

    private void BookCard_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement element)
        {
            MotionService.ScaleTo(element, element.IsMouseOver ? 1.025 : 1.0, MotionService.Fast);
        }
    }

    private void BookCard_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_database.LoadSetting("app.waterfall_right_click", "0") != "1") return;
        if (sender is not FrameworkElement fe || fe.DataContext is not MangaBook book) return;

        // 阻止 ListBox 选中和详情页导航
        e.Handled = true;

        var menu = new System.Windows.Controls.ContextMenu();

        // 切换卡片样式
        var styleItem = new System.Windows.Controls.MenuItem { Header = $"切换卡片样式（当前：{book.StyleName}）" };
        styleItem.Click += (_, _) =>
        {
            book.CycleBookStyle();
            _ = Task.Run(() => _database.SaveMetadata(book));
            book.NotifyAll();
            ScheduleBookViewRefresh(refreshShelfOverview: false);
            StatusText.Text = $"已切换《{book.Title}》的卡片样式：{book.StyleName}。";
        };
        menu.Items.Add(styleItem);

        // 隐藏/取消隐藏
        var hideItem = new System.Windows.Controls.MenuItem { Header = book.IsHidden ? "取消隐藏" : "隐藏作品" };
        hideItem.Click += (_, _) =>
        {
            book.IsHidden = !book.IsHidden;
            _ = Task.Run(() => _database.SaveMetadata(book));
            book.NotifyAll();
            StatusText.Text = book.IsHidden ? $"已隐藏《{book.Title}》。" : $"已取消隐藏《{book.Title}》。";
        };
        menu.Items.Add(hideItem);

        // 隐私封面
        var privacyItem = new System.Windows.Controls.MenuItem { Header = book.IsPrivacyCover ? "取消隐私封面" : "保持隐私封面" };
        privacyItem.Click += (_, _) =>
        {
            book.IsPrivacyCover = !book.IsPrivacyCover;
            _ = Task.Run(() => _database.SaveMetadata(book));
            book.NotifyAll();
            StatusText.Text = book.IsPrivacyCover ? $"已设置《{book.Title}》为隐私封面。" : $"已取消《{book.Title}》的隐私封面。";
        };
        menu.Items.Add(privacyItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // 打开源文件夹
        var openItem = new System.Windows.Controls.MenuItem { Header = "打开源文件夹" };
        openItem.Click += (_, _) =>
        {
            if (Directory.Exists(book.FolderPath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = book.FolderPath,
                    UseShellExecute = true
                });
        };
        menu.Items.Add(openItem);

        menu.PlacementTarget = fe;
        menu.IsOpen = true;
    }

    private bool _isRubberBanding;
    private System.Windows.Point _rubberStart;
    private bool _rubberSubtract;

    private void BooksList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsBatchSelectionMode) return;
        if (e.OriginalSource is not DependencyObject hit) return;
        if (FindAncestor<System.Windows.Controls.ListBoxItem>(hit) is not null) return;
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(hit) is not null) return;
        if (FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(hit) is not null) return;

        _isRubberBanding = true;
        _rubberStart = e.GetPosition(BooksList);
        _rubberSubtract = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        Mouse.Capture(BooksList);

        if (!_rubberSubtract && (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
        {
            foreach (var b in Books) b.IsSelectedForBatch = false;
        }

        System.Windows.Controls.Canvas.SetLeft(RubberBandRect, _rubberStart.X);
        System.Windows.Controls.Canvas.SetTop(RubberBandRect, _rubberStart.Y);
        RubberBandRect.Width = 0;
        RubberBandRect.Height = 0;
        RubberBandRect.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void BooksList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isRubberBanding) return;
        var current = e.GetPosition(BooksList);
        var x = Math.Min(_rubberStart.X, current.X);
        var y = Math.Min(_rubberStart.Y, current.Y);
        var w = Math.Abs(current.X - _rubberStart.X);
        var h = Math.Abs(current.Y - _rubberStart.Y);
        System.Windows.Controls.Canvas.SetLeft(RubberBandRect, x);
        System.Windows.Controls.Canvas.SetTop(RubberBandRect, y);
        RubberBandRect.Width = w;
        RubberBandRect.Height = h;
    }

    private void BooksList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isRubberBanding) return;
        _isRubberBanding = false;
        Mouse.Capture(null);

        var rect = new System.Windows.Rect(
            System.Windows.Controls.Canvas.GetLeft(RubberBandRect),
            System.Windows.Controls.Canvas.GetTop(RubberBandRect),
            RubberBandRect.Width, RubberBandRect.Height);
        RubberBandRect.Visibility = Visibility.Collapsed;

        if (rect.Width < 4 && rect.Height < 4) return;

        var generator = BooksList.ItemContainerGenerator;
        foreach (var item in Books)
        {
            if (generator.ContainerFromItem(item) is not System.Windows.Controls.ListBoxItem container) continue;
            if (!container.IsVisible || container.ActualWidth <= 0) continue;
            try
            {
                var pos = container.TransformToAncestor(BooksList).Transform(new System.Windows.Point(0, 0));
                var itemRect = new System.Windows.Rect(pos.X, pos.Y, container.ActualWidth, container.ActualHeight);
                if (rect.IntersectsWith(itemRect))
                {
                    item.IsSelectedForBatch = !_rubberSubtract;
                }
            }
            catch
            {
                // visual tree transient, skip
            }
        }
        UpdateBatchSelectionState();
        e.Handled = true;
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

    private void DetailDismissOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        BooksList.SelectedItem = null;
        _currentBook = null;
        SetDetailVisible(false);
        e.Handled = true;
    }

    private async void SaveMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null) return;
        if (!_isEditMode)
        {
            StatusText.Text = "当前是只读模式，请先点击编辑。";
            return;
        }

        var title = TitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            StatusText.Text = "书名不能为空。";
            return;
        }

        _currentBook.Title = title;
        var nextAuthor = AuthorBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(nextAuthor)
            && !EnumerateKnownAuthors().Any(author => string.Equals(author, nextAuthor, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = $"作者只能从作者管理中选择：{nextAuthor}";
            RefreshEditAuthorOptions(nextAuthor);
            return;
        }

        _currentBook.Author = nextAuthor;
        _currentBook.ForeignName = ForeignNameBox.Text.Trim();
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
        RefreshEditTagEditor(_currentBook.Tags);

        var book = _currentBook;
        await Task.Run(() => _database.SaveMetadata(book));
        if (!ReferenceEquals(_currentBook, book))
        {
            return;
        }

        book.NotifyAll();
        RefreshLibraryViews(tagManager: false, sort: true);
        RefreshHomeShelves();
        FillMetadataEditors(book);
        SetEditMode(false);
        StatusText.Text = "书籍信息已保存。";
    }

    private void ImportedToday_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEditMode) return;
        ImportedAtBox.Text = DateTime.Today.ToString("yyyy-MM-dd");
    }

    private async void SetCover_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null) return;
        if (!_isEditMode)
        {
            StatusText.Text = "当前是只读模式，请先点击编辑。";
            return;
        }
        if (!int.TryParse(CoverPageBox.Text.Trim(), out var coverPage))
        {
            StatusText.Text = "封面页必须是数字。";
            return;
        }

        _currentBook.CoverPageIndex = Math.Clamp(coverPage - 1, 0, Math.Max(_currentBook.PageCount - 1, 0));
        var book = _currentBook;
        await Task.Run(() => _database.SaveMetadata(book));
        await ReloadCoverAsync(book);
        if (!ReferenceEquals(_currentBook, book))
        {
            return;
        }

        book.NotifyAll();
        StatusText.Text = $"封面已设置为第 {book.CoverPageIndex + 1} 页。";
    }

    private async void CycleBookStyle_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null) return;

        _currentBook.CycleBookStyle();
        var book = _currentBook;
        await Task.Run(() => _database.SaveMetadata(book));
        _currentBook.NotifyAll();
        FillMetadataEditors(book);
        ScheduleBookViewRefresh(refreshShelfOverview: false);
        StatusText.Text = $"已切换《{_currentBook.Title}》的卡片样式：{_currentBook.StyleName}。";
    }

    private async void IncreaseReadCount_Click(object sender, RoutedEventArgs e)
    {
        await ChangeReadCount(1);
    }

    private async void DecreaseReadCount_Click(object sender, RoutedEventArgs e)
    {
        await ChangeReadCount(-1);
    }

    private async void ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null)
        {
            return;
        }

        _currentBook.IsFavorite = !_currentBook.IsFavorite;
        var book = _currentBook;
        await Task.Run(() => _database.SaveMetadata(book));
        _currentBook.NotifyAll();
        FillMetadataEditors(_currentBook);
        RefreshBookFilter();
        RefreshHomeShelves();
        StatusText.Text = _currentBook.IsFavorite
            ? $"已收藏《{_currentBook.Title}》。"
            : $"已取消收藏《{_currentBook.Title}》。";
    }

    private void ReturnToLibrary_Click(object sender, RoutedEventArgs e)
    {
        BooksList.SelectedItem = null;
        _currentBook = null;
        SetEditMode(false);
        SetDetailVisible(false);
        StatusText.Text = "已返回书库。";
    }

    private async Task ChangeReadCount(int delta)
    {
        if (_currentBook is null)
        {
            return;
        }

        _currentBook.ReadCount = Math.Max(0, _currentBook.ReadCount + delta);
        NormalizeReadingStatusForReadCount(_currentBook);
        var book = _currentBook;
        await Task.Run(() => _database.SaveReadCount(book));
        _currentBook.NotifyAll();
        FillMetadataEditors(_currentBook);
        ApplyBookSort(refresh: false);
        RefreshBookFilter();
        RefreshHomeShelves();
        StatusText.Text = $"《{_currentBook.Title}》已标记为读过 {_currentBook.ReadCount} 次。";
    }

    private async Task ReloadCoverAsync(MangaBook book)
    {
        book.CoverImage = null;
        book.CoverImage = await Task.Run(() => _coverCache.LoadOrCreate(book));
    }

    private static void NormalizeReadingStatusForReadCount(MangaBook book)
    {
        if (book.ReadCount > 0)
        {
            book.ReadingStatus = "reading";
        }
    }

    private async void HideBook_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null) return;

        var book = _currentBook;
        book.IsHidden = !book.IsHidden;
        await Task.Run(() => _database.SetHidden(book, book.IsHidden));
        book.NotifyAll();
        RefreshLibraryViews(tagManager: false, sort: false);
        RefreshHomeShelves();

        if (book.IsHidden && OnlyHiddenBox?.IsChecked != true)
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

    private async void ToggleBookPrivacyCover_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null) return;

        var book = _currentBook;
        book.IsPrivacyCover = !book.IsPrivacyCover;
        await Task.Run(() => _database.SetPrivacyCover(book, book.IsPrivacyCover));
        book.NotifyAll();
        FillMetadataEditors(book);
        RefreshHomeShelves();
        StatusText.Text = book.IsPrivacyCover
            ? $"《{book.Title}》已保持隐私封面。"
            : $"《{book.Title}》已取消隐私封面。";
    }

    private async void DeleteBook_Click(object sender, RoutedEventArgs e)
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

        try
        {
            await Task.Run(() => _database.DeleteBook(book));
            _allBooks.Remove(book);
            _currentBook = null;
            BooksList.SelectedItem = null;
            SetDetailVisible(false);
            RefreshLibraryViews(tagManager: false, authors: true);
            RefreshHomeShelves();
            StatusText.Text = $"已删除《{book.Title}》的库记录，源文件未删除。";
            AppLogger.Info("delete-book", $"Deleted library record: {book.Title}, folder={book.FolderPath}");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"删除库记录失败：{ex.Message}";
            AppLogger.Error("delete-book", ex, $"Failed to delete library record: {book.Title}");
        }
    }

    private async void DeleteSourceFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null) return;
        var book = _currentBook;

        if (!IsSafeFolderPathForDeletion(book.FolderPath, _storage.Root))
        {
            StatusText.Text = "源文件夹路径不合法或为空，已取消删除。";
            AppLogger.Warn("delete-source", $"Rejected unsafe folder path: {book.FolderPath}");
            return;
        }

        var first = System.Windows.MessageBox.Show(
            $"确定永久删除《{book.Title}》的源文件夹及其所有内容吗？\n\n" +
            $"路径：{book.FolderPath}\n\n" +
            "此操作不可恢复！",
            "删除源文件",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (first != MessageBoxResult.Yes) return;

        var input = Interaction.InputBox(
            "请输入密码以确认删除源文件：",
            "密码确认",
            "");

        var password = _database.LoadSetting(DeleteSourcePasswordKey, DefaultDeleteSourcePassword);

        if (string.IsNullOrEmpty(input) || input != password)
        {
            StatusText.Text = "密码不正确，已取消。";
            return;
        }

        await DeleteSourceFilesAsync();
    }

    private async Task DeleteSourceFilesAsync()
    {
        var book = _currentBook!;
        var folderPath = book.FolderPath;
        var title = book.Title;

        try
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(folderPath))
                {
                    Directory.Delete(folderPath, recursive: true);
                }

                _database.DeleteBook(book);
            });

            _allBooks.Remove(book);
            _currentBook = null;
            BooksList.SelectedItem = null;
            SetDetailVisible(false);
            RefreshLibraryViews(tagManager: false, authors: true);
            RefreshHomeShelves();
            StatusText.Text = $"已删除《{title}》的源文件夹及库记录。";
            AppLogger.Info("delete-source", $"Deleted source files and record: {title}, folder={folderPath}");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"删除失败：{ex.Message}";
            AppLogger.Error("delete-source", ex, $"Failed to delete source files: {title}, folder={folderPath}");
        }
    }

    private static bool IsSafeFolderPathForDeletion(string folderPath, string appDataRoot)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return false;

        if (!Path.IsPathFullyQualified(folderPath))
            return false;

        string normalized;
        try
        {
            normalized = Path.GetFullPath(folderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return false;
        }

        var root = Path.GetPathRoot(normalized);
        if (string.Equals(root, normalized, StringComparison.OrdinalIgnoreCase))
            return false;

        var systemDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        }
        .Where(d => !string.IsNullOrEmpty(d))
        .Select(d => Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar))
        .ToList();

        foreach (var sysDir in systemDirs)
        {
            if (normalized.Equals(sysDir, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(sysDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        try
        {
            var appRoot = Path.GetFullPath(appDataRoot)
                .TrimEnd(Path.DirectorySeparatorChar);
            if (normalized.Equals(appRoot, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(appRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch
        {
        }

        return true;
    }

    private void ToggleEditMode_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null)
        {
            return;
        }

        SetEditMode(!_isEditMode);
    }

    private void OpenMoreActionsMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu is not null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
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

    private void DetailOpenCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null)
        {
            StatusText.Text = "请先选择一本漫画。";
            return;
        }

        if (_currentBook.Pages.Count == 0)
        {
            StatusText.Text = "这本漫画没有可浏览页面。";
            return;
        }

        EnsureDetailCatalogItems(_currentBook);
        _detailCatalogBook = _currentBook;
        DetailCatalogTitleText.Text = $"目录预览 · {_currentBook.Title}";
        DetailCatalogPreviewImage.Source = null;
        DetailCatalogPreviewText.Text = "选择一页预览";
        MotionService.ShowWithFade(DetailCatalogOverlay);
        StartDetailCatalogThumbnailLoad();
    }

    private void CloseDetailCatalog_Click(object sender, RoutedEventArgs e)
    {
        HideDetailCatalog();
    }

    private void HideDetailCatalog()
    {
        _detailCatalogLoadCancellation?.Cancel();
        _detailCatalogLoadCancellation?.Dispose();
        _detailCatalogLoadCancellation = null;
        _detailCatalogBook = null;
        _detailCatalogPreviewRunId++;
        DetailCatalogPreviewImage.Source = null;
        if (DetailCatalogOverlay is not null)
        {
            MotionService.HideWithFade(DetailCatalogOverlay);
        }
    }

    private void DetailCatalogOverlay_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = false;
    }

    private void DetailCatalogOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HideDetailCatalog();
        e.Handled = true;
    }

    private void DetailCatalogContent_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private static readonly string[] MarkColorGroupA =
    [
        "#EF4444", "#F97316", "#EAB308", "#22C55E",
        "#14B8A6", "#3B82F6", "#6366F1", "#A855F7",
        "#EC4899", "#F43F5E", "#84CC16", "#06B6D4"
    ];
    private static readonly string[] MarkColorGroupB =
    [
        "#991B1B", "#9A3412", "#854D0E", "#166534",
        "#115E59", "#1E3A8A", "#3730A3", "#581C87",
        "#831843", "#9F1239", "#365314", "#164E63"
    ];
    private static readonly string[] MarkColorGroupC =
    [
        "#E879A0", "#F0946C", "#E6B84A", "#7FB884",
        "#5BB5A2", "#6A9FD8", "#8B8EC9", "#B08DC2",
        "#D98CB8", "#E07A6A", "#A3C565", "#54B8C4"
    ];
    private static readonly string[] MarkColorGroupD =
    [
        "#C0392B", "#D35400", "#D4AC0D", "#1E8449",
        "#1A5276", "#2E4085", "#6C3483", "#A93276",
        "#C0395A", "#D4553A", "#27AE60", "#16A085"
    ];

    private string[] GetActiveMarkColors()
    {
        var group = _database.LoadSetting("mark.color_group", "A");
        return group switch
        {
            "B" => MarkColorGroupB,
            "C" => MarkColorGroupC,
            "D" => MarkColorGroupD,
            _ => MarkColorGroupA
        };
    }

    private void AssignBookmarkColors(IList<PageCatalogItem> items, HashSet<int> bookmarks)
    {
        var colors = GetActiveMarkColors();
        var sorted = bookmarks.OrderBy(x => x).ToList();
        foreach (var item in items)
        {
            if (item.IsBookmarked)
            {
                var colorIndex = sorted.IndexOf(item.PageIndex);
                item.BookmarkColor = colors[colorIndex >= 0 ? colorIndex % colors.Length : 0];
            }
        }
    }

    private void EnsureDetailCatalogItems(MangaBook book)
    {
        var bookmarks = _database.LoadBookmarks(book.Id);
        if (DetailPageCatalogItems.Count == book.Pages.Count
            && DetailPageCatalogItems.Select(item => item.Path).SequenceEqual(book.Pages, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var item in DetailPageCatalogItems)
            {
                item.IsBookmarked = bookmarks.Contains(item.PageIndex);
            }
            AssignBookmarkColors(DetailPageCatalogItems, bookmarks);
            return;
        }

        var items = book.Pages
            .Select((path, index) => new PageCatalogItem(index, path)
            {
                IsBookmarked = bookmarks.Contains(index)
            })
            .ToList();
        AssignBookmarkColors(items, bookmarks);
        DetailPageCatalogItems.ReplaceRange(items);
    }

    private void StartDetailCatalogThumbnailLoad()
    {
        _detailCatalogLoadCancellation?.Cancel();
        _detailCatalogLoadCancellation?.Dispose();
        _detailCatalogLoadCancellation = new CancellationTokenSource();
        var token = _detailCatalogLoadCancellation.Token;
        var items = DetailPageCatalogItems.Take(InitialDetailCatalogThumbnailLimit).ToList();

        _ = Task.Run(async () =>
        {
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                if (item.Thumbnail is not null)
                {
                    continue;
                }

                try
                {
                    var thumbnail = ImageLoader.LoadBitmap(item.Path, 180);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested && DetailCatalogOverlay.Visibility == Visibility.Visible)
                        {
                            item.Thumbnail = thumbnail;
                        }
                    }, DispatcherPriority.Background);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
                {
                    AppLogger.Warn("detail-catalog", $"Thumbnail failed: page={item.PageIndex + 1}, path={item.Path}, error={ex.Message}");
                }
            }
        }, token);
    }

    private async void DetailCatalogItem_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PageCatalogItem item || item.Thumbnail is not null)
        {
            return;
        }

        var token = _detailCatalogLoadCancellation?.Token ?? CancellationToken.None;
        try
        {
            var thumbnail = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return ImageLoader.LoadBitmap(item.Path, 180);
            }, token);

            if (!token.IsCancellationRequested && DetailCatalogOverlay.Visibility == Visibility.Visible)
            {
                item.Thumbnail = thumbnail;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            AppLogger.Warn("detail-catalog", $"Lazy thumbnail failed: page={item.PageIndex + 1}, path={item.Path}, error={ex.Message}");
        }
    }

    private async void DetailCatalogItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PageCatalogItem item)
        {
            return;
        }

        var previewRunId = ++_detailCatalogPreviewRunId;
        var token = _detailCatalogLoadCancellation?.Token ?? CancellationToken.None;
        try
        {
            DetailCatalogPreviewText.Text = $"正在预览 {item.PageText}...";
            var preview = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return ImageLoader.LoadBitmap(item.Path, 900, ignoreColorProfile: false);
            }, token);

            if (previewRunId != _detailCatalogPreviewRunId || token.IsCancellationRequested || DetailCatalogOverlay.Visibility != Visibility.Visible)
            {
                return;
            }

            DetailCatalogPreviewImage.Source = preview;
            DetailCatalogPreviewText.Text = $"{item.PageText} · 预览";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            if (previewRunId != _detailCatalogPreviewRunId || token.IsCancellationRequested || DetailCatalogOverlay.Visibility != Visibility.Visible)
            {
                return;
            }

            DetailCatalogPreviewText.Text = $"{item.PageText} 预览失败";
            AppLogger.Warn("detail-catalog", $"Preview failed: page={item.PageIndex + 1}, path={item.Path}, error={ex.Message}");
        }

        e.Handled = true;
    }

    private async void SetDetailCatalogPageAsCover_Click(object sender, RoutedEventArgs e)
    {
        if (_detailCatalogBook is null || (sender as FrameworkElement)?.DataContext is not PageCatalogItem item)
        {
            return;
        }

        var book = _detailCatalogBook;
        if (item.PageIndex < 0 || item.PageIndex >= book.Pages.Count || !string.Equals(book.Pages[item.PageIndex], item.Path, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "目录状态已变化，请重新打开目录。";
            return;
        }

        book.CoverPageIndex = item.PageIndex;
        await Task.Run(() => _database.SaveMetadata(book));
        await ReloadCoverAsync(book);
        book.NotifyAll();
        if (!ReferenceEquals(_currentBook, book) || !ReferenceEquals(_detailCatalogBook, book))
        {
            return;
        }

        FillMetadataEditors(book);
        ScheduleBookViewRefresh(refreshShelfOverview: false);
        StatusText.Text = $"已将第 {item.PageIndex + 1} 页设为封面。";
    }

    private void StartReadingFromCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (_detailCatalogBook is null || (sender as FrameworkElement)?.DataContext is not PageCatalogItem item)
        {
            return;
        }

        var book = _detailCatalogBook;
        if (item.PageIndex < 0 || item.PageIndex >= book.Pages.Count)
        {
            return;
        }

        HideDetailCatalog();
        book.LastReadPageIndex = item.PageIndex;
        _ = Task.Run(() => _database.SaveProgress(book));
        OpenBook(book);
    }

    private void ToggleDetailCatalogBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_detailCatalogBook is null || (sender as FrameworkElement)?.DataContext is not PageCatalogItem item)
        {
            return;
        }

        var book = _detailCatalogBook;
        var newState = !item.IsBookmarked;
        item.IsBookmarked = newState;

        _ = Task.Run(() =>
        {
            if (newState)
            {
                _database.AddBookmark(book.Id, item.PageIndex);
            }
            else
            {
                _database.RemoveBookmark(book.Id, item.PageIndex);
            }
        });

        // 重新分配标记颜色
        var bookmarks = new HashSet<int>(
            DetailPageCatalogItems.Where(i => i.IsBookmarked).Select(i => i.PageIndex));
        AssignBookmarkColors(DetailPageCatalogItems, bookmarks);

        StatusText.Text = newState
            ? $"已标记第 {item.PageIndex + 1} 页。"
            : $"已取消标记第 {item.PageIndex + 1} 页。";
    }

    private async void DeleteSourceFromCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (_detailCatalogBook is null || (sender as FrameworkElement)?.DataContext is not PageCatalogItem item)
        {
            return;
        }

        var enabled = _database.LoadSetting(CatalogDeleteSourceEnabledKey, "1");
        if (enabled != "1")
        {
            StatusText.Text = "目录中删除源文件功能已禁用。";
            return;
        }

        var book = _detailCatalogBook;
        if (!File.Exists(item.Path))
        {
            StatusText.Text = "文件不存在，已取消。";
            return;
        }

        var first = System.Windows.MessageBox.Show(
            $"确定永久删除此页吗？\n\n《{book.Title}》第 {item.PageIndex + 1} 页\n路径：{item.Path}\n\n此操作不可恢复！",
            "删除源文件",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (first != MessageBoxResult.Yes) return;

        var input = Interaction.InputBox(
            "请输入密码以确认删除源文件：",
            "密码确认",
            "");
        var password = _database.LoadSetting(DeleteSourcePasswordKey, DefaultDeleteSourcePassword);
        if (string.IsNullOrEmpty(input) || input != password)
        {
            StatusText.Text = "密码不正确，已取消。";
            return;
        }

        try
        {
            await Task.Run(() => File.Delete(item.Path));

            book.Pages.RemoveAt(item.PageIndex);
            book.PageCount = book.Pages.Count;
            book.TotalBytes = ImageLoader.SumFileBytes(book.Pages);
            book.CoverPageIndex = Math.Clamp(book.CoverPageIndex, 0, Math.Max(0, book.Pages.Count - 1));
            book.LastReadPageIndex = Math.Clamp(book.LastReadPageIndex, 0, Math.Max(0, book.Pages.Count - 1));
            _database.UpsertBook(book);
            book.NotifyAll();

            AppLogger.Info("delete-source", $"Deleted single page: {book.Title}, page={item.PageIndex + 1}, file={item.Path}");
            StatusText.Text = $"已删除《{book.Title}》第 {item.PageIndex + 1} 页。";

            if (book.Pages.Count == 0)
            {
                HideDetailCatalog();
                _database.DeleteBook(book);
                _allBooks.Remove(book);
                _currentBook = null;
                BooksList.SelectedItem = null;
                SetDetailVisible(false);
                RefreshLibraryViews(tagManager: false, authors: true);
                RefreshHomeShelves();
                StatusText.Text = $"《{book.Title}》已无剩余页面，书库记录已删除。";
                return;
            }

            _detailCatalogBook = book;
            EnsureDetailCatalogItems(book);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"删除失败：{ex.Message}";
            AppLogger.Error("delete-source", ex, $"Failed to delete page: {book.Title}, file={item.Path}");
        }
    }

    private async void ManualBackup_Click(object sender, RoutedEventArgs e)
    {
        await CreateManualBackupAsync();
    }

    private async Task CreateManualBackupAsync()
    {
        var backupPath = await Task.Run(() => _database.CreateManualBackup());
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            StatusText.Text = "当前还没有可备份的数据库。";
            return;
        }

        StatusText.Text = $"已创建数据库备份：{Path.GetFileName(backupPath)}";
    }

    private async void DataSafety_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DataSafetyDialog(_storage)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        switch (dialog.RequestedAction)
        {
            case DataSafetyAction.CreateBackup:
                await CreateManualBackupAsync();
                break;
            case DataSafetyAction.OpenBackupFolder:
                OpenBackupFolder();
                break;
            case DataSafetyAction.OpenDataFolder:
                OpenDataFolder();
                break;
            case DataSafetyAction.RestoreDatabase:
                await RestoreDatabaseFromPathAsync(dialog.SelectedDatabasePath);
                break;
            case DataSafetyAction.CheckUpdate:
                await CheckUpdateAsync(null);
                break;
        }
    }

    private async void RestoreDatabase_Click(object sender, RoutedEventArgs e)
    {
        await RestoreDatabaseFromPathAsync(null);
    }

    private async Task RestoreDatabaseFromPathAsync(string? selectedDatabasePath)
    {
        var sourcePath = selectedDatabasePath;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            using var dialog = new WinForms.OpenFileDialog
            {
                Title = "选择要恢复的数据库备份",
                InitialDirectory = Directory.Exists(_storage.BackupPath) ? _storage.BackupPath : _storage.Root,
                Filter = "SQLite 数据库 (*.db)|*.db|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            {
                return;
            }

            sourcePath = dialog.FileName;
        }

        sourcePath = Path.GetFullPath(sourcePath);
        if (sourcePath.Equals(Path.GetFullPath(_storage.DatabasePath), StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "选择的就是当前正在使用的数据库，无需恢复。";
            return;
        }

        var result = System.Windows.MessageBox.Show(
            $"将把所选数据库恢复为当前数据目录的 app.db。\n\n当前数据库会先自动备份，恢复会在重启后生效。\n\n所选文件：{sourcePath}\n\n是否继续?",
            "恢复数据库",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            StatusText.Text = "已取消恢复数据库。";
            return;
        }

        try
        {
            var backupPath = await Task.Run(() =>
            {
                var currentBackup = _database.CreateManualBackup();
                _storage.ScheduleDatabaseRestore(sourcePath);
                return currentBackup;
            });

            StatusText.Text = string.IsNullOrWhiteSpace(backupPath)
                ? "恢复数据库已排队，软件将重启后替换当前 app.db。"
                : $"恢复数据库已排队，当前数据库已备份为：{Path.GetFileName(backupPath)}";

            if (!RestartCurrentProcess())
            {
                StatusText.Text += " 自动重启失败，请手动重启软件完成恢复。";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLogger.Error("storage", ex, "Failed to schedule database restore.");
            StatusText.Text = $"恢复数据库失败：{ex.Message}";
        }
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenBackupFolder();
    }

    private void OpenBackupFolder()
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
        OpenDataFolder();
    }

    private void OpenDataFolder()
    {
        Directory.CreateDirectory(_storage.Root);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_storage.Root}\"",
            UseShellExecute = true
        });
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        await CheckUpdateAsync(sender as System.Windows.Controls.Button);
    }

    private async Task CheckUpdateAsync(System.Windows.Controls.Button? triggerButton)
    {
        if (_isCheckingForUpdates)
        {
            return;
        }

        _isCheckingForUpdates = true;
        if (triggerButton is not null)
        {
            triggerButton.IsEnabled = false;
        }

        StatusText.Text = $"正在检查更新，当前版本 {UpdateService.CurrentVersionText}...";

        try
        {
            var update = await _updateService.CheckLatestAsync();
            if (!update.HasUpdate)
            {
                StatusText.Text = update.Message;
                return;
            }

            var result = System.Windows.MessageBox.Show(
                $"{update.Message}\n\n当前版本：{UpdateService.CurrentVersionText}\n来源：{update.Source}\n更新包：{update.AssetName}\n\n是否现在准备并安装？安装时软件会关闭，更新完成后自动重启。",
                "检查更新",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            if (result != MessageBoxResult.Yes)
            {
                StatusText.Text = $"已取消安装更新：{update.LatestVersion}。";
                return;
            }

            StatusText.Text = $"{update.Message} 正在准备更新包...";
            var progress = new Progress<double>(value =>
            {
                StatusText.Text = $"{update.Message} 准备中 {value:P0}...";
            });

            var packagePath = await _updateService.DownloadPackageAsync(update, progress);
            StatusText.Text = "更新包已准备完成，软件即将关闭并自动替换文件。";
            AppLogger.Info("update", $"Launching updater for {update.LatestVersion}: {packagePath}");
            _updateService.LaunchUpdater(packagePath);
            Close();
        }
        catch (Exception ex)
        {
            AppLogger.Error("update", ex, "Update failed.");
            StatusText.Text = $"更新失败：{ex.Message}";
        }
        finally
        {
            _isCheckingForUpdates = false;
            if (triggerButton is not null)
            {
                triggerButton.IsEnabled = true;
            }
        }
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
            AppLogger.Info("storage", $"Data root changed for next launch: {selectedPath}");

            var result = System.Windows.MessageBox.Show(
                @"数据目录已指定。需要立即重启软件以生效，是否现在重启?",
                @"重启软件",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            if (result == MessageBoxResult.Yes)
            {
                if (!RestartCurrentProcess())
                {
                    StatusText.Text = "数据目录已指定。自动重启失败，请手动重启软件后生效。";
                }
            }
            else
            {
                StatusText.Text = "数据目录已指定，重启软件后生效。当前运行中的数据库不会热切换，避免写库过程中损坏数据。";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            AppLogger.Error("storage", ex, "Failed to update data root.");
            StatusText.Text = $"数据目录设置失败：{ex.Message}";
        }
    }

    private bool RestartCurrentProcess()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true
        });
        Environment.Exit(0);
        return true;
    }

    private async void Relocate_Click(object sender, RoutedEventArgs e)
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

        var selectedPath = dialog.SelectedPath;
        var pages = await Task.Run(() =>
            Directory.EnumerateFiles(selectedPath)
                .Where(ImageLoader.IsSupportedImage)
                .OrderBy(path => path, new NaturalPathComparer())
                .ToList());

        if (pages.Count == 0)
        {
            StatusText.Text = "重定位失败：目标文件夹内没有支持的图片。";
            return;
        }

        var totalBytes = await Task.Run(() => ImageLoader.SumFileBytes(pages));

        _currentBook.FolderPath = selectedPath;
        _currentBook.PageCount = pages.Count;
        _currentBook.TotalBytes = totalBytes;
        _currentBook.IsMissing = false;
        _currentBook.CoverPageIndex = Math.Clamp(_currentBook.CoverPageIndex, 0, pages.Count - 1);
        _currentBook.LastReadPageIndex = Math.Clamp(_currentBook.LastReadPageIndex, 0, pages.Count - 1);
        _currentBook.Pages.Clear();
        foreach (var page in pages)
        {
            _currentBook.Pages.Add(page);
        }

        var book = _currentBook;
        await Task.Run(() =>
        {
            _database.UpdateFolderPath(book);
            _database.SaveMetadata(book);
        });
        _currentBook.CoverImage = await Task.Run(() => _coverCache.LoadOrCreate(book));
        _currentBook.NotifyAll();
        FillMetadataEditors(_currentBook);
        StatusText.Text = "重定位完成。";
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_storage, _database) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (dialog.PrivacyModeChanged)
        {
            IsPrivacyMode = string.Equals(_database.LoadSetting(PrivacyModeSettingKey), "1", StringComparison.Ordinal);
        }

        if (dialog.ShortcutsChanged)
        {
            var shortcuts = _database.LoadShortcuts();
            ApplyShortcuts(shortcuts);
        }

        if (dialog.ThemeChanged)
        {
            SetNavButtonState(HomeNavButton, _currentNavigationKey == "home");
            SetNavButtonState(LibraryNavButton, _currentNavigationKey == "library");
            SetNavButtonState(TagsNavButton, _currentNavigationKey == "tags");
            SetNavButtonState(AuthorsNavButton, _currentNavigationKey == "authors");
        }

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
        }

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

    private async void AddTag_Click(object sender, RoutedEventArgs e)
    {
        if (!TryResolveTagForCreate(TagSearchBox.Text.Trim(), out var tag, out var category, out var isExclusive, out var color))
        {
            return;
        }

        UpsertManagedTag(tag, category, isExclusive, color);
        if (_currentBook is not null)
        {
            AddTagToBookRespectingRules(_currentBook, tag);
            TagsBox.Text = _currentBook.Tags;
            RefreshEditTagEditor(_currentBook.Tags);
            var book = _currentBook;
            await Task.Run(() => _database.SaveMetadata(book));
            _currentBook.NotifyAll();
        }

        RefreshLibraryViews(authors: false, sort: false);
        StatusText.Text = _currentBook is null
            ? $"已创建独立标签：{tag}"
            : $"已添加 Tag：{tag}";
    }

    private void BatchSelection_Changed(object sender, RoutedEventArgs e)
    {
        UpdateBatchSelectionState();
    }

    private static void OnBatchSelectionModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MainWindow window)
        {
            window.UpdateBatchSelectionModeVisuals(clearSelection: !(bool)e.NewValue);
        }
    }

    private void ToggleBatchSelectionMode_Click(object sender, RoutedEventArgs e)
    {
        IsBatchSelectionMode = !IsBatchSelectionMode;
    }

    private void ExitBatchSelectionMode_Click(object sender, RoutedEventArgs e)
    {
        IsBatchSelectionMode = false;
    }

    private void SelectVisibleBooks_Click(object sender, RoutedEventArgs e)
    {
        IsBatchSelectionMode = true;
        foreach (var book in GetVisibleBooks())
        {
            book.IsSelectedForBatch = true;
        }

        UpdateBatchSelectionState();
    }

    private void ClearBatchSelection_Click(object sender, RoutedEventArgs e)
    {
        ClearBatchSelection();
    }

    private async void BatchRemoveTitlePrefix_Click(object sender, RoutedEventArgs e)
    {
        var selectedBooks = GetSelectedBatchBooks();
        if (selectedBooks.Count == 0)
        {
            StatusText.Text = "请先勾选需要批量处理的漫画。";
            return;
        }

        var commonPrefix = GuessCommonTitlePrefix(selectedBooks.Select(book => book.Title));
        var dialog = new RenameDialog(
            "批量去前缀",
            "输入要从书名开头移除的前缀。只会修改确实以该前缀开头的作品。",
            "处理范围",
            $"已选 {selectedBooks.Count} 本",
            "要移除的前缀",
            commonPrefix)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var prefix = dialog.NewName.Trim();
        var updates = selectedBooks
            .Where(book => book.Title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(book => (Book: book, Title: book.Title[prefix.Length..].TrimStart(' ', '-', '_', '—', '－', '·')))
            .Where(item => !string.IsNullOrWhiteSpace(item.Title) && !string.Equals(item.Book.Title, item.Title, StringComparison.Ordinal))
            .ToList();

        if (updates.Count == 0)
        {
            StatusText.Text = "没有书名匹配这个前缀。";
            return;
        }

        var batchData = updates.Select(item => (item.Book.Id, item.Title)).ToList();
        await Task.Run(() => _database.SaveBookTitlesBatch(batchData, "before-batch-title-prefix"));
        foreach (var (book, title) in updates)
        {
            book.Title = title;
            book.NotifyAll();
        }

        RefreshLibraryViews(tagManager: false, authors: false, sort: true);
        RefreshHomeShelves();
        FillCurrentBookIfAffected(selectedBooks);
        StatusText.Text = $"已批量移除前缀：{updates.Count} 本。";
    }

    private async void BatchApplyStyle_Click(object sender, RoutedEventArgs e)
    {
        var selectedBooks = GetSelectedBatchBooks();
        if (selectedBooks.Count == 0)
        {
            StatusText.Text = "请先勾选需要批量处理的漫画。";
            return;
        }

        var targetStyle = Math.Clamp(BatchStyleBox?.SelectedIndex ?? 0, 0, 2);
        foreach (var book in selectedBooks)
        {
            book.BookStyle = targetStyle;
        }

        var batchData = selectedBooks.Select(book => (book.Id, book.BookStyle)).ToList();
        await Task.Run(() => _database.SaveBookStylesBatch(batchData, "before-batch-style"));
        foreach (var book in selectedBooks)
        {
            book.NotifyAll();
        }

        ScheduleBookViewRefresh(refreshShelfOverview: false);
        FillCurrentBookIfAffected(selectedBooks);
        StatusText.Text = $"已批量应用卡片样式 {MangaBook.StyleNames[targetStyle]}：{selectedBooks.Count} 本。";
    }

    private async void BatchAddTag_Click(object sender, RoutedEventArgs e)
    {
        var selectedBooks = GetSelectedBatchBooks();
        if (selectedBooks.Count == 0)
        {
            StatusText.Text = "请先勾选需要批量处理的漫画。";
            return;
        }

        if (!TryResolveTagForBatchAdd(selectedBooks, out var tag, out var category, out var isExclusive, out var color))
        {
            return;
        }

        UpsertManagedTag(tag, category, isExclusive, color);
        var updates = new List<(string BookId, string Tags)>();
        foreach (var book in selectedBooks)
        {
            var before = book.Tags;
            AddTagToBookRespectingRules(book, tag);
            if (!string.Equals(before, book.Tags, StringComparison.Ordinal))
            {
                updates.Add((book.Id, book.Tags));
            }
        }

        if (updates.Count == 0)
        {
            StatusText.Text = $"选中漫画都已经拥有 Tag：{tag}。";
            return;
        }

        await Task.Run(() => _database.SaveBookTagsBatch(updates, "before-batch-add-tag"));
        foreach (var book in selectedBooks)
        {
            book.NotifyAll();
        }

        RefreshLibraryViews(authors: false, sort: false);
        FillCurrentBookIfAffected(selectedBooks);
        StatusText.Text = $"已批量添加 Tag：{tag}，影响 {updates.Count} 本。";
    }

    private async void BatchRemoveTag_Click(object sender, RoutedEventArgs e)
    {
        var selectedBooks = GetSelectedBatchBooks();
        if (selectedBooks.Count == 0)
        {
            StatusText.Text = "请先勾选需要批量处理的漫画。";
            return;
        }

        var initialTag = TagSearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(initialTag))
        {
            initialTag = selectedBooks
                .SelectMany(book => TagService.ParseTags(book.Tags))
                .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key)
                .Select(group => group.Key)
                .FirstOrDefault() ?? "";
        }

        var dialog = new RenameDialog(
            "批量减 Tag",
            "输入要从选中漫画中移除的 Tag 名称。",
            "处理范围",
            $"已选 {selectedBooks.Count} 本",
            "要移除的 Tag",
            initialTag)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var tag = dialog.NewName.Trim();
        var updates = new List<(string BookId, string Tags)>();
        foreach (var book in selectedBooks)
        {
            var tags = TagService.ParseTags(book.Tags)
                .Where(name => !string.Equals(name, tag, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var formatted = TagService.FormatTags(tags);
            if (!string.Equals(book.Tags, formatted, StringComparison.Ordinal))
            {
                book.Tags = formatted;
                updates.Add((book.Id, book.Tags));
            }
        }

        if (updates.Count == 0)
        {
            StatusText.Text = $"选中漫画都没有 Tag：{tag}。";
            return;
        }

        await Task.Run(() => _database.SaveBookTagsBatch(updates, "before-batch-remove-tag"));
        foreach (var book in selectedBooks)
        {
            book.NotifyAll();
        }

        RefreshLibraryViews(authors: false, sort: false);
        FillCurrentBookIfAffected(selectedBooks);
        StatusText.Text = $"已批量移除 Tag：{tag}，影响 {updates.Count} 本。";
    }

    private async void BatchDeleteRecords_Click(object sender, RoutedEventArgs e)
    {
        var selectedBooks = GetSelectedBatchBooks();
        if (selectedBooks.Count == 0)
        {
            StatusText.Text = "请先勾选需要批量处理的漫画。";
            return;
        }

        var result = System.Windows.MessageBox.Show(
            $"确定从书库中删除选中的 {selectedBooks.Count} 本记录吗？\n\n这不会删除硬盘里的漫画文件，只会删除软件内的作者、Tag、进度、封面页等记录。",
            "批量删除库记录",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var input = Interaction.InputBox(
            $"请输入密码以确认批量删除 {selectedBooks.Count} 本库记录：",
            "密码确认",
            "");
        var password = _database.LoadSetting(DeleteSourcePasswordKey, DefaultDeleteSourcePassword);
        if (string.IsNullOrEmpty(input) || input != password)
        {
            StatusText.Text = "密码不正确，已取消。";
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                foreach (var book in selectedBooks)
                {
                    _database.DeleteBook(book);
                }
            });

            foreach (var book in selectedBooks)
            {
                _allBooks.Remove(book);
            }

            if (_currentBook is not null && selectedBooks.Contains(_currentBook))
            {
                _currentBook = null;
                BooksList.SelectedItem = null;
                SetDetailVisible(false);
            }

            RefreshLibraryViews(tagManager: false, authors: true);
            RefreshHomeShelves();
            UpdateBatchSelectionState();
            StatusText.Text = $"已批量删除 {selectedBooks.Count} 本库记录，源文件未删除。";
            AppLogger.Info("batch-delete-records", $"Batch deleted {selectedBooks.Count} library records.");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"批量删除库记录失败：{ex.Message}";
            AppLogger.Error("batch-delete-records", ex, $"Failed to batch delete {selectedBooks.Count} records.");
        }
    }

    private async void BatchDeleteSourceFiles_Click(object sender, RoutedEventArgs e)
    {
        var selectedBooks = GetSelectedBatchBooks();
        if (selectedBooks.Count == 0)
        {
            StatusText.Text = "请先勾选需要批量处理的漫画。";
            return;
        }

        var safeBooks = selectedBooks
            .Where(b => IsSafeFolderPathForDeletion(b.FolderPath, _storage.Root))
            .ToList();
        if (safeBooks.Count == 0)
        {
            StatusText.Text = "没有可安全删除的源文件夹。";
            return;
        }

        var skipped = selectedBooks.Count - safeBooks.Count;

        var first = System.Windows.MessageBox.Show(
            $"确定永久删除选中 {safeBooks.Count} 本的源文件夹及所有内容吗？" +
            (skipped > 0 ? $"\n\n（{skipped} 本路径不合法已跳过）" : "") +
            "\n\n此操作不可恢复！",
            "批量删除源文件",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (first != MessageBoxResult.Yes) return;

        var input = Interaction.InputBox(
            $"请输入密码以确认批量删除 {safeBooks.Count} 本源文件：",
            "密码确认",
            "");
        var password = _database.LoadSetting(DeleteSourcePasswordKey, DefaultDeleteSourcePassword);
        if (string.IsNullOrEmpty(input) || input != password)
        {
            StatusText.Text = "密码不正确，已取消。";
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                foreach (var book in safeBooks)
                {
                    if (Directory.Exists(book.FolderPath))
                    {
                        Directory.Delete(book.FolderPath, recursive: true);
                    }
                    _database.DeleteBook(book);
                }
            });

            foreach (var book in safeBooks)
            {
                _allBooks.Remove(book);
            }

            if (_currentBook is not null && safeBooks.Contains(_currentBook))
            {
                _currentBook = null;
                BooksList.SelectedItem = null;
                SetDetailVisible(false);
            }

            RefreshLibraryViews(tagManager: false, authors: true);
            RefreshHomeShelves();
            UpdateBatchSelectionState();
            StatusText.Text = $"已批量删除 {safeBooks.Count} 本的源文件夹及库记录。";
            AppLogger.Info("batch-delete-source", $"Batch deleted {safeBooks.Count} source files and records.");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"批量删除源文件失败：{ex.Message}";
            AppLogger.Error("batch-delete-source", ex, $"Failed to batch delete source files.");
        }
    }

    private void TagSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RestartDebounceTimer(_tagSearchDebounceTimer);
    }

    private void TagChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TagChip chip })
        {
            return;
        }
        if (IsTagSummaryChip(chip))
        {
            return;
        }

        _tagDragStartPoint = e.GetPosition(this);
        if (IsInsideBooksList(sender as DependencyObject))
        {
            ApplyTagFilter(chip);
            e.Handled = true;
            return;
        }

        if (e.ClickCount >= 2)
        {
            ApplyTagFilter(chip);
            e.Handled = true;
            return;
        }

        StatusText.Text = $"双击 Tag 筛选：{chip.Name}";
    }

    private void TagChip_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is UIElement element)
        {
            MotionService.ScaleTo(element, 1.04, MotionService.Fast);
        }
    }

    private void TagChip_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is UIElement element)
        {
            MotionService.ScaleTo(element, 1.0, MotionService.Fast);
        }
    }

    private void TagChip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TagChip chip } elementInCard
            && IsInsideBooksList(elementInCard))
        {
            MotionService.PressBounce(elementInCard);
            ApplyTagFilter(chip);
            e.Handled = true;
            return;
        }

        if (sender is UIElement element)
        {
            MotionService.PressBounce(element);
        }
    }

    private void TagChip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement element)
        {
            MotionService.ScaleTo(element, element.IsMouseOver ? 1.04 : 1.0, MotionService.Fast);
        }
    }

    private void TagChip_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: TagChip chip } || chip.IsSelected)
        {
            return;
        }
        if (IsTagSummaryChip(chip))
        {
            return;
        }

        var currentPosition = e.GetPosition(this);
        if (_tagDragStartPoint is { } start
            && Math.Abs(currentPosition.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPosition.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new System.Windows.DataObject();
        data.SetData(TagDragDataFormat, chip.Name);
        data.SetData(typeof(string), chip.Name);
        DragDrop.DoDragDrop((DependencyObject)sender, data, System.Windows.DragDropEffects.Copy);
    }

    private void EditTagOption_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_isEditMode || sender is not FrameworkElement { DataContext: TagChip chip })
        {
            return;
        }
        if (e.ClickCount >= 2)
        {
            ApplyTagFilter(chip);
            e.Handled = true;
            return;
        }

        var tags = TagService.ParseTags(TagsBox.Text).ToList();
        if (tags.Any(tag => string.Equals(tag, chip.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (IsExclusiveTag(chip.Name))
        {
            var category = TagCategory(chip.Name);
            tags = tags
                .Where(tag => !IsExclusiveTag(tag) || !string.Equals(TagCategory(tag), category, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        tags.Add(chip.Name);
        TagsBox.Text = NormalizeTagsRespectingRules(tags);
        RefreshEditTagEditor(TagsBox.Text);
    }

    private void EditTagRemove_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_isEditMode || sender is not FrameworkElement { DataContext: TagChip chip })
        {
            return;
        }
        if (e.ClickCount >= 2)
        {
            ApplyTagFilter(chip);
            e.Handled = true;
            return;
        }

        var tags = TagService.ParseTags(TagsBox.Text)
            .Where(tag => !string.Equals(tag, chip.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        TagsBox.Text = TagService.FormatTags(tags);
        RefreshEditTagEditor(TagsBox.Text);
    }

    private void EditTagSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_isEditMode)
        {
            return;
        }

        RefreshEditTagEditor(TagsBox.Text);
    }

    private void EditAuthorSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_isEditMode)
        {
            return;
        }

        RefreshEditAuthorOptions(AuthorBox.Text);
    }

    private void EditAuthorOption_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_isEditMode || sender is not FrameworkElement { DataContext: AuthorItem item })
        {
            return;
        }

        AuthorBox.Text = item.Name;
        RefreshEditAuthorOptions(item.Name);
        e.Handled = true;
    }

    private void EditCreateTag_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEditMode)
        {
            return;
        }

        if (!TryResolveTagForCreate("", out var tag, out var category, out var isExclusive, out var color))
        {
            return;
        }

        UpsertManagedTag(tag, category, isExclusive, color);
        var tags = TagService.ParseTags(TagsBox.Text).ToList();
        if (!tags.Any(name => string.Equals(name, tag, StringComparison.OrdinalIgnoreCase)))
        {
            if (isExclusive)
            {
                tags = tags
                    .Where(name => !IsExclusiveTag(name) || !string.Equals(TagCategory(name), category, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            tags.Add(tag);
        }

        TagsBox.Text = NormalizeTagsRespectingRules(tags);
        RefreshEditTagEditor(TagsBox.Text);
        RefreshLibraryViews(authors: false, sort: false);
        StatusText.Text = $"已加入待保存 Tag：{tag}";
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

        if (!e.Data.GetDataPresent(TagDragDataFormat) && !e.Data.GetDataPresent(typeof(string)))
        {
            return;
        }

        var tag = e.Data.GetData(TagDragDataFormat) as string ?? e.Data.GetData(typeof(string)) as string;
        var book = FindAncestor<System.Windows.Controls.ListBoxItem>((DependencyObject)e.OriginalSource)?.DataContext as MangaBook
            ?? BooksList.SelectedItem as MangaBook;
        if (string.IsNullOrWhiteSpace(tag) || book is null)
        {
            return;
        }

        UpsertManagedTag(tag);
        AddTagToBookRespectingRules(book, tag);
        await Task.Run(() => _database.SaveMetadata(book));
        book.NotifyAll();
        if (ReferenceEquals(book, _currentBook))
        {
            TagsBox.Text = book.Tags;
            RefreshEditTagEditor(book.Tags);
        }
        RefreshLibraryViews(authors: false, sort: false);
        StatusText.Text = $"已给《{book.Title}》添加 Tag：{tag}";
        e.Handled = true;
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
        if (e.Data.GetDataPresent(TagDragDataFormat))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            HideImportDropFeedback();
            e.Handled = true;
            return;
        }

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

    private void AuthorSearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        RestartDebounceTimer(_authorSearchDebounceTimer);
    }

    private void CreateManagedTag_Click(object sender, RoutedEventArgs e)
    {
        if (!TryResolveTagForCreate(TagManagerSearchBox.Text.Trim(), out var tag, out var category, out var isExclusive, out var color))
        {
            return;
        }

        if (EnumerateKnownTags().Any(name => string.Equals(name, tag, StringComparison.OrdinalIgnoreCase)))
        {
            UpsertManagedTag(tag, category, isExclusive, color);
            TagManagerSearchBox.Clear();
            RefreshLibraryViews(authors: false, sort: false, filter: false);
            StatusText.Text = $"标签已存在：{tag}";
            return;
        }

        UpsertManagedTag(tag, category, isExclusive, color);
        TagManagerSearchBox.Clear();
        RefreshLibraryViews(authors: false, sort: false, filter: false);
        StatusText.Text = $"已创建候选标签：{tag}。它会出现在书库 Tag 池，可拖拽到漫画或添加到当前漫画。";
    }

    private void RefreshEditTagEditor(string tagsText)
    {
        var selectedNames = TagService.ParseTags(tagsText).ToList();
        var selectedSet = new HashSet<string>(selectedNames, StringComparer.OrdinalIgnoreCase);
        var query = EditTagSearchBox?.Text.Trim() ?? "";
        var selectedChips = selectedNames
            .Select(name => CreateTagChip(name))
            .ToList();
        var optionChips = string.IsNullOrWhiteSpace(query)
            ? []
            : EnumerateKnownTags()
                .Where(name => !selectedSet.Contains(name))
                .Where(name => name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || TagCategory(name).Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(name => name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
                .ThenBy(name => TagCategoryOrder(TagCategory(name)))
                .ThenBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .Take(18)
                .Select(name => CreateTagChip(name))
                .ToList();

        EditSelectedTagItems.ReplaceRange(selectedChips);
        EditTagOptions.ReplaceRange(optionChips);
    }

    private void RefreshEditAuthorOptions(string query)
    {
        var trimmed = query.Trim();
        var current = _currentBook?.Author ?? "";
        var options = string.IsNullOrWhiteSpace(trimmed)
            ? []
            : EnumerateKnownAuthors()
                .Where(author => !string.Equals(author, current, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(author, trimmed, StringComparison.OrdinalIgnoreCase))
                .Where(author => author.Contains(trimmed, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(author => author.StartsWith(trimmed, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
                .ThenBy(author => author, StringComparer.CurrentCultureIgnoreCase)
                .Take(12)
                .Select(author => new AuthorItem { Name = author, BookCount = CountBooksByAuthor(author) })
                .ToList();

        EditAuthorOptions.ReplaceRange(options);
    }

    private void FillMetadataEditors(MangaBook book)
    {
        DetailPanel.DataContext = book;
        TitleBox.Text = book.Title;
        AuthorBox.Text = book.Author;
        RefreshEditAuthorOptions(book.Author);
        ForeignNameBox.Text = book.ForeignName;
        ProducedAtBox.Text = book.ProducedAt;
        ImportedAtBox.Text = book.ImportedAt;
        TagsBox.Text = book.Tags;
        RefreshEditTagEditor(book.Tags);
        CoverPageBox.Text = (book.CoverPageIndex + 1).ToString();
        ReadCountBox.Text = book.ReadCount.ToString();
        SetSelectedReadingStatus(book.ReadingStatus);
        SummaryBox.Text = book.Summary;
        ReadOnlyAuthorText.Text = EmptyAsPlaceholder(book.Author);
        ReadOnlyForeignNameText.Text = EmptyAsPlaceholder(book.ForeignName);
        ReadOnlyStatusText.Text = book.ReadingStatusText;
        ReadOnlyPageCountText.Text = book.PageCount.ToString();
        ReadOnlyProducedAtText.Text = EmptyAsPlaceholder(book.ProducedAt);
        ReadOnlyImportedAtText.Text = EmptyAsPlaceholder(book.ImportedAt);
        ReadOnlyTagsText.Text = EmptyAsPlaceholder(book.Tags);
        ReadOnlyCoverPageText.Text = (book.CoverPageIndex + 1).ToString();
        DetailCoverPageText.Text = $"第 {book.CoverPageIndex + 1} 页 · {book.StyleName}";
        ReadOnlyReadCountText.Text = book.ReadCountText;
        ReadOnlySummaryText.Text = EmptyAsPlaceholder(book.Summary);
        HideBookButton.Content = book.IsHidden ? "恢复显示" : "隐藏作品";
        HideBookButtonEdit.Content = book.IsHidden ? "恢复显示" : "隐藏作品";
        PrivacyCoverButtonEdit.Content = book.IsPrivacyCover ? "取消隐私封面" : "保持隐私封面";
        ToggleFavoriteButton.Content = book.IsFavorite ? "★ 已收藏" : "☆ 收藏";
        if (MoreActionsHideMenuItem is not null)
        {
            MoreActionsHideMenuItem.Header = book.IsHidden ? "恢复显示" : "隐藏作品";
        }

        // Hero 区
        var displayTitle = string.IsNullOrWhiteSpace(book.Title) ? "未命名作品" : book.Title;
        BookTitleText.Text = displayTitle;
        BookTitleText.ToolTip = displayTitle;
        BookAuthorFilterButton.Content = string.IsNullOrWhiteSpace(book.Author) ? "作者 未指定" : $"作者 {book.Author}";
        BookAuthorFilterButton.IsEnabled = !string.IsNullOrWhiteSpace(book.Author);
        // Meta 行只展示页数；标签由下方胶囊承担，避免视觉重复。
        BookMetaLineText.Text = $"{book.PageCount} 页";

        // 标签胶囊
        SyncBookTagChips(book);
        var tagChips = (book.TagItems ?? (System.Collections.Generic.IEnumerable<TagChip>)System.Array.Empty<TagChip>())
            .Where(tag => !string.IsNullOrWhiteSpace(tag.Name))
            .ToList();
        BookTagChips.ItemsSource = tagChips;
        BookTagChips.Visibility = tagChips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 阅读状态行
        ReadingStatusInlineText.Text = $"{book.ReadingStatusText} · {book.ReadCountText}";

        // 作品信息卡
        ApplyMetaValue(MetaAuthorValueText, book.Author);
        ApplyMetaValue(MetaForeignNameValueText, book.ForeignName);
        ApplyMetaValue(MetaPageCountValueText, book.PageCount.ToString(), forceFilled: true);
        ApplyMetaValue(MetaCoverPageValueText, (book.CoverPageIndex + 1).ToString(), forceFilled: true);
        ApplyMetaValue(MetaProducedAtValueText, book.ProducedAt);
        ApplyMetaValue(MetaImportedAtValueText, book.ImportedAt);

        BuildRatingStars(book);
    }

    private static readonly SolidColorBrush RatingStarEmptyBrush = CreateFrozenBrush("#D1D5DB");
    private static readonly SolidColorBrush RatingStarFilledBrush = CreateFrozenBrush("#F59E0B");
    private static readonly System.Windows.Media.Brush RatingStarHitBrush = System.Windows.Media.Brushes.Transparent;

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void BuildRatingStars(MangaBook book)
    {
        if (RatingStarsHost is null) return;
        RatingStarsHost.Children.Clear();
        for (int i = 1; i <= 5; i++)
        {
            RatingStarsHost.Children.Add(CreateRatingStar(book.Rating, i));
        }
        RatingStarsHost.ToolTip = book.HasRating ? $"评分 {book.RatingText} · 点击调整，再点同处清零" : "未评分 · 点击设置";
    }

    private static readonly Geometry StarGeometry = Geometry.Parse(
        "M 12,2 L 14.39,8.59 L 21,9.39 L 16,14 L 17.39,21 L 12,17.27 L 6.61,21 L 8,14 L 3,9.39 L 9.61,8.59 Z");

    private System.Windows.Controls.Grid CreateRatingStar(double rating, int starIndex)
    {
        const double starSize = 22;
        var grid = new System.Windows.Controls.Grid
        {
            Width = starSize,
            Height = starSize,
            Margin = new Thickness(2, 0, 2, 0),
            Cursor = System.Windows.Input.Cursors.Hand
        };

        grid.Children.Add(BuildStarPath(starSize, RatingStarEmptyBrush, System.Windows.HorizontalAlignment.Center, fillToWidth: null));

        double fillWidth = 0;
        if (rating >= starIndex) fillWidth = starSize;
        else if (rating >= starIndex - 0.5) fillWidth = starSize / 2.0;

        if (fillWidth > 0)
        {
            var clip = new System.Windows.Controls.Border
            {
                Width = fillWidth,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                ClipToBounds = true
            };
            clip.Child = BuildStarPath(starSize, RatingStarFilledBrush, System.Windows.HorizontalAlignment.Left, fillToWidth: starSize);
            grid.Children.Add(clip);
        }

        var leftHit = new System.Windows.Shapes.Rectangle
        {
            Width = starSize / 2.0,
            Fill = RatingStarHitBrush,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Tag = $"{starIndex}|L"
        };
        var rightHit = new System.Windows.Shapes.Rectangle
        {
            Width = starSize / 2.0,
            Fill = RatingStarHitBrush,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Tag = $"{starIndex}|R"
        };
        leftHit.MouseLeftButtonUp += RatingStar_Click;
        rightHit.MouseLeftButtonUp += RatingStar_Click;
        grid.Children.Add(leftHit);
        grid.Children.Add(rightHit);

        return grid;
    }

    private static System.Windows.Shapes.Path BuildStarPath(double size, SolidColorBrush brush, System.Windows.HorizontalAlignment alignment, double? fillToWidth)
    {
        return new System.Windows.Shapes.Path
        {
            Data = StarGeometry,
            Fill = brush,
            Stroke = brush,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform,
            Width = fillToWidth ?? size,
            Height = size,
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private async void RatingStar_Click(object sender, MouseButtonEventArgs e)
    {
        if (_currentBook is null) return;
        if (sender is not System.Windows.Shapes.Rectangle r || r.Tag is not string tag) return;
        e.Handled = true;

        var parts = tag.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var starIndex)) return;
        var newRating = parts[1] == "L" ? starIndex - 0.5 : starIndex;

        if (Math.Abs(_currentBook.Rating - newRating) < 0.01)
        {
            newRating = 0;
        }

        var book = _currentBook;
        book.Rating = newRating;
        await Task.Run(() => _database.SaveMetadata(book));
        BuildRatingStars(book);
        ScheduleBookViewRefresh(refreshShelfOverview: false);
        StatusText.Text = book.HasRating
            ? $"《{book.Title}》评分 {book.RatingText}。"
            : $"《{book.Title}》评分已清除。";
    }

    private void CopyBookTitle_Click(object sender, MouseButtonEventArgs e)
    {
        if (_currentBook is null) return;
        var title = _currentBook.Title ?? "";
        try
        {
            SafeSetClipboard(title);
            StatusText.Text = string.IsNullOrEmpty(title) ? "标题为空，已复制空字符串。" : $"已复制标题：{title}";
        }
        catch
        {
            StatusText.Text = "标题复制失败，可能剪贴板被其他进程占用。";
        }
        e.Handled = true;
    }

    private void ApplyMetaValue(System.Windows.Controls.TextBlock target, string value, bool forceFilled = false)
    {
        if (target is null) return;
        var isEmpty = !forceFilled && string.IsNullOrWhiteSpace(value);
        target.Text = isEmpty ? "未填写" : value;
        target.Style = (System.Windows.Style)FindResource(isEmpty ? "DetailMetaEmptyValueText" : "DetailMetaValueText");
    }

    private void SetDetailVisible(bool visible)
    {
        if (DetailPanel is null || DetailShell is null || DetailColumn is null)
        {
            return;
        }

        DetailColumn.Width = new GridLength(0);
        DetailPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (DetailDismissOverlay is not null)
        {
            DetailDismissOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        if (visible)
        {
            MotionService.ShowWithFade(DetailShell);
        }
        else if (DetailShell.Visibility == Visibility.Visible)
        {
            MotionService.HideWithFade(DetailShell);
        }
        else
        {
            DetailShell.Visibility = Visibility.Collapsed;
        }

        if (!visible)
        {
            HideDetailCatalog();
            SetEditMode(false);
        }
    }

    private void SetEditMode(bool enabled)
    {
        _isEditMode = enabled;
        if (EditModeButton is null)
        {
            return;
        }

        EditModeButton.Content = "编辑";
        EditModeButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        DeleteSourceButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        EditModeHintText.Text = enabled ? "编辑模式：修改后点击“保存信息”" : "只读模式：点击“编辑”后修改信息";
        ReadOnlyInfoPanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        EditFormPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        SaveMetadataButton.IsEnabled = enabled;
        ImportedTodayButton.IsEnabled = enabled;

        foreach (var box in new[] { TitleBox, AuthorBox, ForeignNameBox, ProducedAtBox, ImportedAtBox, TagsBox, CoverPageBox, ReadCountBox, SummaryBox, EditTagSearchBox })
        {
            box.IsReadOnly = !enabled;
            box.Opacity = enabled ? 1.0 : 0.78;
        }

        if (!enabled)
        {
            EditTagOptions.Clear();
            EditAuthorOptions.Clear();
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 仅处理 Ctrl+X（Ctrl+C/V/A 交给 WPF 原生命令路由）
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key != Key.X) return;
        if (Keyboard.FocusedElement is not System.Windows.Controls.TextBox box) return;
        if (box.IsReadOnly || string.IsNullOrEmpty(box.SelectedText)) return;

        // 延迟到按键事件处理完毕后再执行剪切，避免 IME 干扰文本删除
        var text = box.SelectedText;
        var start = box.SelectionStart;
        e.Handled = true;
        Dispatcher.BeginInvoke(() =>
        {
            try { System.Windows.Clipboard.SetText(text); } catch { }
            box.Text = box.Text.Remove(start, text.Length);
            box.Select(start, 0);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// 安全写入剪贴板，带重试（中文输入法组合期间剪贴板可能被锁定）
    /// </summary>
    private static void SafeSetClipboard(string text)
    {
        for (var i = 0; i < 3; i++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, true);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (i < 2)
            {
                System.Threading.Thread.Sleep(30);
            }
        }
    }

    private static string EmptyAsPlaceholder(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "—" : value;
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

        var filtered = tagNames
            .Select(tag => CreateTagChip(tag, _activeTagFilters.Contains(tag)))
            .Where(tag => string.IsNullOrWhiteSpace(query) || tag.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        var sortMode = WaterfallTagSortBox?.SelectedIndex ?? 0;
        var tags = sortMode switch
        {
            1 => filtered
                .OrderByDescending(tag => !string.IsNullOrWhiteSpace(tag.UpdatedAt))
                .ThenByDescending(tag => tag.UpdatedAt, StringComparer.Ordinal)
                .ThenBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            2 => filtered
                .OrderBy(tag => tag.Color, StringComparer.OrdinalIgnoreCase)
                .ThenBy(tag => TagCategoryOrder(tag.Category))
                .ThenBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            3 => filtered
                .OrderByDescending(tag => tag.UsageCount)
                .ThenBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            _ => filtered
                .OrderBy(tag => TagCategoryOrder(tag.Category))
                .ThenBy(tag => tag.Name)
                .ToList(),
        };

        VisibleTags.ReplaceRange(tags);
    }

    private void WaterfallTagSort_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RefreshVisibleTags();
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
            var authors = _allBooks.Where(book => OnlyHiddenBox?.IsChecked == true || !book.IsHidden)
                .Select(book => book.Author.Trim())
                .Where(author => !string.IsNullOrWhiteSpace(author))
                .Concat(_managedAuthors)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(author => author)
                .ToList();

            var filterItems = authors.Prepend("全部作者").ToList();
            AuthorFilters.ReplaceRange(filterItems);

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
        _authorSearchDebounceTimer.Tick += (_, _) =>
        {
            _authorSearchDebounceTimer.Stop();
            RefreshAuthorManagementItems(AuthorSearchBox?.Text?.Trim());
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
        _authorSearchDebounceTimer.Stop();
    }

    private void BookFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingAuthorFilters)
        {
            return;
        }


        RefreshLibraryViews(tagManager: false, sort: false);
    }

    private void OnlyHidden_Changed(object sender, RoutedEventArgs e)
    {
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

    private void ToggleLogPanelSize_Click(object sender, RoutedEventArgs e)
    {
        _isLogPanelExpanded = !_isLogPanelExpanded;
        if (!_isLogPanelVisible)
        {
            _isLogPanelVisible = true;
        }

        UpdateLogPanelVisibility();
    }

    private void AppLogger_LineWritten(string line)
    {
        Dispatcher.InvokeAsync(() => AppendLogOutput(line), DispatcherPriority.Background);
    }

    private void StatusLogTimer_Tick(object? sender, EventArgs e)
    {
        if (StatusText is null)
        {
            return;
        }

        var text = StatusText.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text == _lastStatusLogText)
        {
            return;
        }

        _lastStatusLogText = text;
        AppendLogOutput($"{DateTimeOffset.Now:HH:mm:ss.fff} [STATUS] {text}");
    }

    private void AppendLogOutput(string line)
    {
        if (LogOutputText is null)
        {
            return;
        }

        _liveLogLines.Enqueue(TrimLiveLogLine(line));
        while (_liveLogLines.Count > MaxLiveLogLines)
        {
            _liveLogLines.Dequeue();
        }

        LogOutputText.Text = string.Join(Environment.NewLine, _liveLogLines);
        if (_isLogPanelVisible && LogOutputScrollViewer is not null)
        {
            LogOutputScrollViewer.ScrollToEnd();
        }
    }

    private static string TrimLiveLogLine(string line)
    {
        if (line.Length <= MaxLiveLogLineLength)
        {
            return line;
        }

        return line[..MaxLiveLogLineLength] + " ...";
    }

    private void ClearLogOutput_Click(object sender, RoutedEventArgs e)
    {
        _liveLogLines.Clear();
        if (LogOutputText is not null)
        {
            LogOutputText.Text = "";
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_storage.LogsPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_storage.LogsPath}\"",
            UseShellExecute = true
        });
    }

    private void SetLibraryChromeCollapsed(bool collapsed)
    {
        _libraryChromeCollapsed = collapsed;
        if (LibraryFilterControlsPanel is not null)
        {
            LibraryFilterControlsPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        }
        UpdateBatchSelectionModeVisuals(clearSelection: false);
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
            ? "已进入专注浏览：筛选控件、Tag 池和统计摘要已收起，点击“展开筛选”可恢复。"
            : "已展开筛选区。";
    }

    private void UpdateLogPanelVisibility()
    {
        if (LogPanel is not null)
        {
            LogPanel.Visibility = _isLogPanelVisible ? Visibility.Visible : Visibility.Collapsed;
            LogPanel.Height = _isLogPanelExpanded ? ExpandedLogPanelHeight : NormalLogPanelHeight;
        }

        if (LogPanelToggleButton is not null)
        {
            LogPanelToggleButton.Tag = _isLogPanelVisible ? "active" : "";
        }

        if (LogPanelExpandButton is not null)
        {
            LogPanelExpandButton.Content = _isLogPanelExpanded ? "还原" : "扩大";
        }

        if (_isLogPanelVisible && LogOutputScrollViewer is not null)
        {
            LogOutputScrollViewer.ScrollToEnd();
        }
    }

    private void RefreshBookFilter(bool ensureLibraryView = false)
    {
        CacheBookFilterState();
        ScheduleBookViewRefresh(refreshShelfOverview: true, ensureLibraryView);
    }

    private void ScheduleBookViewRefresh(bool refreshShelfOverview, bool ensureLibraryView = false)
    {
        _refreshShelfOverviewAfterBookFilter |= refreshShelfOverview;
        _ensureLibraryViewAfterBookFilter |= ensureLibraryView;
        _filterCts.Cancel();
        _filterCts = new CancellationTokenSource();

        var refreshShelf = _refreshShelfOverviewAfterBookFilter;
        var ensureLibrary = _ensureLibraryViewAfterBookFilter;
        _refreshShelfOverviewAfterBookFilter = false;
        _ensureLibraryViewAfterBookFilter = false;
        var snapshot = _allBooks.ToList();
        var sortIndex = SortBox?.SelectedIndex ?? 0;
        var sortDescending = _sortDescending;
        var searchQuery = _cachedSearchQuery;
        var statusFilter = _cachedStatusFilter;
        var authorFilter = _cachedAuthorFilter;
        var favoriteOnly = _cachedFavoriteOnly;
        var showHidden = _cachedShowHidden;
        var onlyHidden = _cachedOnlyHidden;
        var activeTags = _cachedActiveTagFilters.ToArray();
        var token = _filterCts.Token;

        _ = ExecuteBookViewRefreshAsync(
            snapshot,
            sortIndex,
            sortDescending,
            searchQuery,
            statusFilter,
            authorFilter,
            favoriteOnly,
            showHidden,
            onlyHidden,
            activeTags,
            refreshShelf,
            ensureLibrary,
            token);
    }

    private async Task ExecuteBookViewRefreshAsync(
        List<MangaBook> snapshot,
        int sortIndex,
        bool sortDescending,
        string searchQuery,
        string statusFilter,
        string authorFilter,
        bool favoriteOnly,
        bool showHidden,
        bool onlyHidden,
        string[] activeTags,
        bool refreshShelfOverview,
        bool ensureLibraryView,
        CancellationToken token)
    {
        List<MangaBook> filtered;
        try
        {
            filtered = await Task.Run(
                () => FilterAndSortBooks(
                    snapshot,
                    sortIndex,
                    sortDescending,
                    searchQuery,
                    statusFilter,
                    authorFilter,
                    favoriteOnly,
                    showHidden,
                    onlyHidden,
                    activeTags,
                    token),
                token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        Books.ReplaceRange(filtered);
        if (refreshShelfOverview)
        {
            RefreshShelfOverview();
        }
        if (ensureLibraryView)
        {
            EnsureLibraryViewCanShowBooks();
        }
    }

    private void CacheBookFilterState()
    {
        _cachedSearchQuery = BookSearchBox?.Text.Trim() ?? "";
        _cachedStatusFilter = GetSelectedStatusFilter();
        _cachedAuthorFilter = AuthorFilterBox?.SelectedItem as string ?? "";
        _cachedFavoriteOnly = FavoriteOnlyBox?.IsChecked == true;
        _cachedShowHidden = false;
        _cachedOnlyHidden = OnlyHiddenBox?.IsChecked == true;
        _cachedActiveTagFilters = _activeTagFilters.ToArray();
    }

    private void RefreshLibraryViews(
        bool tags = true,
        bool tagManager = true,
        bool authors = true,
        bool sort = false,
        bool filter = true,
        bool activeTags = false,
        bool ensureLibraryView = false)
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
            ApplyBookSort(refresh: !filter);
        }
        if (filter)
        {
            RefreshBookFilter(ensureLibraryView);
        }
    }

    private void ToggleTagFilter(TagChip chip)
    {
        if (IsTagSummaryChip(chip))
        {
            return;
        }

        if (_activeTagFilters.Contains(chip.Name))
        {
            _activeTagFilters.Remove(chip.Name);
            StatusText.Text = $"已取消 Tag：{chip.Name}";
        }
        else
        {
            _activeTagFilters.Add(chip.Name);
            StatusText.Text = $"已追加 Tag：{chip.Name}";
        }

        RefreshLibraryViews(tagManager: false, authors: false, sort: false, activeTags: true);
    }

    private void ApplyTagFilter(TagChip chip)
    {
        if (IsTagSummaryChip(chip))
        {
            return;
        }

        ShowLibraryView("library");
        BooksList.SelectedItem = null;
        _currentBook = null;
        SetDetailVisible(false);
        if (!_activeTagFilters.Contains(chip.Name))
        {
            _activeTagFilters.Add(chip.Name);
        }

        RefreshLibraryViews(tagManager: false, authors: false, sort: false, activeTags: true, ensureLibraryView: true);
        StatusText.Text = $"已按 Tag 筛选：{chip.Name}";
    }

    private static bool IsTagSummaryChip(TagChip chip)
    {
        return chip.Name.StartsWith("+", StringComparison.Ordinal);
    }

    private static bool IsTagChipEventSource(object source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: TagChip })
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsInsideBooksList(DependencyObject? source)
    {
        return source is not null && FindAncestor<System.Windows.Controls.ListBoxItem>(source) is not null;
    }

    private void ActiveTagChip_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TagChip chip })
        {
            return;
        }
        if (e.ClickCount >= 2)
        {
            ApplyTagFilter(chip);
            e.Handled = true;
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
        OnlyHiddenBox.IsChecked = false;
        RefreshLibraryViews(tagManager: false, authors: false, sort: false, activeTags: true);
        StatusText.Text = "已清空书架筛选。";
    }

    private void SortBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _sortDescending = (SortBox?.SelectedIndex ?? 0) != 0;
        UpdateSortDirectionButton();
        ApplyBookSort();
    }

    private void ToggleSortDirection_Click(object sender, RoutedEventArgs e)
    {
        _sortDescending = !_sortDescending;
        UpdateSortDirectionButton();
        ApplyBookSort();
    }

    private void UpdateSortDirectionButton()
    {
        if (SortDirectionButton is not null)
        {
            SortDirectionButton.Content = _sortDescending ? "降序" : "升序";
        }
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

    private void ApplyBookSort(bool refresh = true)
    {
        if (SortBox is null)
        {
            return;
        }

        if (refresh)
        {
            ScheduleBookViewRefresh(refreshShelfOverview: false);
        }
    }

    private static List<MangaBook> FilterAndSortBooks(
        List<MangaBook> allBooks,
        int sortIndex,
        bool sortDescending,
        string searchQuery,
        string statusFilter,
        string authorFilter,
        bool favoriteOnly,
        bool showHidden,
        bool onlyHidden,
        string[] activeTags,
        CancellationToken token)
    {
        var filtered = allBooks.Where(book =>
        {
            token.ThrowIfCancellationRequested();
            if (onlyHidden)
            {
                if (!book.IsHidden) return false;
            }
            else if (book.IsHidden && !showHidden)
            {
                return false;
            }

            if (favoriteOnly && !book.IsFavorite)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(statusFilter)
                && !string.Equals(book.ReadingStatus, statusFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(authorFilter)
                && authorFilter != "全部作者"
                && !string.Equals(book.Author, authorFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (activeTags.Length > 0
                && !activeTags.All(activeTag =>
                    book.TagItems.Any(tag => string.Equals(tag.Name, activeTag, StringComparison.OrdinalIgnoreCase))))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(searchQuery))
            {
                return true;
            }

            return Contains(book.Title, searchQuery)
                || Contains(book.Author, searchQuery)
                || Contains(book.CharacterName, searchQuery)
                || Contains(book.ForeignName, searchQuery)
                || Contains(book.Tags, searchQuery)
                || Contains(book.Summary, searchQuery)
                || Contains(book.ProducedAt, searchQuery)
                || Contains(book.ImportedAt, searchQuery)
                || Contains(book.ReadingStatusText, searchQuery)
                || Contains(book.PageCountText, searchQuery)
                || Contains(book.SizeText, searchQuery)
                || Contains(book.IsFavorite ? "收藏" : "", searchQuery)
                || Contains(book.ReadCountText, searchQuery);
        });

        var sorted = SortBooks(filtered, sortIndex, sortDescending);

        return sorted.ToList();
    }

    private static IOrderedEnumerable<MangaBook> SortBooks(IEnumerable<MangaBook> filtered, int sortIndex, bool sortDescending)
    {
        return sortIndex switch
        {
            1 => sortDescending
                ? filtered.OrderByDescending(book => book.Rating).ThenBy(book => book.Title)
                : filtered.OrderBy(book => book.Rating).ThenBy(book => book.Title),
            2 => sortDescending
                ? filtered.OrderByDescending(book => book.PageCount).ThenBy(book => book.Title)
                : filtered.OrderBy(book => book.PageCount).ThenBy(book => book.Title),
            3 => sortDescending
                ? filtered.OrderByDescending(book => book.TotalBytes).ThenBy(book => book.Title)
                : filtered.OrderBy(book => book.TotalBytes).ThenBy(book => book.Title),
            4 => sortDescending
                ? filtered.OrderByDescending(book => book.ReadCount).ThenBy(book => book.Title)
                : filtered.OrderBy(book => book.ReadCount).ThenBy(book => book.Title),
            5 => sortDescending
                ? filtered.OrderByDescending(book => book.ImportedAt).ThenBy(book => book.Title)
                : filtered.OrderBy(book => book.ImportedAt).ThenBy(book => book.Title),
            6 => sortDescending
                ? filtered.OrderByDescending(book => book.ProducedAt).ThenBy(book => book.Title)
                : filtered.OrderBy(book => book.ProducedAt).ThenBy(book => book.Title),
            _ => sortDescending
                ? filtered.OrderByDescending(book => book.Title)
                : filtered.OrderBy(book => book.Title),
        };
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
            || ReadCountBookText is null
            || FilterSummaryText is null
            || ShelfEmptyState is null
            || ShelfEmptyHintText is null)
        {
            return;
        }

        var libraryCount = 0;
        var favoriteCount = 0;
        var readingNowCount = 0;
        var readCount = 0;
        var visibleCount = Books.Count;
        foreach (var book in _allBooks)
        {
            if (_cachedOnlyHidden)
            {
                if (!book.IsHidden) continue;
            }
            else if (book.IsHidden && !_cachedShowHidden)
            {
                continue;
            }

            libraryCount++;
            if (book.IsFavorite)
            {
                favoriteCount++;
            }
            if (book.ReadingStatus == "reading")
            {
                readingNowCount++;
            }
            if (book.ReadCount > 0)
            {
                readCount++;
            }
        }

        VisibleBookCountText.Text = $"{visibleCount} 本";
        TotalBookCountText.Text = _cachedShowHidden
            ? $"/ 共 {_allBooks.Count} 本"
            : $"/ 共 {libraryCount} 本";
        FavoriteCountText.Text = $"{favoriteCount} 本";
        ReadingNowCountText.Text = $"{readingNowCount} 本";
        ReadCountBookText.Text = $"{readCount} 本";
        FilterSummaryText.Text = BuildFilterSummary(visibleCount, libraryCount);

        var isEmpty = visibleCount == 0;
        ShelfEmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        ShelfEmptyHintText.Text = BuildEmptyHint();
        UpdateBatchSelectionState();
    }

    private List<MangaBook> GetVisibleBooks()
    {
        return Books.ToList();
    }

    private List<MangaBook> GetSelectedBatchBooks()
    {
        return _allBooks
            .Where(book => book.IsSelectedForBatch)
            .ToList();
    }

    private void UpdateBatchSelectionState()
    {
        if (BatchSelectionText is null)
        {
            return;
        }

        var selectedCount = _allBooks.Count(book => book.IsSelectedForBatch);
        BatchSelectionText.Text = selectedCount == 0 ? "0 本" : $"{selectedCount} 本";
        UpdateBatchSelectionModeVisuals(clearSelection: false);
    }

    private void UpdateBatchSelectionModeVisuals(bool clearSelection)
    {
        if (clearSelection)
        {
            ClearBatchSelection();
        }

        var showBatchTools = IsBatchSelectionMode && !_libraryChromeCollapsed;
        IsBatchSelectionUiVisible = showBatchTools;
        if (BatchManageShell is not null)
        {
            BatchManageShell.Visibility = showBatchTools ? Visibility.Visible : Visibility.Collapsed;
        }

        if (BatchModeToggleButton is not null)
        {
            BatchModeToggleButton.Content = IsBatchSelectionMode ? "退出多选" : "多选管理";
            BatchModeToggleButton.Background = IsBatchSelectionMode ? GetThemeBrush("Brush.TextPrimary") : GetThemeBrush("Brush.SurfaceMuted");
            BatchModeToggleButton.BorderBrush = IsBatchSelectionMode ? GetThemeBrush("Brush.TextPrimary") : GetThemeBrush("Brush.BorderSubtle");
            BatchModeToggleButton.Foreground = IsBatchSelectionMode ? System.Windows.Media.Brushes.White : GetThemeBrush("Brush.TextPrimary");
        }
    }

    private void ClearBatchSelection()
    {
        foreach (var book in _allBooks.Where(book => book.IsSelectedForBatch))
        {
            book.IsSelectedForBatch = false;
        }

        UpdateBatchSelectionState();
    }

    private void FillCurrentBookIfAffected(IReadOnlyCollection<MangaBook> affectedBooks)
    {
        if (_currentBook is not null && affectedBooks.Contains(_currentBook))
        {
            FillMetadataEditors(_currentBook);
        }
    }

    private static string GuessCommonTitlePrefix(IEnumerable<string> titles)
    {
        var prefix = titles
            .Select(title => title.Trim())
            .Where(title => title.Length > 0)
            .Select(title =>
            {
                var separators = new[] { " - ", "-", "_", "＿", "—", "－", "·", "】", "]" };
                foreach (var separator in separators)
                {
                    var index = title.IndexOf(separator, StringComparison.Ordinal);
                    if (index > 0)
                    {
                        return title[..(index + separator.Length)];
                    }
                }

                return "";
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key.Length)
            .FirstOrDefault();

        return prefix?.Key ?? "";
    }


    private string BuildFilterSummary(int visibleCount, int libraryCount)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_cachedSearchQuery))
        {
            parts.Add($"搜索“{_cachedSearchQuery}”");
        }

        if (!string.IsNullOrWhiteSpace(_cachedAuthorFilter) && _cachedAuthorFilter != "全部作者")
        {
            parts.Add($"作者 {_cachedAuthorFilter}");
        }

        if (!string.IsNullOrWhiteSpace(_cachedStatusFilter))
        {
            parts.Add($"状态 {MapStatusText(_cachedStatusFilter)}");
        }

        if (_cachedActiveTagFilters.Length > 0)
        {
            parts.Add($"Tag {string.Join(" + ", _cachedActiveTagFilters.OrderBy(tag => tag))}");
        }

        if (_cachedFavoriteOnly)
        {
            parts.Add("只看收藏");
        }

        if (_cachedShowHidden)
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
        if (!string.IsNullOrWhiteSpace(_cachedSearchQuery))
        {
            reasons.Add("搜索词");
        }
        if (!string.IsNullOrWhiteSpace(_cachedAuthorFilter) && _cachedAuthorFilter != "全部作者")
        {
            reasons.Add("作者筛选");
        }
        if (!string.IsNullOrWhiteSpace(_cachedStatusFilter))
        {
            reasons.Add("状态筛选");
        }
        if (_cachedActiveTagFilters.Length > 0)
        {
            reasons.Add("Tag 筛选");
        }
        if (_cachedFavoriteOnly)
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

        var homeBooks = _allBooks
            .Where(book => !book.IsHidden && !book.IsMissing && book.Pages.Count > 0)
            .ToList();

        ReplaceBooks(ContinueReadingBooks, homeBooks
            .Where(book => !string.IsNullOrEmpty(book.LastOpenedAt))
            .OrderByDescending(book => book.LastOpenedAt)
            .Take(3));

        var continueIds = ContinueReadingBooks.Select(b => b.Id).ToHashSet();
        ReplaceBooks(RecentReadingBooks, homeBooks
            .Where(book => book.ReadingStatus == "reading" && !continueIds.Contains(book.Id))
            .OrderByDescending(book => book.ReadCount)
            .ThenByDescending(book => book.LastReadPageIndex)
            .Take(4));

        ReplaceBooks(FavoriteShowcaseBooks, homeBooks
            .Where(book => book.IsFavorite)
            .OrderByDescending(book => book.ReadingStatus == "reading")
            .ThenByDescending(book => book.ReadCount)
            .ThenBy(book => book.Title)
            .Take(8));

        ReplaceBooks(RecentlyAddedBooks, homeBooks
            .OrderByDescending(book => book.ImportedAt)
            .ThenByDescending(book => book.ProducedAt)
            .Take(4));

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

    private DateTimeOffset _lastHomeRefreshAt = DateTimeOffset.MinValue;

    private void RefreshRecentReading_Click(object sender, RoutedEventArgs e)
    {
        if (DateTimeOffset.Now - _lastHomeRefreshAt < TimeSpan.FromSeconds(1))
        {
            ShowToast("刷新太快啦，稍后再试");
            return;
        }
        _lastHomeRefreshAt = DateTimeOffset.Now;

        var homeBooks = _allBooks
            .Where(book => !book.IsHidden && !book.IsMissing && book.Pages.Count > 0)
            .ToList();

        var continueIds = ContinueReadingBooks.Select(b => b.Id).ToHashSet();
        var candidates = homeBooks
            .Where(book => book.ReadingStatus == "reading" && !continueIds.Contains(book.Id))
            .ToList();

        if (candidates.Count == 0)
        {
            ShowToast("没有更多在读漫画了");
            return;
        }

        var random = new Random();
        var shuffled = candidates.OrderBy(_ => random.Next()).Take(4).ToList();
        ReplaceBooks(RecentReadingBooks, shuffled);
        RecentReadingEmptyText.Visibility = RecentReadingBooks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshFavorites_Click(object sender, RoutedEventArgs e)
    {
        if (DateTimeOffset.Now - _lastHomeRefreshAt < TimeSpan.FromSeconds(1))
        {
            ShowToast("刷新太快啦，稍后再试");
            return;
        }
        _lastHomeRefreshAt = DateTimeOffset.Now;

        var homeBooks = _allBooks
            .Where(book => !book.IsHidden && !book.IsMissing && book.Pages.Count > 0)
            .ToList();

        var favorites = homeBooks.Where(book => book.IsFavorite).ToList();
        if (favorites.Count <= 8)
        {
            ShowToast("收藏不足 8 本，无需换一批");
            return;
        }

        var random = new Random();
        var shuffled = favorites
            .OrderByDescending(book => book.ReadingStatus == "reading")
            .ThenBy(_ => random.Next())
            .Take(8)
            .ToList();
        ReplaceBooks(FavoriteShowcaseBooks, shuffled);
        FavoriteShowcaseEmptyText.Visibility = FavoriteShowcaseBooks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void ReplaceBooks(RangeObservableCollection<MangaBook> target, IEnumerable<MangaBook> source)
    {
        target.ReplaceRange(source.ToList());
    }

    private void ShowToast(string message, int durationMs = 1500)
    {
        if (ToastPanel is null || ToastText is null) return;
        ToastText.Text = message;
        _toastTimer?.Stop();
        MotionService.ShowWithFade(ToastPanel);
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            MotionService.HideWithFade(ToastPanel);
        };
        _toastTimer.Start();
    }

    private void OpenBook(MangaBook book)
    {
        if (book.IsMissing || book.Pages.Count == 0)
        {
            StatusText.Text = "这本漫画路径失效或没有可阅读图片。";
            return;
        }

        book.LastOpenedAt = DateTimeOffset.Now.ToString("O");
        AppLogger.Info("reader-open", $"Opening reader: {book.Title}, pages={book.Pages.Count}, folder={book.FolderPath}");
        var reader = new ReaderWindow(
            book,
            _database,
            _nextKeys,
            _prevKeys,
            ResolveNextBookRecommendations,
            nextBook => Dispatcher.InvokeAsync(() => OpenBookFromRecommendation(nextBook), DispatcherPriority.ApplicationIdle),
            _coverPipeline,
            openDetailRequest: OpenBookDetailFromReader)
        {
            Owner = this
        };
        reader.Closed += (_, _) =>
        {
            book.NotifyAll();
            ApplyBookSort(refresh: false);
            RefreshBookFilter();
            RefreshHomeShelves();
            if (BooksList.SelectedItem is MangaBook selected && Books.Contains(selected))
                _ = Dispatcher.InvokeAsync(() => BooksList.ScrollIntoView(selected), DispatcherPriority.ApplicationIdle);
        };
        reader.Show();
    }

    private void OpenBookFromRecommendation(MangaBook book)
    {
        _currentBook = book;
        if (Books.Contains(book))
        {
            BooksList.SelectedItem = book;
            _ = Dispatcher.InvokeAsync(() => BooksList.ScrollIntoView(book), DispatcherPriority.ApplicationIdle);
        }
        OpenBook(book);
    }

    private void OpenBookDetailFromReader(MangaBook book)
    {
        _currentBook = book;
        if (Books.Contains(book))
        {
            BooksList.SelectedItem = book;
            _ = Dispatcher.InvokeAsync(() => BooksList.ScrollIntoView(book), DispatcherPriority.ApplicationIdle);
        }
        FillMetadataEditors(book);
        SetEditMode(false);
        SetDetailVisible(true);
    }

    private MangaBook? ResolveNextBookInCurrentView(MangaBook currentBook)
    {
        var visibleBooks = GetVisibleBooks()
            .Where(book => !book.IsMissing && book.Pages.Count > 0)
            .ToList();
        var currentIndex = visibleBooks.FindIndex(book => ReferenceEquals(book, currentBook) || book.Id == currentBook.Id);
        if (currentIndex < 0 || currentIndex + 1 >= visibleBooks.Count)
        {
            return null;
        }

        return visibleBooks[currentIndex + 1];
    }

    private NextBookRecommendations? ResolveNextBookRecommendations(MangaBook currentBook)
    {
        var nextInView = ResolveNextBookInCurrentView(currentBook);

        var candidates = _allBooks
            .Where(b => !b.IsMissing && b.Pages.Count > 0 && b.Id != currentBook.Id)
            .ToList();

        if (candidates.Count == 0)
            return new NextBookRecommendations(nextInView, null, null);

        var random = new Random();

        var sameAuthorBooks = candidates
            .Where(b => !string.IsNullOrWhiteSpace(b.Author)
                        && b.Author.Equals(currentBook.Author, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var sameAuthor = sameAuthorBooks.Count > 0
            ? sameAuthorBooks[random.Next(sameAuthorBooks.Count)]
            : null;

        var currentTags = TagService.ParseTags(currentBook.Tags).ToHashSet(StringComparer.OrdinalIgnoreCase);

        MangaBook? similarTags = null;
        if (currentTags.Count > 0)
        {
            var tagScored = candidates
                .Where(b => b.Id != sameAuthor?.Id)
                .Select(b =>
                {
                    var bookTags = TagService.ParseTags(b.Tags).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    bookTags.IntersectWith(currentTags);
                    return new { Book = b, Score = bookTags.Count };
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(_ => random.Next())
                .FirstOrDefault();

            similarTags = tagScored?.Book;
        }

        similarTags ??= sameAuthor ?? candidates[random.Next(candidates.Count)];

        return new NextBookRecommendations(nextInView, sameAuthor, similarTags);
    }

    private void HomeBook_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(source) is not null)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: MangaBook book })
        {
            OpenBookDetailFromHome(book);
            e.Handled = true;
        }
    }

    private void HomeBookButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MangaBook book })
        {
            OpenBookDetailFromHome(book);
            e.Handled = true;
        }
    }

    private void OpenBookDetailFromHome(MangaBook book)
    {
        ShowLibraryView("library");
        ResetLibraryFilters();

        _currentBook = book;
        BooksList.SelectedItem = Books.Contains(book) ? book : null;
        if (BooksList.SelectedItem is not null)
        {
            BooksList.ScrollIntoView(book);
        }

        FillMetadataEditors(book);
        SetEditMode(false);
        SetDetailVisible(true);
        StatusText.Text = $"已打开详情：{book.Title}";

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (Books.Contains(book))
            {
                BooksList.SelectedItem = book;
                BooksList.ScrollIntoView(book);
            }
        }, DispatcherPriority.ApplicationIdle);
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

    private void NavAuthors_Click(object sender, RoutedEventArgs e)
    {
        ShowAuthorsView();
    }

    private void ShowHomeView()
    {
        _currentNavigationKey = "home";
        if (HomePagePanel is not null) MotionService.ShowWithFade(HomePagePanel);
        if (LibraryPagePanel is not null) MotionService.HideWithFade(LibraryPagePanel);
        if (TagsPagePanel is not null) MotionService.HideWithFade(TagsPagePanel);
        if (AuthorsPagePanel is not null) MotionService.HideWithFade(AuthorsPagePanel);
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
        if (AuthorsPagePanel is not null) MotionService.HideWithFade(AuthorsPagePanel);
        SetDetailVisible(_currentBook is not null && BooksList.SelectedItem is not null);
        UpdateNavigationVisuals();
        RefreshBookFilter(ensureLibraryView: true);
    }

    private void ShowTagsView()
    {
        _currentNavigationKey = "tags";
        if (HomePagePanel is not null) MotionService.HideWithFade(HomePagePanel);
        if (LibraryPagePanel is not null) MotionService.HideWithFade(LibraryPagePanel);
        if (TagsPagePanel is not null) MotionService.ShowWithFade(TagsPagePanel);
        if (AuthorsPagePanel is not null) MotionService.HideWithFade(AuthorsPagePanel);
        SetDetailVisible(false);
        RefreshLibraryViews(tags: false, tagManager: true, authors: false, filter: false);
        UpdateNavigationVisuals();
    }

    private void ShowAuthorsView()
    {
        _currentNavigationKey = "authors";
        if (HomePagePanel is not null) MotionService.HideWithFade(HomePagePanel);
        if (LibraryPagePanel is not null) MotionService.HideWithFade(LibraryPagePanel);
        if (TagsPagePanel is not null) MotionService.HideWithFade(TagsPagePanel);
        if (AuthorsPagePanel is not null) MotionService.ShowWithFade(AuthorsPagePanel);
        SetDetailVisible(false);
        RefreshAuthorManagementItems();
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
        OnlyHiddenBox.IsChecked = false;
        RefreshLibraryViews(tagManager: false, authors: false, sort: false, activeTags: true);
    }

    private void EnsureLibraryViewCanShowBooks()
    {
        if (_allBooks.Count == 0)
        {
            return;
        }

        if (Books.Count > 0)
        {
            return;
        }

        if (HasActiveLibraryFilter())
        {
            ResetLibraryFilters();
            StatusText.Text = "书库中有漫画，但当前筛选没有命中，已自动回到全部漫画。";
            return;
        }

        ShelfEmptyHintText.Text = $"已识别 {_allBooks.Count} 本漫画，但视图没有显示。请重新扫描书库；如果仍为空，这是列表视图刷新问题。";
    }

    private void SetAuthorFilter(string authorName)
    {
        if (AuthorFilterBox is not null && AuthorFilters.Contains(authorName))
        {
            AuthorFilterBox.SelectedItem = authorName;
            RefreshBookFilter();
        }
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
            || OnlyHiddenBox?.IsChecked == true;
    }

    private void UpdateNavigationVisuals()
    {
        SetNavButtonState(HomeNavButton, _currentNavigationKey == "home");
        SetNavButtonState(LibraryNavButton, _currentNavigationKey == "library");
        SetNavButtonState(TagsNavButton, _currentNavigationKey == "tags");
        SetNavButtonState(AuthorsNavButton, _currentNavigationKey == "authors");
    }

    private static void SetNavButtonState(System.Windows.Controls.Button? button, bool active)
    {
        if (button is null)
        {
            return;
        }

        button.Tag = active ? "active" : "";
    }

    private void FastVerticalScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TryScrollNestedHorizontalShelf(sender, e))
        {
            return;
        }

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

    private static bool TryScrollNestedHorizontalShelf(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || sender is not System.Windows.Controls.ScrollViewer outerViewer)
        {
            return false;
        }

        var nestedViewer = FindAncestor<System.Windows.Controls.ScrollViewer>(source);
        if (nestedViewer is null || ReferenceEquals(nestedViewer, outerViewer) || nestedViewer.ScrollableWidth <= 0)
        {
            return false;
        }

        var nextOffset = ClampOffset(nestedViewer.HorizontalOffset - e.Delta * WheelScrollMultiplier, nestedViewer.ScrollableWidth);
        if (Math.Abs(nextOffset - nestedViewer.HorizontalOffset) < 0.1)
        {
            return false;
        }

        nestedViewer.ScrollToHorizontalOffset(nextOffset);
        e.Handled = true;
        return true;
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
            var book = _currentBook;
            _ = Task.Run(() => _database.SaveProgress(book));
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

    private void SyncBookTagChips(MangaBook book)
    {
        var tags = TagService.ParseTags(book.Tags).ToList();
        var current = book.TagItems.ToList();
        var tagItemsMatch = current.Count == tags.Count;
        if (tagItemsMatch)
        {
            for (var i = 0; i < tags.Count; i++)
            {
                var tag = tags[i];
                var item = current[i];
                if (!string.Equals(item.Name, tag, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(item.Category, TagCategory(tag), StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(item.Color, TagColor(tag), StringComparison.OrdinalIgnoreCase))
                {
                    tagItemsMatch = false;
                    break;
                }
            }
        }

        var cardTagItemsMatch = BookCardTagChipsMatch(book);
        if (tagItemsMatch && cardTagItemsMatch)
        {
            return;
        }

        if (!tagItemsMatch)
        {
            book.TagItems.Clear();
            var newTagChips = new List<TagChip>(tags.Count);
            foreach (var tag in tags)
            {
                newTagChips.Add(new TagChip
                {
                    Name = tag,
                    Category = TagCategory(tag),
                    Color = TagColor(tag)
                });
            }
            book.TagItems.AddRange(newTagChips);
        }

        if (!cardTagItemsMatch)
        {
            SyncBookCardTagChips(book);
        }
    }

    private bool BookCardTagChipsMatch(MangaBook book)
    {
        foreach (var item in book.CardTagItems)
        {
            var expectedCategory = IsCardTagSummary(item.Name) ? item.Category : TagCategory(item.Name);
            var expectedColor = IsCardTagSummary(item.Name) ? "#E5E7EB" : TagColor(item.Name);
            if (!string.Equals(item.Category, expectedCategory, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(item.Color, expectedColor, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void SyncBookCardTagChips(MangaBook book)
    {
        var current = book.CardTagItems.ToList();
        book.CardTagItems.Clear();
        var newCardChips = new List<TagChip>(current.Count);
        foreach (var item in current)
        {
            var isSummary = IsCardTagSummary(item.Name);
            newCardChips.Add(new TagChip
            {
                Name = item.Name,
                Category = isSummary ? item.Category : TagCategory(item.Name),
                Color = isSummary ? "#E5E7EB" : TagColor(item.Name)
            });
        }
        book.CardTagItems.AddRange(newCardChips);
    }

    private static bool IsCardTagSummary(string value)
    {
        return value.StartsWith("+", StringComparison.Ordinal);
    }

    private IEnumerable<string> EnumerateKnownTags()
    {
        return DefaultTagPresets.Select(tag => tag.Name)
            .Concat(_managedTags)
            .Concat(_allBooks.SelectMany(book => book.TagItems.Select(tag => tag.Name)))
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Where(tag => !_suppressedTags.Contains(tag) || GetTagUsageCount(tag) > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private void RebuildTagIndex()
    {
        _tagBooksByName.Clear();
        foreach (var book in _allBooks)
        {
            SyncBookTagChips(book);
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
        _tagGroupFilterOptionsDirty = true;
        _managedTags.Clear();
        _managedTagCategories.Clear();
        _managedTagIsExclusive.Clear();
        _managedTagUpdatedAt.Clear();
        _managedTagColors.Clear();
        foreach (var tag in _database.LoadManagedTags().Where(tag => !string.IsNullOrWhiteSpace(tag.Name)))
        {
            _managedTags.Add(tag.Name);
            _managedTagCategories[tag.Name] = tag.Category;
            _managedTagIsExclusive[tag.Name] = tag.IsExclusive;
            _managedTagUpdatedAt[tag.Name] = tag.UpdatedAt;
            if (!string.IsNullOrWhiteSpace(tag.Color))
            {
                _managedTagColors[tag.Name] = tag.Color;
            }
        }

        _suppressedTags.Clear();
        foreach (var tag in _database.LoadSuppressedTags().Where(tag => !string.IsNullOrWhiteSpace(tag)))
        {
            _suppressedTags.Add(tag);
        }
    }

    private void ApplyManagedTags(IReadOnlyList<LibraryDatabase.ManagedTagRecord> tags, IReadOnlyList<string> suppressedTags)
    {
        _tagGroupFilterOptionsDirty = true;
        _managedTags.Clear();
        _managedTagCategories.Clear();
        _managedTagIsExclusive.Clear();
        _managedTagUpdatedAt.Clear();
        _managedTagColors.Clear();
        foreach (var tag in tags.Where(tag => !string.IsNullOrWhiteSpace(tag.Name)))
        {
            _managedTags.Add(tag.Name);
            _managedTagCategories[tag.Name] = tag.Category;
            _managedTagIsExclusive[tag.Name] = tag.IsExclusive;
            _managedTagUpdatedAt[tag.Name] = tag.UpdatedAt;
            if (!string.IsNullOrWhiteSpace(tag.Color))
            {
                _managedTagColors[tag.Name] = tag.Color;
            }
        }
        _suppressedTags.Clear();
        foreach (var tag in suppressedTags.Where(tag => !string.IsNullOrWhiteSpace(tag)))
        {
            _suppressedTags.Add(tag);
        }
    }

    private void ApplyCustomTagColors(string raw)
    {
        _customTagColors.Clear();
        foreach (var color in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsValidHexColor))
        {
            _customTagColors.Add(color);
        }
    }

    private void ApplyManagedAuthors(IReadOnlyList<string> authors)
    {
        _managedAuthors.Clear();
        foreach (var author in authors.Select(author => author.Trim()).Where(author => !string.IsNullOrWhiteSpace(author)))
        {
            _managedAuthors.Add(author);
        }
    }

    private void ApplyShortcuts(Dictionary<string, string> shortcuts)
    {
        if (shortcuts.TryGetValue("reader.next", out var next))
        {
            _nextKeys = ParseKeys(next);
        }
        if (shortcuts.TryGetValue("reader.previous", out var previous))
        {
            _prevKeys = ParseKeys(previous);
        }
    }

    private void SaveCustomTagColors(IEnumerable<string> colors)
    {
        var changed = false;
        foreach (var color in colors.Where(IsValidHexColor))
        {
            changed |= _customTagColors.Add(color);
        }

        if (!changed)
        {
            return;
        }

        var value = string.Join(";", _customTagColors.OrderBy(color => color, StringComparer.OrdinalIgnoreCase));
        _database.SaveSetting(CustomTagColorsSettingKey, value);
    }

    private static bool IsValidHexColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 7 || value[0] != '#')
        {
            return false;
        }

        return value.Skip(1).All(Uri.IsHexDigit);
    }

    private void UpsertManagedTag(string tag, string? category = null, bool? isExclusive = null, string? color = null)
    {
        var resolvedCategory = category ?? TagCategory(tag);
        var resolvedExclusive = isExclusive ?? IsExclusiveTag(tag);
        var requestedColor = color ?? (_managedTagColors.TryGetValue(tag, out var existing) ? existing : "");
        var resolvedColor = !string.IsNullOrWhiteSpace(requestedColor)
            ? requestedColor
            : ResolveCategoryColor(resolvedCategory, tag) ?? TagService.GetColor(tag);
        _tagGroupFilterOptionsDirty = true;
        ApplyCategoryColorLocally(resolvedCategory, resolvedColor, tag, resolvedExclusive);
        _ = Task.Run(() => SaveCategoryColor(resolvedCategory, resolvedColor, tag, resolvedExclusive));
    }

    private List<string> EnumerateTagsInCategory(string category, string? requiredTag = null)
    {
        return EnumerateKnownTags()
            .Append(requiredTag ?? "")
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Where(tag => string.Equals(TagCategory(tag), category, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(requiredTag) && string.Equals(tag, requiredTag, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ApplyCategoryColorLocally(string category, string color, string? primaryTag = null, bool? primaryIsExclusive = null)
    {
        var updatedAt = DateTimeOffset.Now.ToString("O");
        foreach (var tag in EnumerateTagsInCategory(category, primaryTag))
        {
            _suppressedTags.Remove(tag);
            _managedTags.Add(tag);
            _managedTagCategories[tag] = category;
            _managedTagIsExclusive[tag] = primaryTag is not null && string.Equals(tag, primaryTag, StringComparison.OrdinalIgnoreCase)
                ? primaryIsExclusive ?? IsExclusiveTag(tag)
                : IsExclusiveTag(tag);
            _managedTagUpdatedAt[tag] = updatedAt;
            if (!string.IsNullOrWhiteSpace(color))
            {
                _managedTagColors[tag] = color;
            }
        }
    }

    private void SaveCategoryColor(string category, string color, string? primaryTag = null, bool? primaryIsExclusive = null)
    {
        foreach (var tag in EnumerateTagsInCategory(category, primaryTag))
        {
            var isExclusive = primaryTag is not null && string.Equals(tag, primaryTag, StringComparison.OrdinalIgnoreCase)
                ? primaryIsExclusive ?? IsExclusiveTag(tag)
                : IsExclusiveTag(tag);
            _database.SaveManagedTag(tag, category, isExclusive, color);
        }
    }

    private Dictionary<string, string> BuildTagCategoryColorMap(string? excludingTag = null)
    {
        var colors = DefaultTagPresets
            .GroupBy(tag => tag.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Color, StringComparer.OrdinalIgnoreCase);

        foreach (var tag in _managedTags.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(excludingTag) && string.Equals(tag, excludingTag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var category = TagCategory(tag);
            if (_managedTagColors.TryGetValue(tag, out var color)
                && !string.IsNullOrWhiteSpace(color))
            {
                colors[category] = color;
            }
        }

        return colors;
    }

    private Dictionary<string, int> BuildTagCategoryCountMap()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in _managedTags)
        {
            var category = TagCategory(tag);
            counts[category] = counts.GetValueOrDefault(category) + 1;
        }
        return counts;
    }

    private string? ResolveCategoryColor(string category, string? excludingTag = null)
    {
        return BuildTagCategoryColorMap(excludingTag).TryGetValue(category, out var color) ? color : null;
    }

    private bool TryResolveTagForCreate(string initialValue, out string tag, out string category, out bool isExclusive, out string color)
    {
        tag = "";
        category = "自定义";
        isExclusive = false;
        color = "";

        if (EnumerateKnownTags().Any(name => string.Equals(name, initialValue, StringComparison.OrdinalIgnoreCase)))
        {
            tag = initialValue.Trim();
            category = TagCategory(tag);
            isExclusive = IsExclusiveTag(tag);
            color = TagColor(tag);
            return true;
        }

        var dialog = new TagCreateDialog(initialValue, EnumerateKnownTagCategories(), BuildTagCategoryColorMap(), BuildTagCategoryCountMap(), _customTagColors) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.TagName))
        {
            StatusText.Text = "没有创建标签。";
            return false;
        }

        SaveCustomTagColors(dialog.CustomColors);
        tag = dialog.TagName;
        category = dialog.TagCategory;
        isExclusive = dialog.IsExclusive;
        color = dialog.SelectedColor;
        return true;
    }

    private bool TryResolveTagForBatchAdd(
        IReadOnlyList<MangaBook> selectedBooks,
        out string tag,
        out string category,
        out bool isExclusive,
        out string color)
    {
        var initialValue = TagSearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(initialValue))
        {
            initialValue = _activeTagFilters.LastOrDefault()
                ?? selectedBooks
                    .SelectMany(book => TagService.ParseTags(book.Tags))
                    .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key)
                    .Select(group => group.Key)
                    .FirstOrDefault()
                ?? EnumerateKnownTags().FirstOrDefault()
                ?? "";
        }

        if (!string.IsNullOrWhiteSpace(initialValue)
            && EnumerateKnownTags().Any(name => string.Equals(name, initialValue, StringComparison.OrdinalIgnoreCase)))
        {
            tag = initialValue.Trim();
            category = TagCategory(tag);
            isExclusive = IsExclusiveTag(tag);
            color = TagColor(tag);
            return true;
        }

        var dialog = new RenameDialog(
            "批量加 Tag",
            "输入已有 Tag 名称会直接使用；输入新名称则进入创建流程。",
            "处理范围",
            $"{selectedBooks.Count} 本漫画",
            "Tag 名称",
            initialValue)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            tag = "";
            category = "自定义";
            isExclusive = false;
            color = "";
            StatusText.Text = "已取消批量添加 Tag。";
            return false;
        }

        var requestedTag = dialog.NewName.Trim();
        if (EnumerateKnownTags().Any(name => string.Equals(name, requestedTag, StringComparison.OrdinalIgnoreCase)))
        {
            tag = requestedTag;
            category = TagCategory(tag);
            isExclusive = IsExclusiveTag(tag);
            color = TagColor(tag);
            return true;
        }

        return TryResolveTagForCreate(requestedTag, out tag, out category, out isExclusive, out color);
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

    private string TagColor(string tag)
    {
        return ResolveCategoryColor(TagCategory(tag)) ?? TagService.GetColor(tag);
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
                Color = TagColor(tag),
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

        ActiveTagFilters.ReplaceRange(chips);

        ActiveTagSummaryText.Text = chips.Count == 0
            ? "已选 0 个 Tag"
            : $"已选 {chips.Count} 个 Tag";
        ActiveTagFilterList.Visibility = chips.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool _isRefreshingTagFilter;

    private void TagManagerFilter_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isRefreshingTagFilter) return;
        RefreshTagManagementItems();
    }

    private void RefreshTagManagementItems()
    {
        var query = TagManagerSearchBox?.Text.Trim() ?? "";
        var sortMode = TagSortBox?.SelectedIndex ?? 0;

        // Remember current selection before rebuilding items.
        var previousGroupFilter = TagGroupFilterBox?.SelectedIndex > 0
            ? (TagGroupFilterBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string ?? ""
            : "";

        var knownTags = EnumerateKnownTags().ToList();

        // Rebuild group filter options only when tag data changed. Rebuilding on every
        // SelectionChanged clears the drop-down while the user is selecting an item.
        if (_tagGroupFilterOptionsDirty && TagGroupFilterBox is not null)
        {
            _isRefreshingTagFilter = true;
            try
            {
                TagGroupFilterBox.Items.Clear();
                TagGroupFilterBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "全部分组" });
                var categories = knownTags
                    .Select(t => TagCategory(t).Trim())
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(TagCategoryOrder)
                    .ThenBy(c => c, StringComparer.OrdinalIgnoreCase);
                foreach (var cat in categories)
                {
                    TagGroupFilterBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = cat });
                }

                // Restore previous selection if still present
                var restoredIndex = 0;
                if (!string.IsNullOrEmpty(previousGroupFilter))
                {
                    for (int i = 1; i < TagGroupFilterBox.Items.Count; i++)
                    {
                        if (TagGroupFilterBox.Items[i] is System.Windows.Controls.ComboBoxItem item
                            && string.Equals(item.Content as string, previousGroupFilter, StringComparison.OrdinalIgnoreCase))
                        {
                            restoredIndex = i;
                            break;
                        }
                    }
                }
                TagGroupFilterBox.SelectedIndex = restoredIndex;
                _tagGroupFilterOptionsDirty = false;
            }
            finally
            {
                _isRefreshingTagFilter = false;
            }
        }

        var groupFilter = TagGroupFilterBox?.SelectedIndex > 0
            ? (TagGroupFilterBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string ?? ""
            : "";

        // Filter by search query
        var filtered = knownTags
            .Select(tag => CreateTagChip(tag))
            .Where(tag => string.IsNullOrWhiteSpace(query)
                || tag.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || tag.Category.Contains(query, StringComparison.OrdinalIgnoreCase));

        // Filter by group
        if (!string.IsNullOrWhiteSpace(groupFilter))
        {
            filtered = filtered.Where(tag =>
                string.Equals(tag.Category, groupFilter, StringComparison.OrdinalIgnoreCase));
        }

        // Sort
        var sorted = sortMode switch
        {
            1 => filtered.OrderByDescending(tag => tag.UsageCount).ThenBy(tag => tag.Name).ToList(),
            2 => filtered.OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => filtered.OrderBy(tag => TagCategoryOrder(tag.Category))
                         .ThenByDescending(tag => tag.UsageCount)
                         .ThenBy(tag => tag.Name).ToList(),
        };

        TagManagerItems.ReplaceRange(sorted);

        if (TagManagerTotalCountText is not null)
        {
            TagManagerTotalCountText.Text = $"{knownTags.Count} 个";
        }
        if (TagManagerUsedCountText is not null)
        {
            TagManagerUsedCountText.Text = $"{knownTags.Count(tag => GetTagUsageCount(tag) > 0)} 个";
        }
        if (TagManagerStandaloneCountText is not null)
        {
            TagManagerStandaloneCountText.Text = $"{knownTags.Count(tag => GetTagUsageCount(tag) == 0)} 个";
        }
        if (TagManagerGroupCountText is not null)
        {
            TagManagerGroupCountText.Text = $"{knownTags.Select(TagCategory).Distinct(StringComparer.OrdinalIgnoreCase).Count()} 组";
        }
        if (TagManagerEmptyState is not null)
        {
            TagManagerEmptyState.Visibility = sorted.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void RefreshAuthorManagementItems(string? filter = null)
    {
        var bookCounts = _allBooks
            .Where(b => !string.IsNullOrWhiteSpace(b.Author))
            .GroupBy(b => b.Author, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var author in _managedAuthors)
        {
            bookCounts.TryAdd(author, 0);
        }

        var query = bookCounts.Select(item => new AuthorItem { Name = item.Key, BookCount = item.Value });

        if (!string.IsNullOrWhiteSpace(filter))
        {
            query = query.Where(a => a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        var sorted = query.OrderBy(a => a.Name).ToList();
        AuthorManagerItems.ReplaceRange(sorted);

        if (AuthorTotalText is not null)
        {
            AuthorTotalText.Text = $"{AuthorManagerItems.Count} 位";
        }
        if (AuthorBookTotalText is not null)
        {
            AuthorBookTotalText.Text = $"{_allBooks.Count(b => !string.IsNullOrWhiteSpace(b.Author))} 本";
        }
        if (AuthorManagerEmptyState is not null)
        {
            AuthorManagerEmptyState.Visibility = AuthorManagerItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private IEnumerable<string> EnumerateKnownAuthors()
    {
        return _allBooks
            .Select(book => book.Author.Trim())
            .Where(author => !string.IsNullOrWhiteSpace(author))
            .Concat(_managedAuthors)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private int CountBooksByAuthor(string author)
    {
        return _allBooks.Count(book => string.Equals(book.Author, author, StringComparison.OrdinalIgnoreCase));
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
            return IsPollutedTagCategory(tag, category) ? "自定义" : category;
        }

        return TagService.GetCategory(tag);
    }

    private IEnumerable<string> EnumerateKnownTagCategories()
    {
        return DefaultTagPresets.Select(tag => tag.Category)
            .Concat(_managedTagCategories.Select(item => IsPollutedTagCategory(item.Key, item.Value) ? "自定义" : item.Value))
            .Append("自定义")
            .Select(category => category.Trim())
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private bool IsPollutedTagCategory(string tag, string category)
    {
        var trimmed = category.Trim();
        return string.Equals(trimmed, tag, StringComparison.OrdinalIgnoreCase)
            || (_managedTags.Contains(trimmed)
                && !DefaultTagPresets.Any(preset => string.Equals(preset.Category, trimmed, StringComparison.OrdinalIgnoreCase)));
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

    private async void CreateAuthor_Click(object sender, RoutedEventArgs e)
    {
        var initialValue = AuthorSearchBox?.Text?.Trim() ?? "";
        var dialog = new RenameDialog("新增作者", "创建一个暂未关联书籍的作者条目，后续导入或改名会自动合并。", "类型", "独立作者", "作者名称", initialValue)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var authorName = dialog.NewName.Trim();
        if (EnumerateKnownAuthors().Any(author => string.Equals(author, authorName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = $"作者已存在：{authorName}";
            return;
        }

        _managedAuthors.Add(authorName);
        await Task.Run(() => _database.SaveManagedAuthor(authorName));

        if (AuthorSearchBox is not null)
        {
            AuthorSearchBox.Text = "";
        }

        RefreshAuthorFilters();
        RefreshAuthorManagementItems();
        StatusText.Text = $"已新增作者：{authorName}";
    }

    private async void RenameAuthor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AuthorItem item })
        {
            return;
        }

        var dialog = new RenameDialog(item.Name) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.NewName == item.Name)
        {
            return;
        }

        var booksToUpdate = _allBooks
            .Where(b => string.Equals(b.Author, item.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var updates = booksToUpdate.Select(b => (b.Id, dialog.NewName)).ToList();

        var shouldRenameManagedAuthor = _managedAuthors.Contains(item.Name) || updates.Count == 0;
        await Task.Run(() =>
        {
            _database.SaveBookAuthorsBatch(updates, "rename-author");
            if (shouldRenameManagedAuthor)
            {
                _database.RenameManagedAuthor(item.Name, dialog.NewName);
            }
        });
        foreach (var book in booksToUpdate)
        {
            book.Author = dialog.NewName;
            book.NotifyAll();
        }

        if (shouldRenameManagedAuthor)
        {
            _managedAuthors.Remove(item.Name);
            _managedAuthors.Add(dialog.NewName);
        }

        RefreshLibraryViews(sort: true);
        RefreshAuthorManagementItems(AuthorSearchBox?.Text?.Trim());
        StatusText.Text = $@"已将「{item.Name}」重命名为「{dialog.NewName}」，更新了 {updates.Count} 本书籍。";
    }

    private void FilterByAuthor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AuthorItem item })
        {
            return;
        }

        ShowLibraryView("author");
        SetAuthorFilter(item.Name);
        StatusText.Text = $@"已在书库按作者查看：{item.Name}";
    }

    private void DetailAuthorFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null || string.IsNullOrWhiteSpace(_currentBook.Author))
        {
            return;
        }

        ShowLibraryView("author");
        SetAuthorFilter(_currentBook.Author);
        SetDetailVisible(false);
        StatusText.Text = $@"已在书库按作者查看：{_currentBook.Author}";
    }

    private async void DeleteAuthor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AuthorItem item })
        {
            return;
        }

        var booksToUpdate = _allBooks
            .Where(book => string.Equals(book.Author, item.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var result = System.Windows.MessageBox.Show(
            $"确定删除作者“{item.Name}”吗？\n\n会清空 {booksToUpdate.Count} 本漫画的作者字段，并从作者管理中移除该作者。",
            "删除作者",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var updates = booksToUpdate.Select(book => (book.Id, "")).ToList();
        await Task.Run(() =>
        {
            _database.SaveBookAuthorsBatch(updates, "delete-author");
            _database.DeleteManagedAuthor(item.Name);
        });

        foreach (var book in booksToUpdate)
        {
            book.Author = "";
            book.NotifyAll();
        }
        _managedAuthors.Remove(item.Name);

        RefreshLibraryViews(sort: true);
        RefreshAuthorFilters();
        RefreshAuthorManagementItems(AuthorSearchBox?.Text?.Trim());
        StatusText.Text = $"已删除作者：{item.Name}，清空 {booksToUpdate.Count} 本漫画的作者字段。";
    }

    private void TagManagerFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TagChip chip })
        {
            return;
        }
        ShowLibraryView("library");
        if (!_activeTagFilters.Contains(chip.Name))
        {
            _activeTagFilters.Add(chip.Name);
        }
        RefreshLibraryViews(tagManager: false, authors: false, activeTags: true);
        StatusText.Text = chip.UsageCount == 0
            ? $"已按 Tag 筛选：{chip.Name}。当前没有关联漫画，所以结果为空。"
            : $"已在书库按 Tag 查看：{chip.Name}";
    }

    private void TagUsage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        TagManagerFilter_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void OpenTagManagerForTag(string tagName)
    {
        ShowTagsView();
        if (TagManagerSearchBox is not null)
        {
            TagManagerSearchBox.Text = tagName;
        }
    }

    private async void EditTagAcrossLibrary(TagChip chip)
    {
        var relatedBooks = _allBooks
            .Where(book => book.TagItems.Any(item => string.Equals(item.Name, chip.Name, StringComparison.OrdinalIgnoreCase)))
            .Take(3)
            .ToList();
        var dialog = new TagEditDialog(chip, relatedBooks, EnumerateKnownTagCategories(), BuildTagCategoryColorMap(chip.Name), _customTagColors) { Owner = this };
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
        var newColor = dialog.SelectedColor;
        SaveCustomTagColors(dialog.CustomColors);
        if (string.IsNullOrWhiteSpace(newName))
        {
            StatusText.Text = "标签名不能为空。";
            return;
        }

        if (string.Equals(chip.Name, newName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(chip.Category, newCategory, StringComparison.OrdinalIgnoreCase)
            && chip.IsExclusive == newIsExclusive
            && string.Equals(chip.Color, newColor, StringComparison.OrdinalIgnoreCase))
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
            foreach (var book in _allBooks)
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

        var tagBatchData = affectedBooks.Select(item => (item.Book.Id, item.Tags)).ToList();
        var tagBatchReason = renamedTag ? "before-tag-rename" : "before-tag-regroup";
        var doRename = renamedTag && _managedTags.Remove(chip.Name);
        var doSuppress = !doRename && renamedTag && chip.IsBuiltIn;

        await Task.Run(() =>
        {
            _database.SaveBookTagsBatch(tagBatchData, tagBatchReason);
            if (doRename)
            {
                _database.RenameManagedTag(chip.Name, newName, newCategory, newIsExclusive, newColor);
            }
            else
            {
                if (doSuppress)
                {
                    _database.SuppressTag(chip.Name);
                }
                _database.SaveManagedTag(newName, newCategory, newIsExclusive, newColor);
            }
            SaveCategoryColor(newCategory, newColor, newName, newIsExclusive);
        });

        if (doRename)
        {
            _managedTagCategories.Remove(chip.Name);
            _managedTagIsExclusive.Remove(chip.Name);
            _managedTagUpdatedAt.Remove(chip.Name);
            _managedTagColors.Remove(chip.Name);
        }
        else if (doSuppress)
        {
            _suppressedTags.Add(chip.Name);
        }
        _managedTags.Add(newName);
        _managedTagCategories[newName] = newCategory;
        _managedTagIsExclusive[newName] = newIsExclusive;
        _managedTagUpdatedAt[newName] = DateTimeOffset.Now.ToString("O");
        _tagGroupFilterOptionsDirty = true;
        ApplyCategoryColorLocally(newCategory, newColor, newName, newIsExclusive);

        foreach (var (book, tags) in affectedBooks)
        {
            book.Tags = tags;
            book.NotifyAll();
        }

        if (_activeTagFilters.Remove(chip.Name))
        {
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

    private async void DeleteTagAcrossLibrary(TagChip chip)
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
        foreach (var book in _allBooks)
        {
            var tags = book.TagItems.Select(item => item.Name).ToList();
            if (!tags.Any(tag => string.Equals(tag, chip.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var remaining = tags.Where(tag => !string.Equals(tag, chip.Name, StringComparison.OrdinalIgnoreCase));
            affectedBooks.Add((book, string.Join(", ", remaining)));
        }

        var tagBatchData = affectedBooks.Select(item => (item.Book.Id, item.Tags)).ToList();
        var isBuiltIn = chip.IsBuiltIn;
        var chipName = chip.Name;

        await Task.Run(() =>
        {
            _database.SaveBookTagsBatch(tagBatchData, "before-tag-delete");
            _database.DeleteManagedTag(chipName);
            if (isBuiltIn)
            {
                _database.SuppressTag(chipName);
            }
        });

        _managedTags.Remove(chip.Name);
        _managedTagCategories.Remove(chip.Name);
        _managedTagIsExclusive.Remove(chip.Name);
        _managedTagUpdatedAt.Remove(chip.Name);
        _managedTagColors.Remove(chip.Name);
        _tagGroupFilterOptionsDirty = true;
        if (isBuiltIn)
        {
            _suppressedTags.Add(chip.Name);
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
