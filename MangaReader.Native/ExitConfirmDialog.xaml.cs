using System.Windows;

namespace MangaReader.Native;

public partial class ExitConfirmDialog : Window
{
    public bool Confirmed { get; private set; }
    public bool ViewLogRequested { get; private set; }

    public ExitConfirmDialog(string summary)
    {
        InitializeComponent();
        SessionSummaryText.Text = summary;
    }

    private void Yes_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }

    private void No_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }

    private void ViewLog_Click(object sender, RoutedEventArgs e)
    {
        ViewLogRequested = true;
        Confirmed = false;
        DialogResult = false;
    }
}
