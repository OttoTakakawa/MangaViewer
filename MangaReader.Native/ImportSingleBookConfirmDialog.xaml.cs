using MangaReader.Native.Models;
using MangaReader.Native.Services;
using System.Windows;

namespace MangaReader.Native;

public partial class ImportSingleBookConfirmDialog : Window
{
    public string EditedTitle { get; private set; } = "";

    public ImportSingleBookConfirmDialog(BatchImportCandidate candidate)
    {
        InitializeComponent();
        FolderPathText.Text = candidate.FolderPath;
        TitleBox.Text = candidate.Title;
        SummaryText.Text = BuildSummary(candidate);
        TagsText.Text = string.IsNullOrWhiteSpace(candidate.Tags)
            ? "未识别到自动标签。"
            : $"自动标签：{candidate.Tags}";
        UpdateConfirmButton();
    }

    private static string BuildSummary(BatchImportCandidate candidate)
    {
        var totalBytes = ImageLoader.SumFileBytes(candidate.Pages);
        return $"{candidate.PageCount} 张图片 · {FileSizeFormatter.Format(totalBytes)}";
    }

    private void TitleBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        EditedTitle = TitleBox.Text.Trim();
        UpdateConfirmButton();
    }

    private void UpdateConfirmButton()
    {
        ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(TitleBox.Text);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        EditedTitle = TitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(EditedTitle))
        {
            HintText.Text = "标题不能为空。";
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
