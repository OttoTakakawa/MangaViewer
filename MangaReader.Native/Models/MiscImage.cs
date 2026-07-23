using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using MangaReader.Native.Services;

namespace MangaReader.Native.Models;

// 杂图库的单张图片记录。与漫画 books 表完全分离，独立的 misc_images 表存储。
// 评分 0-5（0.5 步长，5 星 10 档），与 books.rating 完全一致，便于直接复用 ReaderWindow。
// Tag 体系走 misc_image_tags 表，不与 managed_tags 互动。
public sealed class MiscImage : INotifyPropertyChanged
{
    // 叠加模式下底部半透明区域空间有限，最多 2 行。
    // 每行 22 单元 ≈ 11 个汉字或 22 个拉丁字符，超出显示 +N。
    private const int MaxCardTagRows = 2;
    private const int MaxCardTagRowUnits = 22;
    private const int MaxCardTagTextUnits = 20;

    private BitmapSource? _thumbnail;
    private string _tags = "";
    private string _comment = "";
    private double _rating;
    private bool _isFavorite;
    private bool _isSelectedForBatch;
    private string _lastOpenedAt = "";
    private int _pixelWidth;
    private int _pixelHeight;

    public string Id { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";

    // '真人' | '绘画' | ''（未分类）
    public string Category { get; set; } = "";

    public int PixelWidth
    {
        get => _pixelWidth;
        set
        {
            if (_pixelWidth == value)
            {
                return;
            }
            _pixelWidth = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AspectRatio));
        }
    }

    public int PixelHeight
    {
        get => _pixelHeight;
        set
        {
            if (_pixelHeight == value)
            {
                return;
            }
            _pixelHeight = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AspectRatio));
        }
    }

    // 宽高比 = w / h。无尺寸时回退 1.0（正方形占位），避免瀑布流布局崩坏
    public double AspectRatio => _pixelWidth > 0 && _pixelHeight > 0
        ? (double)_pixelWidth / _pixelHeight
        : 1.0;

    public string Tags
    {
        get => _tags;
        set
        {
            _tags = MiscTagService.FormatTags(MiscTagService.ParseTags(value));
            RefreshTagItems();
            OnPropertyChanged();
        }
    }

    public string Comment
    {
        get => _comment;
        set
        {
            _comment = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasComment));
        }
    }

    public bool HasComment => _comment.Length > 0;

    // 0-5（0.5 步长，5 星 10 档），与 MangaBook.Rating 完全一致
    public double Rating
    {
        get => _rating;
        set
        {
            var clamped = Math.Clamp(value, 0, 5);
            var quantized = Math.Round(clamped * 2, MidpointRounding.AwayFromZero) / 2;
            if (Math.Abs(_rating - quantized) < 0.0001)
            {
                return;
            }

            _rating = quantized;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasRating));
            OnPropertyChanged(nameof(RatingText));
        }
    }

    public bool HasRating => _rating > 0;
    public string RatingText => _rating.ToString("0.#");

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
            {
                return;
            }

            _isFavorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FavoriteStarText));
        }
    }

    public string FavoriteStarText => _isFavorite ? "★" : "";

    public bool IsSelectedForBatch
    {
        get => _isSelectedForBatch;
        set
        {
            if (_isSelectedForBatch == value)
            {
                return;
            }

            _isSelectedForBatch = value;
            OnPropertyChanged();
        }
    }

    public long FileSize { get; set; }
    public string ImportedAt { get; set; } = "";

    public string LastOpenedAt
    {
        get => _lastOpenedAt;
        set
        {
            _lastOpenedAt = value ?? "";
            OnPropertyChanged();
        }
    }

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public RangeObservableCollection<TagChip> TagItems { get; } = [];
    public RangeObservableCollection<TagChip> CardTagItems { get; } = [];

    public string SizeText => FileSizeFormatter.Format(FileSize);
    public string CategoryText => Category switch
    {
        "真人" => "真人",
        "绘画" => "绘画",
        _ => ""
    };

    // 是否在卡片上显示分类标签：只有显式分类（真人/绘画）才显示，未分类完全隐藏。
    // 后续分类功能会改为入口式，这里保留布尔开关便于将来切换。
    public bool HasCategory
    {
        get
        {
            var c = Category ?? "";
            return string.Equals(c, "真人", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c, "绘画", StringComparison.OrdinalIgnoreCase);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static readonly PropertyChangedEventArgs AllChangedArgs = new("");

    public void NotifyAll()
    {
        PropertyChanged?.Invoke(this, AllChangedArgs);
    }

    public void AddTag(string tag)
    {
        var names = MiscTagService.ParseTags(Tags).ToList();
        if (names.Any(name => string.Equals(name, tag, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        names.Add(tag);
        Tags = MiscTagService.FormatTags(names);
    }

    public void RemoveTag(string tag)
    {
        var names = MiscTagService.ParseTags(Tags)
            .Where(name => !string.Equals(name, tag, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Tags = MiscTagService.FormatTags(names);
    }

    private void RefreshTagItems()
    {
        TagItems.Clear();
        CardTagItems.Clear();

        var tags = MiscTagService.ParseTags(_tags).ToList();
        var tagChips = new List<TagChip>(tags.Count);
        foreach (var tag in tags)
        {
            tagChips.Add(new TagChip
            {
                Name = tag,
                Color = MiscTagService.GetColor(tag)
            });
        }
        TagItems.AddRange(tagChips);

        RefreshCardTagItems(tags);
        OnPropertyChanged(nameof(CardTagItems));
    }

    private void RefreshCardTagItems(IReadOnlyList<string> tags)
    {
        var options = new TagCardLayoutOptions(
            MaxCardTagRows,
            MaxCardTagRowUnits,
            MaxCardTagTextUnits,
            ChromeUnits: 4,
            AllowSpanningLongTag: false,
            ReserveSummarySpace: false);
        CardTagItems.AddRange(TagCardLayout.Build(tags, options, MiscTagService.GetColor));
    }
}
