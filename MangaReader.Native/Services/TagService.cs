namespace MangaReader.Native.Services;

public static class TagService
{
    private static readonly char[] TagSeparators = [',', '，', ';', '；'];

    public static IEnumerable<string> ParseTags(string tags)
    {
        return tags.Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static string FormatTags(IEnumerable<string> tags)
    {
        return string.Join(", ", tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static string GetCategory(string tag, IReadOnlyDictionary<string, string>? managedCategories = null)
    {
        return TagCatalog.GetCategory(tag, managedCategories);
    }

    public static string GetColor(string tag)
    {
        return TagCatalog.GetColor(tag);
    }

    public static bool IsMutuallyExclusiveCategory(string category)
    {
        return string.Equals(category, "内容形态", StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, "色彩规格", StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, "画质规格", StringComparison.OrdinalIgnoreCase);
    }

    public static int CategoryOrder(string category)
    {
        return category switch
        {
            "内容形态" => 0,
            "色彩规格" => 1,
            "画质规格" => 2,
            _ => 3
        };
    }
}
