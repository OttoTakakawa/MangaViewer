using MangaReader.Native.Models;
using MangaReader.Native.Services;
using System.Windows;
using System.Windows.Input;
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
    private string _boundaryHint = "";
    private bool _controlsHidden;
    private bool _isHoldZoomActive;
    private bool _fitPendingInitialLoad = true;
    private double _holdZoomBaseValue = 1;
    private int _pageLoadRequestId;

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
        Closing += ReaderWindow_Closing;
        Loaded += ReaderWindow_Loaded;
        LoadViewerPreferences();
        PageText.Text = "正在准备阅读器...";
    }

    private void ReaderWindow_Loaded(object sender, RoutedEventArgs e)
    {
        AppLogger.Info("reader-open", $"Reader loaded: {_book.Title}, pages={_book.Pages.Count}, start={_book.LastReadPageIndex + 1}");
        Dispatcher.InvokeAsync(() => LoadPage(_book.LastReadPageIndex), DispatcherPriority.ApplicationIdle);
        UpdateZoomText();
        RestartControlsRevealTimer();
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
        var width = GetDisplayedPixelWidth();
        var availableWidth = GetAvailableContentWidth();
        if (width > 0 && availableWidth > 0)
        {
            ZoomSlider.Value = Math.Clamp(availableWidth / width, ZoomSlider.Minimum, ZoomSlider.Maximum);
        }
    }

    private void FitHeight_Click(object sender, RoutedEventArgs e)
    {
        var height = GetDisplayedPixelHeight();
        var availableHeight = GetAvailableContentHeight();
        if (height > 0 && availableHeight > 0)
        {
            ZoomSlider.Value = Math.Clamp(availableHeight / height, ZoomSlider.Minimum, ZoomSlider.Maximum);
        }
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
        else if (e.Key == Key.Escape && _controlsHidden)
        {
            SetControlsHidden(false);
            e.Handled = true;
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
            if (_fitPendingInitialLoad)
            {
                _fitPendingInitialLoad = false;
                FitHeight_Click(this, new RoutedEventArgs());
            }
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

        ZoomText.Text = $"{(int)Math.Round(ZoomSlider.Value * 100)}% · {wheelMode}";
    }

    private void ToggleControlsButton_Click(object sender, RoutedEventArgs e)
    {
        SetControlsHidden(!_controlsHidden);
    }

    private void HiddenControlsBadge_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SetControlsHidden(false);
        RestartControlsRevealTimer();
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
                if (!_controlsHidden)
                {
                    RestartControlsRevealTimer();
                }

                e.Handled = true;
                break;
            case 2:
                if (!_controlsHidden)
                {
                    RestartControlsRevealTimer();
                }

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

                if (!_controlsHidden)
                {
                    RestartControlsRevealTimer();
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
            RestartControlsRevealTimer();
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
        if (_controlsHidden)
        {
            return;
        }

        RestartControlsRevealTimer();
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
        if (_controlsHidden || !IsLoaded)
        {
            return;
        }

        _controlsRevealTimer.Stop();
        _controlsRevealTimer.Start();
    }

    private void ControlsRevealTimer_Tick(object? sender, EventArgs e)
    {
        _controlsRevealTimer.Stop();
        if (_isHoldZoomActive)
        {
            return;
        }

        if (IsAnyReaderDropdownOpen())
        {
            RestartControlsRevealTimer();
            return;
        }

        SetControlsHidden(true);
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
}
