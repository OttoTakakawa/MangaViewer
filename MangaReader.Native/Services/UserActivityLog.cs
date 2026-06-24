using System.Text;
using System.Text.Json;

namespace MangaReader.Native.Services;

public sealed record UserActivityEntry(
    DateTimeOffset Time,
    string Type,
    string Summary,
    int AffectedCount,
    int SucceededCount,
    int SkippedCount,
    int FailedCount,
    string Detail);

public sealed class UserActivityLog
{
    private const int MaxMemoryEntries = 80;
    private readonly object _syncRoot = new();
    private readonly Queue<UserActivityEntry> _historyEntries = new();
    private readonly Queue<UserActivityEntry> _sessionEntries = new();
    private string _activityPath = "";

    public void Initialize(AppStorage storage)
    {
        storage.EnsureCreated();
        var activityDirectory = Path.Combine(storage.Root, "activity");
        Directory.CreateDirectory(activityDirectory);
        _activityPath = Path.Combine(activityDirectory, $"activity-{DateTimeOffset.Now:yyyyMMdd}.jsonl");
        LoadRecentEntries();
    }

    public void Record(string type, string summary, int affectedCount = 0, int succeededCount = 0, int skippedCount = 0, int failedCount = 0, string detail = "")
    {
        if (string.IsNullOrWhiteSpace(_activityPath))
        {
            return;
        }

        var entry = new UserActivityEntry(
            DateTimeOffset.Now,
            type.Trim(),
            summary.Trim(),
            affectedCount,
            succeededCount,
            skippedCount,
            failedCount,
            detail.Trim());

        lock (_syncRoot)
        {
            Enqueue(_historyEntries, entry);
            Enqueue(_sessionEntries, entry);
            try
            {
                File.AppendAllText(_activityPath, JsonSerializer.Serialize(entry) + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                AppLogger.Warn("activity-log", $"写入用户操作记录失败：{ex.Message}");
            }
        }
    }

    public IReadOnlyList<UserActivityEntry> GetRecent(int count = 10)
    {
        lock (_syncRoot)
        {
            return _historyEntries
                .Reverse()
                .Take(Math.Max(1, count))
                .ToList();
        }
    }

    public IReadOnlyList<UserActivityEntry> GetRecentSession(int count = 10)
    {
        lock (_syncRoot)
        {
            return _sessionEntries
                .Reverse()
                .Take(Math.Max(1, count))
                .ToList();
        }
    }

    public string BuildExitSummary(int count = 6)
    {
        var recent = GetRecentSession(count);
        if (recent.Count == 0)
        {
            return "本次未记录到书库数据变更。";
        }

        var builder = new StringBuilder();
        builder.AppendLine("最近修改：");
        foreach (var entry in recent)
        {
            builder
                .Append(entry.Time.ToString("HH:mm"))
                .Append("  ")
                .Append(entry.Summary);

            if (entry.AffectedCount > 0)
            {
                builder.Append($"（影响 {entry.AffectedCount} 本");
                if (entry.FailedCount > 0 || entry.SkippedCount > 0)
                {
                    builder.Append($"，成功 {entry.SucceededCount}，跳过 {entry.SkippedCount}，失败 {entry.FailedCount}");
                }
                builder.Append(')');
            }

            builder.AppendLine();
        }

        builder.AppendLine("点击“操作历史”可查看更完整记录。");
        return builder.ToString().TrimEnd();
    }

    public string BuildHistoryReport(int count = 80)
    {
        var recent = GetRecent(count);
        if (recent.Count == 0)
        {
            return "暂无用户操作记录。";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"最近 {recent.Count} 条用户操作记录");
        builder.AppendLine();
        foreach (var entry in recent)
        {
            builder.AppendLine($"{entry.Time:yyyy-MM-dd HH:mm:ss}  {entry.Summary}");
            builder.AppendLine($"类型：{entry.Type}");
            builder.AppendLine($"影响：{entry.AffectedCount}　本　成功：{entry.SucceededCount}　跳过：{entry.SkippedCount}　失败：{entry.FailedCount}");
            if (!string.IsNullOrWhiteSpace(entry.Detail))
            {
                builder.AppendLine("详情：");
                builder.AppendLine(entry.Detail);
            }
            builder.AppendLine(new string('-', 36));
        }

        return builder.ToString().TrimEnd();
    }

    private void LoadRecentEntries()
    {
        lock (_syncRoot)
        {
            _historyEntries.Clear();
            _sessionEntries.Clear();
            if (!File.Exists(_activityPath))
            {
                return;
            }

            try
            {
                foreach (var line in File.ReadLines(_activityPath, Encoding.UTF8).Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(MaxMemoryEntries))
                {
                    var entry = JsonSerializer.Deserialize<UserActivityEntry>(line);
                    if (entry is not null)
                    {
                        Enqueue(_historyEntries, entry);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                AppLogger.Warn("activity-log", $"读取用户操作记录失败：{ex.Message}");
            }
        }
    }

    private static void Enqueue(Queue<UserActivityEntry> queue, UserActivityEntry entry)
    {
        queue.Enqueue(entry);
        while (queue.Count > MaxMemoryEntries)
        {
            queue.Dequeue();
        }
    }
}
