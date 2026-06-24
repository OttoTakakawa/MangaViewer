using MangaReader.Native.Models;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MangaReader.Native.Services;

public sealed class LibraryReverseOrganizer
{
    private const int MaxPathLength = 240;

    public ReverseOrganizePlan BuildPlan(IEnumerable<MangaBook> sourceBooks, ReverseOrganizeOptions options)
    {
        var issues = new List<ReverseOrganizeValidationIssue>();
        var items = new List<ReverseOrganizeItem>();
        var targetPaths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var targetRoot = NormalizePath(options.TargetRoot);
        var books = sourceBooks.ToList();
        var includedBooks = books
            .Where(book => !(options.ExcludeHidden && book.IsHidden))
            .Where(book => !(options.ExcludeEmptyAuthor && string.IsNullOrWhiteSpace(book.Author)))
            .Where(book => !(options.ExcludeMissingSource && !Directory.Exists(NormalizePath(book.FolderPath))))
            .ToList();
        var authorCounts = includedBooks
            .Where(book => !string.IsNullOrWhiteSpace(book.Author))
            .GroupBy(book => NormalizeAuthorKey(book.Author), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var book in includedBooks)
        {
            var sourcePath = NormalizePath(book.FolderPath);
            var sourceExists = Directory.Exists(sourcePath);
            var targetPath = BuildTargetPath(book, targetRoot, options, authorCounts);
            var item = new ReverseOrganizeItem
            {
                BookId = book.Id,
                Title = book.Title,
                Author = book.Author,
                SourcePath = sourcePath,
                TargetPath = targetPath,
                Tags = book.Tags,
                PageCount = book.PageCount,
                SourceBytes = sourceExists ? GetDirectoryBytes(sourcePath) : Math.Max(0, book.TotalBytes),
                SourceFileCount = sourceExists ? CountFiles(sourcePath) : 0,
                SourceExists = sourceExists,
                IsHidden = book.IsHidden
            };

            ValidateItemBasics(item, issues);
            ResolveTargetConflict(item, options, targetPaths, issues);
            items.Add(item);
        }

        ValidatePlanShell(targetRoot, options, items, issues);

        return new ReverseOrganizePlan
        {
            Options = options,
            Items = items,
            Issues = issues
        };
    }

    public async Task<ReverseOrganizeResult> ExecuteCopyAsync(
        ReverseOrganizePlan plan,
        IProgress<ReverseOrganizeProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(plan.Options.TargetRoot);

        var executableItems = plan.Items.Where(item => item.CanExecute).ToList();
        var succeeded = 0;
        var skipped = plan.Items.Count(item => item.Status == ReverseOrganizeItemStatus.Skipped || !item.CanExecute);
        var failed = 0;
        var completed = 0;
        var canceled = false;

        foreach (var item in executableItems)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                item.Status = ReverseOrganizeItemStatus.Canceled;
                item.Message = "用户取消，未执行复制。";
                skipped++;
                continue;
            }

            progress?.Report(new ReverseOrganizeProgress
            {
                Stage = "复制中",
                CurrentTitle = item.DisplayTitle,
                CompletedCount = completed,
                TotalCount = executableItems.Count,
                SucceededCount = succeeded,
                SkippedCount = skipped,
                FailedCount = failed
            });

            try
            {
                if (!Directory.Exists(item.SourcePath))
                {
                    item.Status = ReverseOrganizeItemStatus.Failed;
                    item.Message = "源目录不存在。";
                    failed++;
                }
                else if (Directory.Exists(item.TargetPath))
                {
                    item.Status = ReverseOrganizeItemStatus.Skipped;
                    item.Message = "目标目录已存在，已跳过。";
                    skipped++;
                }
                else
                {
                    await Task.Run(() => CopyDirectory(item.SourcePath, item.TargetPath, cancellationToken), cancellationToken);
                    VerifyCopiedItem(item);
                    item.Status = ReverseOrganizeItemStatus.Copied;
                    item.Message = "复制完成。";
                    succeeded++;
                }
            }
            catch (OperationCanceledException)
            {
                canceled = true;
                item.Status = ReverseOrganizeItemStatus.Canceled;
                item.Message = "用户取消，当前作品复制未完成。";
                skipped++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                item.Status = ReverseOrganizeItemStatus.Failed;
                item.Message = ex.Message;
                failed++;
            }

            completed++;
            progress?.Report(new ReverseOrganizeProgress
            {
                Stage = canceled ? "取消中" : "复制中",
                CurrentTitle = item.DisplayTitle,
                CompletedCount = completed,
                TotalCount = executableItems.Count,
                SucceededCount = succeeded,
                SkippedCount = skipped,
                FailedCount = failed
            });
        }

        var result = new ReverseOrganizeResult
        {
            TargetRoot = plan.Options.TargetRoot,
            Canceled = canceled,
            TotalCount = plan.Items.Count,
            CopiedCount = succeeded,
            SkippedCount = skipped,
            FailedCount = failed,
            Items = plan.Items
        };

        var manifestPath = WriteManifest(plan, result);
        return new ReverseOrganizeResult
        {
            TargetRoot = result.TargetRoot,
            ManifestPath = manifestPath,
            Canceled = result.Canceled,
            TotalCount = result.TotalCount,
            CopiedCount = result.CopiedCount,
            SkippedCount = result.SkippedCount,
            FailedCount = result.FailedCount,
            Items = result.Items
        };
    }

    public string WriteManifest(ReverseOrganizePlan plan, ReverseOrganizeResult result)
    {
        Directory.CreateDirectory(plan.Options.TargetRoot);
        var manifestPath = Path.Combine(plan.Options.TargetRoot, $"manifest-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
        var manifest = new ReverseOrganizeManifest
        {
            CreatedAt = DateTimeOffset.Now,
            Mode = "copy",
            TargetRoot = plan.Options.TargetRoot,
            Template = plan.Options.Template == ReverseOrganizeTemplate.AuthorYearTitle ? "{author}/{year} - {title}" : "{author}/{title}",
            BookCount = result.TotalCount,
            CopiedCount = result.CopiedCount,
            SkippedCount = result.SkippedCount,
            FailedCount = result.FailedCount,
            Canceled = result.Canceled,
            Books = result.Items.Select(item => new ReverseOrganizeManifestBook
            {
                BookId = item.BookId,
                Title = item.Title,
                Author = item.Author,
                SourcePath = item.SourcePath,
                TargetPath = item.TargetPath,
                Tags = TagService.ParseTags(item.Tags).ToArray(),
                PageCount = item.PageCount,
                TotalBytes = item.SourceBytes,
                FileCount = item.SourceFileCount,
                Status = item.Status.ToString().ToLowerInvariant(),
                Error = item.Status == ReverseOrganizeItemStatus.Failed ? item.Message : ""
            }).ToList()
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, options));
        return manifestPath;
    }

    private static void ValidatePlanShell(
        string targetRoot,
        ReverseOrganizeOptions options,
        IReadOnlyList<ReverseOrganizeItem> items,
        List<ReverseOrganizeValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            issues.Add(new ReverseOrganizeValidationIssue
            {
                Severity = ReverseOrganizeIssueSeverity.Error,
                Type = "目标目录",
                Message = "必须选择目标根目录。"
            });
            return;
        }

        foreach (var forbiddenRoot in options.ForbiddenRoots.Where(path => !string.IsNullOrWhiteSpace(path)).Select(NormalizePath))
        {
            if (IsSameOrChildPath(targetRoot, forbiddenRoot) || IsSameOrChildPath(forbiddenRoot, targetRoot))
            {
                issues.Add(new ReverseOrganizeValidationIssue
                {
                    Severity = ReverseOrganizeIssueSeverity.Error,
                    Type = "目标目录风险",
                    Message = "目标目录不能位于软件数据目录、应用目录或书库根目录的内部，也不能包含这些目录。",
                    TargetPath = targetRoot
                });
            }
        }

        foreach (var sourcePath in items.Select(item => item.SourcePath).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsSameOrChildPath(targetRoot, sourcePath))
            {
                issues.Add(new ReverseOrganizeValidationIssue
                {
                    Severity = ReverseOrganizeIssueSeverity.Error,
                    Type = "目标目录风险",
                    Message = "目标目录不能放在源漫画目录内部。",
                    SourcePath = sourcePath,
                    TargetPath = targetRoot
                });
                break;
            }
        }

        var executableBytes = items.Where(item => item.CanExecute).Sum(item => item.SourceBytes);
        if (executableBytes > 0 && TryGetAvailableBytes(targetRoot, out var availableBytes) && availableBytes < executableBytes)
        {
            issues.Add(new ReverseOrganizeValidationIssue
            {
                Severity = ReverseOrganizeIssueSeverity.Error,
                Type = "磁盘空间",
                Message = $"目标磁盘剩余空间不足。需要 {FormatSize(executableBytes)}，可用 {FormatSize(availableBytes)}。",
                TargetPath = targetRoot
            });
        }
    }

    private static void ValidateItemBasics(ReverseOrganizeItem item, List<ReverseOrganizeValidationIssue> issues)
    {
        if (!item.SourceExists)
        {
            item.CanExecute = false;
            item.Status = ReverseOrganizeItemStatus.Skipped;
            item.Message = "源目录不存在。";
            issues.Add(new ReverseOrganizeValidationIssue
            {
                Severity = ReverseOrganizeIssueSeverity.Warning,
                Type = "源目录缺失",
                Message = "源目录不存在，执行时会跳过。",
                BookTitle = item.Title,
                SourcePath = item.SourcePath,
                TargetPath = item.TargetPath
            });
        }

        if (item.TargetPath.Length > MaxPathLength)
        {
            item.CanExecute = false;
            item.Status = ReverseOrganizeItemStatus.Skipped;
            item.Message = "目标路径过长。";
            issues.Add(new ReverseOrganizeValidationIssue
            {
                Severity = ReverseOrganizeIssueSeverity.Error,
                Type = "路径过长",
                Message = $"目标路径超过 {MaxPathLength} 字符。",
                BookTitle = item.Title,
                SourcePath = item.SourcePath,
                TargetPath = item.TargetPath
            });
        }
    }

    private static void ResolveTargetConflict(
        ReverseOrganizeItem item,
        ReverseOrganizeOptions options,
        Dictionary<string, int> targetPaths,
        List<ReverseOrganizeValidationIssue> issues)
    {
        var originalTargetPath = item.TargetPath;
        var exists = Directory.Exists(originalTargetPath);
        var duplicated = targetPaths.ContainsKey(originalTargetPath);
        if (!exists && !duplicated)
        {
            targetPaths[originalTargetPath] = 1;
            return;
        }

        if (options.ConflictStrategy == ReverseOrganizeConflictStrategy.Skip)
        {
            item.CanExecute = false;
            item.Status = ReverseOrganizeItemStatus.Skipped;
            item.Message = "目标目录冲突，按策略跳过。";
            issues.Add(new ReverseOrganizeValidationIssue
            {
                Severity = ReverseOrganizeIssueSeverity.Warning,
                Type = "同名冲突",
                Message = "目标目录已存在或计划内重复，将跳过。",
                BookTitle = item.Title,
                SourcePath = item.SourcePath,
                TargetPath = originalTargetPath
            });
            return;
        }

        var index = targetPaths.TryGetValue(originalTargetPath, out var usedCount) ? usedCount + 1 : 2;
        string candidate;
        do
        {
            candidate = $"{originalTargetPath} ({index})";
            index++;
        }
        while (Directory.Exists(candidate) || targetPaths.ContainsKey(candidate));

        targetPaths[originalTargetPath] = index - 1;
        targetPaths[candidate] = 1;
        item.TargetPath = candidate;
        issues.Add(new ReverseOrganizeValidationIssue
        {
            Severity = ReverseOrganizeIssueSeverity.Info,
            Type = "同名冲突",
            Message = "目标目录冲突，已自动追加序号。",
            BookTitle = item.Title,
            SourcePath = item.SourcePath,
            TargetPath = candidate
        });
    }

    private static string BuildTargetPath(MangaBook book, string targetRoot, ReverseOrganizeOptions options, IReadOnlyDictionary<string, int> authorCounts)
    {
        var title = string.IsNullOrWhiteSpace(book.Title) ? book.Id : book.Title.Trim();
        var useSingleBookCollection = ShouldUseSingleBookCollection(book, options, authorCounts);
        var author = useSingleBookCollection
            ? options.SingleBookCollectionName
            : book.Author.Trim();
        var authorFolder = SanitizePathPart(string.IsNullOrWhiteSpace(author) ? "单本合集" : author);
        var titleFolder = SanitizePathPart(title);
        var workFolder = options.Template == ReverseOrganizeTemplate.AuthorYearTitle
            ? $"{SanitizePathPart(book.ProducedAt)} - {titleFolder}".Trim(' ', '-')
            : titleFolder;
        if (string.IsNullOrWhiteSpace(workFolder))
        {
            workFolder = titleFolder;
        }

        return NormalizePath(Path.Combine(targetRoot, authorFolder, workFolder));
    }

    private static bool ShouldUseSingleBookCollection(MangaBook book, ReverseOrganizeOptions options, IReadOnlyDictionary<string, int> authorCounts)
    {
        if (string.IsNullOrWhiteSpace(book.Author))
        {
            return true;
        }

        var threshold = Math.Max(0, options.SmallAuthorThreshold);
        if (threshold <= 1)
        {
            return false;
        }

        var key = NormalizeAuthorKey(book.Author);
        return authorCounts.TryGetValue(key, out var count) && count < threshold;
    }

    private static string NormalizeAuthorKey(string author)
    {
        return author.Trim();
    }

    private static void CopyDirectory(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetPath);
        foreach (var directory in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(targetPath, Path.GetRelativePath(sourcePath, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetFile = Path.Combine(targetPath, Path.GetRelativePath(sourcePath, file));
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(file, targetFile, overwrite: false);
        }
    }

    private static void VerifyCopiedItem(ReverseOrganizeItem item)
    {
        var copiedFileCount = CountFiles(item.TargetPath);
        var copiedBytes = GetDirectoryBytes(item.TargetPath);
        if (copiedFileCount != item.SourceFileCount || copiedBytes != item.SourceBytes)
        {
            throw new IOException($"复制校验失败：源文件 {item.SourceFileCount} 个/{item.SourceBytes} bytes，目标文件 {copiedFileCount} 个/{copiedBytes} bytes。");
        }
    }

    private static int CountFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            AppLogger.Warn("reverse-organize", $"统计文件数失败：{path} · {ex.Message}");
            return 0;
        }
    }

    private static long GetDirectoryBytes(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            AppLogger.Warn("reverse-organize", $"统计目录大小失败：{path} · {ex.Message}");
            return 0;
        }
    }

    private static bool TryGetAvailableBytes(string targetRoot, out long availableBytes)
    {
        availableBytes = 0;
        try
        {
            var root = Path.GetPathRoot(targetRoot);
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            availableBytes = new DriveInfo(root).AvailableFreeSpace;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLogger.Warn("reverse-organize", $"读取目标磁盘空间失败：{targetRoot} · {ex.Message}");
            return false;
        }
    }

    private static string SanitizePathPart(string value)
    {
        var result = string.IsNullOrWhiteSpace(value) ? "未命名" : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(invalid, '_');
        }

        return result.Trim().TrimEnd('.');
    }

    private static string NormalizePath(string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? ""
            : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsSameOrChildPath(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedCandidate = NormalizePath(candidate);
        var normalizedRoot = NormalizePath(root);
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatSize(long bytes)
    {
        const double gb = 1024d * 1024d * 1024d;
        const double mb = 1024d * 1024d;
        return bytes >= gb ? $"{bytes / gb:0.##}GB" : $"{Math.Max(1, bytes / mb):0.#}MB";
    }

    private sealed class ReverseOrganizeManifest
    {
        public int SchemaVersion { get; init; } = 1;
        public DateTimeOffset CreatedAt { get; init; }
        public string Mode { get; init; } = "copy";
        public string TargetRoot { get; init; } = "";
        public string Template { get; init; } = "";
        public int BookCount { get; init; }
        public int CopiedCount { get; init; }
        public int SkippedCount { get; init; }
        public int FailedCount { get; init; }
        public bool Canceled { get; init; }
        public IReadOnlyList<ReverseOrganizeManifestBook> Books { get; init; } = [];
    }

    private sealed class ReverseOrganizeManifestBook
    {
        public string BookId { get; init; } = "";
        public string Title { get; init; } = "";
        public string Author { get; init; } = "";
        public string SourcePath { get; init; } = "";
        public string TargetPath { get; init; } = "";
        public IReadOnlyList<string> Tags { get; init; } = [];
        public int PageCount { get; init; }
        public long TotalBytes { get; init; }
        public int FileCount { get; init; }
        public string Status { get; init; } = "";
        public string Error { get; init; } = "";
    }
}
