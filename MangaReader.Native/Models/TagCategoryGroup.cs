using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace MangaReader.Native.Models;

/// <summary>
/// 标签池按分类分组的视图模型，支持折叠/展开。
/// </summary>
public sealed class TagCategoryGroup : INotifyPropertyChanged
{
    private bool _isExpanded = true;
    private int _totalCount;

    public required string Category { get; init; } = "自定义";

    public RangeObservableCollection<TagChip> Tags { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TagsVisibility));
                OnPropertyChanged(nameof(Arrow));
                ExpandedChanged?.Invoke(this, value);
            }
        }
    }

    public Visibility TagsVisibility => _isExpanded ? Visibility.Visible : Visibility.Collapsed;

    public string Arrow => _isExpanded ? "▼" : "▶";

    public int TotalCount
    {
        get => _totalCount;
        set
        {
            if (_totalCount != value)
            {
                _totalCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Header));
            }
        }
    }

    public int TotalUsageCount { get; set; }

    public string Header => $"{Category}（{TotalCount}）";

    /// <summary>用户手动切换折叠/展开时触发，用于持久化。程序matic 设置不触发。</summary>
    public event Action<TagCategoryGroup, bool>? ExpandedChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
