using MangaReader.Native.Models;
using System.Text;

namespace MangaReader.Native.Services;

public sealed class LibraryDataInspector
{
    public string BuildHealthReport(IEnumerable<MangaBook> books)
    {
        var list = books.ToList();
        var missingFolders = list.Where(book => !Directory.Exists(book.FolderPath)).ToList();
        var emptyBooks = list.Where(book => book.PageCount <= 0 || book.Pages.Count == 0).ToList();
        var emptyAuthors = list.Where(book => string.IsNullOrWhiteSpace(book.Author)).ToList();
        var emptyTags = list.Where(book => string.IsNullOrWhiteSpace(book.Tags)).ToList();
        var duplicatePaths = list
            .GroupBy(book => book.FolderPath, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine("书库健康检查");
        builder.AppendLine($"检查作品：{list.Count} 本");
        builder.AppendLine();
        AppendIssueSummary(builder, "缺失源目录", missingFolders.Count);
        AppendIssueSummary(builder, "页数为空或页面缓存为空", emptyBooks.Count);
        AppendIssueSummary(builder, "作者为空", emptyAuthors.Count);
        AppendIssueSummary(builder, "Tag 为空", emptyTags.Count);
        AppendIssueSummary(builder, "同一路径重复记录", duplicatePaths.Sum(group => group.Count()));
        builder.AppendLine();

        AppendBookSection(builder, "缺失源目录", missingFolders);
        AppendBookSection(builder, "页数为空或页面缓存为空", emptyBooks);
        AppendBookSection(builder, "作者为空", emptyAuthors);
        AppendBookSection(builder, "Tag 为空", emptyTags);

        if (duplicatePaths.Count > 0)
        {
            builder.AppendLine("同一路径重复记录：");
            foreach (var group in duplicatePaths.Take(30))
            {
                builder.AppendLine($"- {group.Key}");
                foreach (var book in group)
                {
                    builder.AppendLine($"  · {book.Title}");
                }
            }
            builder.AppendLine();
        }

        if (missingFolders.Count + emptyBooks.Count + duplicatePaths.Count == 0)
        {
            builder.AppendLine("没有发现高风险结构问题。作者为空、Tag 为空属于可整理项，不会阻塞使用。");
        }

        return builder.ToString().TrimEnd();
    }

    public string BuildDuplicateReport(IEnumerable<MangaBook> books)
    {
        var list = books.ToList();
        var candidates = list
            .Where(book => !string.IsNullOrWhiteSpace(book.Title))
            .GroupBy(book => BuildDuplicateKey(book), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .OrderByDescending(group => group.Count())
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine("疑似重复作品检测");
        builder.AppendLine($"检查作品：{list.Count} 本");
        builder.AppendLine($"疑似重复组：{candidates.Count} 组");
        builder.AppendLine();

        if (candidates.Count == 0)
        {
            builder.AppendLine("未发现基于标题、作者、页数和容量的强重复候选。");
            return builder.ToString().TrimEnd();
        }

        var index = 1;
        foreach (var group in candidates.Take(50))
        {
            builder.AppendLine($"疑似重复组 {index++}");
            foreach (var book in group.OrderBy(book => book.Title, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"- {book.Title}");
                builder.AppendLine($"  作者：{Display(book.Author)}");
                builder.AppendLine($"  页数：{book.PageCount}，容量：{FormatBytes(book.TotalBytes)}");
                builder.AppendLine($"  路径：{book.FolderPath}");
            }
            builder.AppendLine();
        }

        builder.AppendLine("第一版只检测和展示，不自动合并、不自动删除。");
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

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024 / 1024:0.##} GB";
        }

        return $"{bytes / 1024d / 1024:0.##} MB";
    }
}
