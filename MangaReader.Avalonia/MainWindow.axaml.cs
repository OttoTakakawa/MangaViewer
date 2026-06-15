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
    private const int MaxLogLines = 300;
    private readonly AppStorage _storage;
    private readonly LibraryDatabase _database;
    private readonly LibraryScanner _scanner = new();
    private readonly ObservableCollection<MangaBook> _books = [];
    private readonly ObservableCollection<TagManagerItem> _tagRows = [];
    private readonly ObservableCollection<AuthorManagerItem> _authorRows = [];
    private readonly List<MangaBook> _allBooks = [];
    private readonly Queue<string> _logLines = new();
    private readonly HashSet<string> _activeTagFilters = new(StringComparer.OrdinalIgnoreCase);
    private MangaBook? _selectedBook;
    private string? _currentRoot;
    private bool _isLogExpanded;

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
            RefreshHomeStats();
            AppendLog($"启动完成 · 数据目录：{_storage.Root}");
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
        AppendLog($"指定漫画根目录：{path}");
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

    private void TagManagerSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        RefreshManagers();
    }

    private void AuthorSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        RefreshManagers();
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
        RefreshManagers();
        ShowPage(TagsPagePanel, "标签");
    }

    private void NavAuthors_Click(object? sender, RoutedEventArgs e)
    {
        RefreshManagers();
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
        RefreshHomeStats();
        AppendLog($"{(_selectedBook.IsFavorite ? "收藏" : "取消收藏")}：{_selectedBook.Title}");
        RenderSelectedBook();
        ApplyFilter();
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
        AppendLog($"开始阅读：{_selectedBook.Title}");

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

    private async void Relocate_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedBook is null)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "重新定位漫画目录",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder?.TryGetLocalPath() is not { } path || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        var pages = Directory.EnumerateFiles(path)
            .Where(ImageFileService.IsSupportedImage)
            .OrderBy(file => file, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (pages.Count == 0)
        {
            StatusText.Text = "重新定位失败：目录内没有支持的图片。";
            AppendLog($"重新定位失败：{path}");
            return;
        }

        _selectedBook.Id = BookId.FromFolderPath(path);
        _selectedBook.FolderPath = path;
        _selectedBook.PageCount = pages.Count;
        _selectedBook.TotalBytes = ImageFileService.SumFileBytes(pages);
        _selectedBook.CoverPageIndex = Math.Clamp(_selectedBook.CoverPageIndex, 0, pages.Count - 1);
        _selectedBook.LastReadPageIndex = Math.Clamp(_selectedBook.LastReadPageIndex, 0, pages.Count - 1);
        _selectedBook.IsMissing = false;
        _selectedBook.Pages.Clear();
        foreach (var page in pages)
        {
            _selectedBook.Pages.Add(page);
        }

        _database.UpdateFolderPath(_selectedBook);
        RenderSelectedBook();
        ApplyFilter();
        AppendLog($"重新定位：{_selectedBook.Title} -> {path}");
    }

    private void ToggleHidden_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedBook is null)
        {
            return;
        }

        _selectedBook.IsHidden = !_selectedBook.IsHidden;
        _database.SetHidden(_selectedBook, _selectedBook.IsHidden);
        AppendLog($"{(_selectedBook.IsHidden ? "隐藏" : "恢复")}作品：{_selectedBook.Title}");
        RenderSelectedBook();
        ApplyFilter();
    }

    private void DeleteBook_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedBook is null)
        {
            return;
        }

        var title = _selectedBook.Title;
        _database.DeleteBook(_selectedBook);
        _allBooks.Remove(_selectedBook);
        _selectedBook = null;
        RefreshManagers();
        RefreshHomeStats();
        ApplyFilter();
        BooksList.SelectedIndex = _books.Count > 0 ? 0 : -1;
        RenderSelectedBook();
        AppendLog($"删除库记录：{title}");
    }

    private void Edit_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedBook is null)
        {
            return;
        }

        FillEditForm(_selectedBook);
        EditFormPanel.IsVisible = true;
    }

    private void CancelEdit_Click(object? sender, RoutedEventArgs e)
    {
        EditFormPanel.IsVisible = false;
    }

    private void SaveEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedBook is null)
        {
            return;
        }

        _selectedBook.Title = EditTitleBox.Text?.Trim() ?? "";
        _selectedBook.Author = EditAuthorBox.Text?.Trim() ?? "";
        _selectedBook.ForeignName = EditForeignNameBox.Text?.Trim() ?? "";
        _selectedBook.CharacterName = EditCharacterNameBox.Text?.Trim() ?? "";
        _selectedBook.ProducedAt = EditProducedAtBox.Text?.Trim() ?? "";
        _selectedBook.Tags = EditTagsBox.Text?.Trim() ?? "";
        _selectedBook.Summary = EditSummaryBox.Text?.Trim() ?? "";
        _selectedBook.CoverPageIndex = Math.Clamp(ToInt(EditCoverPageBox.Value, 1) - 1, 0, Math.Max(_selectedBook.PageCount - 1, 0));
        _selectedBook.ReadCount = ToInt(EditReadCountBox.Value, 0);
        _selectedBook.ReadingStatus = EditReadingStatusBox.SelectedIndex == 1 ? "reading" : "unread";

        _database.SaveMetadata(_selectedBook);
        EditFormPanel.IsVisible = false;
        RefreshManagers();
        RefreshHomeStats();
        ApplyFilter();
        RenderSelectedBook();
        AppendLog($"保存作品信息：{_selectedBook.Title}");
    }

    private void ToggleLog_Click(object? sender, RoutedEventArgs e)
    {
        _isLogExpanded = !_isLogExpanded;
        LogScroll.Height = _isLogExpanded ? 420 : 160;
        ToggleLogButton.Content = _isLogExpanded ? "还原" : "扩大";
    }

    private void OpenGitHub_Click(object? sender, RoutedEventArgs e)
    {
        OpenPath("https://github.com/OttoTakakawa/MangaViewer/releases");
    }

    private void ClearFilters_Click(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        TagSearchBox.Text = "";
        SortBox.SelectedIndex = 0;
        AuthorFilterBox.SelectedIndex = 0;
        StatusFilterBox.SelectedIndex = 0;
        FavoriteOnlyBox.IsChecked = false;
        ShowHiddenBox.IsChecked = false;
        _activeTagFilters.Clear();
        ApplyFilter();
        AppendLog("清除书库筛选");
    }

    private void TagManagerList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TagManagerList.SelectedItem is not TagManagerItem item)
        {
            return;
        }

        RenameTagNameBox.Text = item.Name;
        RenameTagCategoryBox.Text = item.Category;
        RenameTagColorBox.Text = item.Color;
    }

    private void AuthorManagerList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AuthorManagerList.SelectedItem is AuthorManagerItem item)
        {
            RenameAuthorNameBox.Text = item.Name;
        }
    }

    private void CreateTag_Click(object? sender, RoutedEventArgs e)
    {
        var name = NewTagNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText.Text = "标签名不能为空。";
            return;
        }

        var category = string.IsNullOrWhiteSpace(RenameTagCategoryBox.Text) ? TagService.GetCategory(name) : RenameTagCategoryBox.Text.Trim();
        var color = string.IsNullOrWhiteSpace(RenameTagColorBox.Text) ? TagService.GetColor(name) : RenameTagColorBox.Text.Trim();
        _database.SaveManagedTag(name, category, TagService.IsMutuallyExclusiveCategory(category), color);
        NewTagNameBox.Text = "";
        RefreshManagers();
        AppendLog($"新增标签：{name}");
    }

    private void FilterByTag_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TagManagerItem item })
        {
            return;
        }

        _activeTagFilters.Clear();
        _activeTagFilters.Add(item.Name);
        TagSearchBox.Text = "";
        ShowPage(LibraryPagePanel, "书库");
        ApplyFilter();
        AppendLog($"按 Tag 筛选：{item.Name}");
    }

    private void RenameTag_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TagManagerItem item })
        {
            return;
        }

        var newName = RenameTagNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(newName))
        {
            StatusText.Text = "标签名不能为空。";
            return;
        }

        var category = string.IsNullOrWhiteSpace(RenameTagCategoryBox.Text) ? item.Category : RenameTagCategoryBox.Text.Trim();
        var color = string.IsNullOrWhiteSpace(RenameTagColorBox.Text) ? item.Color : RenameTagColorBox.Text.Trim();
        var affected = new List<(string BookId, string Tags)>();
        foreach (var book in _allBooks.Where(book => TagService.ParseTags(book.Tags).Any(tag => string.Equals(tag, item.Name, StringComparison.OrdinalIgnoreCase))))
        {
            var tags = TagService.ParseTags(book.Tags)
                .Select(tag => string.Equals(tag, item.Name, StringComparison.OrdinalIgnoreCase) ? newName : tag)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            book.Tags = TagService.FormatTags(tags);
            affected.Add((book.Id, book.Tags));
        }

        if (affected.Count > 0)
        {
            _database.SaveBookTagsBatch(affected, "rename-tag-avalonia");
        }

        if (!string.Equals(item.Name, newName, StringComparison.OrdinalIgnoreCase))
        {
            _database.RenameManagedTag(item.Name, newName, category, TagService.IsMutuallyExclusiveCategory(category), color);
            if (_activeTagFilters.Remove(item.Name))
            {
                _activeTagFilters.Add(newName);
            }
        }
        else
        {
            _database.SaveManagedTag(newName, category, TagService.IsMutuallyExclusiveCategory(category), color);
        }

        RefreshManagers();
        ApplyFilter();
        RenderSelectedBook();
        AppendLog($"重命名标签：{item.Name} -> {newName}，影响 {affected.Count} 本");
    }

    private void DeleteTag_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TagManagerItem item })
        {
            return;
        }

        var affected = new List<(string BookId, string Tags)>();
        foreach (var book in _allBooks.Where(book => TagService.ParseTags(book.Tags).Any(tag => string.Equals(tag, item.Name, StringComparison.OrdinalIgnoreCase))))
        {
            var tags = TagService.ParseTags(book.Tags)
                .Where(tag => !string.Equals(tag, item.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            book.Tags = TagService.FormatTags(tags);
            affected.Add((book.Id, book.Tags));
        }

        if (affected.Count > 0)
        {
            _database.SaveBookTagsBatch(affected, "delete-tag-avalonia");
        }

        _database.DeleteManagedTag(item.Name);
        _database.SuppressTag(item.Name);
        _activeTagFilters.Remove(item.Name);
        RefreshManagers();
        ApplyFilter();
        RenderSelectedBook();
        AppendLog($"删除标签：{item.Name}，影响 {affected.Count} 本");
    }

    private void CreateAuthor_Click(object? sender, RoutedEventArgs e)
    {
        var name = AuthorSearchBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText.Text = "作者名不能为空。";
            return;
        }

        _database.SaveManagedAuthor(name);
        AuthorSearchBox.Text = "";
        RefreshManagers();
        AppendLog($"新增作者：{name}");
    }

    private void FilterByAuthor_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: AuthorManagerItem item })
        {
            return;
        }

        SelectAuthorFilter(item.Name);
        ShowPage(LibraryPagePanel, "书库");
        ApplyFilter();
        AppendLog($"按作者筛选：{item.Name}");
    }

    private void RenameAuthor_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: AuthorManagerItem item })
        {
            return;
        }

        var newName = RenameAuthorNameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(newName))
        {
            StatusText.Text = "作者名不能为空。";
            return;
        }

        var affected = _allBooks
            .Where(book => string.Equals(book.Author, item.Name, StringComparison.OrdinalIgnoreCase))
            .Select(book =>
            {
                book.Author = newName;
                return (book.Id, newName);
            })
            .ToList();

        if (affected.Count > 0)
        {
            _database.SaveBookAuthorsBatch(affected, "rename-author-avalonia");
        }

        _database.RenameManagedAuthor(item.Name, newName);
        RefreshManagers();
        SelectAuthorFilter(newName);
        ApplyFilter();
        RenderSelectedBook();
        AppendLog($"重命名作者：{item.Name} -> {newName}，影响 {affected.Count} 本");
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
        AppendLog($"扫描完成：{root} · {_allBooks.Count} 本");
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

        var tagQuery = TagSearchBox.Text?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(tagQuery))
        {
            filtered = filtered.Where(book => TagService.ParseTags(book.Tags).Any(tag => Contains(tag, tagQuery)));
        }

        if (_activeTagFilters.Count > 0)
        {
            filtered = filtered.Where(book =>
            {
                var tags = TagService.ParseTags(book.Tags).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return _activeTagFilters.All(tags.Contains);
            });
        }

        var selectedAuthor = AuthorFilterBox.SelectedItem as string;
        if (!string.IsNullOrWhiteSpace(selectedAuthor) && selectedAuthor != "全部作者")
        {
            filtered = filtered.Where(book => string.Equals(book.Author, selectedAuthor, StringComparison.OrdinalIgnoreCase));
        }

        filtered = StatusFilterBox.SelectedIndex switch
        {
            1 => filtered.Where(book => book.ReadCount <= 0 && book.ReadingStatus == "unread"),
            2 => filtered.Where(book => book.ReadCount > 0 || book.ReadingStatus == "reading"),
            _ => filtered
        };

        if (FavoriteOnlyBox.IsChecked == true)
        {
            filtered = filtered.Where(book => book.IsFavorite);
        }

        if (ShowHiddenBox.IsChecked != true)
        {
            filtered = filtered.Where(book => !book.IsHidden);
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

        ActiveTagSummaryText.Text = _activeTagFilters.Count == 0
            ? "已选 0 个 Tag"
            : $"已选 {_activeTagFilters.Count} 个 Tag：{string.Join("，", _activeTagFilters)}";
    }

    private void RefreshManagers()
    {
        var selectedAuthor = AuthorFilterBox.SelectedItem as string;
        var authors = _allBooks
            .Select(book => book.Author.Trim())
            .Where(author => !string.IsNullOrWhiteSpace(author))
            .Concat(_database.LoadManagedAuthors())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(author => author, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        AuthorFilterBox.ItemsSource = new[] { "全部作者" }.Concat(authors).ToList();
        SelectAuthorFilter(string.IsNullOrWhiteSpace(selectedAuthor) ? "全部作者" : selectedAuthor);

        var managedTags = _database.LoadManagedTags().ToDictionary(tag => tag.Name, StringComparer.OrdinalIgnoreCase);
        var suppressedTags = _database.LoadSuppressedTags().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tagQuery = TagManagerSearchBox.Text?.Trim() ?? "";
        _tagRows.Clear();
        var tagRows = _allBooks
            .SelectMany(book => TagService.ParseTags(book.Tags))
            .Concat(managedTags.Keys)
            .Where(tag => !suppressedTags.Contains(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(tag =>
            {
                var usage = _allBooks.Count(book => TagService.ParseTags(book.Tags).Any(name => string.Equals(name, tag, StringComparison.OrdinalIgnoreCase)));
                managedTags.TryGetValue(tag, out var managed);
                var category = managed?.Category;
                if (string.IsNullOrWhiteSpace(category))
                {
                    category = TagService.GetCategory(tag);
                }

                var color = managed?.Color;
                if (string.IsNullOrWhiteSpace(color))
                {
                    color = TagService.GetColor(tag);
                }

                return new TagManagerItem(tag, category, color, usage, managed is not null);
            })
            .Where(tag => string.IsNullOrWhiteSpace(tagQuery) || Contains(tag.Name, tagQuery) || Contains(tag.Category, tagQuery))
            .OrderByDescending(tag => tag.UsageCount)
            .ThenBy(tag => tag.Category, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var tag in tagRows)
        {
            _tagRows.Add(tag);
        }
        TagManagerStatsText.Text = $"{tagRows.Count} 个 · {tagRows.Count(tag => tag.UsageCount > 0)} 个已使用";

        _authorRows.Clear();
        var authorQuery = AuthorSearchBox.Text?.Trim() ?? "";
        var authorRows = authors
            .Select(author => new AuthorManagerItem(author, _allBooks.Count(book => string.Equals(book.Author, author, StringComparison.OrdinalIgnoreCase)), _database.LoadManagedAuthors().Contains(author, StringComparer.OrdinalIgnoreCase)))
            .Where(author => string.IsNullOrWhiteSpace(authorQuery) || Contains(author.Name, authorQuery))
            .OrderByDescending(author => author.BookCount)
            .ThenBy(author => author.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var author in authorRows)
        {
            _authorRows.Add(author);
        }
        AuthorManagerStatsText.Text = $"{authorRows.Count} 位 · {_allBooks.Count(book => !string.IsNullOrWhiteSpace(book.Author))} 本";
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

    private void SelectAuthorFilter(string author)
    {
        if (AuthorFilterBox.ItemsSource is not IEnumerable<string> authors)
        {
            return;
        }

        AuthorFilterBox.SelectedItem = authors.FirstOrDefault(item => string.Equals(item, author, StringComparison.OrdinalIgnoreCase)) ?? "全部作者";
    }

    private void FillEditForm(MangaBook book)
    {
        EditTitleBox.Text = book.Title;
        EditAuthorBox.Text = book.Author;
        EditForeignNameBox.Text = book.ForeignName;
        EditCharacterNameBox.Text = book.CharacterName;
        EditProducedAtBox.Text = book.ProducedAt;
        EditTagsBox.Text = book.Tags;
        EditSummaryBox.Text = book.Summary;
        EditCoverPageBox.Maximum = Math.Max(book.PageCount, 1);
        EditCoverPageBox.Value = book.CoverPageIndex + 1;
        EditReadCountBox.Value = book.ReadCount;
        EditReadingStatusBox.SelectedIndex = book.ReadingStatus == "reading" ? 1 : 0;
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
        HideButton.Content = book.IsHidden ? "恢复作品" : "隐藏作品";
        TagChips.ItemsSource = book.TagItems.Count > 0 ? book.TagItems : null;
        SummaryText.Text = string.IsNullOrWhiteSpace(book.Summary) ? "暂无简介" : book.Summary;
        MetaAuthorText.Text = Empty(book.Author, "未知作者");
        ForeignNameText.Text = Empty(book.ForeignName, "未填写");
        CharacterNameText.Text = Empty(book.CharacterName, "未填写");
        PageCountText.Text = book.PageCount.ToString();
        CoverPageText.Text = $"{book.CoverPageIndex + 1} / {Math.Max(book.PageCount, 1)}";
        ProducedAtText.Text = Empty(book.ProducedAt, "未填写");
        ReadCountText.Text = $"{book.ReadingStatusText} · {book.ReadCount} 次";
        SizeText.Text = book.SizeText;
        ImportedAtText.Text = Empty(book.ImportedAt, "未填写");
        FolderPathText.Text = book.FolderPath;

        LoadCover(book);
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {TrimLogLine(message)}";
        _logLines.Enqueue(line);
        while (_logLines.Count > MaxLogLines)
        {
            _logLines.Dequeue();
        }

        LogText.Text = string.Join(Environment.NewLine, _logLines);
    }

    private static string TrimLogLine(string message)
    {
        return message.Length <= 900 ? message : message[..900] + "...";
    }

    private static int ToInt(decimal? value, int fallback)
    {
        return value.HasValue ? decimal.ToInt32(value.Value) : fallback;
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

public sealed record TagManagerItem(string Name, string Category, string Color, int UsageCount, bool IsManaged)
{
    public string Summary => $"{Category} · {UsageCount} 本 · {(IsManaged ? "用户标签" : "书籍标签")}";
}

public sealed record AuthorManagerItem(string Name, int BookCount, bool IsManaged)
{
    public string Summary => $"{BookCount} 本 · {(IsManaged ? "用户作者" : "书籍作者")}";
}
