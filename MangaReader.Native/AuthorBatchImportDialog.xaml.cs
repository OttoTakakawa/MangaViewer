using MangaReader.Native.Models;
using System.Collections.ObjectModel;
using System.Windows;

namespace MangaReader.Native;

public partial class AuthorBatchImportDialog : Window
{
    public string AuthorName => AuthorBox.Text.Trim();
    public ObservableCollection<BatchImportCandidate> Candidates { get; }

    public AuthorBatchImportDialog(string folderPath, string authorName, IReadOnlyList<BatchImportCandidate> candidates)
    {
        InitializeComponent();
        Candidates = new ObservableCollection<BatchImportCandidate>(candidates);
        AuthorBox.Text = authorName;
        CandidateList.ItemsSource = Candidates;
        TitleText.Text = $"是否将“{Path.GetFileName(folderPath)}”作为作者批量导入？";
        FolderCountText.Text = $"{Candidates.Count} 个";
        BookCountText.Text = $"{Candidates.Count} 本";

        Loaded += (_, _) =>
        {
            AuthorBox.Focus();
            AuthorBox.SelectAll();
        };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AuthorName))
        {
            HintText.Text = "作者名不能为空。";
            return;
        }

        foreach (var candidate in Candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Title))
            {
                HintText.Text = "每一本漫画都需要标题。";
                return;
            }
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
