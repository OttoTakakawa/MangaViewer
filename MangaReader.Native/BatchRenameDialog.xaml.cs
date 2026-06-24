using MangaReader.Native.Models;
using System.Windows;
using System.Windows.Controls;

namespace MangaReader.Native;

public sealed record BatchRenameUpdate(MangaBook Book, string NewTitle);

public partial class BatchRenameDialog : Window
{
    private readonly IReadOnlyList<MangaBook> _books;
    private bool _isInitializing;

    public IReadOnlyList<BatchRenameUpdate> Updates { get; private set; } = [];

    public BatchRenameDialog(IReadOnlyList<MangaBook> books, string initialPrefix)
    {
        _isInitializing = true;
        InitializeComponent();
        _books = books;
        ScopeText.Text = $"已选 {books.Count} 本漫画。先选择规则，再确认旧标题到新标题的预览。";
        ModeBox.SelectedIndex = 0;
        FindBox.Text = initialPrefix;
        ReplaceBox.Text = "";
        _isInitializing = false;
        RefreshModeLabels();
        RefreshPreview();
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ModeBox is null || FindBox is null || ReplaceBox is null)
        {
            return;
        }

        RefreshModeLabels();
        RefreshPreview();
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

    private void RefreshPreview()
    {
        var updates = BuildUpdates();
        Updates = updates;
        ConfirmButton.IsEnabled = updates.Count > 0;
        PreviewSummaryText.Text = $"将修改 {updates.Count} / {_books.Count} 本。";
        PreviewTextBox.Text = updates.Count == 0
            ? "没有可修改的标题。"
            : string.Join(Environment.NewLine, updates.Select(item => $"{item.Book.Title}  ->  {item.NewTitle}"));
    }

    private List<BatchRenameUpdate> BuildUpdates()
    {
        var input = FindBox.Text;
        var replacement = ReplaceBox.Text;
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        var result = new List<BatchRenameUpdate>();
        foreach (var book in _books)
        {
            var newTitle = BuildNewTitle(book.Title, input, replacement);
            if (string.IsNullOrWhiteSpace(newTitle)
                || string.Equals(book.Title, newTitle, StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(new BatchRenameUpdate(book, newTitle));
        }

        return result;
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
        RefreshPreview();
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
}
