using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace MangaReader.Native.Services;

public sealed class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/OttoTakakawa/MangaViewer/releases/latest";
    private const string UpdaterRelativePath = "Updater\\MangaReader.Updater.exe";
    private readonly AppStorage _storage;
    private static readonly HttpClient Client = CreateClient();

    public UpdateService(AppStorage storage)
    {
        _storage = storage;
    }

    public static string CurrentVersionText => GetCurrentVersion().ToString(3);

    public async Task<UpdateCheckResult> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(LatestReleaseUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return UpdateCheckResult.Failed($"检查更新失败：GitHub 返回 {(int)response.StatusCode}。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: cancellationToken);
        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
        {
            return UpdateCheckResult.Failed("检查更新失败：Release 信息为空。");
        }

        var latestVersion = ParseVersion(release.TagName);
        if (latestVersion is null)
        {
            return UpdateCheckResult.Failed($"检查更新失败：无法识别版本号 {release.TagName}。");
        }

        var currentVersion = GetCurrentVersion();
        if (latestVersion <= currentVersion)
        {
            return UpdateCheckResult.UpToDate(release.TagName, currentVersion);
        }

        var asset = release.Assets
            .Where(item => item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (asset is null || string.IsNullOrWhiteSpace(asset.DownloadUrl))
        {
            return UpdateCheckResult.Failed($"发现新版本 {release.TagName}，但 Release 没有可下载的 zip 更新包。");
        }

        return UpdateCheckResult.UpdateAvailable(release.TagName, currentVersion, asset.DownloadUrl, asset.Name);
    }

    public async Task<string> DownloadPackageAsync(UpdateCheckResult update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!update.HasUpdate || string.IsNullOrWhiteSpace(update.DownloadUrl))
        {
            throw new InvalidOperationException("没有可下载的更新包。");
        }

        var updateDirectory = Path.Combine(_storage.Root, "updates");
        Directory.CreateDirectory(updateDirectory);
        var packagePath = Path.Combine(updateDirectory, SanitizeFileName(update.AssetName ?? $"MangaReader-{update.LatestVersion}.zip"));

        using var response = await Client.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(packagePath);

        var buffer = new byte[1024 * 96];
        long totalRead = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;
            if (contentLength is > 0)
            {
                progress?.Report(Math.Clamp(totalRead / (double)contentLength.Value, 0, 1));
            }
        }

        progress?.Report(1);
        return packagePath;
    }

    public void LaunchUpdater(string packagePath)
    {
        var updaterPath = ResolveUpdaterPath();
        if (!File.Exists(updaterPath))
        {
            throw new FileNotFoundException("未找到更新器 MangaReader.Updater.exe。请使用正式发布包运行自动更新。", updaterPath);
        }

        var currentProcess = Process.GetCurrentProcess();
        var targetDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var executableName = Path.GetFileName(Environment.ProcessPath) ?? "MangaReader.Native.exe";

        Process.Start(new ProcessStartInfo
        {
            FileName = updaterPath,
            Arguments = $"--package \"{packagePath}\" --target \"{targetDirectory}\" --exe \"{executableName}\" --pid {currentProcess.Id}",
            WorkingDirectory = Path.GetDirectoryName(updaterPath),
            UseShellExecute = true
        });
    }

    private static string ResolveUpdaterPath()
    {
        var publishedPath = Path.Combine(AppContext.BaseDirectory, UpdaterRelativePath);
        if (File.Exists(publishedPath))
        {
            return publishedPath;
        }

        var developmentPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "MangaReader.Updater",
            "bin",
            "Debug",
            "net8.0-windows",
            "MangaReader.Updater.exe"));

        return developmentPath;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MangaReader/{CurrentVersionText}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Version GetCurrentVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
    }

    private static Version? ParseVersion(string text)
    {
        var normalized = text.Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out var version) ? version : null;
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '_');
        }

        return fileName;
    }

    private sealed record GitHubRelease(
        [property: System.Text.Json.Serialization.JsonPropertyName("tag_name")] string TagName,
        [property: System.Text.Json.Serialization.JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);

    private sealed record GitHubAsset(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("browser_download_url")] string DownloadUrl);
}

public sealed record UpdateCheckResult(
    bool HasUpdate,
    bool IsCurrent,
    string LatestVersion,
    Version CurrentVersion,
    string? DownloadUrl,
    string? AssetName,
    string Message)
{
    public static UpdateCheckResult UpdateAvailable(string latestVersion, Version currentVersion, string downloadUrl, string assetName)
    {
        return new UpdateCheckResult(true, false, latestVersion, currentVersion, downloadUrl, assetName, $"发现新版本 {latestVersion}。");
    }

    public static UpdateCheckResult UpToDate(string latestVersion, Version currentVersion)
    {
        return new UpdateCheckResult(false, true, latestVersion, currentVersion, null, null, $"当前已是最新版本：{currentVersion.ToString(3)}。");
    }

    public static UpdateCheckResult Failed(string message)
    {
        return new UpdateCheckResult(false, false, "", new Version(0, 0, 0), null, null, message);
    }
}
