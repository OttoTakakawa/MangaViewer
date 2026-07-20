using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using MangaReader.Native.Services;

namespace MangaReader.Native.Models;

// 杂图库的单张图片记录。与漫画 books 表完全分离，独立的 misc_images 表存储。
// 评分采用 0-10（0.5 步长），与 books.rating（0-5）互不干扰。
// Tag 体系走 misc_image_tags 表，不与 managed_tags 互动。
public sealed class MiscImage : INotifyPropertyChanged
{
    private const int MaxCardTagRows = 1;
    private const int MaxCardTagRowUnits = 18;
    private const int MaxCardTagTextUnits = 16;

    private BitmapSource? _thumbnail;
    private string _tags = "";
    private string _comment = "";
    private double _rating;
    private bool _isFavorite;
    private bool _isSelectedForBatch;
    private string _lastOpenedAt = "";

    public string Id { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";

    // '真人' | '绘画' | ''（未分类）
    public string Category { get; set; } = "";

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

    // 0-10，0.5 步长
    public double Rating
    {
        get => _rating;
        set
        {
            var clamped = Math.Clamp(value, 0, 10);
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

    public string SizeText => FormatSize(FileSize);
    public string CategoryText => Category switch
    {
        "真人" => "真人",
        "绘画" => "绘画",
        _ => "未分类"
    };

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
        if (tags.Count == 0)
        {
            return;
        }

        var rows = new int[MaxCardTagRows];
        var visible = new List<(TagChip Chip, int Row, int Units)>();

        foreach (var tag in tags)
        {
            if (!TryPlaceCardTag(tag, rows, out var row, out var units))
            {
                continue;
            }

            visible.Add((new TagChip
            {
                Name = tag,
                Color = MiscTagService.GetColor(tag)
            }, row, units));
        }

        var hiddenCount = tags.Count - visible.Count;
        var visibleChips = new List<TagChip>(visible.Count);
        foreach (var item in visible)
        {
            visibleChips.Add(item.Chip);
        }
        CardTagItems.AddRange(visibleChips);

        if (hiddenCount > 0)
        {
            CardTagItems.Add(new TagChip
            {
                Name = $"+{hiddenCount}",
                Color = "#E5E7EB"
            });
        }
    }

    private static bool TryPlaceCardTag(string tag, int[] rows, out int row, out int units)
    {
        row = -1;
        var textUnits = CountCardTagTextUnits(tag);
        units = textUnits + 4;

        if (textUnits > MaxCardTagTextUnits)
        {
            return false;
        }

        if (units <= MaxCardTagRowUnits)
        {
            for (var i = 0; i < rows.Length; i++)
            {
                if (rows[i] + units <= MaxCardTagRowUnits)
                {
                    rows[i] += units;
                    row = i;
                    return true;
                }
            }

            return false;
        }

        return false;
    }

    private static int CountCardTagTextUnits(string text)
    {
        var units = 0;
        foreach (var c in text)
        {
            units += c <= 0x007F ? 1 : 2;
        }

        return units;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0MB";
        }

        const double mb = 1024d * 1024d;
        const double gb = 1024d * 1024d * 1024d;
        return bytes >= gb
            ? $"{bytes / gb:0.##}G"
            : $"{Math.Max(1, bytes / mb):0.#}MB";
    }
}
