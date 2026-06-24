using System.Collections.ObjectModel;

namespace MangaReader.Native.Models;

public sealed class GovernanceReport
{
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public ObservableCollection<GovernanceGroup> Groups { get; init; } = [];
}

public sealed class GovernanceGroup
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public ObservableCollection<GovernanceItem> Items { get; init; } = [];
}

public sealed class GovernanceItem
{
    public string GroupKey { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string SearchText { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public string Author { get; init; } = "";
    public string Tags { get; init; } = "";
    public string BookId { get; init; } = "";
    public int PageCount { get; init; }
    public long TotalBytes { get; init; }
    public string Detail { get; init; } = "";
}
