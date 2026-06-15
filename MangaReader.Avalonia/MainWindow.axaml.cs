using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using MangaReader.Avalonia.Services;
using MangaReader.Core.Models;
using MangaReader.Core.Services;

namespace MangaReader.Avalonia;

public sealed partial class MainWindow : Window
{
    private readonly AppStorage _storage;
    private readonly LibraryDatabase _database;
    private readonly LibraryScanner _scanner = new();
    private readonly ObservableCollection<MangaBook> _books = [];
    private readonly ObservableCollection<string> _tagRows = [];
    private readonly ObservableCollection<string> _authorRows = [];
    private readonly List<MangaBook> _allBooks = [];
    private MangaBook? _selectedBook;
    private string? _currentRoot;

    public MainWindow()
    {
        InitializeComponent();

        _storage = AvaloniaAppPaths.CreateStorage();
        _storage.EnsureCreated();
        _database = new LibraryDatabase(_storage);
        _database.Initialize();

        BooksList.ItemsSource = _books;
        TagManagerList.ItemsSource = _tagRows;
        AuthorManagerList.ItemsSource = _authorRows;
        ShowPage(LibraryPagePanel, "书库");
        LoadInitialLibrary();
    }

    private void LoadInitialLibrary()
    {
        var roots = _database.LoadLibraryRoots();
        _currentRoot = roots.LastOrDefault(Directory.Exists);
        if (_currentRoot is null)
        {
            StatusText.Text = $"数据目录：{_storage.Root}";
            return;
        }

        ScanRoot(_currentRoot);
    }

    private async void PickFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择漫画根目录",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder?.TryGetLocalPath() is not { } path || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _database.SaveLibraryRoot(path);
        _currentRoot = path;
        ScanRoot(path);
    }

    private void Rescan_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentRoot is null)
        {
            StatusText.Text = "请先选择漫画根目录。";
            return;
        }

        ScanRoot(_currentRoot);
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void BookFilter_Changed(object? sender, RoutedEventArgs e)
    {
        ApplyFilter();
    }

    private void BookFilter_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void NavHome_Click(object? sender, RoutedEventArgs e)
    {
        ShowPage(HomePagePanel, "主页");
    }

    private void NavLibrary_Click(object? sender, RoutedEventArgs e)
    {
        ShowPage(LibraryPagePanel, "书库");
    }

    private void NavTags_Click(object? sender, RoutedEventArgs e)
    {
        ShowPage(TagsPagePanel, "标签");
    }

    private void NavAuthors_Click(object? sender, RoutedEventArgs e)
    {
        ShowPage(AuthorsPagePanel, "作者");
    }

    private void BooksList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedBook = BooksList.SelectedItem as MangaBook;
        RenderSelectedBook();
    }

    private void Favorite_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedBook is null)
        {
            return;
        }

        _selectedBook.IsFavorite = !_selectedBook.IsFavorite;
        _database.SaveMetadata(_selectedBook);
        RenderSelectedBook();
        BooksList.ItemsSource = null;
        BooksList.ItemsSource = _books;
    }

    private void Read_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedBook is null)
        {
            return;
        }

        _selectedBook.ReadCount += 1;
        _database.SaveProgress(_selectedBook);
        _database.SaveReadCount(_selectedBook);

        var reader = new ReaderWindow(_selectedBook, _database)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        reader.Show(this);
        RenderSelectedBook();
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedBook is null || !Directory.Exists(_selectedBook.FolderPath))
        {
            return;
        }

        OpenPath(_selectedBook.FolderPath);
    }

    private void OpenGitHub_Click(object? sender, RoutedEventArgs e)
    {
        OpenPath("https://github.com/OttoTakakawa/MangaViewer/releases");
    }

    private void ScanRoot(string root)
    {
        if (!Directory.Exists(root))
        {
            StatusText.Text = "目录不存在，请重新选择。";
            return;
        }

        StatusText.Text = "正在扫描...";
        var saved = _database.LoadBooksByPath();
        var books = _scanner.Scan(root, saved);
        _database.UpsertBooksBatch(books);

        _allBooks.Clear();
        _allBooks.AddRange(books);
        RefreshManagers();
        RefreshHomeStats();
        ApplyFilter();

        StatusText.Text = $"已扫描 {_allBooks.Count} 本 · 数据目录：{_storage.Root}";
        BooksList.SelectedIndex = _books.Count > 0 ? 0 : -1;
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        IEnumerable<MangaBook> filtered = string.IsNullOrWhiteSpace(query)
            ? _allBooks
            : _allBooks.Where(book =>
                Contains(book.Title, query)
                || Contains(book.Author, query)
                || Contains(book.Tags, query)
                || Contains(book.Summary, query));

        if (FavoriteOnlyBox.IsChecked == true)
        {
            filtered = filtered.Where(book => book.IsFavorite);
        }

        filtered = SortBox.SelectedIndex switch
        {
            1 => filtered.OrderBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase),
            2 => filtered.OrderBy(book => book.Author, StringComparer.CurrentCultureIgnoreCase).ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase),
            3 => filtered.OrderByDescending(book => book.PageCount),
            _ => filtered.OrderByDescending(book => book.ImportedAt).ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase)
        };

        _books.Clear();
        foreach (var book in filtered)
        {
            _books.Add(book);
        }
    }

    private void RefreshManagers()
    {
        _tagRows.Clear();
        foreach (var tag in _allBooks
                     .SelectMany(book => TagService.ParseTags(book.Tags))
                     .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(group => group.Count())
                     .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            _tagRows.Add($"{tag.Key} · {tag.Count()} 本 · {TagService.GetCategory(tag.Key)}");
        }

        _authorRows.Clear();
        foreach (var author in _allBooks
                     .GroupBy(book => string.IsNullOrWhiteSpace(book.Author) ? "未知作者" : book.Author, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(group => group.Count())
                     .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            _authorRows.Add($"{author.Key} · {author.Count()} 本");
        }
    }

    private void RefreshHomeStats()
    {
        HomeBookCountText.Text = $"{_allBooks.Count} 本";
        HomeFavoriteCountText.Text = $"{_allBooks.Count(book => book.IsFavorite)} 本";
        HomeStorageText.Text = _storage.Root;
    }

    private void ShowPage(Control page, string title)
    {
        HomePagePanel.IsVisible = ReferenceEquals(page, HomePagePanel);
        LibraryPagePanel.IsVisible = ReferenceEquals(page, LibraryPagePanel);
        TagsPagePanel.IsVisible = ReferenceEquals(page, TagsPagePanel);
        AuthorsPagePanel.IsVisible = ReferenceEquals(page, AuthorsPagePanel);
        PageTitleText.Text = title;
    }

    private void RenderSelectedBook()
    {
        var book = _selectedBook;
        if (book is null)
        {
            TitleText.Text = "未选择作品";
            AuthorText.Text = "作者 -";
            MetaText.Text = "0 页";
            SummaryText.Text = "";
            TagChips.ItemsSource = null;
            CoverImage.Source = null;
            CoverPlaceholder.IsVisible = true;
            return;
        }

        TitleText.Text = string.IsNullOrWhiteSpace(book.Title) ? "未命名作品" : book.Title;
        AuthorText.Text = "作者 " + (string.IsNullOrWhiteSpace(book.Author) ? "未知作者" : book.Author);
        MetaText.Text = $"{book.PageCount} 页 · {book.ReadStateText}";
        FavoriteButton.Content = book.IsFavorite ? "★ 已收藏" : "☆ 收藏";
        TagChips.ItemsSource = book.TagItems.Count > 0 ? book.TagItems : null;
        SummaryText.Text = string.IsNullOrWhiteSpace(book.Summary) ? "暂无简介" : book.Summary;
        MetaAuthorText.Text = Empty(book.Author, "未知作者");
        ForeignNameText.Text = Empty(book.ForeignName, "未填写");
        PageCountText.Text = book.PageCount.ToString();
        CoverPageText.Text = $"{book.CoverPageIndex + 1} / {Math.Max(book.PageCount, 1)}";
        ImportedAtText.Text = Empty(book.ImportedAt, "未填写");
        FolderPathText.Text = book.FolderPath;

        LoadCover(book);
    }

    private void LoadCover(MangaBook book)
    {
        CoverImage.Source = null;
        CoverPlaceholder.IsVisible = true;

        if (book.Pages.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(book.CoverPageIndex, 0, book.Pages.Count - 1);
        var path = book.Pages[index];
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            CoverImage.Source = new Bitmap(stream);
            CoverPlaceholder.IsVisible = false;
        }
        catch
        {
            CoverImage.Source = null;
            CoverPlaceholder.IsVisible = true;
        }
    }

    private static bool Contains(string value, string query)
    {
        return value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string Empty(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", path);
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }

            Process.Start("xdg-open", path);
        }
        catch
        {
        }
    }
}
