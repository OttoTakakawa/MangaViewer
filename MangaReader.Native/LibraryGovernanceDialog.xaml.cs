using MangaReader.Native.Models;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;

namespace MangaReader.Native;

public partial class LibraryGovernanceDialog : Window
{
    private readonly List<GovernanceRow> _allRows = [];
    private readonly ObservableCollection<GovernanceRow> _filteredRows = [];

    public Action<GovernanceItem>? SearchRequested { get; set; }
    public Action<GovernanceItem>? FilterByAuthorRequested { get; set; }
    public Action<GovernanceItem>? OpenDetailRequested { get; set; }
    public Action<GovernanceItem>? OpenFolderRequested { get; set; }

    public LibraryGovernanceDialog(GovernanceReport report)
    {
        InitializeComponent();
        Title = report.Title;
        TitleText.Text = report.Title;
        SummaryText.Text = report.Summary;

        foreach (var group in report.Groups)
        {
            foreach (var item in group.Items)
            {
                _allRows.Add(new GovernanceRow(group.Title, group.Description, item));
            }
        }

        ItemsList.ItemsSource = _filteredRows;
        GroupFilterBox.Items.Add("全部分组");
        foreach (var title in report.Groups.Select(group => group.Title).Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            GroupFilterBox.Items.Add(title);
        }
        GroupFilterBox.SelectedIndex = 0;
        RefreshRows();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        RefreshRows();
    }

    private void RefreshRows()
    {
        var query = SearchBox.Text.Trim();
        var selectedGroup = GroupFilterBox.SelectedItem as string;
        var filtered = _allRows
            .Where(row => string.IsNullOrWhiteSpace(selectedGroup)
                || selectedGroup == "全部分组"
                || string.Equals(row.GroupTitle, selectedGroup, StringComparison.CurrentCultureIgnoreCase))
            .Where(row => string.IsNullOrWhiteSpace(query) || row.Matches(query))
            .ToList();

        _filteredRows.Clear();
        foreach (var row in filtered)
        {
            _filteredRows.Add(row);
        }

        CountText.Text = $"当前 {filtered.Count} 项 / 总计 {_allRows.Count} 项";
        if (_filteredRows.Count > 0 && ItemsList.SelectedItem is null)
        {
            ItemsList.SelectedIndex = 0;
        }
        else if (_filteredRows.Count == 0)
        {
            ItemTitleText.Text = "没有命中的条目";
            ItemSubtitleText.Text = "";
            DetailTextBox.Text = "";
            UpdateActionButtons(null);
        }
    }

    private void ItemsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var row = ItemsList.SelectedItem as GovernanceRow;
        if (row is null)
        {
            ItemTitleText.Text = "请选择左侧条目";
            ItemSubtitleText.Text = "";
            DetailTextBox.Text = "";
            UpdateActionButtons(null);
            return;
        }

        ItemTitleText.Text = row.Item.Title;
        ItemSubtitleText.Text = string.IsNullOrWhiteSpace(row.Item.Subtitle)
            ? row.GroupDescription
            : $"{row.Item.Subtitle}{Environment.NewLine}{row.GroupDescription}";
        DetailTextBox.Text = row.Item.Detail;
        UpdateActionButtons(row.Item);
    }

    private void UpdateActionButtons(GovernanceItem? item)
    {
        SearchButton.IsEnabled = item is not null && SearchRequested is not null && !string.IsNullOrWhiteSpace(item.SearchText);
        AuthorButton.IsEnabled = item is not null && FilterByAuthorRequested is not null && !string.IsNullOrWhiteSpace(item.Author);
        DetailButton.IsEnabled = item is not null && OpenDetailRequested is not null && (!string.IsNullOrWhiteSpace(item.BookId) || !string.IsNullOrWhiteSpace(item.Title));
        FolderButton.IsEnabled = item is not null && OpenFolderRequested is not null && !string.IsNullOrWhiteSpace(item.FolderPath);
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is GovernanceRow row)
        {
            SearchRequested?.Invoke(row.Item);
            Close();
        }
    }

    private void Author_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is GovernanceRow row)
        {
            FilterByAuthorRequested?.Invoke(row.Item);
            Close();
        }
    }

    private void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is GovernanceRow row)
        {
            OpenDetailRequested?.Invoke(row.Item);
            Close();
        }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is GovernanceRow row)
        {
            OpenFolderRequested?.Invoke(row.Item);
            Close();
        }
    }

    private void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DetailButton.IsEnabled)
        {
            Detail_Click(sender, new RoutedEventArgs());
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private sealed class GovernanceRow
    {
        public GovernanceRow(string groupTitle, string groupDescription, GovernanceItem item)
        {
            GroupTitle = groupTitle;
            GroupDescription = groupDescription;
            Item = item;
        }

        public string GroupTitle { get; }
        public string GroupDescription { get; }
        public GovernanceItem Item { get; }
        public string Title => Item.Title;
        public string Subtitle => Item.Subtitle;

        public bool Matches(string query)
        {
            return GroupTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || Subtitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || Item.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || Item.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || Item.Tags.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || Item.FolderPath.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || Item.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
