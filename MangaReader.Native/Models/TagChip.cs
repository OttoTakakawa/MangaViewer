namespace MangaReader.Native.Models;

public sealed class TagChip
{
    public string Name { get; set; } = "";
    public string RawName { get; set; } = "";
    public string Category { get; set; } = "自定义";
    public string Color { get; set; } = "#EFE5DA";
    public bool IsSelected { get; set; }
    public bool IsExcluded { get; set; }
    public int UsageCount { get; set; }
    public bool IsBuiltIn { get; set; }
    public bool IsExclusive { get; set; }
    public string SourceText { get; set; } = "用户标签";
    public string TypeText => IsExclusive ? "互斥" : "可叠加";
    public string UpdatedAt { get; set; } = "";
    public string UpdatedAtText => string.IsNullOrWhiteSpace(UpdatedAt) ? "最近更新：未记录" : $"最近更新：{UpdatedAt}";
    public List<MangaBook> PreviewBooks { get; set; } = [];
}
