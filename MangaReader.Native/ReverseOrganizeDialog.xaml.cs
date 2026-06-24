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
    private readonly LibraryReverseOrganizer _organizer = new();
    private readonly ObservableCollection<ReverseOrganizeValidationIssue> _issues = [];
    private readonly ObservableCollection<ReverseOrganizeItem> _items = [];
    private ReverseOrganizePlan? _plan;
    private CancellationTokenSource? _runCancellation;
    private bool _isRunning;

    public ReverseOrganizeResult? CompletedResult { get; private set; }

    public ReverseOrganizeDialog(
        IReadOnlyList<MangaBook> allBooks,
        IReadOnlyList<MangaBook> visibleBooks,
        IReadOnlyList<MangaBook> selectedBooks,
        IReadOnlyList<string> forbiddenRoots)
    {
        InitializeComponent();
        _allBooks = allBooks;
        _visibleBooks = visibleBooks;
        _selectedBooks = selectedBooks;
        _forbiddenRoots = forbiddenRoots;

        IssuesList.ItemsSource = _issues;
        ItemsList.ItemsSource = _items;
        ScopeBox.SelectedIndex = 0;
        TemplateBox.SelectedIndex = 0;
        ConflictBox.SelectedIndex = 0;
        LoadAuthors();
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
        if (ScopeBox is null)
        {
            return;
        }

        AuthorBox.Visibility = ScopeBox.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        _plan = null;
        _issues.Clear();
        _items.Clear();
        StartButton.IsEnabled = false;
        OpenManifestButton.IsEnabled = false;
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
            ReplaceItems(CompletedResult.Items);
            ProgressBar.Value = 1;
            ProgressText.Text = $"安全导出完成：成功 {CompletedResult.CopiedCount}，跳过 {CompletedResult.SkippedCount}，失败 {CompletedResult.FailedCount}。";
            OpenManifestButton.IsEnabled = File.Exists(CompletedResult.ManifestPath);
            OpenTargetButton.IsEnabled = Directory.Exists(CompletedResult.TargetRoot);
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
        return ScopeBox.SelectedIndex switch
        {
            1 => _visibleBooks,
            2 => _selectedBooks,
            3 => _allBooks.Where(book => string.Equals(book.Author, AuthorBox.SelectedItem as string, StringComparison.CurrentCultureIgnoreCase)),
            _ => _allBooks
        };
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
}
