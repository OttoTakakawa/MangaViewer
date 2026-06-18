namespace MangaReader.Native.Services;

public static class TagCatalog
{
    public static readonly string[] PresetColors =
    [
        "#F4B6C2", "#B7D7A8", "#A9CCE3", "#F7DC6F",
        "#D7BDE2", "#F5CBA7", "#AED6F1", "#A3E4D7"
    ];

    public static readonly TagPreset[] BuiltInPresets =
    [
        new("单行本", "内容形态", "#E8F1FF", true),
        new("同人志", "内容形态", "#E8F1FF", true),
        new("CG", "内容形态", "#E8F1FF", true),
        new("杂图合集", "内容形态", "#E8F1FF", true),
        new("画集", "内容形态", "#E8F1FF", true),
        new("短篇", "内容形态", "#E8F1FF", true),
        new("全彩", "色彩规格", "#FFF2D6", true),
        new("黑白", "色彩规格", "#FFF2D6", true),
        new("部分彩色", "色彩规格", "#FFF2D6", true),
        new("高清", "画质规格", "#EAF7E8", true),
        new("超清", "画质规格", "#EAF7E8", true),
        new("扫图", "画质规格", "#EAF7E8", true),
        new("修图版", "画质规格", "#EAF7E8", true)
    ];

    public static string GetCategory(string tag, IReadOnlyDictionary<string, string>? managedCategories = null)
    {
        if (managedCategories is not null && managedCategories.TryGetValue(tag, out var category))
        {
            return category;
        }

        return BuiltInPresets.FirstOrDefault(item => string.Equals(item.Name, tag, StringComparison.OrdinalIgnoreCase))?.Category
            ?? "自定义";
    }

    public static string GetColor(string tag)
    {
        var preset = BuiltInPresets.FirstOrDefault(item => string.Equals(item.Name, tag, StringComparison.OrdinalIgnoreCase));
        if (preset is not null)
        {
            return preset.Color;
        }

        return PresetColors[Math.Abs(tag.GetHashCode()) % PresetColors.Length];
    }
}

public sealed record TagPreset(string Name, string Category, string Color, bool IsExclusive);
