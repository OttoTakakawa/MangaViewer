namespace MangaReader.Native.Models;

public enum ReverseOrganizeTemplate
{
    AuthorTitle,
    AuthorYearTitle
}

public enum ReverseOrganizeConflictStrategy
{
    AppendNumber,
    Skip
}

public enum ReverseOrganizeIssueSeverity
{
    Info,
    Warning,
    Error
}

public enum ReverseOrganizeItemStatus
{
    Pending,
    Copied,
    Redirected,
    Skipped,
    Failed,
    Canceled
}

public sealed class ReverseOrganizeOptions
{
    public string TargetRoot { get; init; } = "";
    public ReverseOrganizeTemplate Template { get; init; } = ReverseOrganizeTemplate.AuthorTitle;
    public ReverseOrganizeConflictStrategy ConflictStrategy { get; init; } = ReverseOrganizeConflictStrategy.AppendNumber;
    public string EmptyAuthorName { get; init; } = "未指定作者";
    public string SingleBookCollectionName { get; init; } = "单本合集";
    public int SmallAuthorThreshold { get; init; } = 2;
    public bool ExcludeHidden { get; init; }
    public bool ExcludeMissingSource { get; init; } = true;
    public bool ExcludeEmptyAuthor { get; init; }
    public IReadOnlyList<string> ForbiddenRoots { get; init; } = [];
}

public sealed class ReverseOrganizePlan
{
    public ReverseOrganizeOptions Options { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<ReverseOrganizeItem> Items { get; init; } = [];
    public IReadOnlyList<ReverseOrganizeValidationIssue> Issues { get; init; } = [];
    public long TotalBytes => Items.Where(item => item.CanExecute).Sum(item => item.SourceBytes);
    public int ExecutableCount => Items.Count(item => item.CanExecute);
    public bool HasErrors => Issues.Any(issue => issue.Severity == ReverseOrganizeIssueSeverity.Error);
}

public sealed class ReverseOrganizeItem
{
    public string BookId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string TargetPath { get; set; } = "";
    public string Tags { get; init; } = "";
    public int PageCount { get; init; }
    public long SourceBytes { get; init; }
    public int SourceFileCount { get; init; }
    public bool SourceExists { get; init; }
    public bool IsHidden { get; init; }
    public bool CanExecute { get; set; } = true;
    public ReverseOrganizeItemStatus Status { get; set; } = ReverseOrganizeItemStatus.Pending;
    public string Message { get; set; } = "";

    public string DisplayTitle => string.IsNullOrWhiteSpace(Author) ? Title : $"{Author} / {Title}";
    public string StatusText => Status switch
    {
        ReverseOrganizeItemStatus.Copied => "已复制",
        ReverseOrganizeItemStatus.Redirected => "已重定向",
        ReverseOrganizeItemStatus.Skipped => "跳过",
        ReverseOrganizeItemStatus.Failed => "失败",
        ReverseOrganizeItemStatus.Canceled => "取消",
        _ => "待执行"
    };
    public string SizeText => FormatSize(SourceBytes);

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0MB";
        }

        const double mb = 1024d * 1024d;
        const double gb = 1024d * 1024d * 1024d;
        return bytes >= gb ? $"{bytes / gb:0.##}GB" : $"{Math.Max(1, bytes / mb):0.#}MB";
    }
}

public sealed class ReverseOrganizeValidationIssue
{
    public ReverseOrganizeIssueSeverity Severity { get; init; }
    public string Type { get; init; } = "";
    public string Message { get; init; } = "";
    public string BookTitle { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string TargetPath { get; init; } = "";

    public string SeverityText => Severity switch
    {
        ReverseOrganizeIssueSeverity.Error => "错误",
        ReverseOrganizeIssueSeverity.Warning => "警告",
        _ => "提示"
    };
}

public sealed class ReverseOrganizeProgress
{
    public string Stage { get; init; } = "";
    public string CurrentTitle { get; init; } = "";
    public int CompletedCount { get; init; }
    public int TotalCount { get; init; }
    public int SucceededCount { get; init; }
    public int SkippedCount { get; init; }
    public int FailedCount { get; init; }
}

public sealed class ReverseOrganizeResult
{
    public string TargetRoot { get; init; } = "";
    public string ManifestPath { get; init; } = "";
    public bool Canceled { get; init; }
    public int TotalCount { get; init; }
    public int CopiedCount { get; init; }
    public int SkippedCount { get; init; }
    public int FailedCount { get; init; }
    public IReadOnlyList<ReverseOrganizeItem> Items { get; init; } = [];
}

public sealed class ReverseOrganizePendingRedirectRecord
{
    public string BookId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public string ManifestPath { get; init; } = "";
    public string TargetRoot { get; init; } = "";
    public string CreatedAt { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
}
