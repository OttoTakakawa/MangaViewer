using MangaReader.Native.Services;
using System.Windows.Threading;

namespace MangaReader.Native;

public partial class App : System.Windows.Application
{
    private static bool _isShuttingDownAfterUnhandledException;

    public App()
    {
        AppLogger.Initialize(new AppStorage());
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        AppLogger.Info("app", "Application constructed.");
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        AppLogger.Info("app", $"Application exit. Code={e.ApplicationExitCode}");
        base.OnExit(e);
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (!_isShuttingDownAfterUnhandledException)
        {
            _isShuttingDownAfterUnhandledException = true;
            try { AppLogger.Crash("ui-dispatcher", e.Exception, "Unhandled UI exception. Application will shut down."); }
            catch { /* 日志写失败不阻止关闭 */ }
        }

        e.Handled = true;
        Current.Shutdown(1);
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            try { AppLogger.Crash("app-domain", exception, $"Unhandled domain exception. IsTerminating={e.IsTerminating}"); }
            catch { /* 日志写失败不阻止流程 */ }
            return;
        }

        try { AppLogger.Warn("app-domain", $"Unhandled non-exception object. IsTerminating={e.IsTerminating}"); }
        catch { }
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try { AppLogger.Crash("task-scheduler", e.Exception, "Unobserved task exception."); }
        catch { /* 日志写失败不阻止 SetObserved */ }
        e.SetObserved();
    }
}

