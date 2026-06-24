using System.Threading.Tasks;
using System.Windows;
using MangaReader.Native.Services;

namespace MangaReader.Native;

public partial class UpdateCheckDialog : Window
{
    private TaskCompletionSource<bool>? _installDecision;

    public bool WasClosed { get; private set; }

    public UpdateCheckDialog()
    {
        InitializeComponent();
        SetChecking(UpdateService.CurrentVersionText);
    }

    public void SetChecking(string currentVersion)
    {
        DialogTitleText.Text = "检查更新";
        DialogDescriptionText.Text = $"当前版本 {currentVersion}";
        UpdateMessageText.Text = "正在检查本地更新包与 GitHub Release...";
        UpdateProgressBar.IsIndeterminate = true;
        UpdateProgressBar.Value = 0;
        UpdatePercentText.Visibility = Visibility.Collapsed;
        CloseButton.Content = "取消";
        CloseButton.IsEnabled = true;
        InstallButton.Visibility = Visibility.Collapsed;
        InstallButton.IsEnabled = false;
    }

    public Task<bool> ShowUpdateAvailableAsync(UpdateCheckResult update)
    {
        _installDecision = new TaskCompletionSource<bool>();
        DialogTitleText.Text = "发现更新";
        DialogDescriptionText.Text = $"当前版本 {UpdateService.CurrentVersionText}，可更新到 {update.LatestVersion}";
        UpdateMessageText.Text = $"{update.Message}\n\n来源：{update.Source}\n更新包：{update.AssetName}\n\n是否现在准备并安装？安装时软件会关闭，更新完成后自动重启。";
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = 100;
        UpdatePercentText.Visibility = Visibility.Collapsed;
        CloseButton.Content = "以后再说";
        CloseButton.IsEnabled = true;
        InstallButton.Visibility = Visibility.Visible;
        InstallButton.IsEnabled = true;
        Activate();
        return _installDecision.Task;
    }

    public void SetNoUpdate(UpdateCheckResult update)
    {
        DialogTitleText.Text = "检查完成";
        DialogDescriptionText.Text = "没有发现可用更新";
        UpdateMessageText.Text = update.Message;
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = 100;
        UpdatePercentText.Visibility = Visibility.Collapsed;
        CloseButton.Content = "知道了";
        CloseButton.IsEnabled = true;
        InstallButton.Visibility = Visibility.Collapsed;
        InstallButton.IsEnabled = false;
        Activate();
    }

    public void SetFailed(string message)
    {
        DialogTitleText.Text = "检查失败";
        DialogDescriptionText.Text = "更新检查没有完成";
        UpdateMessageText.Text = message;
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = 0;
        UpdatePercentText.Visibility = Visibility.Collapsed;
        CloseButton.Content = "关闭";
        CloseButton.IsEnabled = true;
        InstallButton.Visibility = Visibility.Collapsed;
        InstallButton.IsEnabled = false;
        Activate();
    }

    public void SetPreparing(string message)
    {
        DialogTitleText.Text = "准备更新";
        DialogDescriptionText.Text = "正在准备更新包";
        UpdateMessageText.Text = message;
        UpdateProgressBar.IsIndeterminate = true;
        UpdateProgressBar.Value = 0;
        UpdatePercentText.Visibility = Visibility.Collapsed;
        CloseButton.IsEnabled = false;
        InstallButton.Visibility = Visibility.Collapsed;
        InstallButton.IsEnabled = false;
        Activate();
    }

    public void SetProgress(string message, double progress)
    {
        var normalized = Math.Clamp(progress, 0, 1);
        UpdateMessageText.Text = message;
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = normalized * 100;
        UpdatePercentText.Visibility = Visibility.Visible;
        UpdatePercentText.Text = normalized >= 1 ? "100%" : $"约 {normalized:P0}";
    }

    protected override void OnClosed(EventArgs e)
    {
        WasClosed = true;
        _installDecision?.TrySetResult(false);
        base.OnClosed(e);
    }

    private void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        _installDecision?.TrySetResult(true);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _installDecision?.TrySetResult(false);
        Close();
    }
}
