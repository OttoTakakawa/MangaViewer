using System.Collections.Concurrent;

namespace MangaReader.Native.Services;

// 杂图库独立 tag 服务。
// 与漫画 TagService 完全隔离：走 misc_image_tags 表，颜色/分类独立存储，
// 内置缓存避免 O(M²) 颜色查询（仿 MangaView 已有的 managed tag 缓存策略）。
public static class MiscTagService
{
    private static readonly char[] TagSeparators = [',', '，', ';', '；'];

    // name → (category, color) 内存镜像；由上层在 LoadMiscTags / UpsertMiscTag 后调用 RefreshCache 同步。
    private static readonly ConcurrentDictionary<string, (string Category, string Color)> _cache = new(StringComparer.OrdinalIgnoreCase);

    // 内置预设颜色：杂图使用更柔和的色板，与漫画 TagCatalog.PresetColors 区分。
    public static readonly string[] PresetColors =
    [
        "#C7B8EA", "#F4B6C2", "#B7E4C7", "#FFE5A8",
        "#A8DCE3", "#F2C99B", "#D9C2F0", "#B5E8C9"
    ];

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

    public static string GetCategory(string tag)
    {
        return _cache.TryGetValue(tag, out var entry) ? entry.Category : "";
    }

    public static string GetColor(string tag)
    {
        if (_cache.TryGetValue(tag, out var entry) && !string.IsNullOrWhiteSpace(entry.Color))
        {
            return entry.Color;
        }

        return PresetColors[Math.Abs(tag.GetHashCode()) % PresetColors.Length];
    }

    public static string GetTextColor(string tag)
    {
        return GetTextColorForBackground(GetColor(tag));
    }

    public static string GetTextColorForBackground(string backgroundColor)
    {
        return ComputeLuminance(backgroundColor) < 0.5 ? "#FFFFFF" : "#111827";
    }

    // 由 MainWindow 在加载/修改 misc_image_tags 后调用，刷新内存缓存。
    public static void RefreshCache(IEnumerable<LibraryDatabase.ManagedTagRecord> records)
    {
        _cache.Clear();
        foreach (var record in records)
        {
            _cache[record.Name] = (record.Category ?? "", record.Color ?? "");
        }
    }

    public static void UpsertLocal(string name, string category, string color)
    {
        _cache[name] = (category ?? "", color ?? "");
    }

    public static void RemoveLocal(string name)
    {
        _cache.TryRemove(name, out _);
    }

    private static double ComputeLuminance(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length < 7 || hex[0] != '#')
        {
            return 0.5;
        }

        try
        {
            var r = Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0;
            var g = Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0;
            var b = Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0;
            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }
        catch
        {
            return 0.5;
        }
    }
}
