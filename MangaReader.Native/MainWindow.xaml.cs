using MangaReader.Native.Models;
using MangaReader.Native.Services;
using System.ComponentModel;
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

    private const double WheelScrollMultiplier = 1.45;
    private static readonly TimeSpan SearchDebounceInterval = TimeSpan.FromMilliseconds(220);
    private static readonly TagPreset[] DefaultTagPresets = TagCatalog.BuiltInPresets;

    private static SolidColorBrush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
    private static readonly SolidColorBrush DarkBrush = FrozenBrush("#111827");
    private static readonly SolidColorBrush GrayForegroundBrush = FrozenBrush("#374151");
    private static readonly SolidColorBrush LightBackgroundBrush = FrozenBrush("#F8FAFC");
    private static readonly SolidColorBrush LightBorderBrush = FrozenBrush("#E5E7EB");

    private readonly AppStorage _storage = new();
    private readonly LibraryScanner _scanner = new();
    private readonly BatchImportAnalyzer _batchImportAnalyzer = new();
    private readonly LibraryDatabase _database;
    private readonly CoverCache _coverCache;
    private readonly CoverThumbnailPipeline _coverPipeline;
    private readonly UpdateService _updateService;
    private MangaBook? _currentBook;
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _importCancellation;
    private List<Key> _nextKeys = [Key.Right, Key.Space];
    private List<Key> _prevKeys = [Key.Left];
    private bool _isEditMode;
    private ICollectionView? _booksView;
    private readonly HashSet<string> _activeTagFilters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _managedTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _managedTagCategories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _managedTagIsExclusive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _managedTagUpdatedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _managedTagColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<MangaBook>> _tagBooksByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _suppressedTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _visibleCoverReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _bookSearchDebounceTimer = new() { Interval = SearchDebounceInterval };
    private readonly DispatcherTimer _tagSearchDebounceTimer = new() { Interval = SearchDebounceInterval };
    private readonly DispatcherTimer _tagManagerSearchDebounceTimer = new() { Interval = SearchDebounceInterval };
    private readonly DispatcherTimer _authorSearchDebounceTimer = new() { Interval = SearchDebounceInterval };
    private bool _isRefreshingAuthorFilters;
    private bool _libraryChromeCollapsed;
    private bool _isLogPanelVisible;
    private bool _isDetailDrawerCollapsed;
    private bool _isCheckingForUpdates;
    private string _currentNavigationKey = "home";
    private string _cachedSearchQuery = "";
    private string _cachedStatusFilter = "";
    private string _cachedAuthorFilter = "";
    private bool _cachedFavoriteOnly;
    private bool _cachedShowHidden;
    private string[] _cachedActiveTagFilters = [];

    public RangeObservableCollection<MangaBook> Books { get; } = [];
    public RangeObservableCollection<TagChip> VisibleTags { get; } = [];
    public RangeObservableCollection<TagChip> ActiveTagFilters { get; } = [];
    public RangeObservableCollection<TagChip> TagManagerItems { get; } = [];
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

    public RangeObservableCollection<MangaBook> ContinueReadingBooks { get; } = [];
    public RangeObservableCollection<MangaBook> RecentReadingBooks { get; } = [];
    public RangeObservableCollection<MangaBook> FavoriteShowcaseBooks { get; } = [];
    public RangeObservableCollection<MangaBook> RecentlyAddedBooks { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _booksView = CollectionViewSource.GetDefaultView(Books);
        _booksView.Filter = FilterBook;

        _storage.EnsureCreated();
        _database = new LibraryDatabase(_storage);
        _updateService = new UpdateService(_storage);
        _coverCache = new CoverCache(_storage);
        _coverPipeline = new CoverThumbnailPipeline(_coverCache);
        SetDetailVisible(false);
        ShowHomeView();
        UpdateStoragePathText();
        UpdateLogPanelVisibility();

        ConfigureSearchDebounceTimers();
        VersionText.Text = $"v{UpdateService.CurrentVersionText}";
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
            await Task.Run(() => _database.Initialize());
            LoadManagedTags();
            LoadShortcuts();

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
        var booksByPath = Books.ToDictionary(book => book.FolderPath, StringComparer.OrdinalIgnoreCase);
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
        Books.AddRange(newBooks);

        HideImportProgress();
        RefreshLibraryViews(sort: true);
        RefreshHomeShelves();
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
            var scanned = await Task.Run(() =>
            {
                var savedBooks = _database.LoadBooksByPath();
                var all = new List<MangaBook>();
                foreach (var root in roots)
                {
                    token.ThrowIfCancellationRequested();
                    all.AddRange(_scanner.Scan(root, savedBooks));
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

            var missingBooks = scanned.MissingBooks;
            var visibleBooks = scanned.Scanned.Concat(missingBooks).ToList();
            await Task.Run(() => _database.UpsertBooksBatch(visibleBooks), token);

            Books.AddRange(visibleBooks);

            RefreshLibraryViews(sort: true);
            RefreshHomeShelves();
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

        var book = _currentBook;
        await Task.Run(() => _database.SaveMetadata(book));
        _currentBook.NotifyAll();
        RefreshLibraryViews(tagManager: false, sort: true);
        RefreshHomeShelves();
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
        _currentBook.CoverImage = await Task.Run(() => _coverCache.LoadOrCreate(book));
        _currentBook.NotifyAll();
        StatusText.Text = $"封面已设置为第 {_currentBook.CoverPageIndex + 1} 页。";
    }

    private async void CycleBookStyle_Click(object sender, RoutedEventArgs e)
    {
        if (_currentBook is null) return;

        _currentBook.CycleBookStyle();
        var book = _currentBook;
        await Task.Run(() => _database.SaveMetadata(book));
        _currentBook.NotifyAll();
        _booksView?.Refresh();
        StatusText.Text = $"已切换《{_currentBook.Title}》的卡片样式：样式 {_currentBook.BookStyleIndex + 1}。";
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

    private async Task ChangeReadCount(int delta)
    {
        if (_currentBook is null)
        {
            return;
        }

        _currentBook.ReadCount = Math.Max(0, _currentBook.ReadCount + delta);
        var book = _currentBook;
        await Task.Run(() => _database.SaveReadCount(book));
        _currentBook.NotifyAll();
        FillMetadataEditors(_currentBook);
        ApplyBookSort(refresh: false);
        RefreshBookFilter();
        RefreshHomeShelves();
        StatusText.Text = $"《{_currentBook.Title}》已标记为读过 {_currentBook.ReadCount} 次。";
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

        await Task.Run(() => _database.DeleteBook(book));
        Books.Remove(book);
        _currentBook = null;
        BooksList.SelectedItem = null;
        SetDetailVisible(false);
        RefreshLibraryViews(tagManager: false, authors: true);
        RefreshHomeShelves();
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

    private async void ManualBackup_Click(object sender, RoutedEventArgs e)
    {
        var backupPath = await Task.Run(() => _database.CreateManualBackup());
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

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_isCheckingForUpdates)
        {
            return;
        }

        _isCheckingForUpdates = true;
        CheckUpdateButton.IsEnabled = false;
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
            CheckUpdateButton.IsEnabled = true;
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
            StoragePathText.Text = $"下次启动将使用：{selectedPath}";
            AppLogger.Info("storage", $"Data root changed for next launch: {selectedPath}");

            var result = System.Windows.MessageBox.Show(
                @"数据目录已指定。需要立即重启软件以生效，是否现在重启?",
                @"重启软件",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            if (result == MessageBoxResult.Yes)
            {
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                {
                    StatusText.Text = "数据目录已指定。自动重启失败，请手动重启软件后生效。";
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = true
                });
                Environment.Exit(0);
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

    private async void SaveShortcuts_Click(object sender, RoutedEventArgs e)
    {
        var next = ParseKeys(NextShortcutBox.Text);
        var prev = ParseKeys(PrevShortcutBox.Text);
        if (next.Count == 0 || prev.Count == 0)
        {
            StatusText.Text = "快捷键不能为空。示例：Right,Space";
            return;
        }

        if (next.Intersect(prev).Any())
        {
            StatusText.Text = "快捷键冲突：上一页和下一页不能使用相同按键。";
            return;
        }

        _nextKeys = next;
        _prevKeys = prev;
        var nextText = NextShortcutBox.Text.Trim();
        var prevText = PrevShortcutBox.Text.Trim();
        await Task.Run(() =>
        {
            _database.SaveShortcut("reader.next", nextText);
            _database.SaveShortcut("reader.previous", prevText);
        });
        StatusText.Text = "快捷键已保存。";
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

        var targetStyle = Math.Clamp(BatchStyleBox?.SelectedIndex ?? 0, 0, 3);
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

        _booksView?.Refresh();
        FillCurrentBookIfAffected(selectedBooks);
        StatusText.Text = $"已批量应用卡片样式 {targetStyle + 1}：{selectedBooks.Count} 本。";
    }

    private async void BatchAddTag_Click(object sender, RoutedEventArgs e)
    {
        var selectedBooks = GetSelectedBatchBooks();
        if (selectedBooks.Count == 0)
        {
            StatusText.Text = "请先勾选需要批量处理的漫画。";
            return;
        }

        if (!TryResolveTagForCreate(TagSearchBox.Text.Trim(), out var tag, out var category, out var isExclusive, out var color))
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
        await Task.Run(() => _database.SaveMetadata(book));
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

    private void FillMetadataEditors(MangaBook book)
    {
        TitleBox.Text = book.Title;
        AuthorBox.Text = book.Author;
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
        ReadOnlyForeignNameText.Text = EmptyAsPlaceholder(book.ForeignName);
        ReadOnlyStatusText.Text = book.ReadingStatusText;
        ReadOnlyFavoriteText.Text = book.IsFavorite ? "已收藏" : "未收藏";
        ReadOnlyPageCountText.Text = book.PageCount.ToString();
        ReadOnlyProducedAtText.Text = EmptyAsPlaceholder(book.ProducedAt);
        ReadOnlyImportedAtText.Text = EmptyAsPlaceholder(book.ImportedAt);
        ReadOnlyTagsText.Text = EmptyAsPlaceholder(book.Tags);
        ReadOnlyCoverPageText.Text = (book.CoverPageIndex + 1).ToString();
        ReadOnlyReadCountText.Text = book.ReadCountText;
        ReadOnlySummaryText.Text = EmptyAsPlaceholder(book.Summary);
        HideBookButton.Content = book.IsHidden ? "恢复显示" : "隐藏作品";
        HideBookButtonEdit.Content = book.IsHidden ? "恢复显示" : "隐藏作品";
        ToggleFavoriteButton.Content = book.IsFavorite ? "取消收藏" : "收藏";
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

        foreach (var box in new[] { TitleBox, ForeignNameBox, ProducedAtBox, ImportedAtBox, TagsBox, CoverPageBox, ReadCountBox, SummaryBox })
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

        VisibleTags.ReplaceRange(tags);
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

    private void ToggleLibraryChrome_Click(object sender, RoutedEventArgs e)
    {
        SetLibraryChromeCollapsed(!_libraryChromeCollapsed);
    }

    private void ToggleStatusPanel_Click(object sender, RoutedEventArgs e)
    {
        _isLogPanelVisible = !_isLogPanelVisible;
        UpdateLogPanelVisibility();
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

    private void RefreshBookFilter()
    {
        CacheBookFilterState();
        _booksView?.Refresh();
        RefreshShelfOverview();
    }

    private void CacheBookFilterState()
    {
        _cachedSearchQuery = BookSearchBox?.Text.Trim() ?? "";
        _cachedStatusFilter = GetSelectedStatusFilter();
        _cachedAuthorFilter = AuthorFilterBox?.SelectedItem as string ?? "";
        _cachedFavoriteOnly = FavoriteOnlyBox?.IsChecked == true;
        _cachedShowHidden = ShowHiddenBox?.IsChecked == true;
        _cachedActiveTagFilters = _activeTagFilters.ToArray();
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
            ApplyBookSort(refresh: !filter);
        }
        if (filter)
        {
            RefreshBookFilter();
        }
    }

    private async void TagChip_Click(object sender, MouseButtonEventArgs e)
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
            var book = _currentBook;
            await Task.Run(() => _database.SaveMetadata(book));
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

    private void ApplyBookSort(bool refresh = true)
    {
        if (_booksView is null || SortBox is null)
        {
            return;
        }

        _booksView.SortDescriptions.Clear();
        switch (SortBox.SelectedIndex)
        {
            case 1:
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.LastReadPageIndex), ListSortDirection.Descending));
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.Title), ListSortDirection.Ascending));
                break;
            case 2:
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.PageCount), ListSortDirection.Descending));
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.Title), ListSortDirection.Ascending));
                break;
            case 3:
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.TotalBytes), ListSortDirection.Descending));
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.Title), ListSortDirection.Ascending));
                break;
            case 4:
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.ReadCount), ListSortDirection.Descending));
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.Title), ListSortDirection.Ascending));
                break;
            case 5:
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.ImportedAt), ListSortDirection.Descending));
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.Title), ListSortDirection.Ascending));
                break;
            case 6:
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.ProducedAt), ListSortDirection.Descending));
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.Title), ListSortDirection.Ascending));
                break;
            default:
                _booksView.SortDescriptions.Add(new SortDescription(nameof(MangaBook.Title), ListSortDirection.Ascending));
                break;
        }

        if (refresh)
        {
            _booksView.Refresh();
        }
    }

    private bool FilterBook(object item)
    {
        if (item is not MangaBook book)
        {
            return false;
        }

        if (book.IsHidden && !_cachedShowHidden)
        {
            return false;
        }

        if (_cachedFavoriteOnly && !book.IsFavorite)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_cachedStatusFilter)
            && !string.Equals(book.ReadingStatus, _cachedStatusFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_cachedAuthorFilter)
            && _cachedAuthorFilter != "全部作者"
            && !string.Equals(book.Author, _cachedAuthorFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_cachedActiveTagFilters.Length > 0
            && !_cachedActiveTagFilters.All(activeTag =>
                book.TagItems.Any(tag => string.Equals(tag.Name, activeTag, StringComparison.OrdinalIgnoreCase))))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_cachedSearchQuery))
        {
            return true;
        }

        return Contains(book.Title, _cachedSearchQuery)
            || Contains(book.Author, _cachedSearchQuery)
            || Contains(book.CharacterName, _cachedSearchQuery)
            || Contains(book.ForeignName, _cachedSearchQuery)
            || Contains(book.Tags, _cachedSearchQuery)
            || Contains(book.Summary, _cachedSearchQuery)
            || Contains(book.ProducedAt, _cachedSearchQuery)
            || Contains(book.ImportedAt, _cachedSearchQuery)
            || Contains(book.ReadingStatusText, _cachedSearchQuery)
            || Contains(book.PageCountText, _cachedSearchQuery)
            || Contains(book.SizeText, _cachedSearchQuery)
            || Contains(book.IsFavorite ? "收藏" : "", _cachedSearchQuery)
            || Contains(book.ReadCountText, _cachedSearchQuery);
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

        var libraryCount = 0;
        var favoriteCount = 0;
        var readingNowCount = 0;
        var finishedCount = 0;
        foreach (var book in Books)
        {
            if (book.IsHidden && !_cachedShowHidden)
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
            if (book.ReadingStatus == "finished")
            {
                finishedCount++;
            }
        }

        var visibleCount = Books.Count(FilterBook);

        VisibleBookCountText.Text = $"{visibleCount} 本";
        TotalBookCountText.Text = _cachedShowHidden
            ? $"/ 共 {Books.Count} 本"
            : $"/ 共 {libraryCount} 本";
        FavoriteCountText.Text = $"{favoriteCount} 本";
        ReadingNowCountText.Text = $"{readingNowCount} 本";
        FinishedCountText.Text = $"{finishedCount} 本";
        FilterSummaryText.Text = BuildFilterSummary(visibleCount, libraryCount);

        var isEmpty = visibleCount == 0;
        ShelfEmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        ShelfEmptyHintText.Text = BuildEmptyHint();
        UpdateBatchSelectionState();
    }

    private List<MangaBook> GetVisibleBooks()
    {
        return Books.Where(FilterBook).ToList();
    }

    private List<MangaBook> GetSelectedBatchBooks()
    {
        return Books
            .Where(book => book.IsSelectedForBatch)
            .ToList();
    }

    private void UpdateBatchSelectionState()
    {
        if (BatchSelectionText is null)
        {
            return;
        }

        var selectedCount = Books.Count(book => book.IsSelectedForBatch);
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
            BatchModeToggleButton.Background = IsBatchSelectionMode ? DarkBrush : LightBackgroundBrush;
            BatchModeToggleButton.BorderBrush = IsBatchSelectionMode ? DarkBrush : LightBorderBrush;
            BatchModeToggleButton.Foreground = IsBatchSelectionMode ? System.Windows.Media.Brushes.White : DarkBrush;
        }
    }

    private void ClearBatchSelection()
    {
        foreach (var book in Books.Where(book => book.IsSelectedForBatch))
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
            .Take(3));

        ReplaceBooks(RecentReadingBooks, homeBooks
            .Where(book => book.LastReadPageIndex > 0 || book.ReadCount > 0 || book.ReadingStatus == "finished")
            .OrderByDescending(book => book.LastReadPageIndex)
            .ThenByDescending(book => book.ReadCount)
            .Take(4));

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

    private static void ReplaceBooks(RangeObservableCollection<MangaBook> target, IEnumerable<MangaBook> source)
    {
        target.ReplaceRange(source.ToList());
    }

    private void OpenBook(MangaBook book)
    {
        if (book.IsMissing || book.Pages.Count == 0)
        {
            StatusText.Text = "这本漫画路径失效或没有可阅读图片。";
            return;
        }

        AppLogger.Info("reader-open", $"Opening reader: {book.Title}, pages={book.Pages.Count}, folder={book.FolderPath}");
        var reader = new ReaderWindow(
            book,
            _database,
            _nextKeys,
            _prevKeys,
            ResolveNextBookInCurrentView,
            nextBook => Dispatcher.InvokeAsync(() => OpenBook(nextBook), DispatcherPriority.ApplicationIdle))
        {
            Owner = this
        };
        reader.Closed += (_, _) =>
        {
            book.NotifyAll();
            ApplyBookSort(refresh: false);
            RefreshBookFilter();
            RefreshHomeShelves();
        };
        reader.Show();
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
        RefreshBookFilter();
        EnsureLibraryViewCanShowBooks();
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
        ShowHiddenBox.IsChecked = false;
        RefreshLibraryViews(tagManager: false, authors: false, sort: false, activeTags: true);
    }

    private void EnsureLibraryViewCanShowBooks()
    {
        if (Books.Count == 0 || _booksView is null)
        {
            return;
        }

        if (Books.Any(FilterBook))
        {
            return;
        }

        if (HasActiveLibraryFilter())
        {
            ResetLibraryFilters();
            StatusText.Text = "书库中有漫画，但当前筛选没有命中，已自动回到全部漫画。";
            return;
        }

        ShelfEmptyHintText.Text = $"已识别 {Books.Count} 本漫画，但视图没有显示。请重新扫描书库；如果仍为空，这是列表视图刷新问题。";
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
            || ShowHiddenBox?.IsChecked == true;
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

        button.Background = active ? DarkBrush : System.Windows.Media.Brushes.Transparent;
        button.Foreground = active ? System.Windows.Media.Brushes.White : GrayForegroundBrush;
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

    private void UpsertManagedTag(string tag, string? category = null, bool? isExclusive = null, string? color = null)
    {
        var resolvedCategory = category ?? TagCategory(tag);
        var resolvedExclusive = isExclusive ?? IsExclusiveTag(tag);
        var resolvedColor = color ?? (_managedTagColors.TryGetValue(tag, out var existing) ? existing : "");
        _suppressedTags.Remove(tag);
        _managedTags.Add(tag);
        _managedTagCategories[tag] = resolvedCategory;
        _managedTagIsExclusive[tag] = resolvedExclusive;
        _managedTagUpdatedAt[tag] = DateTimeOffset.Now.ToString("O");
        if (!string.IsNullOrWhiteSpace(resolvedColor))
        {
            _managedTagColors[tag] = resolvedColor;
        }
        _ = Task.Run(() => _database.SaveManagedTag(tag, resolvedCategory, resolvedExclusive, resolvedColor));
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

        var dialog = new TagCreateDialog(initialValue, _managedTagCategories.Keys) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.TagName))
        {
            StatusText.Text = "没有创建标签。";
            return false;
        }

        tag = dialog.TagName;
        category = dialog.TagCategory;
        isExclusive = dialog.IsExclusive;
        color = dialog.SelectedColor;
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

    private string TagColor(string tag)
    {
        if (_managedTagColors.TryGetValue(tag, out var managedColor) && !string.IsNullOrWhiteSpace(managedColor))
        {
            return managedColor;
        }
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

        ActiveTagFilters.ReplaceRange(chips);

        ActiveTagSummaryText.Text = chips.Count == 0
            ? "已选 0 个 Tag"
            : $"已选 {chips.Count} 个 Tag";
        ActiveTagFilterList.Visibility = chips.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshTagManagementItems()
    {
        var query = TagManagerSearchBox?.Text.Trim() ?? "";
        var knownTags = EnumerateKnownTags().ToList();
        var chips = knownTags
            .Select(tag => CreateTagChip(tag))
            .Where(tag => string.IsNullOrWhiteSpace(query)
                || tag.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || tag.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(tag => TagCategoryOrder(tag.Category))
            .ThenByDescending(tag => tag.UsageCount)
            .ThenBy(tag => tag.Name)
            .ToList();

        TagManagerItems.ReplaceRange(chips);

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
        if (TagManagerEmptyState is not null)
        {
            TagManagerEmptyState.Visibility = chips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void RefreshAuthorManagementItems(string? filter = null)
    {
        var query = Books
            .Where(b => !string.IsNullOrWhiteSpace(b.Author))
            .GroupBy(b => b.Author, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AuthorItem { Name = g.Key, BookCount = g.Count() });

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
            AuthorBookTotalText.Text = $"{Books.Count(b => !string.IsNullOrWhiteSpace(b.Author))} 本";
        }
        if (AuthorManagerEmptyState is not null)
        {
            AuthorManagerEmptyState.Visibility = AuthorManagerItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

        var booksToUpdate = Books
            .Where(b => string.Equals(b.Author, item.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var updates = booksToUpdate.Select(b => (b.Id, dialog.NewName)).ToList();

        await Task.Run(() => _database.SaveBookAuthorsBatch(updates, "rename-author"));
        foreach (var book in booksToUpdate)
        {
            book.Author = dialog.NewName;
            book.NotifyAll();
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

    private async void EditTagAcrossLibrary(TagChip chip)
    {
        var relatedBooks = Books
            .Where(book => book.TagItems.Any(item => string.Equals(item.Name, chip.Name, StringComparison.OrdinalIgnoreCase)))
            .Take(3)
            .ToList();
        var dialog = new TagEditDialog(chip, relatedBooks, _managedTagCategories.Keys) { Owner = this };
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
        if (!string.IsNullOrWhiteSpace(newColor))
        {
            _managedTagColors[newName] = newColor;
        }

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
