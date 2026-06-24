using MangaReader.Native.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace MangaReader.Native;

public sealed record BatchRenameUpdate(MangaBook Book, string NewTitle);

public partial class BatchRenameDialog : Window
{
    private readonly IReadOnlyList<MangaBook> _books;
    private bool _isInitializing;

    public ObservableCollection<BatchRenameRow> Rows { get; } = [];
    public IReadOnlyList<BatchRenameUpdate> Updates { get; private set; } = [];

    public BatchRenameDialog(IReadOnlyList<MangaBook> books, string initialPrefix)
    {
        _isInitializing = true;
        InitializeComponent();
        _books = books;
        DataContext = this;
        foreach (var book in books)
        {
            var row = new BatchRenameRow(book);
            row.PropertyChanged += Row_PropertyChanged;
            Rows.Add(row);
        }

        ScopeText.Text = $"已选 {books.Count} 本漫画。规则只是生成草稿，下面每一行的新标题都可以手动编辑。";
        ModeBox.SelectedIndex = 0;
        FindBox.Text = initialPrefix;
        ReplaceBox.Text = "";
        _isInitializing = false;
        RefreshModeLabels();
        ApplyRuleToRows();
        RefreshSummary();
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ModeBox is null || FindBox is null || ReplaceBox is null)
        {
            return;
        }

        RefreshModeLabels();
    }

    private void RefreshModeLabels()
    {
        var mode = ModeBox.SelectedIndex;
        ReplaceBox.IsEnabled = mode == 3;
        FindLabelText.Text = mode switch
        {
            0 => "要移除的开头前缀",
            1 => "要添加的开头前缀",
            2 => "要添加的结尾后缀",
            _ => "要替换的文本"
        };
        ReplaceLabelText.Text = mode == 3 ? "替换为" : "仅替换模式使用";
    }

    private void ApplyRule_Click(object sender, RoutedEventArgs e)
    {
        ApplyRuleToRows();
        RefreshSummary();
    }

    private void ApplyRuleToRows()
    {
        var input = FindBox.Text;
        var replacement = ReplaceBox.Text;
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        foreach (var row in Rows)
        {
            row.NewTitle = BuildNewTitle(row.OriginalTitle, input, replacement);
        }
    }

    private string BuildNewTitle(string title, string input, string replacement)
    {
        return ModeBox.SelectedIndex switch
        {
            0 when title.StartsWith(input, StringComparison.OrdinalIgnoreCase) =>
                title[input.Length..].TrimStart(' ', '-', '_', '—', '－', '·'),
            1 when !title.StartsWith(input, StringComparison.OrdinalIgnoreCase) =>
                $"{input}{title}",
            2 when !title.EndsWith(input, StringComparison.OrdinalIgnoreCase) =>
                $"{title}{input}",
            3 when title.Contains(input, StringComparison.OrdinalIgnoreCase) =>
                title.Replace(input, replacement, StringComparison.OrdinalIgnoreCase).Trim(),
            _ => title
        };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        RefreshUpdates();
        if (Updates.Count == 0)
        {
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows)
        {
            row.NewTitle = row.OriginalTitle;
        }

        RefreshSummary();
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BatchRenameRow.NewTitle)
            || e.PropertyName == nameof(BatchRenameRow.Status))
        {
            RefreshSummary();
        }
    }

    private void RefreshSummary()
    {
        RefreshUpdates();
        var emptyCount = Rows.Count(row => string.IsNullOrWhiteSpace(row.NewTitle));
        PreviewSummaryText.Text = emptyCount > 0
            ? $"将修改 {Updates.Count} / {_books.Count} 本。{emptyCount} 行新标题为空，会被跳过。"
            : $"将修改 {Updates.Count} / {_books.Count} 本。";
        ConfirmButton.IsEnabled = Updates.Count > 0;
    }

    private void RefreshUpdates()
    {
        Updates = Rows
            .Where(row => row.IsChanged && !string.IsNullOrWhiteSpace(row.NewTitle))
            .Select(row => new BatchRenameUpdate(row.Book, row.NewTitle))
            .ToList();
    }
}

public sealed class BatchRenameRow : INotifyPropertyChanged
{
    private string _newTitle;

    public BatchRenameRow(MangaBook book)
    {
        Book = book;
        OriginalTitle = book.Title;
        _newTitle = book.Title;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MangaBook Book { get; }
    public string OriginalTitle { get; }

    public string NewTitle
    {
        get => _newTitle;
        set
        {
            if (string.Equals(_newTitle, value, StringComparison.Ordinal))
            {
                return;
            }

            _newTitle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsChanged));
            OnPropertyChanged(nameof(Status));
        }
    }

    public bool IsChanged => !string.Equals(OriginalTitle, NewTitle, StringComparison.Ordinal);

    public string Status
    {
        get
        {
            if (string.IsNullOrWhiteSpace(NewTitle))
            {
                return "空标题";
            }

            return IsChanged ? "将修改" : "不变";
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
