using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MangaReader.Native.Models;

public sealed class AuthorItem : INotifyPropertyChanged
{
    private int _bookCount;

    public required string Name { get; set; }

    public int BookCount
    {
        get => _bookCount;
        set
        {
            if (_bookCount != value)
            {
                _bookCount = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
