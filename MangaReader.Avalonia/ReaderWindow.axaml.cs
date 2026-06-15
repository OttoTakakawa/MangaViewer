using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using MangaReader.Core.Models;
using MangaReader.Core.Services;

namespace MangaReader.Avalonia;

public sealed partial class ReaderWindow : Window
{
    private const int MaxCachedPages = 5;
    private static readonly TimeSpan AdjacentPreloadDelay = TimeSpan.FromMilliseconds(90);

    private readonly MangaBook _book;
    private readonly LibraryDatabase? _database;
    private readonly Dictionary<int, Bitmap> _pageCache = [];
    private readonly LinkedList<int> _lru = [];
    private CancellationTokenSource? _preloadCancellation;
    private int _pageIndex;
    private double _zoom = 1;

    private enum FitMode
    {
        Width,
        Height,
        Original
    }

    public ReaderWindow()
        : this(new MangaBook { Title = "阅读器" }, null)
    {
    }

    public ReaderWindow(MangaBook book, LibraryDatabase? database)
    {
        InitializeComponent();
        _book = book;
        _database = database;
        _pageIndex = Math.Clamp(book.LastReadPageIndex, 0, Math.Max(book.Pages.Count - 1, 0));
        Title = book.Title;
        TitleText.Text = book.Title;
        Closing += (_, _) => DisposeCache();
        RenderPage();
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Previous_Click(object? sender, RoutedEventArgs e)
    {
        Navigate(-1);
    }

    private void Next_Click(object? sender, RoutedEventArgs e)
    {
        Navigate(1);
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        OpenPath(_book.FolderPath);
    }

    private void ReaderWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.A:
            case Key.Left:
                Navigate(-1);
                e.Handled = true;
                break;
            case Key.D:
            case Key.Right:
            case Key.Space:
                Navigate(1);
                e.Handled = true;
                break;
            case Key.W:
                SetFitMode(FitMode.Height);
                e.Handled = true;
                break;
            case Key.E:
                SetFitMode(FitMode.Width);
                e.Handled = true;
                break;
            case Key.Q:
                ToggleReadingMode();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void ReaderOption_Changed(object? sender, SelectionChangedEventArgs e)
    {
        RenderPage();
    }

    private void ZoomSlider_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        _zoom = Math.Clamp(e.NewValue, ZoomSlider.Minimum, ZoomSlider.Maximum);
        ApplyImageSizes();
    }

    private void ReaderScrollViewer_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (GetFitMode() is not FitMode.Original)
        {
            ApplyFitMode();
        }
    }

    private void Navigate(int delta)
    {
        if (_book.Pages.Count == 0)
        {
            return;
        }

        _pageIndex = Math.Clamp(_pageIndex + delta, 0, _book.Pages.Count - 1);
        _book.LastReadPageIndex = _pageIndex;
        _database?.SaveProgress(_book);
        RenderPage();
    }

    private void RenderPage()
    {
        PageImageLeft.Source = null;
        PageImageRight.Source = null;
        PageImageRight.IsVisible = false;

        if (_book.Pages.Count == 0)
        {
            PlaceholderText.IsVisible = true;
            PageText.Text = "0 / 0";
            return;
        }

        var pageIndexes = GetVisiblePageIndexes().ToArray();
        PageText.Text = pageIndexes.Length == 2
            ? $"{pageIndexes[0] + 1}-{pageIndexes[1] + 1} / {_book.Pages.Count}"
            : $"{_pageIndex + 1} / {_book.Pages.Count}";
        PlaceholderText.IsVisible = false;

        try
        {
            PageImageLeft.Source = LoadPage(pageIndexes[0]);
            if (pageIndexes.Length == 2)
            {
                PageImageRight.Source = LoadPage(pageIndexes[1]);
                PageImageRight.IsVisible = true;
            }

            ApplyFitMode();
            ScheduleAdjacentPreload();
        }
        catch
        {
            PlaceholderText.IsVisible = true;
        }
    }

    private IEnumerable<int> GetVisiblePageIndexes()
    {
        yield return _pageIndex;

        if (!IsDoublePageMode())
        {
            yield break;
        }

        var second = _pageIndex + 1;
        if (second < _book.Pages.Count)
        {
            yield return second;
        }
    }

    private Bitmap LoadPage(int index)
    {
        if (_pageCache.TryGetValue(index, out var cached))
        {
            Touch(index);
            return cached;
        }

        var path = _book.Pages[index];
        using var stream = File.OpenRead(path);
        var bitmap = new Bitmap(stream);
        AddToCache(index, bitmap);
        return bitmap;
    }

    private void AddToCache(int index, Bitmap bitmap)
    {
        if (_pageCache.ContainsKey(index))
        {
            _pageCache[index].Dispose();
            _pageCache[index] = bitmap;
            Touch(index);
            return;
        }

        _pageCache[index] = bitmap;
        _lru.AddFirst(index);
        while (_lru.Count > MaxCachedPages)
        {
            var last = _lru.Last?.Value;
            if (last is null)
            {
                break;
            }

            _lru.RemoveLast();
            if (_pageCache.Remove(last.Value, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    private void Touch(int index)
    {
        var node = _lru.Find(index);
        if (node is null)
        {
            _lru.AddFirst(index);
            return;
        }

        _lru.Remove(node);
        _lru.AddFirst(node);
    }

    private void ScheduleAdjacentPreload()
    {
        _preloadCancellation?.Cancel();
        _preloadCancellation?.Dispose();
        _preloadCancellation = new CancellationTokenSource();
        var token = _preloadCancellation.Token;
        var candidates = GetAdjacentPreloadCandidates().ToArray();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AdjacentPreloadDelay, token).ConfigureAwait(false);
                foreach (var index in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    if (_pageCache.ContainsKey(index))
                    {
                        continue;
                    }

                    _ = LoadPage(index);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }, token);
    }

    private IEnumerable<int> GetAdjacentPreloadCandidates()
    {
        var step = IsDoublePageMode() ? 2 : 1;
        var next = _pageIndex + step;
        var previous = _pageIndex - step;
        if (next < _book.Pages.Count)
        {
            yield return next;
        }
        if (IsDoublePageMode() && next + 1 < _book.Pages.Count)
        {
            yield return next + 1;
        }
        if (previous >= 0)
        {
            yield return previous;
        }
        if (IsDoublePageMode() && previous + 1 < _book.Pages.Count)
        {
            yield return previous + 1;
        }
    }

    private bool IsDoublePageMode()
    {
        return ReadingModeBox.SelectedIndex == 1;
    }

    private FitMode GetFitMode()
    {
        return FitModeBox.SelectedIndex switch
        {
            0 => FitMode.Width,
            2 => FitMode.Original,
            _ => FitMode.Height
        };
    }

    private void SetFitMode(FitMode mode)
    {
        FitModeBox.SelectedIndex = mode switch
        {
            FitMode.Width => 0,
            FitMode.Original => 2,
            _ => 1
        };
        ApplyFitMode();
    }

    private void ToggleReadingMode()
    {
        ReadingModeBox.SelectedIndex = IsDoublePageMode() ? 0 : 1;
        RenderPage();
    }

    private void ApplyFitMode()
    {
        var imageSize = GetVisibleContentSize();
        if (imageSize.Width <= 0 || imageSize.Height <= 0)
        {
            return;
        }

        var viewport = ReaderScrollViewer.Bounds;
        var availableWidth = Math.Max(1, viewport.Width - 64);
        var availableHeight = Math.Max(1, viewport.Height - 64);

        _zoom = GetFitMode() switch
        {
            FitMode.Width => Math.Clamp(availableWidth / imageSize.Width, ZoomSlider.Minimum, ZoomSlider.Maximum),
            FitMode.Height => Math.Clamp(availableHeight / imageSize.Height, ZoomSlider.Minimum, ZoomSlider.Maximum),
            _ => 1
        };

        if (Math.Abs(ZoomSlider.Value - _zoom) > 0.001)
        {
            ZoomSlider.Value = _zoom;
        }

        ApplyImageSizes();
    }

    private Size GetVisibleContentSize()
    {
        if (PageImageLeft.Source is not Bitmap left)
        {
            return default;
        }

        var leftSize = left.PixelSize.ToSize(1);
        if (PageImageRight.IsVisible && PageImageRight.Source is Bitmap right)
        {
            var rightSize = right.PixelSize.ToSize(1);
            return new Size(leftSize.Width + rightSize.Width + 16, Math.Max(leftSize.Height, rightSize.Height));
        }

        return leftSize;
    }

    private void ApplyImageSizes()
    {
        ApplyImageSize(PageImageLeft);
        ApplyImageSize(PageImageRight);
    }

    private void ApplyImageSize(Image image)
    {
        if (image.Source is not Bitmap bitmap)
        {
            image.Width = double.NaN;
            image.Height = double.NaN;
            return;
        }

        var size = bitmap.PixelSize.ToSize(1);
        image.Width = Math.Max(1, size.Width * _zoom);
        image.Height = Math.Max(1, size.Height * _zoom);
    }

    private void DisposeCache()
    {
        _preloadCancellation?.Cancel();
        _preloadCancellation?.Dispose();
        _preloadCancellation = null;

        foreach (var bitmap in _pageCache.Values)
        {
            bitmap.Dispose();
        }
        _pageCache.Clear();
        _lru.Clear();
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", path);
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }

            Process.Start("xdg-open", path);
        }
        catch
        {
        }
    }
}
