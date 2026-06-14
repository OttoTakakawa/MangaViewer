using System.Text;

namespace MangaReader.Native.Services;

public static class AppLogger
{
    private const int MaxLogFiles = 20;
    private static readonly object syncRoot = new();
    private static string _logsPath = "";
    private static string _currentLogPath = "";
    private static StreamWriter? _writer;

    public static event Action<string>? LineWritten;

    public static void Initialize(AppStorage storage)
    {
        storage.EnsureCreated();
        _logsPath = storage.LogsPath;
        Directory.CreateDirectory(_logsPath);
        PruneOldLogs();
        Info("app", "Application logging initialized.");
    }

    public static void Info(string scope, string message)
    {
        Write("INFO", scope, message);
    }

    public static void Warn(string scope, string message)
    {
        Write("WARN", scope, message);
    }

    public static void Error(string scope, Exception exception, string message = "")
    {
        Write("ERROR", scope, BuildExceptionMessage(exception, message));
    }

    public static void Crash(string scope, Exception exception, string message = "")
    {
        Write("ERROR", scope, BuildExceptionMessage(exception, message));
        WriteCrashSnapshot(scope, exception, message);
    }

    private static void Write(string level, string scope, string message)
    {
        if (string.IsNullOrWhiteSpace(_logsPath))
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:O} [{level}] [{scope}] {message}";
        try
        {
            lock (syncRoot)
            {
                EnsureWriter();
                _writer?.WriteLine(line);
                _writer?.Flush();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        LineWritten?.Invoke(line);
    }

    private static void EnsureWriter()
    {
        var path = GetDailyLogPath();
        if (path == _currentLogPath && _writer is not null)
        {
            return;
        }

        _writer?.Dispose();
        _currentLogPath = path;
        _writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = false };
    }

    private static void WriteCrashSnapshot(string scope, Exception exception, string message)
    {
        if (string.IsNullOrWhiteSpace(_logsPath))
        {
            return;
        }

        var fileName = $"crash-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.log";
        var content = new StringBuilder()
            .AppendLine($"Time: {DateTimeOffset.Now:O}")
            .AppendLine($"Scope: {scope}")
            .AppendLine($"Message: {message}")
            .AppendLine($"OS: {Environment.OSVersion}")
            .AppendLine($".NET: {Environment.Version}")
            .AppendLine($"BaseDirectory: {AppContext.BaseDirectory}")
            .AppendLine()
            .AppendLine(exception.ToString())
            .ToString();

        try
        {
            lock (syncRoot)
            {
                File.WriteAllText(Path.Combine(_logsPath, fileName), content, Encoding.UTF8);
                PruneOldLogs();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string BuildExceptionMessage(Exception exception, string message)
    {
        return string.IsNullOrWhiteSpace(message)
            ? exception.ToString()
            : $"{message}{Environment.NewLine}{exception}";
    }

    private static string GetDailyLogPath()
    {
        return Path.Combine(_logsPath, $"app-{DateTimeOffset.Now:yyyyMMdd}.log");
    }

    private static void PruneOldLogs()
    {
        if (string.IsNullOrWhiteSpace(_logsPath) || !Directory.Exists(_logsPath))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_logsPath, "*.log")
                     .Select(path => new FileInfo(path))
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Skip(MaxLogFiles))
        {
            try
            {
                file.Delete();
            }
            catch
            {
                // Logging cleanup must never crash the app.
            }
        }
    }
}
