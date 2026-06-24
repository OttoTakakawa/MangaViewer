using MangaReader.Native.Models;
using MangaReader.Native.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private const int MaxQualityFitCacheEntries = 6;
    private const long MaxQualityFitCacheBytes = 96L * 1024 * 1024;
    private const long MemoryPressureQualityFitCacheBytes = 48L * 1024 * 1024;
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
    private static readonly SolidColorBrush QuickRatingFilledBrush = FrozenBrush("#F59E0B");
    private static readonly SolidColorBrush QuickRatingEmptyBrush = FrozenBrush("#D1D5DB");
    private static readonly System.Windows.Media.Brush QuickRatingHitBrush = System.Windows.Media.Brushes.Transparent;
    private static readonly Geometry QuickRatingStarGeometry = Geometry.Parse(
        "M 12,2 L 14.39,8.59 L 21,9.39 L 16,14 L 17.39,21 L 12,17.27 L 6.61,21 L 8,14 L 3,9.39 L 9.61,8.59 Z");
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
    private List<Key> _fullscreenKeys = [Key.W];
    private List<Key> _hideuiKeys = [Key.D, Key.H, Key.Tab];
    private List<Key> _paginationKeys = [Key.S];
    private readonly Func<MangaBook, NextBookRecommendations?>? _nextBookResolver;
    private readonly Action<MangaBook>? _openBookRequest;
    private readonly Action<MangaBook>? _openDetailRequest;
    private readonly CoverThumbnailPipeline? _coverPipeline;
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
    private const double ZoomMin = 0.05;
    private const double ZoomMax = 3;
    private double _currentZoom = 1;
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
    private NextBookRecommendations? _pendingRecommendations;
    private CancellationTokenSource? _catalogLoadCancellation;
    private Dictionary<int, string> _bookmarks = [];
    private System.Windows.Point? _holdZoomLastPointerInViewport;
    private readonly object _pageCacheLock = new();
    private readonly Dictionary<string, LinkedListNode<PageCacheEntry>> _pageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<PageCacheEntry> _pageCacheLru = new();
    private readonly Dictionary<string, LinkedListNode<PageCacheEntry>> _qualityFitCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<PageCacheEntry> _qualityFitCacheLru = new();
    private long _qualityFitCacheBytes;
    private LoadedPage? _currentLoadedPage;
    private bool _currentRightToLeft;
    private CancellationTokenSource? _qualityFitCancellation;
    private int _qualityFitRequestId;

    public RangeObservableCollection<PageCatalogItem> PageCatalogItems { get; } = [];

    private enum FitMode
    {
        Width,
        Height,
        Original
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
        Func<MangaBook, NextBookRecommendations?>? nextBookResolver = null,
        Action<MangaBook>? openBookRequest = null,
        CoverThumbnailPipeline? coverPipeline = null,
        Action<MangaBook>? openDetailRequest = null)
    {
        InitializeComponent();
        _book = book;
        _database = database;
        _nextKeys = nextKeys;
        _prevKeys = prevKeys;
        _nextBookResolver = nextBookResolver;
        _openBookRequest = openBookRequest;
        _coverPipeline = coverPipeline;
        _openDetailRequest = openDetailRequest;
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
        UpdateToolbarMenuLabels();
        BuildQuickRatingStars();
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
        _qualityFitCancellation?.Cancel();
        _qualityFitCancellation?.Dispose();
        _qualityFitCancellation = null;
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
        if (_isNextBookPromptOpen) return;

        var recs = _nextBookResolver?.Invoke(_book);
        if (recs is null || (recs.NextInView is null && recs.SameAuthor is null && recs.SimilarTags is null))
        {
            _boundaryHint = "已经是最后一页";
            UpdateNavigationState();
            return;
        }

        ShowNextBookPrompt(recs);
    }

    private void ShowNextBookPrompt(NextBookRecommendations recs)
    {
        _isNextBookPromptOpen = true;
        _pendingRecommendations = recs;
        ReleaseHoldZoom();
        CloseReaderDropdowns();

        BindNextBookCard(NextBookCard1, NextBookTitle1, recs.NextInView);
        BindNextBookCard(NextBookCard2, NextBookTitle2, recs.SameAuthor);
        BindNextBookCard(NextBookCard3, NextBookTitle3, recs.SimilarTags);
        BuildQuickRatingStars();

        if (NextBookConfirmOverlay is not null)
            NextBookConfirmOverlay.Visibility = Visibility.Visible;
    }

    private void BuildQuickRatingStars()
    {
        if (QuickRatingStarsHost is null || QuickRatingText is null)
        {
            return;
        }

        QuickRatingStarsHost.Children.Clear();
        QuickRatingText.Text = _book.HasRating ? $"当前 {_book.RatingText} 分 · 再点同分清零" : "未评分";

        for (var rating = 1; rating <= 5; rating++)
        {
            QuickRatingStarsHost.Children.Add(CreateQuickRatingStar(_book.Rating, rating));
        }
    }

    private System.Windows.Controls.Grid CreateQuickRatingStar(double rating, int starIndex)
    {
        const double starSize = 22;
        var grid = new System.Windows.Controls.Grid
        {
            Width = starSize,
            Height = starSize,
            Margin = new Thickness(3, 0, 3, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = $"左半 {starIndex - 0.5:0.#} 分，右半 {starIndex} 分"
        };

        grid.Children.Add(BuildQuickRatingStarPath(starSize, QuickRatingEmptyBrush, System.Windows.HorizontalAlignment.Center, fillToWidth: null));

        double fillWidth = 0;
        if (rating >= starIndex)
        {
            fillWidth = starSize;
        }
        else if (rating >= starIndex - 0.5)
        {
            fillWidth = starSize / 2.0;
        }

        if (fillWidth > 0)
        {
            var clip = new Border
            {
                Width = fillWidth,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                ClipToBounds = true
            };
            clip.Child = BuildQuickRatingStarPath(starSize, QuickRatingFilledBrush, System.Windows.HorizontalAlignment.Left, fillToWidth: starSize);
            grid.Children.Add(clip);
        }

        var leftHit = new System.Windows.Shapes.Rectangle
        {
            Width = starSize / 2.0,
            Fill = QuickRatingHitBrush,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Tag = $"{starIndex}|L"
        };
        var rightHit = new System.Windows.Shapes.Rectangle
        {
            Width = starSize / 2.0,
            Fill = QuickRatingHitBrush,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Tag = $"{starIndex}|R"
        };
        leftHit.MouseLeftButtonUp += QuickRatingStar_Click;
        rightHit.MouseLeftButtonUp += QuickRatingStar_Click;
        grid.Children.Add(leftHit);
        grid.Children.Add(rightHit);

        return grid;
    }

    private static System.Windows.Shapes.Path BuildQuickRatingStarPath(double size, SolidColorBrush brush, System.Windows.HorizontalAlignment alignment, double? fillToWidth)
    {
        return new System.Windows.Shapes.Path
        {
            Data = QuickRatingStarGeometry,
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

    private async void QuickRatingStar_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Shapes.Rectangle { Tag: string tag })
        {
            return;
        }

        e.Handled = true;
        var parts = tag.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var starIndex))
        {
            return;
        }

        var rating = parts[1] == "L" ? starIndex - 0.5 : starIndex;
        var newRating = Math.Abs(_book.Rating - rating) < 0.01 ? 0 : rating;
        _book.Rating = newRating;
        BuildQuickRatingStars();

        try
        {
            await Task.Run(() => _database.SaveMetadata(_book));
            _boundaryHint = _book.HasRating ? $"已评分 {_book.RatingText}" : "评分已清除";
            UpdateNavigationState();
        }
        catch (Exception ex)
        {
            AppLogger.Error("reader-rating", ex, $"阅读器快捷评分保存失败：book={_book.Title}, rating={newRating}");
            _boundaryHint = "评分保存失败";
            UpdateNavigationState();
        }
    }

    private async void BindNextBookCard(Border? card, System.Windows.Controls.TextBlock? title, MangaBook? book)
    {
        if (card is null || title is null) return;
        if (book is null)
        {
            card.Visibility = Visibility.Collapsed;
            return;
        }
        card.Visibility = Visibility.Visible;
        title.Text = book.Title;
        if (card.Child is System.Windows.Controls.StackPanel sp)
        {
            var border = sp.Children.OfType<Border>().FirstOrDefault();
            if (border?.Child is System.Windows.Controls.Image img)
            {
                if (book.CoverImage is not null)
                    img.Source = book.CoverImage;
                else if (_coverPipeline is not null)
                {
                    var cover = await _coverPipeline.LoadAsync(book);
                    if (cover is not null)
                        img.Source = cover;
                }
            }
        }
    }

    private void HideNextBookPrompt()
    {
        _isNextBookPromptOpen = false;
        _pendingRecommendations = null;
        if (NextBookConfirmOverlay is not null)
            NextBookConfirmOverlay.Visibility = Visibility.Collapsed;
    }

    private void OpenBookFromRecommendation(MangaBook? book)
    {
        HideNextBookPrompt();
        if (book is null) return;
        _openBookRequest?.Invoke(book);
        Close();
    }

    private void NextBookCard1_Click(object sender, MouseButtonEventArgs e)
        => OpenBookFromRecommendation(_pendingRecommendations?.NextInView);

    private void NextBookCard2_Click(object sender, MouseButtonEventArgs e)
        => OpenBookFromRecommendation(_pendingRecommendations?.SameAuthor);

    private void NextBookCard3_Click(object sender, MouseButtonEventArgs e)
        => OpenBookFromRecommendation(_pendingRecommendations?.SimilarTags);

    private void NextBookGoFirst_Click(object sender, RoutedEventArgs e)
    {
        HideNextBookPrompt();
        RequestPageLoad(0);
    }

    private void NextBookCancel_Click(object sender, RoutedEventArgs e)
    {
        HideNextBookPrompt();
        _boundaryHint = "已经是最后一页";
        UpdateNavigationState();
    }

    private void OpenDetailFromReader_Click(object sender, RoutedEventArgs e)
    {
        _openDetailRequest?.Invoke(_book);
        Close();
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

    private void OriginalSize_Click(object sender, RoutedEventArgs e)
    {
        SetFitMode(FitMode.Original);
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

        if (_fitMode == FitMode.Original)
        {
            RestoreOriginalReaderSources();
            NormalizeDisplayedImageSizing();
            ApplyDoublePageGap();
            ApplyZoom(1);
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
        if (TryApplyQualityFit(FitMode.Width))
        {
            return;
        }

        var width = GetDisplayedPixelWidth();
        var availableWidth = GetAvailableContentWidth();
        if (width > 0 && availableWidth > 0)
        {
            ApplyZoom(Math.Clamp(availableWidth / width, ZoomMin, ZoomMax));
        }
    }

    private void ApplyFitHeight()
    {
        if (TryApplyQualityFit(FitMode.Height))
        {
            return;
        }

        var height = GetDisplayedPixelHeight();
        var availableHeight = GetAvailableContentHeight();
        if (height > 0 && availableHeight > 0)
        {
            ApplyZoom(Math.Clamp(availableHeight / height, ZoomMin, ZoomMax));
        }
    }

    private bool TryApplyQualityFit(FitMode mode)
    {
        if (_qualityMode != ReaderQualityMode.Quality || mode == FitMode.Original || _currentLoadedPage is null)
        {
            return false;
        }

        RestoreOriginalReaderSources();
        NormalizeDisplayedImageSizing();
        ApplyDoublePageGap();

        var sourceWidth = GetDisplayedPixelWidth();
        var sourceHeight = GetDisplayedPixelHeight();
        var available = mode == FitMode.Width ? GetAvailableContentWidth() : GetAvailableContentHeight();
        var source = mode == FitMode.Width ? sourceWidth : sourceHeight;
        if (source <= 0 || available <= 0)
        {
            return false;
        }

        var scale = Math.Clamp(available / source, ZoomMin, ZoomMax);
        if (scale >= 0.93)
        {
            CancelQualityFitRequest();
            ApplyZoom(scale);
            return true;
        }

        if (ReaderImage.Source is not BitmapSource left)
        {
            return false;
        }

        var useDouble = ReaderImageRight.Visibility == Visibility.Visible && ReaderImageRight.Source is BitmapSource;
        var right = useDouble ? ReaderImageRight.Source as BitmapSource : null;
        var cacheKey = CreateQualityFitCacheKey(mode, left, right, scale);
        if (TryGetQualityFitCache(cacheKey, out var cached))
        {
            ApplyFittedReaderSources(cached);
            ApplyZoom(1);
            return true;
        }

        ApplyZoom(scale);
        StartQualityFitRequest(cacheKey, left, right, scale, mode, _requestedPageIndex);
        return true;
    }

    private string CreateQualityFitCacheKey(FitMode mode, BitmapSource left, BitmapSource? right, double scale)
    {
        var leftWidth = Math.Max(1, (int)Math.Round(left.PixelWidth * scale));
        var leftHeight = Math.Max(1, (int)Math.Round(left.PixelHeight * scale));
        var rightText = right is null
            ? "single"
            : $"{Math.Max(1, (int)Math.Round(right.PixelWidth * scale))}x{Math.Max(1, (int)Math.Round(right.PixelHeight * scale))}";
        return $"{_requestedPageIndex}:{mode}:{_currentRightToLeft}:{left.PixelWidth}x{left.PixelHeight}->{leftWidth}x{leftHeight}:{rightText}";
    }

    private async void StartQualityFitRequest(
        string cacheKey,
        BitmapSource left,
        BitmapSource? right,
        double scale,
        FitMode mode,
        int pageIndex)
    {
        CancelQualityFitRequest();
        var requestId = ++_qualityFitRequestId;
        var fitCancellation = new CancellationTokenSource();
        _qualityFitCancellation = fitCancellation;
        var token = fitCancellation.Token;

        try
        {
            var result = await Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                var sharpenAmount = GetSharpenAmount(scale);
                token.ThrowIfCancellationRequested();
                var first = CreateCrispFitBitmap(left, scale, sharpenAmount);
                token.ThrowIfCancellationRequested();
                var second = right is null ? null : CreateCrispFitBitmap(right, scale, sharpenAmount);
                token.ThrowIfCancellationRequested();
                var fitted = new LoadedPage(first, second, second is not null);
                stopwatch.Stop();
                return new QualityFitResult(fitted, EstimateLoadedPageBytes(fitted), stopwatch.ElapsedMilliseconds, sharpenAmount);
            }, token);

            if (token.IsCancellationRequested
                || requestId != _qualityFitRequestId
                || _requestedPageIndex != pageIndex
                || _fitMode != mode
                || _qualityMode != ReaderQualityMode.Quality)
            {
                return;
            }

            AddQualityFitCache(cacheKey, result.Page, result.ByteSize);
            ApplyFittedReaderSources(result.Page);
            ApplyZoom(1);
            AppLogger.Info(
                "reader-quality-fit",
                $"{_book.Title} page={pageIndex + 1}, mode={mode}, scale={scale:0.###}, bytes={result.ByteSize / 1024 / 1024}MB, sharpen={result.SharpenAmount:0.###}, elapsed={result.ElapsedMs}ms, cache={_qualityFitCacheBytes / 1024 / 1024}MB");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Warn("reader-quality-fit", $"{_book.Title} crisp fit skipped: {ex.Message}");
        }
        finally
        {
            if (_qualityFitCancellation == fitCancellation)
            {
                _qualityFitCancellation.Dispose();
                _qualityFitCancellation = null;
            }
        }
    }

    private void CancelQualityFitRequest()
    {
        _qualityFitCancellation?.Cancel();
        _qualityFitCancellation?.Dispose();
        _qualityFitCancellation = null;
    }

    private void UpdateFitButtons()
    {
        SetFitButtonState(FitWidthButton, _fitMode == FitMode.Width);
        SetFitButtonState(FitHeightButton, _fitMode == FitMode.Height);
        SetFitButtonState(OriginalSizeButton, _fitMode == FitMode.Original);
        UpdateToolbarMenuLabels();
    }

    private static void SetFitButtonState(System.Windows.Controls.Button button, bool active)
    {
        button.Background = active ? FitActiveBg : FitInactiveBg;
        button.BorderBrush = active ? FitActiveBorder : FitInactiveBorder;
        button.Foreground = active ? FitActiveFg : FitInactiveFg;
    }

    private void ApplyZoom(double value)
    {
        _currentZoom = Math.Clamp(value, ZoomMin, ZoomMax);
        if (ImageScale is null) return;
        ImageScale.ScaleX = _currentZoom;
        ImageScale.ScaleY = _currentZoom;
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
            DoublePageGapText.Text = $"双页间距 {(int)Math.Round(e.NewValue)}";
        }

        if (IsLoaded && !_isLoadingViewerPreferences)
        {
            RestartDoublePageGapSaveTimer();
        }
    }

    private void ReadingModeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateToolbarMenuLabels();
        RequestPageLoad(_requestedPageIndex, immediate: true, forceReload: true);
    }

    private void WheelModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateToolbarMenuLabels();
        var value = WheelModeBox.SelectedIndex.ToString();
        _ = Task.Run(() => _database.SaveShortcut("reader.wheelmode", value));
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
        if (_fullscreenKeys.Contains(key))
        {
            ToggleFullscreen();
            return true;
        }
        if (_hideuiKeys.Contains(key))
        {
            SetControlsHidden(!_controlsHidden);
            return true;
        }
        switch (key)
        {
            case Key.E:
                SetFitMode(FitMode.Height);
                return true;
            case Key.Q:
                SetFitMode(FitMode.Width);
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
            case Key.OemComma:
                GoToPreviousBookmark();
                return true;
            case Key.OemPeriod:
                GoToNextBookmark();
                return true;
            case Key.M:
                ToggleCurrentPageBookmark();
                return true;
            default:
                return false;
        }
    }

    private void GoToPreviousBookmark()
    {
        if (_bookmarks.Count == 0)
        {
            StatusCatalogFeedback("没有标记。");
            return;
        }

        var prev = _bookmarks.Keys.Where(b => b < _requestedPageIndex).DefaultIfEmpty(-1).Max();
        if (prev < 0)
        {
            StatusCatalogFeedback("已经是第一个标记。");
            return;
        }

        RequestPageLoad(prev, immediate: true);
        StatusCatalogFeedback($"跳转到标记：第 {prev + 1} 页");
    }

    private void GoToNextBookmark()
    {
        if (_bookmarks.Count == 0)
        {
            StatusCatalogFeedback("没有标记。");
            return;
        }

        var next = _bookmarks.Keys.Where(b => b > _requestedPageIndex).DefaultIfEmpty(-1).Min();
        if (next < 0)
        {
            StatusCatalogFeedback("已经是最后一个标记。");
            return;
        }

        RequestPageLoad(next, immediate: true);
        StatusCatalogFeedback($"跳转到标记：第 {next + 1} 页");
    }

    private void RequestPageLoad(int pageIndex, bool immediate = false, bool forceReload = false)
    {
        if (_isClosing || _book.Pages.Count == 0)
        {
            return;
        }

        var safeIndex = Math.Clamp(pageIndex, 0, _book.Pages.Count - 1);
        _requestedPageIndex = safeIndex;
        UpdateSignButton();

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
        CancelQualityFitRequest();
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

            _currentLoadedPage = page;
            _currentRightToLeft = rightToLeft;
            RestoreOriginalReaderSources();

            NormalizeDisplayedImageSizing();
            ApplyDoublePageGap();
            ReaderScrollViewer.ScrollToVerticalOffset(0);
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

    private bool IsDoublePageMode() => (ReadingModeBox?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() == "双页";
    private bool IsRightToLeftMode() => (DirectionBox?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() == "从右到左";
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
    private sealed record PageCacheEntry(string Key, LoadedPage Page, long ByteSize = 0);
    private sealed record QualityFitResult(LoadedPage Page, long ByteSize, long ElapsedMs, double SharpenAmount);

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
            _qualityFitCache.Clear();
            _qualityFitCacheLru.Clear();
            _qualityFitCacheBytes = 0;
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
                TrimQualityFitCache();
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

    private void RestoreOriginalReaderSources()
    {
        if (_currentLoadedPage is not { } page)
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
            if (_currentRightToLeft)
            {
                ReaderImage.Source = page.Second;
                ReaderImageRight.Source = page.First;
            }
            else
            {
                ReaderImageRight.Source = page.Second;
            }
        }
    }

    private void ApplyFittedReaderSources(LoadedPage page)
    {
        ReaderImage.Source = page.First;
        ReaderImageRight.Source = null;
        ReaderImageRight.Visibility = Visibility.Collapsed;
        _displayedPageCount = 1;
        var leftSize = GetDevicePixelAlignedDipSize(page.First);
        ReaderImage.Width = leftSize.Width;
        ReaderImage.Height = leftSize.Height;
        ReaderImage.Stretch = Stretch.Fill;
        _pageSlotWidth = leftSize.Width;
        _pageSlotHeight = leftSize.Height;

        if (page.UseDouble && page.Second is not null)
        {
            _displayedPageCount = 2;
            ReaderImageRight.Visibility = Visibility.Visible;
            ReaderImageRight.Source = page.Second;
            var size = GetDevicePixelAlignedDipSize(page.Second);
            ReaderImageRight.Width = size.Width;
            ReaderImageRight.Height = size.Height;
            ReaderImageRight.Stretch = Stretch.Fill;
            _pageSlotWidth += size.Width;
            _pageSlotHeight = Math.Max(_pageSlotHeight, size.Height);
        }

        ApplyDoublePageGap();
    }

    private bool TryGetQualityFitCache(string key, out LoadedPage page)
    {
        lock (_pageCacheLock)
        {
            if (_qualityFitCache.TryGetValue(key, out var node))
            {
                _qualityFitCacheLru.Remove(node);
                _qualityFitCacheLru.AddFirst(node);
                page = node.Value.Page;
                return true;
            }
        }

        page = null!;
        return false;
    }

    private void AddQualityFitCache(string key, LoadedPage page, long byteSize)
    {
        lock (_pageCacheLock)
        {
            if (_qualityFitCache.TryGetValue(key, out var existing))
            {
                _qualityFitCacheBytes -= existing.Value.ByteSize;
                existing.Value = new PageCacheEntry(key, page, byteSize);
                _qualityFitCacheBytes += byteSize;
                _qualityFitCacheLru.Remove(existing);
                _qualityFitCacheLru.AddFirst(existing);
            }
            else
            {
                var node = new LinkedListNode<PageCacheEntry>(new PageCacheEntry(key, page, byteSize));
                _qualityFitCacheLru.AddFirst(node);
                _qualityFitCache[key] = node;
                _qualityFitCacheBytes += byteSize;
            }

            TrimQualityFitCache();
        }
    }

    private void TrimQualityFitCache()
    {
        var byteLimit = IsMemoryPressureHigh() ? MemoryPressureQualityFitCacheBytes : MaxQualityFitCacheBytes;
        while ((_qualityFitCacheLru.Count > MaxQualityFitCacheEntries || _qualityFitCacheBytes > byteLimit)
            && _qualityFitCacheLru.Last is not null)
        {
            var last = _qualityFitCacheLru.Last;
            _qualityFitCache.Remove(last.Value.Key);
            _qualityFitCacheBytes -= last.Value.ByteSize;
            _qualityFitCacheLru.RemoveLast();
        }

        if (_qualityFitCacheBytes < 0)
        {
            _qualityFitCacheBytes = 0;
        }
    }

    private static long EstimateLoadedPageBytes(LoadedPage page)
    {
        var total = EstimateBitmapBytes(page.First);
        if (page.Second is not null)
        {
            total += EstimateBitmapBytes(page.Second);
        }

        return total;
    }

    private static long EstimateBitmapBytes(BitmapSource source)
    {
        var bitsPerPixel = source.Format.BitsPerPixel > 0 ? source.Format.BitsPerPixel : 32;
        var stride = ((source.PixelWidth * bitsPerPixel + 31) / 32) * 4;
        return (long)stride * source.PixelHeight;
    }

    private static double GetSharpenAmount(double scale)
    {
        if (scale >= 0.85)
        {
            return 0.025;
        }

        return 0;
    }

    private static BitmapSource CreateCrispFitBitmap(BitmapSource source, double scale, double sharpenAmount)
    {
        var targetWidth = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
        var scaled = DownsampleInSteps(source, targetWidth, targetHeight);

        var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var stride = targetWidth * 4;
        var pixels = new byte[stride * targetHeight];
        converted.CopyPixels(pixels, stride, 0);
        if (sharpenAmount > 0)
        {
            SharpenBgraPixels(pixels, targetWidth, targetHeight, stride, sharpenAmount);
        }

        var result = BitmapSource.Create(targetWidth, targetHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static BitmapSource DownsampleInSteps(BitmapSource source, int targetWidth, int targetHeight)
    {
        BitmapSource current = source;
        var currentWidth = source.PixelWidth;
        var currentHeight = source.PixelHeight;

        while (currentWidth > targetWidth * 2 && currentHeight > targetHeight * 2)
        {
            var nextWidth = Math.Max(targetWidth, currentWidth / 2);
            var nextHeight = Math.Max(targetHeight, currentHeight / 2);
            current = ScaleBitmap(current, nextWidth, nextHeight);
            currentWidth = nextWidth;
            currentHeight = nextHeight;
        }

        return currentWidth == targetWidth && currentHeight == targetHeight
            ? current
            : ScaleBitmap(current, targetWidth, targetHeight);
    }

    private static BitmapSource ScaleBitmap(BitmapSource source, int targetWidth, int targetHeight)
    {
        var scaled = new TransformedBitmap(
            source,
            new ScaleTransform(
                (double)targetWidth / source.PixelWidth,
                (double)targetHeight / source.PixelHeight));
        scaled.Freeze();
        return scaled;
    }

    private static void SharpenBgraPixels(byte[] pixels, int width, int height, int stride, double amount)
    {
        if (width < 3 || height < 3 || amount <= 0)
        {
            return;
        }

        var original = (byte[])pixels.Clone();
        for (var y = 1; y < height - 1; y++)
        {
            var row = y * stride;
            var up = row - stride;
            var down = row + stride;
            for (var x = 1; x < width - 1; x++)
            {
                var i = row + x * 4;
                for (var c = 0; c < 3; c++)
                {
                    var center = original[i + c];
                    var edge = center * 4
                        - original[i - 4 + c]
                        - original[i + 4 + c]
                        - original[up + x * 4 + c]
                        - original[down + x * 4 + c];
                    pixels[i + c] = ClampToByte(center + edge * amount);
                }
            }
        }
    }

    private static byte ClampToByte(double value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return value >= 255 ? (byte)255 : (byte)Math.Round(value);
    }

    private void NormalizeQualityImageSizing()
    {
        if (ReaderImageRight.Visibility == Visibility.Visible
            && ReaderImage.Source is BitmapSource left
            && ReaderImageRight.Source is BitmapSource right)
        {
            var leftSize = GetDevicePixelAlignedDipSize(left);
            var rightSize = GetDevicePixelAlignedDipSize(right);
            ReaderImage.Width = leftSize.Width;
            ReaderImage.Height = leftSize.Height;
            ReaderImage.Stretch = Stretch.Fill;
            ReaderImageRight.Width = rightSize.Width;
            ReaderImageRight.Height = rightSize.Height;
            ReaderImageRight.Stretch = Stretch.Fill;
            _pageSlotWidth = leftSize.Width + rightSize.Width;
            _pageSlotHeight = Math.Max(leftSize.Height, rightSize.Height);
            return;
        }

        if (ReaderImage.Source is BitmapSource single)
        {
            var size = GetDevicePixelAlignedDipSize(single);
            ReaderImage.Width = size.Width;
            ReaderImage.Height = size.Height;
            ReaderImage.Stretch = Stretch.Fill;
            _pageSlotWidth = size.Width;
            _pageSlotHeight = size.Height;
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

    private System.Windows.Size GetDevicePixelAlignedDipSize(BitmapSource source)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
        var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
        return new System.Windows.Size(
            Math.Max(1, source.PixelWidth / scaleX),
            Math.Max(1, source.PixelHeight / scaleY));
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

        var zoom = _currentZoom;
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
        if (MoreMenu is not null) MoreMenu.IsOpen = false;
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
                ApplyZoom(_currentZoom + (e.Delta > 0 ? WheelZoomStep : -WheelZoomStep));
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

        SetControlsHidden(!_controlsHidden);
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

        var targetZoom = Math.Clamp(_holdZoomBaseValue * HoldZoomFactor, ZoomMin, ZoomMax);
        if (Math.Abs(_currentZoom - targetZoom) > 0.001)
        {
            ApplyZoom(targetZoom);
        }

        Dispatcher.InvokeAsync(
            () => ScrollZoomPointUnderMouse(imageHostPosition, pointerInViewport),
            DispatcherPriority.Loaded);
    }

    private void BeginHoldZoom(MouseButtonEventArgs e)
    {
        _isHoldZoomActive = true;
        _holdZoomBaseValue = _currentZoom;
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
        ApplyZoom(_holdZoomBaseValue);
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

            if (shortcuts.TryGetValue("reader.key.fullscreen", out var fsKeys) && fsKeys.Length > 0)
                _fullscreenKeys = ParseKeys(fsKeys);
            if (shortcuts.TryGetValue("reader.key.hideui", out var hideKeys) && hideKeys.Length > 0)
                _hideuiKeys = ParseKeys(hideKeys);
            if (shortcuts.TryGetValue("reader.key.pagination", out var pagKeys) && pagKeys.Length > 0)
                _paginationKeys = ParseKeys(pagKeys);
        }
        finally
        {
            _isLoadingViewerPreferences = false;
        }

        ApplyDoublePageGap();
        UpdateQualityModeButton();
        UpdateToolbarMenuLabels();
    }

    private void UpdateQualityModeButton()
    {
        if (QualityModeButton is null)
        {
            return;
        }

        QualityModeButton.Content = _qualityMode == ReaderQualityMode.Quality ? "质量" : "性能";
        QualityModeButton.ToolTip = _qualityMode == ReaderQualityMode.Quality
            ? "当前为质量模式：原图解码，适配模式生成清晰适配图；原始模式为 1:1"
            : "当前为性能模式：按视口降采样，降低内存压力";
        UpdateToolbarMenuLabels();
    }

    private void UpdateToolbarMenuLabels()
    {
        if (DoublePageGapPanel is not null)
        {
            DoublePageGapPanel.Visibility = IsDoublePageMode() ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static string GetSelectedComboText(System.Windows.Controls.ComboBox? comboBox, string fallback)
    {
        return (comboBox?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? fallback;
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
            || DirectionBox?.IsDropDownOpen == true
            || MoreMenu?.IsOpen == true;
    }

    private void MoreMenuButton_Click(object sender, RoutedEventArgs e)
    {
        OpenButtonContextMenu(MoreMenuButton);
    }

    private void OpenButtonContextMenu(System.Windows.Controls.Button button)
    {
        if (button.ContextMenu is null)
        {
            return;
        }

        CloseReaderDropdowns();
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        button.ContextMenu.IsOpen = true;
    }

    private void SinglePageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ReadingModeBox.SelectedIndex = 0;
    }

    private void DoublePageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ReadingModeBox.SelectedIndex = 1;
    }

    private void LeftToRightMenuItem_Click(object sender, RoutedEventArgs e)
    {
        DirectionBox.SelectedIndex = 0;
    }

    private void RightToLeftMenuItem_Click(object sender, RoutedEventArgs e)
    {
        DirectionBox.SelectedIndex = 1;
    }

    private void WheelPageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        WheelModeBox.SelectedIndex = 0;
    }

    private void WheelZoomMenuItem_Click(object sender, RoutedEventArgs e)
    {
        WheelModeBox.SelectedIndex = 1;
    }

    private void WheelScrollMenuItem_Click(object sender, RoutedEventArgs e)
    {
        WheelModeBox.SelectedIndex = 2;
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

    private static List<Key> ParseKeys(string text)
    {
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Enum.TryParse<Key>(value, true, out var key) ? key : (Key?)null)
            .Where(key => key is not null)
            .Select(key => key!.Value)
            .Distinct()
            .ToList();
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
        UpdateToolbarMenuLabels();
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
        ToggleFullscreen();
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
        ScheduleFitModeApply();
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
        ScheduleFitModeApply();
    }

    private void UpdatePresentationButton()
    {
        if (FullscreenButton is null)
        {
            return;
        }

        FullscreenButton.Content = _isFullscreen ? "窗口" : "全屏";
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

    private void PageCatalogOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HidePageCatalog();
        e.Handled = true;
    }

    private void PageCatalogContent_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void EnsurePageCatalogItems()
    {
        if (PageCatalogItems.Count == _book.Pages.Count)
        {
            return;
        }

        _bookmarks = _database.LoadBookmarks(_book.Id);
        UpdateSignButton();
        PageCatalogItems.Clear();
        var catalogItems = new List<PageCatalogItem>(_book.Pages.Count);
        for (var i = 0; i < _book.Pages.Count; i++)
        {
            catalogItems.Add(new PageCatalogItem(i, _book.Pages[i])
            {
                IsBookmarked = _bookmarks.ContainsKey(i),
                BookmarkLabel = _bookmarks.GetValueOrDefault(i, "")
            });
        }
        PageCatalogItems.AddRange(catalogItems);
        AssignBookmarkColors();
    }

    private void MarkCatalogBookmark_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PageCatalogItem item)
        {
            return;
        }

        var dialog = new BookmarkLabelDialog(item.PageIndex) { Owner = this };
        var label = "";
        if (dialog.ShowDialog() == true && !dialog.IsSkipped)
        {
            label = dialog.BookmarkLabel;
        }

        item.IsBookmarked = true;
        item.BookmarkLabel = label;
        _bookmarks[item.PageIndex] = label;
        _ = Task.Run(() => _database.AddBookmark(_book.Id, item.PageIndex, label));
        AssignBookmarkColors();
        UpdateSignButton();
        StatusCatalogFeedback(label.Length > 0
            ? $"已标记第 {item.PageIndex + 1} 页：「{label}」"
            : $"已标记第 {item.PageIndex + 1} 页。");
    }

    private void EditCatalogBookmark_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PageCatalogItem item)
        {
            return;
        }

        var dialog = new BookmarkLabelDialog(item.PageIndex, item.BookmarkLabel) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var label = dialog.IsSkipped ? "" : dialog.BookmarkLabel;
        item.BookmarkLabel = label;
        _bookmarks[item.PageIndex] = label;
        _ = Task.Run(() => _database.AddBookmark(_book.Id, item.PageIndex, label));
        AssignBookmarkColors();
        UpdateSignButton();
        StatusCatalogFeedback(label.Length > 0
            ? $"已更新标记：「{label}」"
            : "已清空标记内容。");
    }

    private void RemoveCatalogBookmark_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PageCatalogItem item)
        {
            return;
        }

        item.IsBookmarked = false;
        item.BookmarkLabel = "";
        _bookmarks.Remove(item.PageIndex);
        _ = Task.Run(() => _database.RemoveBookmark(_book.Id, item.PageIndex));
        AssignBookmarkColors();
        UpdateSignButton();
        StatusCatalogFeedback($"已取消标记第 {item.PageIndex + 1} 页。");
    }

    private void SignButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleCurrentPageBookmark();
    }

    private void ToggleCurrentPageBookmark()
    {
        var isBookmarked = _bookmarks.ContainsKey(_requestedPageIndex);
        if (isBookmarked)
        {
            _bookmarks.Remove(_requestedPageIndex);
            _ = Task.Run(() => _database.RemoveBookmark(_book.Id, _requestedPageIndex));
            SyncCatalogItemBookmarkLabel();
            AssignBookmarkColors();
            UpdateSignButton();
            StatusCatalogFeedback($"已取消标记第 {_requestedPageIndex + 1} 页。");
            return;
        }

        var dialog = new BookmarkLabelDialog(_requestedPageIndex)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var label = dialog.IsSkipped ? "" : dialog.BookmarkLabel;
        _bookmarks[_requestedPageIndex] = label;
        _ = Task.Run(() => _database.AddBookmark(_book.Id, _requestedPageIndex, label));
        SyncCatalogItemBookmarkLabel();
        AssignBookmarkColors();
        UpdateSignButton();
        StatusCatalogFeedback(label.Length > 0
            ? $"已标记第 {_requestedPageIndex + 1} 页：「{label}」"
            : $"已标记第 {_requestedPageIndex + 1} 页。");
    }

    private void UpdateSignButton()
    {
        var isBookmarked = _bookmarks.ContainsKey(_requestedPageIndex);
        var label = isBookmarked ? _bookmarks[_requestedPageIndex] : "";
        SignButton.Content = isBookmarked
            ? (label.Length > 0 ? $"✓{label}" : "✓已标记")
            : "标记";
        SignButton.Opacity = isBookmarked ? 1.0 : 0.7;
    }

    private void SyncCatalogItemBookmarkLabel()
    {
        var item = PageCatalogItems.FirstOrDefault(ci => ci.PageIndex == _requestedPageIndex);
        if (item is not null)
        {
            item.IsBookmarked = _bookmarks.ContainsKey(_requestedPageIndex);
            item.BookmarkLabel = _bookmarks.GetValueOrDefault(_requestedPageIndex, "");
        }
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

    private void AssignBookmarkColors()
    {
        var colors = _database.LoadSetting("mark.color_group", "A") == "B" ? MarkColorGroupB : MarkColorGroupA;
        var sorted = _bookmarks.Keys.OrderBy(x => x).ToList();
        foreach (var item in PageCatalogItems)
        {
            if (item.IsBookmarked)
            {
                var colorIndex = sorted.IndexOf(item.PageIndex);
                item.BookmarkColor = colors[colorIndex >= 0 ? colorIndex % colors.Length : 0];
            }
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
        _book.CoverImage = _coverPipeline is not null
            ? await _coverPipeline.LoadAsync(book)
            : await Task.Run(() => ImageLoader.LoadBitmap(item.Path, 240));
        _book.NotifyAll();
        StatusCatalogFeedback($"已将第 {item.PageIndex + 1} 页设为封面。");
    }

    private void StatusCatalogFeedback(string message)
    {
        _boundaryHint = message;
        UpdateNavigationState();
    }

}
