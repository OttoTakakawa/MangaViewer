using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MangaReader.Native.Models;

public sealed class PageCatalogItem : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private bool _isBookmarked;
    private string _bookmarkLabel = "";
    private string _bookmarkColor = "#EF4444";
    private SolidColorBrush? _bookmarkBrushCache;

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

    public bool IsBookmarked
    {
        get => _isBookmarked;
        set
        {
            _isBookmarked = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BookmarkMenuHeader));
            OnPropertyChanged(nameof(IsNotBookmarked));
        }
    }

    public bool IsNotBookmarked => !_isBookmarked;

    public string BookmarkLabel
    {
        get => _bookmarkLabel;
        set
        {
            _bookmarkLabel = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasBookmarkLabel));
        }
    }

    public bool HasBookmarkLabel => _bookmarkLabel.Length > 0;

    public string BookmarkColor
    {
        get => _bookmarkColor;
        set
        {
            _bookmarkColor = value;
            _bookmarkBrushCache = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BookmarkBrush));
        }
    }

    public System.Windows.Media.Brush BookmarkBrush
    {
        get
        {
            if (_bookmarkBrushCache is null || _bookmarkBrushCache.Color.ToString() != _bookmarkColor)
            {
                try
                {
                    var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_bookmarkColor);
                    _bookmarkBrushCache = new System.Windows.Media.SolidColorBrush(color);
                    _bookmarkBrushCache.Freeze();
                }
                catch
                {
                    _bookmarkBrushCache = System.Windows.Media.Brushes.Transparent;
                }
            }
            return _bookmarkBrushCache;
        }
    }

    public string BookmarkMenuHeader => IsBookmarked ? "取消标记" : "标记此页";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
