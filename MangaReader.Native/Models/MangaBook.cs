using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using MangaReader.Native.Services;

namespace MangaReader.Native.Models;

public sealed class MangaBook : INotifyPropertyChanged
{
    private int _lastReadPageIndex;
    private int _readCount;
    private BitmapSource? _coverImage;
    private string _tags = "";
    private string _readingStatus = "unread";

    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string CharacterName { get; set; } = "";
    public string ForeignName { get; set; } = "";
    public string ProducedAt { get; set; } = "";
    public string ImportedAt { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Tags
    {
        get => _tags;
        set
        {
            _tags = TagService.FormatTags(TagService.ParseTags(value));
            RefreshTagItems();
            OnPropertyChanged();
        }
    }
    public string FolderPath { get; set; } = "";
    public int PageCount { get; set; }
    public int CoverPageIndex { get; set; }
    public int BookStyle { get; set; } = -1;
    public bool IsMissing { get; set; }
    public bool IsHidden { get; set; }
    public bool IsFavorite { get; set; }
    public ObservableCollection<string> Pages { get; } = [];
    public ObservableCollection<TagChip> TagItems { get; } = [];

    public int LastReadPageIndex
    {
        get => _lastReadPageIndex;
        set
        {
            _lastReadPageIndex = Math.Clamp(value, 0, Math.Max(PageCount - 1, 0));
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressText));
        }
    }

    public int ReadCount
    {
        get => _readCount;
        set
        {
            _readCount = Math.Max(0, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReadCountText));
            OnPropertyChanged(nameof(ReadCountBadgeText));
        }
    }

    public string ReadingStatus
    {
        get => _readingStatus;
        set
        {
            _readingStatus = NormalizeReadingStatus(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReadingStatusText));
            OnPropertyChanged(nameof(StatusBadgeText));
        }
    }

    public BitmapSource? CoverImage
    {
        get => _coverImage;
        set
        {
            _coverImage = value;
            OnPropertyChanged();
        }
    }

    public string ProgressText => PageCount <= 0 ? "0 / 0" : $"{LastReadPageIndex + 1} / {PageCount}";
    public string ReadCountText => ReadCount <= 0 ? "未标记读过" : $"读过 {ReadCount} 次";
    public string ReadCountBadgeText => ReadCount <= 0 ? "" : ReadCountText;
    public string ReadingStatusText => ReadingStatus switch
    {
        "reading" => "在读",
        "finished" => "已读",
        "paused" => "搁置",
        _ => "未读"
    };
    public string StatusBadgeText => IsFavorite ? $"收藏 · {ReadingStatusText}" : ReadingStatusText;
    public string MissingText => IsMissing ? "路径失效" : "";
    public string HiddenText => IsHidden ? "已隐藏" : "";
    public int BookStyleIndex => BookStyle >= 0 ? BookStyle % 4 : (Id.GetHashCode() & 0x7FFFFFFF) % 4;
    public double BookWidth => BookStyleIndex switch
    {
        1 => 138,
        2 => 154,
        3 => 132,
        4 => 150,
        _ => 146
    };
    public double BookHeight => BookStyleIndex switch
    {
        1 => 214,
        2 => 198,
        3 => 206,
        4 => 218,
        _ => 206
    };
    public double SpineWidth => BookStyleIndex switch
    {
        1 => 10,
        2 => 28,
        3 => 0,
        4 => 14,
        _ => 18
    };
    public double BookTilt => BookStyleIndex switch
    {
        _ => 0
    };
    public string BookAccentColor => BookStyleIndex switch
    {
        1 => "#D7A86E",
        2 => "#8DA7BE",
        3 => "#CDB7A0",
        4 => "#B7A0CD",
        _ => "#D8CABA"
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void NotifyAll()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Author));
        OnPropertyChanged(nameof(CharacterName));
        OnPropertyChanged(nameof(ForeignName));
        OnPropertyChanged(nameof(ProducedAt));
        OnPropertyChanged(nameof(ImportedAt));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(FolderPath));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(CoverPageIndex));
        OnPropertyChanged(nameof(BookStyle));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(ReadingStatus));
        OnPropertyChanged(nameof(ReadingStatusText));
        OnPropertyChanged(nameof(StatusBadgeText));
        OnPropertyChanged(nameof(MissingText));
        OnPropertyChanged(nameof(HiddenText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ReadCount));
        OnPropertyChanged(nameof(ReadCountText));
        OnPropertyChanged(nameof(ReadCountBadgeText));
        OnPropertyChanged(nameof(BookStyleIndex));
        OnPropertyChanged(nameof(BookWidth));
        OnPropertyChanged(nameof(BookHeight));
        OnPropertyChanged(nameof(SpineWidth));
        OnPropertyChanged(nameof(BookTilt));
        OnPropertyChanged(nameof(BookAccentColor));
    }

    public void AddTag(string tag)
    {
        var names = TagService.ParseTags(Tags).ToList();
        if (names.Any(name => string.Equals(name, tag, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        names.Add(tag);
        Tags = TagService.FormatTags(names);
    }

    public void CycleBookStyle()
    {
        BookStyle = (BookStyleIndex + 1) % 4;
        OnPropertyChanged(nameof(BookStyle));
        OnPropertyChanged(nameof(BookStyleIndex));
        OnPropertyChanged(nameof(BookWidth));
        OnPropertyChanged(nameof(BookHeight));
        OnPropertyChanged(nameof(SpineWidth));
        OnPropertyChanged(nameof(BookTilt));
        OnPropertyChanged(nameof(BookAccentColor));
    }

    private void RefreshTagItems()
    {
        TagItems.Clear();
        foreach (var tag in TagService.ParseTags(_tags))
        {
            TagItems.Add(new TagChip
            {
                Name = tag,
                Color = TagColor(tag)
            });
        }
    }

    private static string TagColor(string tag)
    {
        return TagService.GetColor(tag);
    }

    private static string NormalizeReadingStatus(string status)
    {
        return status switch
        {
            "reading" or "finished" or "paused" => status,
            _ => "unread"
        };
    }
}
