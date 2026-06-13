using MangaReader.Native.Models;
using MangaReader.Native.Services;
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
    private readonly DispatcherTimer _controlsRevealTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
    private readonly MangaBook _book;
    private readonly LibraryDatabase _database;
    private readonly List<Key> _nextKeys;
    private readonly List<Key> _prevKeys;
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
    private int _backgroundMode;

    private enum FitMode
    {
        Width,
        Height
    }

    public ReaderWindow(MangaBook book, LibraryDatabase database, List<Key> nextKeys, List<Key> prevKeys)
    {
        InitializeComponent();
        _book = book;
        _database = database;
        _nextKeys = nextKeys;
        _prevKeys = prevKeys;
        Title = book.Title;
        TitleText.Text = book.Title;
        _controlsRevealTimer.Tick += ControlsRevealTimer_Tick;
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
        UpdateZoomText();
    }

    private void ReaderWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _controlsRevealTimer.Stop();
        _database.SaveProgress(_book);
        _database.SaveShortcut("reader.wheelmode", WheelModeBox.SelectedIndex.ToString());
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
            _boundaryHint = "已经是最后一页";
            UpdateNavigationState();
            return;
        }

        LoadPage(targetIndex);
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
        UpdateZoomText();
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
        button.Background = active ? BrushFrom("#E8F8FAFC") : BrushFrom("#1A0F172A");
        button.BorderBrush = active ? BrushFrom("#F0FFFFFF") : BrushFrom("#22FFFFFF");
        button.Foreground = active ? BrushFrom("#0F172A") : BrushFrom("#F9FAFB");
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ImageScale is null) return;
        ImageScale.ScaleX = e.NewValue;
        ImageScale.ScaleY = e.NewValue;
        UpdateZoomText();
    }

    private void ReadingModeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded) LoadPage(_book.LastReadPageIndex);
    }

    private void WheelModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateZoomText();
        if (IsLoaded)
        {
            _database.SaveShortcut("reader.wheelmode", WheelModeBox.SelectedIndex.ToString());
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
            if (_isFullscreen)
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
                ToggleFullscreen();
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
        var requestId = ++_pageLoadRequestId;
        var safeIndex = Math.Clamp(pageIndex, 0, _book.Pages.Count - 1);
        _boundaryHint = "";
        var firstPath = _book.Pages[safeIndex];
        var doublePageMode = IsDoublePageMode();
        var rightToLeft = IsRightToLeftMode();

        try
        {
            var page = await Task.Run(() =>
            {
                var first = ImageLoader.LoadBitmap(firstPath, 2600);
                var useDouble = doublePageMode && safeIndex + 1 < _book.Pages.Count && !IsLandscape(first);
                BitmapSource? second = null;
                if (useDouble)
                {
                    second = ImageLoader.LoadBitmap(_book.Pages[safeIndex + 1], 2600);
                }

                return new LoadedPage(first, second, useDouble);
            });

            if (requestId != _pageLoadRequestId)
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

            _book.LastReadPageIndex = safeIndex;
            _database.SaveProgress(_book);
            UpdateNavigationState();
            _ = Dispatcher.InvokeAsync(ApplyFitMode, DispatcherPriority.Loaded);
            HideReaderMessage();
            PageText.Text = "";
            PlayPageFade();
            AppLogger.Info("reader-load-page", $"Loaded page {_book.LastReadPageIndex + 1} for {_book.Title}.");
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

    private double GetDisplayedPixelWidth()
    {
        var left = ReaderImage.Source is BitmapSource l ? l.Width : 0;
        var right = ReaderImageRight.Visibility == Visibility.Visible && ReaderImageRight.Source is BitmapSource r ? r.Width : 0;
        return left + right;
    }

    private double GetDisplayedPixelHeight()
    {
        var left = ReaderImage.Source is BitmapSource l ? l.Height : 0;
        var right = ReaderImageRight.Visibility == Visibility.Visible && ReaderImageRight.Source is BitmapSource r ? r.Height : 0;
        return Math.Max(left, right);
    }

    private double GetAvailableContentWidth()
    {
        var viewportWidth = ReaderScrollViewer.ViewportWidth > 0 ? ReaderScrollViewer.ViewportWidth : ReaderScrollViewer.ActualWidth;
        var hostMargin = ImageHost.Margin.Left + ImageHost.Margin.Right;
        var imageMargin = ReaderImage.Margin.Left + ReaderImage.Margin.Right;
        var totalImageMargins = ReaderImageRight.Visibility == Visibility.Visible ? imageMargin * 2 : imageMargin;
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
        PageText.Text = $"{_book.LastReadPageIndex + 1}-{endPage} / {_book.PageCount}";
        if (!string.IsNullOrWhiteSpace(_boundaryHint))
        {
            PageText.Text += $"  ·  {_boundaryHint}";
        }

        var isFirst = _book.LastReadPageIndex <= 0;
        var isLast = _book.LastReadPageIndex + _displayedPageCount >= _book.PageCount;
        PreviousPageButton.IsEnabled = !isFirst;
        NextPageButton.IsEnabled = !isLast;
    }

    private void UpdateZoomText()
    {
        if (ZoomText is null || WheelModeBox is null)
        {
            return;
        }

        var wheelMode = WheelModeBox.SelectedIndex switch
        {
            1 => "缩放",
            2 => "滚动",
            _ => "翻页"
        };

        var fitMode = _fitMode == FitMode.Width ? "适宽" : "适高";
        ZoomText.Text = $"{fitMode} · {(int)Math.Round(ZoomSlider.Value * 100)}% · {wheelMode}";
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
        if (e.ClickCount > 1)
        {
            SetControlsHidden(!_controlsHidden);
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            NavigateByClickPosition(e.GetPosition(ReaderScrollViewer));
            e.Handled = true;
            return;
        }

        _isHoldZoomActive = true;
        _holdZoomBaseValue = ZoomSlider.Value;
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

        var contentPoint = ImageHost.TranslatePoint(imageHostPosition, ImageScrollContent);
        ReaderScrollViewer.ScrollToHorizontalOffset(Math.Max(0, contentPoint.X - pointerInViewport.X));
        ReaderScrollViewer.ScrollToVerticalOffset(Math.Max(0, contentPoint.Y - pointerInViewport.Y));
    }

    private void ReleaseHoldZoom()
    {
        if (!_isHoldZoomActive)
        {
            return;
        }

        _isHoldZoomActive = false;
        ZoomSlider.Value = _holdZoomBaseValue;
        if (Mouse.Captured == ReaderScrollViewer)
        {
            Mouse.Capture(null);
        }
    }

    private void LoadViewerPreferences()
    {
        var shortcuts = _database.LoadShortcuts();
        if (shortcuts.TryGetValue("reader.wheelmode", out var wheelMode)
            && int.TryParse(wheelMode, out var wheelModeIndex)
            && wheelModeIndex >= 0
            && wheelModeIndex < WheelModeBox.Items.Count)
        {
            WheelModeBox.SelectedIndex = wheelModeIndex;
        }
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

    private void PlayPageFade()
    {
        MotionService.PlayPageSwapFeedback(ImageHost);
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
        var (outer, page, label) = _backgroundMode switch
        {
            1 => ("#F8FAFC", "#FFFFFF", "白"),
            2 => ("#EDE1CC", "#FDF6E7", "纸"),
            _ => ("#050608", "#050608", "黑")
        };

        var outerBrush = BrushFrom(outer);
        var pageBrush = BrushFrom(page);
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

    private static SolidColorBrush BrushFrom(string color)
    {
        return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
    }

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            _isFullscreen = false;
            WindowStyle = _previousWindowStyle;
            WindowState = _previousWindowState;
            FullscreenButton.Content = "全屏";
        }
        else
        {
            _isFullscreen = true;
            _previousWindowStyle = WindowStyle;
            _previousWindowState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            FullscreenButton.Content = "退出";
        }
    }
}
