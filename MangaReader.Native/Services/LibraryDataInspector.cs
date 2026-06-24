using MangaReader.Native.Models;
using System.Collections.ObjectModel;
using System.Text;

namespace MangaReader.Native.Services;

public sealed class LibraryDataInspector
{
    public GovernanceReport BuildHealthGovernanceReport(IEnumerable<MangaBook> books)
    {
        var list = books.ToList();
        var missingFolders = list.Where(book => !Directory.Exists(book.FolderPath)).ToList();
        var emptyBooks = list.Where(book => book.PageCount <= 0 || book.Pages.Count == 0).ToList();
        var emptyAuthors = list.Where(book => string.IsNullOrWhiteSpace(book.Author)).ToList();
        var emptyTags = list.Where(book => string.IsNullOrWhiteSpace(book.Tags)).ToList();
        var duplicatePaths = list
            .GroupBy(book => book.FolderPath, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groups = new List<GovernanceGroup>();
        AddGroup(groups, "missing-folder", "缺失源目录", "本地目录不存在，通常需要重新定位或清理记录。", missingFolders, BuildHealthItemDetail);
        AddGroup(groups, "empty-pages", "页数为空或页面缓存为空", "目录存在，但当前记录没有有效页面。", emptyBooks, BuildHealthItemDetail);
        AddGroup(groups, "empty-author", "作者为空", "这类作品不影响使用，但会影响后续整理与过滤。", emptyAuthors, BuildHealthItemDetail);
        AddGroup(groups, "empty-tags", "Tag 为空", "这类作品建议在治理阶段补齐 Tag。", emptyTags, BuildHealthItemDetail);

        if (duplicatePaths.Count > 0)
        {
            groups.Add(new GovernanceGroup
            {
                Key = "duplicate-path",
                Title = "同一路径重复记录",
                Description = "多个库记录指向同一目录，建议逐项确认并清理。",
                Items = ToObservable(duplicatePaths
                    .SelectMany(group => group.Select(book => new GovernanceItem
                    {
                        GroupKey = "duplicate-path",
                        Title = book.Title,
                        Subtitle = $"{Display(book.Author)} · {book.PageCount} 页 · {FormatBytes(book.TotalBytes)}",
                        SearchText = book.Title,
                        FolderPath = book.FolderPath,
                        Author = book.Author,
                        Tags = book.Tags,
                        BookId = book.Id,
                        PageCount = book.PageCount,
                        TotalBytes = book.TotalBytes,
                        Detail = $"问题：同一路径重复记录{Environment.NewLine}作品：{book.Title}{Environment.NewLine}作者：{Display(book.Author)}{Environment.NewLine}页数：{book.PageCount}{Environment.NewLine}容量：{FormatBytes(book.TotalBytes)}{Environment.NewLine}路径：{book.FolderPath}"
                    })))
            });
        }

        var summary = new StringBuilder();
        summary.AppendLine($"检查作品：{list.Count} 本");
        summary.AppendLine($"问题分组：{groups.Count} 组");
        summary.AppendLine($"缺失目录：{missingFolders.Count}");
        summary.AppendLine($"空页面记录：{emptyBooks.Count}");
        summary.AppendLine($"作者为空：{emptyAuthors.Count}");
        summary.AppendLine($"Tag 为空：{emptyTags.Count}");
        summary.AppendLine($"重复路径记录：{duplicatePaths.Sum(group => group.Count())}");

        if (groups.Count == 0)
        {
            summary.AppendLine();
            summary.AppendLine("没有发现需要处理的结构问题。");
        }

        return new GovernanceReport
        {
            Title = "书库健康检查",
            Summary = summary.ToString().TrimEnd(),
            Groups = ToObservable(groups)
        };
    }

    public GovernanceReport BuildDuplicateGovernanceReport(IEnumerable<MangaBook> books)
    {
        var list = books.ToList();
        var candidates = list
            .Where(book => !string.IsNullOrWhiteSpace(book.Title))
            .GroupBy(book => BuildDuplicateKey(book), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groups = new List<GovernanceGroup>();
        var index = 1;
        foreach (var group in candidates)
        {
            groups.Add(new GovernanceGroup
            {
                Key = group.Key,
                Title = $"疑似重复组 {index++}",
                Description = $"共 {group.Count()} 项，按标题、作者、页数和容量进行强匹配。",
                Items = ToObservable(group
                        .OrderBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
                        .Select(book => new GovernanceItem
                        {
                            GroupKey = group.Key,
                            Title = book.Title,
                            Subtitle = $"{Display(book.Author)} · {book.PageCount} 页 · {FormatBytes(book.TotalBytes)}",
                            SearchText = book.Title,
                            FolderPath = book.FolderPath,
                            Author = book.Author,
                            Tags = book.Tags,
                            BookId = book.Id,
                            PageCount = book.PageCount,
                            TotalBytes = book.TotalBytes,
                            Detail = $"疑似重复候选{Environment.NewLine}作品：{book.Title}{Environment.NewLine}作者：{Display(book.Author)}{Environment.NewLine}页数：{book.PageCount}{Environment.NewLine}容量：{FormatBytes(book.TotalBytes)}{Environment.NewLine}路径：{book.FolderPath}{Environment.NewLine}Tag：{Display(book.Tags)}"
                        }))
            });
        }

        var summary = new StringBuilder();
        summary.AppendLine($"检查作品：{list.Count} 本");
        summary.AppendLine($"疑似重复组：{groups.Count} 组");
        summary.AppendLine($"候选作品：{groups.Sum(group => group.Items.Count)} 本");
        summary.AppendLine();
        summary.AppendLine("第一版只检测和展示，不自动合并、不自动删除。");

        if (groups.Count == 0)
        {
            summary.AppendLine("未发现基于标题、作者、页数和容量的强重复候选。");
        }

        return new GovernanceReport
        {
            Title = "疑似重复作品检测",
            Summary = summary.ToString().TrimEnd(),
            Groups = ToObservable(groups)
        };
    }

    public string BuildHealthReport(IEnumerable<MangaBook> books)
    {
        var report = BuildHealthGovernanceReport(books);
        var builder = new StringBuilder();
        builder.AppendLine(report.Title);
        builder.AppendLine(report.Summary);
        builder.AppendLine();
        foreach (var group in report.Groups)
        {
            builder.AppendLine($"{group.Title}：");
            foreach (var item in group.Items.Take(50))
            {
                builder.AppendLine($"- {item.Title} | {item.FolderPath}");
            }
            if (group.Items.Count > 50)
            {
                builder.AppendLine($"... 还有 {group.Items.Count - 50} 项未显示");
            }
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd();
    }

    public string BuildDuplicateReport(IEnumerable<MangaBook> books)
    {
        var report = BuildDuplicateGovernanceReport(books);
        var builder = new StringBuilder();
        builder.AppendLine(report.Title);
        builder.AppendLine(report.Summary);
        builder.AppendLine();
        foreach (var group in report.Groups.Take(50))
        {
            builder.AppendLine(group.Title);
            foreach (var item in group.Items)
            {
                builder.AppendLine($"- {item.Title}");
                builder.AppendLine($"  {item.Subtitle}");
                builder.AppendLine($"  路径：{item.FolderPath}");
            }
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd();
    }

    private static string BuildDuplicateKey(MangaBook book)
    {
        var title = NormalizeKey(book.Title);
        var author = NormalizeKey(book.Author);
        if (string.IsNullOrWhiteSpace(title))
        {
            return "";
        }

        return $"{author}|{title}|{book.PageCount}|{book.TotalBytes / 1024 / 1024}";
    }

    private static string NormalizeKey(string value)
    {
        return new string(value.Trim().ToLowerInvariant().Where(ch => !char.IsWhiteSpace(ch)).ToArray());
    }

    private static void AppendIssueSummary(StringBuilder builder, string name, int count)
    {
        builder.AppendLine($"- {name}：{count}");
    }

    private static void AppendBookSection(StringBuilder builder, string title, IReadOnlyList<MangaBook> books)
    {
        if (books.Count == 0)
        {
            return;
        }

        builder.AppendLine($"{title}：");
        foreach (var book in books.Take(50))
        {
            builder.AppendLine($"- {book.Title} | {book.FolderPath}");
        }
        if (books.Count > 50)
        {
            builder.AppendLine($"... 还有 {books.Count - 50} 项未显示");
        }
        builder.AppendLine();
    }

    private static string Display(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "未指定" : value;
    }

    private static string BuildHealthItemDetail(MangaBook book, string issueName)
    {
        return $"问题：{issueName}{Environment.NewLine}作品：{book.Title}{Environment.NewLine}作者：{Display(book.Author)}{Environment.NewLine}页数：{book.PageCount}{Environment.NewLine}容量：{FormatBytes(book.TotalBytes)}{Environment.NewLine}路径：{book.FolderPath}{Environment.NewLine}Tag：{Display(book.Tags)}";
    }

    private static void AddGroup(
        List<GovernanceGroup> groups,
        string key,
        string title,
        string description,
        IReadOnlyList<MangaBook> books,
        Func<MangaBook, string, string> detailBuilder)
    {
        if (books.Count == 0)
        {
            return;
        }

        groups.Add(new GovernanceGroup
        {
            Key = key,
            Title = title,
            Description = description,
            Items = ToObservable(books
                .OrderBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
                .Select(book => new GovernanceItem
                {
                    GroupKey = key,
                    Title = book.Title,
                    Subtitle = $"{Display(book.Author)} · {book.PageCount} 页 · {FormatBytes(book.TotalBytes)}",
                    SearchText = book.Title,
                    FolderPath = book.FolderPath,
                    Author = book.Author,
                    Tags = book.Tags,
                    BookId = book.Id,
                    PageCount = book.PageCount,
                    TotalBytes = book.TotalBytes,
                    Detail = detailBuilder(book, title)
                }))
        });
    }

    private static ObservableCollection<T> ToObservable<T>(IEnumerable<T> source)
    {
        return new ObservableCollection<T>(source);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024 / 1024:0.##} GB";
        }

        return $"{bytes / 1024d / 1024:0.##} MB";
    }
}
