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
    private readonly DispatcherTimer _controlsRevealTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
    private readonly DispatcherTimer _doublePageGapSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private readonly MangaBook _book;
    private readonly LibraryDatabase _database;
    private readonly List<Key> _nextKeys;
    private readonly List<Key> _prevKeys;
    private readonly Func<MangaBook, MangaBook?>? _nextBookResolver;
    private readonly Action<MangaBook>? _openBookRequest;
    private int _displayedPageCount = 1;
    private FitMode _fitMode = FitMode.Height;
    private string _boundaryHint = "";
    private bool _controlsHidden;
    private bool _isHoldZoomActive;
    private bool _isFullscreen;
    private WindowStyle _previousWindowStyle;
    private WindowState _previousWindowState;
    private double _holdZoomBaseValue = 1;
    private int _pageLoadRequestId;
    private CancellationTokenSource? _pageLoadCancellation;
    private int _backgroundMode;
    private bool _isLoadingViewerPreferences;
    private bool _isNextBookPromptOpen;
    private MangaBook? _pendingNextBook;
    private CancellationTokenSource? _catalogLoadCancellation;
    private System.Windows.Point? _holdZoomLastPointerInViewport;

    public ObservableCollection<PageCatalogItem> PageCatalogItems { get; } = [];

    private enum FitMode
    {
        Width,
        Height
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
        DataContext = this;
        Title = book.Title;
        TitleText.Text = book.Title;
        _controlsRevealTimer.Tick += ControlsRevealTimer_Tick;
        _doublePageGapSaveTimer.Tick += DoublePageGapSaveTimer_Tick;
        KeyDown += ReaderWindow_KeyDown;
        SizeChanged += ReaderWindow_SizeChanged;
        Closing += ReaderWindow_Closing;
        Loaded += ReaderWindow_Loaded;
        LoadViewerPreferences();
        ApplyReaderBackground();
        UpdateFitButtons();
    }

    private void ReaderWindow_Loaded(object sender, RoutedEventArgs e)
    {
        AppLogger.Info("reader-open", $"Reader loaded: {_book.Title}, pages={_book.Pages.Count}, start={_book.LastReadPageIndex + 1}");
        Dispatcher.InvokeAsync(() => LoadPage(_book.LastReadPageIndex), DispatcherPriority.ApplicationIdle);
    }

    private void ReaderWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _controlsRevealTimer.Stop();
        _doublePageGapSaveTimer.Stop();
        _pageLoadCancellation?.Cancel();
        _pageLoadCancellation?.Dispose();
        _pageLoadCancellation = null;
        _catalogLoadCancellation?.Cancel();
        _catalogLoadCancellation?.Dispose();
        _catalogLoadCancellation = null;
        var book = _book;
        var wheelMode = WheelModeBox.SelectedIndex.ToString();
        _ = Task.Run(() =>
        {
            _database.SaveProgress(book);
            _database.SaveShortcut("reader.wheelmode", wheelMode);
        });
        SaveDoublePageGapPreference();
    }

    private void PreviousPage_Click(object sender, RoutedEventArgs e)
    {
        var targetIndex = _book.LastReadPageIndex - GetPreviousStep();
        if (targetIndex < 0)
        {
            _boundaryHint = "已经是第一页";
            UpdateNavigationState();
            return;
        }

        LoadPage(targetIndex);
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        var targetIndex = _book.LastReadPageIndex + _displayedPageCount;
        if (targetIndex >= _book.Pages.Count)
        {
            TryGoToNextBook();
            return;
        }

        LoadPage(targetIndex);
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
        if (_fitMode == FitMode.Width)
        {
            ApplyFitWidth();
            return;
        }

        ApplyFitHeight();
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
        if (IsLoaded) LoadPage(_book.LastReadPageIndex);
    }

    private void WheelModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            var value = WheelModeBox.SelectedIndex.ToString();
            _ = Task.Run(() => _database.SaveShortcut("reader.wheelmode", value));
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

    private async void LoadPage(int pageIndex)
    {
        if (_book.Pages.Count == 0) return;
        HideNextBookPrompt();
        var requestId = ++_pageLoadRequestId;
        _pageLoadCancellation?.Cancel();
        _pageLoadCancellation?.Dispose();
        var loadCancellation = new CancellationTokenSource();
        _pageLoadCancellation = loadCancellation;
        var cancellationToken = loadCancellation.Token;
        var safeIndex = Math.Clamp(pageIndex, 0, _book.Pages.Count - 1);
        _boundaryHint = "";
        var firstPath = _book.Pages[safeIndex];
        var doublePageMode = IsDoublePageMode();
        var rightToLeft = IsRightToLeftMode();

        try
        {
            var singleDecodeWidth = GetDecodePixelWidth(false);
            var doubleDecodeWidth = GetDecodePixelWidth(true);
            var page = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = ImageLoader.LoadBitmap(firstPath, singleDecodeWidth);
                cancellationToken.ThrowIfCancellationRequested();
                var useDouble = doublePageMode && safeIndex + 1 < _book.Pages.Count && !IsLandscape(first);
                BitmapSource? second = null;
                if (useDouble)
                {
                    second = ImageLoader.LoadBitmap(_book.Pages[safeIndex + 1], doubleDecodeWidth);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return new LoadedPage(first, second, useDouble);
            }, cancellationToken);

            if (requestId != _pageLoadRequestId || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ReaderImage.Source = page.First;
            ReaderImageRight.Source = null;
            ReaderImageRight.Visibility = Visibility.Collapsed;
            _displayedPageCount = 1;
            AppLogger.Info(
                "reader-load-page",
                $"Decoded page {safeIndex + 1} for {_book.Title}. size={page.First.PixelWidth}x{page.First.PixelHeight}, path={firstPath}");

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
            var progressBook = _book;
            await Task.Run(() => _database.SaveProgress(progressBook));
            UpdateNavigationState();
            _ = Dispatcher.InvokeAsync(ApplyFitMode, DispatcherPriority.Loaded);
            HideReaderMessage();
            AppLogger.Info("reader-load-page", $"Loaded page {_book.LastReadPageIndex + 1} for {_book.Title}.");
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
    private static bool IsLandscape(BitmapSource image) => image.PixelWidth > image.PixelHeight * 1.15;
    private sealed record LoadedPage(BitmapSource First, BitmapSource? Second, bool UseDouble);

    private void NormalizeDisplayedImageSizing()
    {
        if (ReaderImageRight.Visibility == Visibility.Visible
            && ReaderImage.Source is BitmapSource left
            && ReaderImageRight.Source is BitmapSource right)
        {
            var normalizedHeight = Math.Max(left.Height, right.Height);
            ReaderImage.Height = normalizedHeight;
            ReaderImageRight.Height = normalizedHeight;
            ReaderImage.Width = double.NaN;
            ReaderImageRight.Width = double.NaN;
            ReaderImage.Stretch = Stretch.Uniform;
            ReaderImageRight.Stretch = Stretch.Uniform;
            return;
        }

        ReaderImage.Height = double.NaN;
        ReaderImageRight.Height = double.NaN;
        ReaderImage.Width = double.NaN;
        ReaderImageRight.Width = double.NaN;
        ReaderImage.Stretch = Stretch.None;
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
        var left = ReaderImage.Source is BitmapSource l ? GetNormalizedPageWidth(l) : 0;
        var right = ReaderImageRight.Visibility == Visibility.Visible && ReaderImageRight.Source is BitmapSource r ? GetNormalizedPageWidth(r) : 0;
        return left + right;
    }

    private double GetDisplayedPixelHeight()
    {
        var left = ReaderImage.Source is BitmapSource l ? l.Height : 0;
        var right = ReaderImageRight.Visibility == Visibility.Visible && ReaderImageRight.Source is BitmapSource r ? r.Height : 0;
        return Math.Max(left, right);
    }

    private double GetNormalizedPageWidth(BitmapSource image)
    {
        if (ReaderImageRight.Visibility != Visibility.Visible
            || ReaderImage.Source is not BitmapSource left
            || ReaderImageRight.Source is not BitmapSource right)
        {
            return image.Width;
        }

        var normalizedHeight = Math.Max(left.Height, right.Height);
        return image.Height > 0 ? image.Width * normalizedHeight / image.Height : image.Width;
    }

    private int GetDecodePixelWidth(bool isDoublePage)
    {
        var viewport = ReaderScrollViewer.ViewportWidth > 0 ? ReaderScrollViewer.ViewportWidth : ReaderScrollViewer.ActualWidth;
        if (viewport <= 0) viewport = 960;
        var zoom = ZoomSlider?.Value ?? 1.0;
        var perPage = isDoublePage ? viewport / 2.0 : viewport;
        var decoded = perPage * zoom * 1.2;
        return (int)Math.Clamp(decoded, 800, 2600);
    }

    private double GetAvailableContentWidth()
    {
        var viewportWidth = ReaderScrollViewer.ViewportWidth > 0 ? ReaderScrollViewer.ViewportWidth : ReaderScrollViewer.ActualWidth;
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
        var viewportHeight = ReaderScrollViewer.ViewportHeight > 0 ? ReaderScrollViewer.ViewportHeight : ReaderScrollViewer.ActualHeight;
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

    private void ReaderScrollViewer_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PageCatalogOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        _isHoldZoomActive = true;
        _holdZoomBaseValue = ZoomSlider.Value;
        _holdZoomLastPointerInViewport = null;
        try
        {
            Mouse.Capture(ReaderScrollViewer);
            UpdateHoldZoom(e.GetPosition(ImageHost), e.GetPosition(ReaderScrollViewer));
            e.Handled = true;
        }
        catch
        {
            ReleaseHoldZoom();
            throw;
        }
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

    private void ReaderScrollViewer_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseHoldZoom();
        e.Handled = true;
    }

    private void ReaderRoot_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
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
        if (Mouse.Captured == ReaderScrollViewer)
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
        }
        finally
        {
            _isLoadingViewerPreferences = false;
        }

        ApplyDoublePageGap();
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
        Dispatcher.InvokeAsync(ApplyFitMode, DispatcherPriority.Loaded);
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
        LoadPage(item.PageIndex);
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
