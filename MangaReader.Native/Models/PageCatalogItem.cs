using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace MangaReader.Native.Models;

public sealed class PageCatalogItem : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private bool _isBookmarked;

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
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
