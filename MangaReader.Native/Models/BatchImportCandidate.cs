using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MangaReader.Native.Models;

public sealed class BatchImportCandidate : INotifyPropertyChanged
{
    private string _title = "";

    public string FolderPath { get; set; } = "";
    public string FolderName { get; set; } = "";
    public int PageCount { get; set; }
    public string Tags { get; set; } = "";
    public IReadOnlyList<string> Pages { get; set; } = [];

    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            OnPropertyChanged();
        }
    }

    public string SummaryText => $"{PageCount} 张 · {Tags}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
