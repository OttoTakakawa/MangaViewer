using System.Windows;

namespace MangaReader.Native;

public partial class BookmarkLabelDialog : Window
{
    public string BookmarkLabel => LabelBox.Text.Trim();
    public bool IsSkipped { get; private set; }

    public BookmarkLabelDialog(int pageIndex, string initialLabel = "")
    {
        InitializeComponent();
        Title = $"书签标签 - 第 {pageIndex + 1} 页";
        LabelBox.Text = initialLabel;
        Loaded += (_, _) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Activate();
                Topmost = true;
                Focus();
                LabelBox.Focus();
                LabelBox.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        IsSkipped = true;
        DialogResult = true;
    }
}
