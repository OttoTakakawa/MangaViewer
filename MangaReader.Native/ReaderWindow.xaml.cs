using MangaReader.Native.Models;
using MangaReader.Native.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls;

namespace MangaReader.Native;

public partial class ReaderWindow : Window
{
    private const double WheelZoomStep = 0.08;
    private const double HoldZoomFactor = 2.6;
    private static readonly TimeSpan PageLoadCoalesceDelay = TimeSpan.FromMilliseconds(24);
    private static readonly TimeSpan ProgressSaveDelay = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan FitModeApplyDelay = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan AdjacentPreloadDelay = TimeSpan.FromMilliseconds(90);
    private const double FixedPageSlotHeight = 1600;
    private const double PortraitPageSlotAspect = 0.707;
    private const int MaxReaderPageCacheEntries = 5;
    private const int MaxQualityReaderPageCacheEntries = 3;
    private const int MemoryPressureReaderPageCacheEntries = 2;

    private static SolidColorBrush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
    // SetFitButtonState brushes
    private static readonly SolidColorBrush FitActiveBg = FrozenBrush("#E8F8FAFC");
    private static readonly SolidColorBrush FitInactiveBg = FrozenBrush("#1A0F172A");
    private static readonly SolidColorBrush FitActiveBorder = FrozenBrush("#F0FFFFFF");
    private static readonly SolidColorBrush FitInactiveBorder = FrozenBrush("#22FFFFFF");
    private static readonly SolidColorBrush FitActiveFg = FrozenBrush("#0F172A");
    private static readonly SolidColorBrush FitInactiveFg = FrozenBrush("#F9FAFB");
    // ApplyReaderBackground brushes
    private static readonly SolidColorBrush BgWhiteOuter = FrozenBrush("#F8FAFC");
    private static readonly SolidColorBrush BgWhitePage = FrozenBrush("#FFFFFF");
    private static readonly SolidColorBrush BgPaperOuter = FrozenBrush("#EDE1CC");
    private static readonly SolidColorBrush BgPaperPage = FrozenBrush("#FDF6E7");
    private static readonly SolidColorBrush BgDark = FrozenBrush("#050608");
    private const double DefaultDoublePageGap = 8;
    private const string DoublePageGapPreferenceKey = "reader.doublepage.gap";
    private const string ReaderQualityModePreferenceKey = "reader.qualitymode";
    private readonly DispatcherTimer _controlsRevealTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
    private readonly DispatcherTimer _doublePageGapSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private readonly DispatcherTimer _pageLoadCoalesceTimer = new() { Interval = PageLoadCoalesceDelay };
    private readonly DispatcherTimer _progressSaveTimer = new() { Interval = ProgressSaveDelay };
    private readonly DispatcherTimer _fitModeApplyTimer = new() { Interval = FitModeApplyDelay };
    private readonly MangaBook _book;
    private readonly LibraryDatabase _database;
    private readonly List<Key> _nextKeys;
    private readonly List<Key> _prevKeys;
    private readonly Func<MangaBook, MangaBook?>? _nextBookResolver;
    private readonly Action<MangaBook>? _openBookRequest;
    private int _displayedPageCount = 1;
    private FitMode _fitMode = FitMode.Height;
    private ReaderQualityMode _qualityMode = ReaderQualityMode.Quality;
    private string _boundaryHint = "";
    private bool _controlsHidden;
    private bool _isHoldZoomActive;
    private bool _isFullscreen;
    private WindowStyle _previousWindowStyle;
    private WindowState _previousWindowState;
    private double _holdZoomBaseValue = 1;
    private int _pageLoadRequestId;
    private int _requestedPageIndex;
    private int? _queuedPageIndex;
    private int? _activePageLoadIndex;
    private CancellationTokenSource? _pageLoadCancellation;
    private CancellationTokenSource? _pagePreloadCancellation;
    private int _backgroundMode;
    private bool _isLoadingViewerPreferences;
    private bool _isNextBookPromptOpen;
    private bool _isPageDecodeActive;
    private bool _isClosing;
    private bool _hasPendingProgressSave;
    private double _pageSlotWidth;
    private double _pageSlotHeight;
    private MangaBook? _pendingNextBook;
    private CancellationTokenSource? _catalogLoadCancellation;
    private System.Windows.Point? _holdZoomLastPointerInViewport;
    private readonly object _pageCacheLock = new();
    private readonly Dictionary<string, LinkedListNode<PageCacheEntry>> _pageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<PageCacheEntry> _pageCacheLru = new();

    public ObservableCollection<PageCatalogItem> PageCatalogItems { get; } = [];

    private enum FitMode
    {
        Width,
        Height
    }

    private enum ReaderQualityMode
    {
        Quality,
        Performance
    }

    public ReaderWindow(
        MangaBook book,
        LibraryDatabase database,
        List<Key> nextKeys,
        List<Key> prevKeys,
        Func<MangaBook, MangaBook?>? nextBookResolver = null,
        Action<MangaBook>? openBookRequest = null)
    {
        InitializeComponent();
        _book = book;
        _database = database;
        _nextKeys = nextKeys;
        _prevKeys = prevKeys;
        _nextBookResolver = nextBookResolver;
        _openBookRequest = openBookRequest;
        _requestedPageIndex = Math.Clamp(book.LastReadPageIndex, 0, Math.Max(0, book.PageCount - 1));
        DataContext = this;
        Title = book.Title;
        TitleText.Text = book.Title;
        _controlsRevealTimer.Tick += ControlsRevealTimer_Tick;
        _doublePageGapSaveTimer.Tick += DoublePageGapSaveTimer_Tick;
        _pageLoadCoalesceTimer.Tick += PageLoadCoalesceTimer_Tick;
        _progressSaveTimer.Tick += ProgressSaveTimer_Tick;
        _fitModeApplyTimer.Tick += FitModeApplyTimer_Tick;
        KeyDown += ReaderWindow_KeyDown;
        SizeChanged += ReaderWindow_SizeChanged;
        Closing += ReaderWindow_Closing;
        Loaded += ReaderWindow_Loaded;
        LoadViewerPreferences();
        ApplyReaderBackground();
        UpdateFitButtons();
        UpdateQualityModeButton();
    }

    private void ReaderWindow_Loaded(object sender, RoutedEventArgs e)
    {
        AppLogger.Info("reader-open", $"Reader loaded: {_book.Title}, pages={_book.Pages.Count}, start={_book.LastReadPageIndex + 1}");
        Dispatcher.InvokeAsync(() => RequestPageLoad(_book.LastReadPageIndex, immediate: true), DispatcherPriority.ApplicationIdle);
    }

    private void ReaderWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _isClosing = true;
        _controlsRevealTimer.Stop();
        _doublePageGapSaveTimer.Stop();
        _pageLoadCoalesceTimer.Stop();
        _progressSaveTimer.Stop();
        _fitModeApplyTimer.Stop();
        _hasPendingProgressSave = false;
        _pageLoadCancellation?.Cancel();
        _pageLoadCancellation?.Dispose();
        _pageLoadCancellation = null;
        _pagePreloadCancellation?.Cancel();
        _pagePreloadCancellation?.Dispose();
        _pagePreloadCancellation = null;
        _catalogLoadCancellation?.Cancel();
        _catalogLoadCancellation?.Dispose();
        _catalogLoadCancellation = null;
        ClearReaderPageCache();
        var book = _book;
        var wheelMode = WheelModeBox.SelectedIndex.ToString();
        _ = Task.Run(() =>
        {
            SaveProgressSafely(book);
            _database.SaveShortcut("reader.wheelmode", wheelMode);
        });
        SaveDoublePageGapPreference();
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        var targetIndex = _requestedPageIndex - GetPreviousStep();
        if (targetIndex < 0)
        {
            _boundaryHint = "已经是第一页";
            UpdateNavigationState();
            return;
        }

        RequestPageLoad(targetIndex);
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        var targetIndex = _requestedPageIndex + GetNavigationStepForRequestedPage();
        if (targetIndex >= _book.Pages.Count)
        {
            TryGoToNextBook();
            return;
        }

        RequestPageLoad(targetIndex);
    }

    private void TryGoToNextBook()
    {
        if (_isNextBookPromptOpen)
        {
            return;
        }

        var nextBook = _nextBookResolver?.Invoke(_book);
        if (nextBook is null)
        {
            _boundaryHint = "已经是最后一页";
            UpdateNavigationState();
            return;
        }

        ShowNextBookPrompt(nextBook);
    }

    private void ShowNextBookPrompt(MangaBook nextBook)
    {
        _isNextBookPromptOpen = true;
        _pendingNextBook = nextBook;
        ReleaseHoldZoom();
        CloseReaderDropdowns();
        if (NextBookConfirmText is not null)
        {
            NextBookConfirmText.Text = $"是否前往当前筛选顺序里的下一本漫画？\n\n下一本：{nextBook.Title}";
        }
        if (NextBookConfirmOverlay is not null)
        {
            NextBookConfirmOverlay.Visibility = Visibility.Visible;
        }
    }

    private void HideNextBookPrompt()
    {
        _isNextBookPromptOpen = false;
        _pendingNextBook = null;
        if (NextBookConfirmOverlay is not null)
        {
            NextBookConfirmOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void NextBookConfirm_Click(object sender, RoutedEventArgs e)
    {
        var nextBook = _pendingNextBook;
        HideNextBookPrompt();
        if (nextBook is null)
        {
            return;
        }

        _openBookRequest?.Invoke(nextBook);
        Close();
    }

    private void NextBookCancel_Click(object sender, RoutedEventArgs e)
    {
        HideNextBookPrompt();
        _boundaryHint = "已经是最后一页";
        UpdateNavigationState();
    }

    private void NextBookConfirmOverlay_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
    }

    private void FitWidth_Click(object sender, RoutedEventArgs e)
    {
        SetFitMode(FitMode.Width);
    }

    private void FitHeight_Click(object sender, RoutedEventArgs e)
    {
        SetFitMode(FitMode.Height);
    }

    private void SetFitMode(FitMode mode)
    {
        _fitMode = mode;
        ApplyFitMode();
        UpdateFitButtons();
    }

    private void ApplyFitMode()
    {
        UpdateImageScrollStage();
        if (_fitMode == FitMode.Width)
        {
            ApplyFitWidth();
            return;
        }

        ApplyFitHeight();
    }

    private void ApplyFitModeForRequest(int requestId)
    {
        if (requestId != _pageLoadRequestId)
        {
            return;
        }

        ApplyFitMode();
    }

    private void ScheduleFitModeApply()
    {
        if (_isClosing)
        {
            return;
        }

        _fitModeApplyTimer.Stop();
        _fitModeApplyTimer.Start();
    }

    private void FitModeApplyTimer_Tick(object? sender, EventArgs e)
    {
        _fitModeApplyTimer.Stop();
        if (!_isClosing && IsLoaded)
        {
            ApplyFitMode();
        }
    }

    private void SaveProgressSafely(MangaBook book)
    {
        try
        {
            _database.SaveProgress(book);
        }
        catch (Exception ex)
        {
            AppLogger.Error("reader-save-progress", ex, $"Failed to save progress for {book.Title}.");
        }
    }

    private void ScheduleProgressSave()
    {
        if (_isClosing)
        {
            return;
        }

        _hasPendingProgressSave = true;
        _progressSaveTimer.Stop();
        _progressSaveTimer.Start();
    }

    private void ProgressSaveTimer_Tick(object? sender, EventArgs e)
    {
        _progressSaveTimer.Stop();
        if (!_hasPendingProgressSave || _isClosing)
        {
            return;
        }

        _hasPendingProgressSave = false;
        var book = _book;
        _ = Task.Run(() => SaveProgressSafely(book));
    }

    private void ApplyFitWidth()
    {
        var width = GetDisplayedPixelWidth();
        var availableWidth = GetAvailableContentWidth();
        if (width > 0 && availableWidth > 0)
        {
            ZoomSlider.Value = Math.Clamp(availableWidth / width, ZoomSlider.Minimum, ZoomSlider.Maximum);
        }
    }

    private void ApplyFitHeight()
    {
        var height = GetDisplayedPixelHeight();
        var availableHeight = GetAvailableContentHeight();
        if (height > 0 && availableHeight > 0)
        {
            ZoomSlider.Value = Math.Clamp(availableHeight / height, ZoomSlider.Minimum, ZoomSlider.Maximum);
        }
    }

    private void UpdateFitButtons()
    {
        SetFitButtonState(FitWidthButton, _fitMode == FitMode.Width);
        SetFitButtonState(FitHeightButton, _fitMode == FitMode.Height);
    }

    private static void SetFitButtonState(System.Windows.Controls.Button button, bool active)
    {
        button.Background = active ? FitActiveBg : FitInactiveBg;
        button.BorderBrush = active ? FitActiveBorder : FitInactiveBorder;
        button.Foreground = active ? FitActiveFg : FitInactiveFg;
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImageScale is null) return;
        ImageScale.ScaleX = e.NewValue;
        ImageScale.ScaleY = e.NewValue;
    }

    private void DoublePageGapSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyDoublePageGap();
        if (_fitMode == FitMode.Width && IsLoaded)
        {
            ApplyFitWidth();
        }

        if (DoublePageGapText is not null)
        {
            DoublePageGapText.Text = $"间距 {(int)Math.Round(e.NewValue)}";
        }

        if (IsLoaded && !_isLoadingViewerPreferences)
        {
            RestartDoublePageGapSaveTimer();
        }
    }

    private void ReadingModeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded) RequestPageLoad(_requestedPageIndex, immediate: true, forceReload: true);
    }

    private void WheelModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            var value = WheelModeBox.SelectedIndex.ToString();
            _ = Task.Run(() => _database.SaveShortcut("reader.wheelmode", value));
        }
    }

    private void ToggleQualityModeButton_Click(object sender, RoutedEventArgs e)
    {
        _qualityMode = _qualityMode == ReaderQualityMode.Quality
            ? ReaderQualityMode.Performance
            : ReaderQualityMode.Quality;
        UpdateQualityModeButton();
        ClearReaderPageCache();
        _ = Task.Run(() => _database.SaveShortcut(ReaderQualityModePreferenceKey, _qualityMode.ToString()));
        if (IsLoaded)
        {
            RequestPageLoad(_requestedPageIndex, immediate: true, forceReload: true);
        }
    }

    private void ReaderWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (HandleFixedShortcut(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (_nextKeys.Contains(e.Key))
        {
            NextPage_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (_prevKeys.Contains(e.Key))
        {
            PreviousPage_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Tab || e.Key == Key.H)
        {
            SetControlsHidden(!_controlsHidden);
            e.Handled = true;
        }
        else if (e.Key == Key.D1)
        {
            WheelModeBox.SelectedIndex = 0;
            e.Handled = true;
        }
        else if (e.Key == Key.D2)
        {
            WheelModeBox.SelectedIndex = 1;
            e.Handled = true;
        }
        else if (e.Key == Key.D3)
        {
            WheelModeBox.SelectedIndex = 2;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (PageCatalogOverlay.Visibility == Visibility.Visible)
            {
                HidePageCatalog();
                e.Handled = true;
            }
            else if (_isNextBookPromptOpen)
            {
                NextBookCancel_Click(sender, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (_isFullscreen)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
            else if (_controlsHidden)
            {
                SetControlsHidden(false);
                e.Handled = true;
            }
        }
    }

    private bool HandleFixedShortcut(Key key)
    {
        switch (key)
        {
            case Key.W:
                CyclePresentationMode();
                return true;
            case Key.E:
                SetFitMode(FitMode.Height);
                return true;
            case Key.Q:
                SetFitMode(FitMode.Width);
                return true;
            case Key.D:
                SetControlsHidden(!_controlsHidden);
                return true;
            case Key.S:
                ToggleReadingMode();
                return true;
            case Key.Z:
                CycleWheelMode();
                return true;
            case Key.X:
                Close();
                return true;
            case Key.C:
                ToggleReadingDirection();
                return true;
            case Key.A:
                CycleBackground();
                return true;
            default:
                return false;
        }
    }

    private void RequestPageLoad(int pageIndex, bool immediate = false, bool forceReload = false)
    {
        if (_isClosing || _book.Pages.Count == 0)
        {
            return;
        }

        var safeIndex = Math.Clamp(pageIndex, 0, _book.Pages.Count - 1);
        _requestedPageIndex = safeIndex;

        if (_isPageDecodeActive)
        {
            if (forceReload || _activePageLoadIndex != safeIndex)
            {
                _queuedPageIndex = safeIndex;
                _pageLoadCancellation?.Cancel();
            }
            return;
        }

        _queuedPageIndex = safeIndex;
        _pageLoadCoalesceTimer.Stop();
        if (immediate)
        {
            StartQueuedPageLoad();
            return;
        }

        _pageLoadCoalesceTimer.Start();
    }

    private void PageLoadCoalesceTimer_Tick(object? sender, EventArgs e)
    {
        _pageLoadCoalesceTimer.Stop();
        StartQueuedPageLoad();
    }

    private void StartQueuedPageLoad()
    {
        if (_isPageDecodeActive || _queuedPageIndex is not { } pageIndex)
        {
            return;
        }

        _queuedPageIndex = null;
        LoadPageCore(pageIndex);
    }

    private async void LoadPageCore(int pageIndex)
    {
        if (_book.Pages.Count == 0) return;
        HideNextBookPrompt();
        var requestId = ++_pageLoadRequestId;
        _pageLoadCancellation?.Cancel();
        _pageLoadCancellation?.Dispose();
        var loadCancellation = new CancellationTokenSource();
        _pageLoadCancellation = loadCancellation;
        _pagePreloadCancellation?.Cancel();
        _pagePreloadCancellation?.Dispose();
        _pagePreloadCancellation = null;
        var cancellationToken = loadCancellation.Token;
        var safeIndex = Math.Clamp(pageIndex, 0, _book.Pages.Count - 1);
        _boundaryHint = "";
        var firstPath = _book.Pages[safeIndex];
        var doublePageMode = IsDoublePageMode();
        var rightToLeft = IsRightToLeftMode();
        _isPageDecodeActive = true;
        _activePageLoadIndex = safeIndex;

        try
        {
            var singleDecodeWidth = GetReaderDecodePixelWidth(false);
            var doubleDecodeWidth = GetReaderDecodePixelWidth(true);
            var cacheKey = CreateReaderPageCacheKey(safeIndex, doublePageMode, singleDecodeWidth, doubleDecodeWidth);
            var page = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return GetOrDecodeReaderPage(cacheKey, safeIndex, doublePageMode, singleDecodeWidth, doubleDecodeWidth, cancellationToken);
            }, cancellationToken);

            if (requestId != _pageLoadRequestId || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ReaderImage.Source = page.First;
            ReaderImageRight.Source = null;
            ReaderImageRight.Visibility = Visibility.Collapsed;
            _displayedPageCount = 1;

            if (page.UseDouble && page.Second is not null)
            {
                _displayedPageCount = 2;
                ReaderImageRight.Visibility = Visibility.Visible;
                if (rightToLeft)
                {
                    ReaderImage.Source = page.Second;
                    ReaderImageRight.Source = page.First;
                }
                else
                {
                    ReaderImageRight.Source = page.Second;
                }
            }

            NormalizeDisplayedImageSizing();
            ApplyDoublePageGap();
            _book.LastReadPageIndex = safeIndex;
            if (safeIndex > 0 && _book.ReadingStatus == "unread")
            {
                _book.ReadingStatus = "reading";
            }
            UpdateNavigationState();
            HideReaderMessage();
            ApplyFitModeForRequest(requestId);
            ScheduleProgressSave();
            ScheduleAdjacentPagePreload(safeIndex, doublePageMode, singleDecodeWidth, doubleDecodeWidth);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error("reader-load-page", ex, $"Failed to load page for {_book.Title}. page={safeIndex + 1}, path={firstPath}");
            var message = $"图片读取失败：{ex.Message}";
            PageText.Text = message;
            ShowReaderMessage("图片读取失败", $"{message}\n\n{firstPath}");
        }
        finally
        {
            if (_pageLoadCancellation == loadCancellation)
            {
                _pageLoadCancellation = null;
            }

            loadCancellation.Dispose();
            _isPageDecodeActive = false;
            _activePageLoadIndex = null;
            if (!_isClosing && _queuedPageIndex is not null)
            {
                _pageLoadCoalesceTimer.Stop();
                _pageLoadCoalesceTimer.Start();
            }
        }
    }

    private void ShowReaderMessage(string title, string message)
    {
        ReaderMessageTitle.Text = title;
        ReaderMessageText.Text = message;
        ReaderMessagePanel.Visibility = Visibility.Visible;
    }

    private void HideReaderMessage()
    {
        ReaderMessagePanel.Visibility = Visibility.Collapsed;
    }

    private bool IsDoublePageMode() => (ReadingModeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() == "双页";
    private bool IsRightToLeftMode() => (DirectionBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() == "从右到左";
    private int GetPreviousStep() => IsDoublePageMode() ? 2 : 1;
    private int GetNavigationStepForRequestedPage()
    {
        if (!IsDoublePageMode())
        {
            return 1;
        }

        return _requestedPageIndex == _book.LastReadPageIndex
            ? _displayedPageCount
            : 2;
    }

    private static bool IsLandscape(BitmapSource image) => image.PixelWidth > image.PixelHeight * 1.15;
    private sealed record LoadedPage(BitmapSource First, BitmapSource? Second, bool UseDouble);
    private sealed record PageCacheEntry(string Key, LoadedPage Page);

    private string CreateReaderPageCacheKey(int pageIndex, bool doublePageMode, int singleDecodeWidth, int doubleDecodeWidth)
    {
        var mode = doublePageMode ? "double" : "single";
        if (_qualityMode == ReaderQualityMode.Quality)
        {
            return $"{pageIndex}:{mode}:quality";
        }

        return $"{pageIndex}:{mode}:performance:{singleDecodeWidth}:{doubleDecodeWidth}";
    }

    private LoadedPage GetOrDecodeReaderPage(
        string cacheKey,
        int pageIndex,
        bool doublePageMode,
        int singleDecodeWidth,
        int doubleDecodeWidth,
        CancellationToken cancellationToken)
    {
        if (TryGetReaderPageCache(cacheKey, out var cached))
        {
            return cached;
        }

        var page = DecodeReaderPage(pageIndex, doublePageMode, singleDecodeWidth, doubleDecodeWidth, cancellationToken);
        AddReaderPageCache(cacheKey, page);
        return page;
    }

    private LoadedPage DecodeReaderPage(
        int pageIndex,
        bool doublePageMode,
        int singleDecodeWidth,
        int doubleDecodeWidth,
        CancellationToken cancellationToken)
    {
        var firstPath = _book.Pages[pageIndex];
        var firstDecodeWidth = doublePageMode ? doubleDecodeWidth : singleDecodeWidth;
        var first = ImageLoader.LoadBitmap(firstPath, firstDecodeWidth, ignoreColorProfile: false);
        cancellationToken.ThrowIfCancellationRequested();
        var useDouble = doublePageMode && pageIndex + 1 < _book.Pages.Count && !IsLandscape(first);
        if (doublePageMode && !useDouble && firstDecodeWidth != singleDecodeWidth)
        {
            first = ImageLoader.LoadBitmap(firstPath, singleDecodeWidth, ignoreColorProfile: false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        BitmapSource? second = null;
        if (useDouble)
        {
            second = ImageLoader.LoadBitmap(_book.Pages[pageIndex + 1], doubleDecodeWidth, ignoreColorProfile: false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new LoadedPage(first, second, useDouble);
    }

    private bool TryGetReaderPageCache(string key, out LoadedPage page)
    {
        lock (_pageCacheLock)
        {
            if (_pageCache.TryGetValue(key, out var node))
            {
                _pageCacheLru.Remove(node);
                _pageCacheLru.AddFirst(node);
                page = node.Value.Page;
                return true;
            }
        }

        page = null!;
        return false;
    }

    private void AddReaderPageCache(string key, LoadedPage page)
    {
        lock (_pageCacheLock)
        {
            if (_pageCache.TryGetValue(key, out var existing))
            {
                existing.Value = new PageCacheEntry(key, page);
                _pageCacheLru.Remove(existing);
                _pageCacheLru.AddFirst(existing);
            }
            else
            {
                var node = new LinkedListNode<PageCacheEntry>(new PageCacheEntry(key, page));
                _pageCacheLru.AddFirst(node);
                _pageCache[key] = node;
            }

            TrimReaderPageCache(GetReaderPageCacheLimit());
        }
    }

    private int GetReaderPageCacheLimit()
    {
        if (IsMemoryPressureHigh())
        {
            return MemoryPressureReaderPageCacheEntries;
        }

        return _qualityMode == ReaderQualityMode.Quality
            ? MaxQualityReaderPageCacheEntries
            : MaxReaderPageCacheEntries;
    }

    private void TrimReaderPageCache(int maxEntries)
    {
        while (_pageCache.Count > maxEntries && _pageCacheLru.Last is not null)
        {
            var last = _pageCacheLru.Last;
            _pageCacheLru.RemoveLast();
            _pageCache.Remove(last.Value.Key);
        }
    }

    private void ClearReaderPageCache()
    {
        lock (_pageCacheLock)
        {
            _pageCache.Clear();
            _pageCacheLru.Clear();
        }
    }

    private static bool IsMemoryPressureHigh()
    {
        var memoryInfo = GC.GetGCMemoryInfo();
        return memoryInfo.HighMemoryLoadThresholdBytes > 0
            && memoryInfo.MemoryLoadBytes > memoryInfo.HighMemoryLoadThresholdBytes * 0.82;
    }

    private void ScheduleAdjacentPagePreload(int currentPageIndex, bool doublePageMode, int singleDecodeWidth, int doubleDecodeWidth)
    {
        if (_isClosing || IsMemoryPressureHigh())
        {
            lock (_pageCacheLock)
            {
                TrimReaderPageCache(MemoryPressureReaderPageCacheEntries);
            }
            return;
        }

        _pagePreloadCancellation?.Cancel();
        _pagePreloadCancellation?.Dispose();
        var preloadCancellation = new CancellationTokenSource();
        _pagePreloadCancellation = preloadCancellation;
        var token = preloadCancellation.Token;
        var candidates = GetAdjacentPreloadCandidates(currentPageIndex, doublePageMode).ToArray();
        if (candidates.Length == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AdjacentPreloadDelay, token).ConfigureAwait(false);
                foreach (var candidate in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    if (_isClosing || _isPageDecodeActive || _queuedPageIndex is not null || IsMemoryPressureHigh())
                    {
                        return;
                    }

                    var cacheKey = CreateReaderPageCacheKey(candidate, doublePageMode, singleDecodeWidth, doubleDecodeWidth);
                    if (TryGetReaderPageCache(cacheKey, out _))
                    {
                        continue;
                    }

                    var page = DecodeReaderPage(candidate, doublePageMode, singleDecodeWidth, doubleDecodeWidth, token);
                    AddReaderPageCache(cacheKey, page);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                AppLogger.Warn("reader-preload", $"{_book.Title} adjacent preload skipped: {ex.Message}");
            }
        }, token);
    }

    private IEnumerable<int> GetAdjacentPreloadCandidates(int currentPageIndex, bool doublePageMode)
    {
        var step = doublePageMode ? 2 : 1;
        var next = currentPageIndex + step;
        if (next < _book.Pages.Count)
        {
            yield return next;
        }

        var previous = currentPageIndex - step;
        if (previous >= 0)
        {
            yield return previous;
        }
    }

    private void NormalizeDisplayedImageSizing()
    {
        if (_qualityMode == ReaderQualityMode.Quality)
        {
            NormalizeQualityImageSizing();
            return;
        }

        NormalizePerformanceImageSizing();
    }

    private void NormalizeQualityImageSizing()
    {
        if (ReaderImageRight.Visibility == Visibility.Visible
            && ReaderImage.Source is BitmapSource left
            && ReaderImageRight.Source is BitmapSource right)
        {
            ReaderImage.Width = left.PixelWidth;
            ReaderImage.Height = left.PixelHeight;
            ReaderImage.Stretch = Stretch.Fill;
            ReaderImageRight.Width = right.PixelWidth;
            ReaderImageRight.Height = right.PixelHeight;
            ReaderImageRight.Stretch = Stretch.Fill;
            _pageSlotWidth = left.PixelWidth + right.PixelWidth;
            _pageSlotHeight = Math.Max(left.PixelHeight, right.PixelHeight);
            return;
        }

        if (ReaderImage.Source is BitmapSource single)
        {
            ReaderImage.Width = single.PixelWidth;
            ReaderImage.Height = single.PixelHeight;
            ReaderImage.Stretch = Stretch.Fill;
            _pageSlotWidth = single.PixelWidth;
            _pageSlotHeight = single.PixelHeight;
        }
        else
        {
            _pageSlotWidth = 0;
            _pageSlotHeight = 0;
            ReaderImage.Width = double.NaN;
            ReaderImage.Height = double.NaN;
            ReaderImage.Stretch = Stretch.None;
        }

        ReaderImageRight.Height = double.NaN;
        ReaderImageRight.Width = double.NaN;
        ReaderImageRight.Stretch = Stretch.None;
    }

    private void NormalizePerformanceImageSizing()
    {
        var slotHeight = FixedPageSlotHeight;
        if (ReaderImageRight.Visibility == Visibility.Visible
            && ReaderImage.Source is BitmapSource left
            && ReaderImageRight.Source is BitmapSource right)
        {
            _pageSlotHeight = slotHeight;
            var slotWidth = slotHeight * PortraitPageSlotAspect;
            _pageSlotWidth = slotWidth * 2;
            ReaderImage.Width = slotWidth;
            ReaderImage.Height = _pageSlotHeight;
            ReaderImageRight.Width = slotWidth;
            ReaderImageRight.Height = _pageSlotHeight;
            ReaderImage.Stretch = Stretch.Uniform;
            ReaderImageRight.Stretch = Stretch.Uniform;
            return;
        }

        if (ReaderImage.Source is BitmapSource single)
        {
            var aspect = IsLandscape(single)
                ? Math.Max(0.1, (double)single.PixelWidth / single.PixelHeight)
                : PortraitPageSlotAspect;
            _pageSlotHeight = slotHeight;
            _pageSlotWidth = slotHeight * aspect;
            ReaderImage.Width = _pageSlotWidth;
            ReaderImage.Height = _pageSlotHeight;
            ReaderImage.Stretch = Stretch.Uniform;
        }
        else
        {
            _pageSlotWidth = 0;
            _pageSlotHeight = 0;
            ReaderImage.Width = double.NaN;
            ReaderImage.Height = double.NaN;
            ReaderImage.Stretch = Stretch.None;
        }

        ReaderImageRight.Height = double.NaN;
        ReaderImageRight.Width = double.NaN;
        ReaderImageRight.Stretch = Stretch.None;
    }

    private void ApplyDoublePageGap()
    {
        if (ReaderImage is null || ReaderImageRight is null || DoublePageGapSlider is null)
        {
            return;
        }

        var gap = Math.Clamp(DoublePageGapSlider.Value, DoublePageGapSlider.Minimum, DoublePageGapSlider.Maximum);
        if (ReaderImageRight.Visibility == Visibility.Visible)
        {
            var halfGap = gap / 2;
            ReaderImage.Margin = new Thickness(4, 4, halfGap, 4);
            ReaderImageRight.Margin = new Thickness(halfGap, 4, 4, 4);
            return;
        }

        ReaderImage.Margin = new Thickness(4);
        ReaderImageRight.Margin = new Thickness(4);
    }

    private double GetDisplayedPixelWidth()
    {
        if (_pageSlotWidth <= 0)
        {
            return 0;
        }

        return _pageSlotWidth;
    }

    private double GetDisplayedPixelHeight()
    {
        return _pageSlotHeight;
    }

    private int GetReaderDecodePixelWidth(bool isDoublePage)
    {
        if (_qualityMode == ReaderQualityMode.Quality)
        {
            return 0;
        }

        var viewport = GetReaderViewportWidth();
        if (viewport <= 0)
        {
            viewport = 960;
        }

        var zoom = ZoomSlider?.Value ?? 1.0;
        var perPage = isDoublePage ? viewport / 2.0 : viewport;
        var decoded = perPage * zoom * 1.2;
        return (int)Math.Clamp(decoded, 800, 3200);
    }

    private double GetReaderViewportWidth()
    {
        var viewportWidth = ReaderScrollViewer.ViewportWidth > 0 ? ReaderScrollViewer.ViewportWidth : ReaderScrollViewer.ActualWidth;
        var padding = ReaderScrollViewer.Padding.Left + ReaderScrollViewer.Padding.Right;
        return Math.Max(0, viewportWidth - padding);
    }

    private double GetReaderViewportHeight()
    {
        var viewportHeight = ReaderScrollViewer.ViewportHeight > 0 ? ReaderScrollViewer.ViewportHeight : ReaderScrollViewer.ActualHeight;
        var padding = ReaderScrollViewer.Padding.Top + ReaderScrollViewer.Padding.Bottom;
        return Math.Max(0, viewportHeight - padding);
    }

    private void UpdateImageScrollStage()
    {
        var viewportWidth = GetReaderViewportWidth();
        var viewportHeight = GetReaderViewportHeight();
        if (viewportWidth > 0)
        {
            ImageScrollContent.MinWidth = viewportWidth;
        }

        if (viewportHeight > 0)
        {
            ImageScrollContent.MinHeight = viewportHeight;
        }
    }

    private double GetAvailableContentWidth()
    {
        var viewportWidth = GetReaderViewportWidth();
        var hostMargin = ImageHost.Margin.Left + ImageHost.Margin.Right;
        var leftImageMargin = ReaderImage.Margin.Left + ReaderImage.Margin.Right;
        var rightImageMargin = ReaderImageRight.Visibility == Visibility.Visible
            ? ReaderImageRight.Margin.Left + ReaderImageRight.Margin.Right
            : 0;
        var totalImageMargins = leftImageMargin + rightImageMargin;
        return Math.Max(0, viewportWidth - hostMargin - totalImageMargins);
    }

    private double GetAvailableContentHeight()
    {
        var viewportHeight = GetReaderViewportHeight();
        var hostMargin = ImageHost.Margin.Top + ImageHost.Margin.Bottom;
        var imageMargin = ReaderImage.Margin.Top + ReaderImage.Margin.Bottom;
        return Math.Max(0, viewportHeight - hostMargin - imageMargin);
    }

    private void UpdateNavigationState()
    {
        var endPage = Math.Min(_book.LastReadPageIndex + _displayedPageCount, _book.PageCount);
        var pageText = _displayedPageCount > 1 && endPage > _book.LastReadPageIndex + 1
            ? $"{_book.LastReadPageIndex + 1}-{endPage} / {_book.PageCount}"
            : $"{_book.LastReadPageIndex + 1} / {_book.PageCount}";
        PageText.Text = pageText;
        if (HiddenPageText is not null)
        {
            HiddenPageText.Text = pageText;
        }
        if (!string.IsNullOrWhiteSpace(_boundaryHint))
        {
            PageText.Text += $"  ·  {_boundaryHint}";
        }

    }

    private void ToggleControlsButton_Click(object sender, RoutedEventArgs e)
    {
        SetControlsHidden(!_controlsHidden);
    }

    private void HiddenControlsBadge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SetControlsHidden(false);
    }

    private void SetControlsHidden(bool hidden)
    {
        if (hidden)
        {
            CloseReaderDropdowns();
            ReleaseHoldZoom();
        }

        _controlsHidden = hidden;
        if (TopToolbar is not null) TopToolbar.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        if (BottomToolbar is not null) BottomToolbar.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        if (HiddenControlsBadge is not null) HiddenControlsBadge.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        if (ToggleControlsButton is not null) ToggleControlsButton.Content = hidden ? "显示" : "隐藏";
        UpdatePresentationButton();

        if (hidden)
        {
            _controlsRevealTimer.Stop();
        }
    }

    private void CloseReaderDropdowns()
    {
        if (WheelModeBox is not null) WheelModeBox.IsDropDownOpen = false;
        if (ReadingModeBox is not null) ReadingModeBox.IsDropDownOpen = false;
        if (DirectionBox is not null) DirectionBox.IsDropDownOpen = false;
    }

    private void ReaderScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (PageCatalogOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        switch (WheelModeBox.SelectedIndex)
        {
            case 1:
                ZoomSlider.Value = Math.Clamp(
                    ZoomSlider.Value + (e.Delta > 0 ? WheelZoomStep : -WheelZoomStep),
                    ZoomSlider.Minimum,
                    ZoomSlider.Maximum);
                e.Handled = true;
                break;
            case 2:
                break;
            default:
                if (e.Delta > 0)
                {
                    PreviousPage_Click(sender, new RoutedEventArgs());
                }
                else
                {
                    NextPage_Click(sender, new RoutedEventArgs());
                }

                e.Handled = true;
                break;
        }
    }

    private void ReaderScrollViewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PageCatalogOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        NavigateByClickPosition(e.GetPosition(ReaderScrollViewer));
        e.Handled = true;
    }

    private void ReaderScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        CyclePresentationMode();
        e.Handled = true;
    }

    private void ReaderRoot_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PageCatalogOverlay.Visibility == Visibility.Visible || IsPointerOverReaderChrome(e.OriginalSource))
        {
            return;
        }

        BeginHoldZoom(e);
        e.Handled = true;
    }

    private void NavigateByClickPosition(System.Windows.Point pointerInViewport)
    {
        var viewportWidth = ReaderScrollViewer.ActualWidth > 0
            ? ReaderScrollViewer.ActualWidth
            : ActualWidth;

        if (pointerInViewport.X < viewportWidth * 0.36)
        {
            PreviousPage_Click(this, new RoutedEventArgs());
            return;
        }

        NextPage_Click(this, new RoutedEventArgs());
    }

    private void ReaderScrollViewer_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isHoldZoomActive)
        {
            return;
        }

        UpdateHoldZoom(e.GetPosition(ImageHost), e.GetPosition(ReaderScrollViewer));
        e.Handled = true;
    }

    private void ReaderScrollViewer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = false;
    }

    private void ReaderRoot_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (PageCatalogOverlay.Visibility == Visibility.Visible || IsPointerOverReaderChrome(e.OriginalSource))
        {
            return;
        }

        ReleaseHoldZoom();
        e.Handled = true;
    }

    private void ReaderRoot_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isHoldZoomActive)
        {
            return;
        }

        UpdateHoldZoom(e.GetPosition(ImageHost), e.GetPosition(ReaderScrollViewer));
        e.Handled = true;
    }

    private void UpdateHoldZoom(System.Windows.Point imageHostPosition, System.Windows.Point pointerInViewport)
    {
        if (ImageHost.ActualWidth <= 0 || ImageHost.ActualHeight <= 0)
        {
            return;
        }

        var targetZoom = Math.Clamp(_holdZoomBaseValue * HoldZoomFactor, ZoomSlider.Minimum, ZoomSlider.Maximum);
        if (Math.Abs(ZoomSlider.Value - targetZoom) > 0.001)
        {
            ZoomSlider.Value = targetZoom;
        }

        Dispatcher.InvokeAsync(
            () => ScrollZoomPointUnderMouse(imageHostPosition, pointerInViewport),
            DispatcherPriority.Loaded);
    }

    private void BeginHoldZoom(MouseButtonEventArgs e)
    {
        _isHoldZoomActive = true;
        _holdZoomBaseValue = ZoomSlider.Value;
        _holdZoomLastPointerInViewport = null;
        try
        {
            Mouse.Capture(ReaderRoot, CaptureMode.SubTree);
            UpdateHoldZoom(e.GetPosition(ImageHost), e.GetPosition(ReaderScrollViewer));
        }
        catch
        {
            ReleaseHoldZoom();
            throw;
        }
    }

    private static bool IsPointerOverReaderChrome(object originalSource)
    {
        for (var current = originalSource as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.Popup)
            {
                return true;
            }

            if (current is FrameworkElement { Name: "TopToolbar" or "BottomToolbar" or "HiddenControlsBadge" or "NextBookConfirmOverlay" or "PageCatalogOverlay" })
            {
                return true;
            }
        }

        return false;
    }

    private void ScrollZoomPointUnderMouse(System.Windows.Point imageHostPosition, System.Windows.Point pointerInViewport)
    {
        if (!_isHoldZoomActive || ImageScrollContent is null)
        {
            return;
        }

        if (_holdZoomLastPointerInViewport is { } previousPointer)
        {
            var delta = pointerInViewport - previousPointer;
            ReaderScrollViewer.ScrollToHorizontalOffset(Math.Max(0, ReaderScrollViewer.HorizontalOffset - delta.X));
            ReaderScrollViewer.ScrollToVerticalOffset(Math.Max(0, ReaderScrollViewer.VerticalOffset - delta.Y));
            _holdZoomLastPointerInViewport = pointerInViewport;
            return;
        }

        var contentPoint = ImageHost.TranslatePoint(imageHostPosition, ImageScrollContent);
        ReaderScrollViewer.ScrollToHorizontalOffset(Math.Max(0, contentPoint.X - pointerInViewport.X));
        ReaderScrollViewer.ScrollToVerticalOffset(Math.Max(0, contentPoint.Y - pointerInViewport.Y));
        _holdZoomLastPointerInViewport = pointerInViewport;
    }

    private void ReleaseHoldZoom()
    {
        if (!_isHoldZoomActive)
        {
            return;
        }

        _isHoldZoomActive = false;
        _holdZoomLastPointerInViewport = null;
        ZoomSlider.Value = _holdZoomBaseValue;
        if (Mouse.Captured == ReaderRoot)
        {
            Mouse.Capture(null);
        }
    }

    private void LoadViewerPreferences()
    {
        _isLoadingViewerPreferences = true;
        var shortcuts = _database.LoadShortcuts();
        try
        {
            if (shortcuts.TryGetValue("reader.wheelmode", out var wheelMode)
                && int.TryParse(wheelMode, out var wheelModeIndex)
                && wheelModeIndex >= 0
                && wheelModeIndex < WheelModeBox.Items.Count)
            {
                WheelModeBox.SelectedIndex = wheelModeIndex;
            }

            if (shortcuts.TryGetValue(DoublePageGapPreferenceKey, out var gapText)
                && double.TryParse(gapText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gap))
            {
                DoublePageGapSlider.Value = Math.Clamp(gap, DoublePageGapSlider.Minimum, DoublePageGapSlider.Maximum);
            }
            else
            {
                DoublePageGapSlider.Value = DefaultDoublePageGap;
            }

            if (shortcuts.TryGetValue(ReaderQualityModePreferenceKey, out var qualityMode)
                && Enum.TryParse<ReaderQualityMode>(qualityMode, ignoreCase: true, out var parsedQualityMode))
            {
                _qualityMode = parsedQualityMode;
            }
            else
            {
                _qualityMode = ReaderQualityMode.Quality;
            }
        }
        finally
        {
            _isLoadingViewerPreferences = false;
        }

        ApplyDoublePageGap();
        UpdateQualityModeButton();
    }

    private void UpdateQualityModeButton()
    {
        if (QualityModeButton is null)
        {
            return;
        }

        QualityModeButton.Content = _qualityMode == ReaderQualityMode.Quality ? "质量" : "性能";
        QualityModeButton.ToolTip = _qualityMode == ReaderQualityMode.Quality
            ? "当前为质量模式：原图解码，按原始像素布局"
            : "当前为性能模式：按视口降采样，降低内存压力";
    }

    private void RestartDoublePageGapSaveTimer()
    {
        _doublePageGapSaveTimer.Stop();
        _doublePageGapSaveTimer.Start();
    }

    private void DoublePageGapSaveTimer_Tick(object? sender, EventArgs e)
    {
        _doublePageGapSaveTimer.Stop();
        SaveDoublePageGapPreference();
    }

    private void SaveDoublePageGapPreference()
    {
        if (DoublePageGapSlider is null)
        {
            return;
        }

        var gap = Math.Clamp(DoublePageGapSlider.Value, DoublePageGapSlider.Minimum, DoublePageGapSlider.Maximum);
        var value = gap.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        _ = Task.Run(() => _database.SaveShortcut(DoublePageGapPreferenceKey, value));
    }

    private void RestartControlsRevealTimer()
    {
        _controlsRevealTimer.Stop();
    }

    private void ControlsRevealTimer_Tick(object? sender, EventArgs e)
    {
        _controlsRevealTimer.Stop();
    }

    private bool IsAnyReaderDropdownOpen()
    {
        return WheelModeBox?.IsDropDownOpen == true
            || ReadingModeBox?.IsDropDownOpen == true
            || DirectionBox?.IsDropDownOpen == true;
    }

    private void ReaderWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScheduleFitModeApply();
    }

    private void CycleBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        CycleBackground();
    }

    private void CycleBackground()
    {
        _backgroundMode = (_backgroundMode + 1) % 3;
        ApplyReaderBackground();
    }

    private void ApplyReaderBackground()
    {
        var (outerBrush, pageBrush, label) = _backgroundMode switch
        {
            1 => (BgWhiteOuter, BgWhitePage, "白"),
            2 => (BgPaperOuter, BgPaperPage, "纸"),
            _ => (BgDark, BgDark, "黑")
        };

        ReaderRoot.Background = outerBrush;
        ReaderBackdrop.Background = outerBrush;
        ReaderScrollViewer.Background = outerBrush;
        ImageHost.Background = pageBrush;
        BackgroundButton.Content = $"背景:{label}";
    }

    private void ToggleReadingMode()
    {
        ReadingModeBox.SelectedIndex = ReadingModeBox.SelectedIndex == 0 ? 1 : 0;
    }

    private void ToggleReadingDirection()
    {
        DirectionBox.SelectedIndex = DirectionBox.SelectedIndex == 0 ? 1 : 0;
    }

    private void CycleWheelMode()
    {
        WheelModeBox.SelectedIndex = (WheelModeBox.SelectedIndex + 1) % WheelModeBox.Items.Count;
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        CyclePresentationMode();
    }

    private void CyclePresentationMode()
    {
        if (!_isFullscreen)
        {
            EnterFullscreen();
            SetControlsHidden(false);
            return;
        }

        if (!_controlsHidden)
        {
            SetControlsHidden(true);
            return;
        }

        ExitFullscreen();
        SetControlsHidden(false);
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
            return;
        }

        EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        if (_isFullscreen)
        {
            return;
        }

        _isFullscreen = true;
        _previousWindowStyle = WindowStyle;
        _previousWindowState = WindowState;
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
        UpdatePresentationButton();
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen)
        {
            return;
        }

        _isFullscreen = false;
        WindowStyle = _previousWindowStyle;
        WindowState = _previousWindowState;
        UpdatePresentationButton();
    }

    private void UpdatePresentationButton()
    {
        if (FullscreenButton is null)
        {
            return;
        }

        FullscreenButton.Content = !_isFullscreen
            ? "全屏"
            : _controlsHidden
                ? "窗口"
                : "隐藏UI";
    }

    private void CatalogButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPageCatalog();
    }

    private void ClosePageCatalog_Click(object sender, RoutedEventArgs e)
    {
        HidePageCatalog();
    }

    private void ShowPageCatalog()
    {
        ReleaseHoldZoom();
        CloseReaderDropdowns();
        PageCatalogOverlay.Visibility = Visibility.Visible;
        EnsurePageCatalogItems();
        StartPageCatalogThumbnailLoad();
    }

    private void HidePageCatalog()
    {
        PageCatalogOverlay.Visibility = Visibility.Collapsed;
    }

    private void PageCatalogOverlay_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = false;
    }

    private void EnsurePageCatalogItems()
    {
        if (PageCatalogItems.Count == _book.Pages.Count)
        {
            return;
        }

        PageCatalogItems.Clear();
        for (var i = 0; i < _book.Pages.Count; i++)
        {
            PageCatalogItems.Add(new PageCatalogItem(i, _book.Pages[i]));
        }
    }

    private void StartPageCatalogThumbnailLoad()
    {
        _catalogLoadCancellation?.Cancel();
        _catalogLoadCancellation?.Dispose();
        _catalogLoadCancellation = new CancellationTokenSource();
        var token = _catalogLoadCancellation.Token;
        var items = PageCatalogItems.ToList();

        _ = Task.Run(async () =>
        {
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                if (item.Thumbnail is not null)
                {
                    continue;
                }

                BitmapSource? thumbnail = null;
                try
                {
                    thumbnail = ImageLoader.LoadBitmap(item.Path, 180);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
                {
                    AppLogger.Warn("reader-catalog", $"Thumbnail failed: page={item.PageIndex + 1}, path={item.Path}, error={ex.Message}");
                }

                if (thumbnail is not null)
                {
                    await Dispatcher.InvokeAsync(() => item.Thumbnail = thumbnail, DispatcherPriority.Background);
                }
            }
        }, token);
    }

    private void PageCatalogItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PageCatalogItem item)
        {
            return;
        }

        HidePageCatalog();
        RequestPageLoad(item.PageIndex, immediate: true);
        e.Handled = true;
    }

    private async void SetCatalogPageAsCover_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PageCatalogItem item)
        {
            return;
        }

        _book.CoverPageIndex = item.PageIndex;
        var book = _book;
        await Task.Run(() => _database.SaveMetadata(book));
        _book.CoverImage = await Task.Run(() => ImageLoader.LoadBitmap(item.Path, 240));
        _book.NotifyAll();
        StatusCatalogFeedback($"已将第 {item.PageIndex + 1} 页设为封面。");
    }

    private void StatusCatalogFeedback(string message)
    {
        _boundaryHint = message;
        UpdateNavigationState();
    }

    public sealed class PageCatalogItem : INotifyPropertyChanged
    {
        private BitmapSource? _thumbnail;

        public PageCatalogItem(int pageIndex, string path)
        {
            PageIndex = pageIndex;
            Path = path;
        }

        public int PageIndex { get; }
        public string Path { get; }
        public string PageText => $"第 {PageIndex + 1} 页";

        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                _thumbnail = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
