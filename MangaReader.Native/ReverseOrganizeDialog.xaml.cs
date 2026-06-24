using MangaReader.Native.Models;
using MangaReader.Native.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinForms = System.Windows.Forms;

namespace MangaReader.Native;

public partial class ReverseOrganizeDialog : Window
{
    private readonly IReadOnlyList<MangaBook> _allBooks;
    private readonly IReadOnlyList<MangaBook> _visibleBooks;
    private readonly IReadOnlyList<MangaBook> _selectedBooks;
    private readonly IReadOnlyList<string> _forbiddenRoots;
    private readonly LibraryDatabase _database;
    private readonly LibraryReverseOrganizer _organizer = new();
    private readonly ObservableCollection<ReverseOrganizeValidationIssue> _issues = [];
    private readonly ObservableCollection<ReverseOrganizeItem> _items = [];
    private ReverseOrganizePlan? _plan;
    private CancellationTokenSource? _runCancellation;
    private bool _isRunning;
    private bool _isInitializing;
    private int _pendingRedirectCount;

    public ReverseOrganizeResult? CompletedResult { get; private set; }
    public int RedirectedCount { get; private set; }

    public ReverseOrganizeDialog(
        LibraryDatabase database,
        IReadOnlyList<MangaBook> allBooks,
        IReadOnlyList<MangaBook> visibleBooks,
        IReadOnlyList<MangaBook> selectedBooks,
        IReadOnlyList<string> forbiddenRoots)
    {
        _isInitializing = true;
        InitializeComponent();
        _database = database;
        _allBooks = SanitizeBooks(allBooks);
        _visibleBooks = SanitizeBooks(visibleBooks);
        _selectedBooks = SanitizeBooks(selectedBooks);
        _forbiddenRoots = (forbiddenRoots ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        IssuesList.ItemsSource = _issues;
        ItemsList.ItemsSource = _items;
        ScopeBox.SelectedIndex = 0;
        TemplateBox.SelectedIndex = 0;
        ConflictBox.SelectedIndex = 0;
        LoadAuthors();
        RefreshPendingRedirectState();
        _isInitializing = false;
        RefreshPlanSummary();
    }

    private void LoadAuthors()
    {
        AuthorBox.Items.Clear();
        foreach (var author in _allBooks
                     .Select(book => book.Author)
                     .Where(author => !string.IsNullOrWhiteSpace(author))
                     .Distinct(StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(author => author, StringComparer.CurrentCultureIgnoreCase))
        {
            AuthorBox.Items.Add(author);
        }

        if (AuthorBox.Items.Count > 0)
        {
            AuthorBox.SelectedIndex = 0;
        }
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing
            || ScopeBox is null
            || AuthorBox is null
            || StartButton is null
            || OpenManifestButton is null
            || OpenTargetButton is null
            || BuildPlanButton is null
            || ProgressBar is null
            || ProgressText is null
            || PlanSummaryText is null
            || PendingRedirectText is null
            || EmptyAuthorBox is null
            || ExcludeHiddenBox is null
            || ExcludeMissingBox is null
            || ExcludeEmptyAuthorBox is null
            || TargetRootBox is null
            || TemplateBox is null
            || ConflictBox is null
            || RedirectButton is null)
        {
            return;
        }

        AuthorBox.Visibility = ScopeBox.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        _plan = null;
        _issues.Clear();
        _items.Clear();
        StartButton.IsEnabled = false;
        OpenManifestButton.IsEnabled = false;
        OpenTargetButton.IsEnabled = false;
        RedirectButton.IsEnabled = _pendingRedirectCount > 0;
        ProgressBar.Value = 0;
        ProgressText.Text = "";
        RefreshPlanSummary();
    }

    private void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "选择反向规整导出的目标根目录",
            SelectedPath = Directory.Exists(TargetRootBox.Text) ? TargetRootBox.Text : ""
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            TargetRootBox.Text = dialog.SelectedPath;
        }
    }

    private void BuildPlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var books = ResolveSourceBooks().ToList();
            var options = BuildOptions();
            _plan = _organizer.BuildPlan(books, options);
            ReplaceIssues(_plan.Issues);
            ReplaceItems(_plan.Items);
            RefreshPlanSummary();
            StartButton.IsEnabled = !_plan.HasErrors && _plan.ExecutableCount > 0;
            OpenTargetButton.IsEnabled = !string.IsNullOrWhiteSpace(options.TargetRoot);
            ProgressText.Text = _plan.HasErrors
                ? "预检发现错误，请先处理红色风险项。"
                : $"预检完成，可执行 {_plan.ExecutableCount} 本。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            System.Windows.MessageBox.Show($"生成预检失败：{ex.Message}", "反向规整目录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _plan.HasErrors || _plan.ExecutableCount == 0 || _isRunning)
        {
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            "将开始安全导出。\n\n本操作只复制文件，不移动、不删除源目录、不修改数据库。\n是否继续？",
            "确认安全导出",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _isRunning = true;
        _runCancellation = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        BuildPlanButton.IsEnabled = false;
        CancelRunButton.IsEnabled = true;
        ProgressBar.Value = 0;
        ProgressText.Text = "准备复制...";

        var progress = new Progress<ReverseOrganizeProgress>(UpdateProgress);
        try
        {
            CompletedResult = await _organizer.ExecuteCopyAsync(_plan, progress, _runCancellation.Token);
            var pendingRecords = CompletedResult.Items
                .Where(item => item.Status == ReverseOrganizeItemStatus.Copied)
                .Select(item => new ReverseOrganizePendingRedirectRecord
                {
                    BookId = item.BookId,
                    Title = item.Title,
                    Author = item.Author,
                    SourcePath = item.SourcePath,
                    TargetPath = item.TargetPath,
                    ManifestPath = CompletedResult.ManifestPath,
                    TargetRoot = CompletedResult.TargetRoot,
                    CreatedAt = DateTimeOffset.Now.ToString("O"),
                    UpdatedAt = DateTimeOffset.Now.ToString("O")
                })
                .ToList();

            if (pendingRecords.Count > 0)
            {
                await Task.Run(() => _database.SavePendingReverseOrganizeRedirects(pendingRecords));
            }

            ReplaceItems(CompletedResult.Items);
            ProgressBar.Value = 1;
            ProgressText.Text = $"安全导出完成：成功 {CompletedResult.CopiedCount}，跳过 {CompletedResult.SkippedCount}，失败 {CompletedResult.FailedCount}。";
            OpenManifestButton.IsEnabled = File.Exists(CompletedResult.ManifestPath);
            OpenTargetButton.IsEnabled = Directory.Exists(CompletedResult.TargetRoot);
            RefreshPendingRedirectState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ProgressText.Text = $"安全导出失败：{ex.Message}";
            System.Windows.MessageBox.Show(ProgressText.Text, "反向规整目录", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isRunning = false;
            CancelRunButton.IsEnabled = false;
            BuildPlanButton.IsEnabled = true;
        }
    }

    private async void Redirect_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        var pendingRecords = await Task.Run(() => _database.LoadPendingReverseOrganizeRedirects());
        if (pendingRecords.Count == 0)
        {
            RefreshPendingRedirectState();
            ProgressText.Text = "当前没有待重定向的漫画。";
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"将把 {pendingRecords.Count} 本已复制漫画的数据库目录切换到新路径。\n\n本操作不复制文件、不删除源目录，只修改数据库中的 folder_path。\n是否继续？",
            "确认目录重定向",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _isRunning = true;
        RedirectButton.IsEnabled = false;
        BuildPlanButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        CancelRunButton.IsEnabled = false;
        ProgressBar.Maximum = Math.Max(1, pendingRecords.Count);
        ProgressBar.Value = 0;

        try
        {
            var booksById = _allBooks.ToDictionary(book => book.Id, StringComparer.OrdinalIgnoreCase);
            var updatedBooks = new List<MangaBook>();
            var completedBookIds = new List<string>();
            var failedCount = 0;
            var skippedCount = 0;
            var completed = 0;

            foreach (var record in pendingRecords)
            {
                ProgressBar.Value = completed;
                ProgressText.Text = $"目录重定向：{completed}/{pendingRecords.Count} · 成功 {updatedBooks.Count} · 跳过 {skippedCount} · 失败 {failedCount}{Environment.NewLine}{BuildDisplayTitle(record.Author, record.Title)}";

                if (!booksById.TryGetValue(record.BookId, out var book))
                {
                    skippedCount++;
                    completed++;
                    continue;
                }

                if (!Directory.Exists(record.TargetPath))
                {
                    failedCount++;
                    UpdateDisplayedItem(record.BookId, ReverseOrganizeItemStatus.Failed, "目标目录不存在，未执行重定向。");
                    completed++;
                    continue;
                }

                book.FolderPath = record.TargetPath;
                book.IsMissing = false;
                updatedBooks.Add(book);
                completedBookIds.Add(record.BookId);
                UpdateDisplayedItem(record.BookId, ReverseOrganizeItemStatus.Redirected, "已更新数据库路径。");
                completed++;
            }

            await Task.Run(() =>
            {
                _database.UpdateFolderPathBatch(updatedBooks, "before-reverse-organize-redirect");
                _database.RemovePendingReverseOrganizeRedirects(completedBookIds);
            });

            RedirectedCount += updatedBooks.Count;
            ProgressBar.Value = Math.Max(1, pendingRecords.Count);
            ProgressText.Text = $"目录重定向完成：成功 {updatedBooks.Count}，跳过 {skippedCount}，失败 {failedCount}。";
            RefreshPendingRedirectState();
            ItemsList.Items.Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException || ex is InvalidOperationException)
        {
            ProgressText.Text = $"目录重定向失败：{ex.Message}";
            System.Windows.MessageBox.Show(ProgressText.Text, "反向规整目录", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshPendingRedirectState();
        }
        finally
        {
            _isRunning = false;
            BuildPlanButton.IsEnabled = true;
            StartButton.IsEnabled = _plan is not null && !_plan.HasErrors && _plan.ExecutableCount > 0;
        }
    }

    private void CancelRun_Click(object sender, RoutedEventArgs e)
    {
        _runCancellation?.Cancel();
        ProgressText.Text = "正在取消，已复制的文件会保留并写入 manifest...";
    }

    private void OpenManifest_Click(object sender, RoutedEventArgs e)
    {
        var manifestPath = CompletedResult?.ManifestPath;
        if (!string.IsNullOrWhiteSpace(manifestPath) && File.Exists(manifestPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = manifestPath,
                UseShellExecute = true
            });
        }
    }

    private void OpenTarget_Click(object sender, RoutedEventArgs e)
    {
        var targetRoot = CompletedResult?.TargetRoot ?? TargetRootBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(targetRoot) && Directory.Exists(targetRoot))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = targetRoot,
                UseShellExecute = true
            });
        }
    }

    private void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsList.SelectedItem is not ReverseOrganizeItem item)
        {
            return;
        }

        var path = Directory.Exists(item.TargetPath) ? item.TargetPath : item.SourcePath;
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            System.Windows.MessageBox.Show("安全导出正在执行，请先取消或等待完成。", "反向规整目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Close();
    }

    private IEnumerable<MangaBook> ResolveSourceBooks()
    {
        var books = ScopeBox.SelectedIndex switch
        {
            1 => _visibleBooks,
            2 => _selectedBooks,
            3 => _allBooks.Where(book => string.Equals(book.Author, AuthorBox.SelectedItem as string, StringComparison.CurrentCultureIgnoreCase)),
            _ => _allBooks
        };

        return books.Where(book => book is not null);
    }

    private ReverseOrganizeOptions BuildOptions()
    {
        return new ReverseOrganizeOptions
        {
            TargetRoot = TargetRootBox.Text.Trim(),
            Template = TemplateBox.SelectedIndex == 1 ? ReverseOrganizeTemplate.AuthorYearTitle : ReverseOrganizeTemplate.AuthorTitle,
            ConflictStrategy = ConflictBox.SelectedIndex == 1 ? ReverseOrganizeConflictStrategy.Skip : ReverseOrganizeConflictStrategy.AppendNumber,
            EmptyAuthorName = string.IsNullOrWhiteSpace(EmptyAuthorBox.Text) ? "未指定作者" : EmptyAuthorBox.Text.Trim(),
            ExcludeHidden = ExcludeHiddenBox.IsChecked == true,
            ExcludeMissingSource = ExcludeMissingBox.IsChecked == true,
            ExcludeEmptyAuthor = ExcludeEmptyAuthorBox.IsChecked == true,
            ForbiddenRoots = _forbiddenRoots
        };
    }

    private void UpdateProgress(ReverseOrganizeProgress progress)
    {
        ProgressBar.Maximum = Math.Max(1, progress.TotalCount);
        ProgressBar.Value = Math.Clamp(progress.CompletedCount, 0, Math.Max(1, progress.TotalCount));
        ProgressText.Text = $"{progress.Stage}：{progress.CompletedCount}/{progress.TotalCount} · 成功 {progress.SucceededCount} · 跳过 {progress.SkippedCount} · 失败 {progress.FailedCount}{Environment.NewLine}{progress.CurrentTitle}";
        ItemsList.Items.Refresh();
    }

    private void RefreshPlanSummary()
    {
        if (PlanSummaryText is null || ScopeBox is null)
        {
            return;
        }

        var sourceCount = ResolveSourceBooks().Count();
        var selectedCount = _selectedBooks.Count;
        var visibleCount = _visibleBooks.Count;
        if (_plan is null)
        {
            PlanSummaryText.Text = $"当前范围约 {sourceCount} 本。当前选中 {selectedCount} 本，当前视图 {visibleCount} 本。";
            return;
        }

        var errors = _plan.Issues.Count(issue => issue.Severity == ReverseOrganizeIssueSeverity.Error);
        var warnings = _plan.Issues.Count(issue => issue.Severity == ReverseOrganizeIssueSeverity.Warning);
        PlanSummaryText.Text = $"计划 {_plan.Items.Count} 本，可执行 {_plan.ExecutableCount} 本，总大小 {FormatSize(_plan.TotalBytes)}。错误 {errors}，警告 {warnings}。";
    }

    private void RefreshPendingRedirectState()
    {
        var pendingCount = _database.LoadPendingReverseOrganizeRedirects().Count;
        _pendingRedirectCount = pendingCount;
        if (PendingRedirectText is not null)
        {
            PendingRedirectText.Text = pendingCount > 0
                ? $"待重定向 {pendingCount} 本。复制完成后可单独执行数据库路径切换。"
                : "当前没有待重定向记录。";
        }

        if (RedirectButton is not null)
        {
            RedirectButton.IsEnabled = !_isRunning && pendingCount > 0;
        }
    }

    private void ReplaceIssues(IEnumerable<ReverseOrganizeValidationIssue> issues)
    {
        _issues.Clear();
        foreach (var issue in issues)
        {
            _issues.Add(issue);
        }
    }

    private void ReplaceItems(IEnumerable<ReverseOrganizeItem> items)
    {
        _items.Clear();
        foreach (var item in items)
        {
            _items.Add(item);
        }
    }

    private static string FormatSize(long bytes)
    {
        const double gb = 1024d * 1024d * 1024d;
        const double mb = 1024d * 1024d;
        return bytes >= gb ? $"{bytes / gb:0.##}GB" : $"{Math.Max(1, bytes / mb):0.#}MB";
    }

    private void UpdateDisplayedItem(string bookId, ReverseOrganizeItemStatus status, string message)
    {
        var item = _items.FirstOrDefault(current => string.Equals(current.BookId, bookId, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        item.Status = status;
        item.Message = message;
    }

    private static string BuildDisplayTitle(string author, string title)
    {
        return string.IsNullOrWhiteSpace(author) ? title : $"{author} / {title}";
    }

    private static IReadOnlyList<MangaBook> SanitizeBooks(IEnumerable<MangaBook>? books)
    {
        return (books ?? [])
            .Where(book => book is not null)
            .ToList();
    }
}
