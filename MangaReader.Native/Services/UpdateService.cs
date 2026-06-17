using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
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

    public static string CurrentVersionText => FormatVersion(GetCurrentVersion());

    public async Task<UpdateCheckResult> CheckLatestAsync(CancellationToken cancellationToken = default)
    {
        var localUpdate = CheckLocalUpdate();
        if (localUpdate is not null)
        {
            return localUpdate;
        }

        using var response = await Client.GetAsync(LatestReleaseUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return UpdateCheckResult.Failed($"本地没有发现更新包，GitHub 检查失败：返回 {(int)response.StatusCode}。");
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

        var asset = (release.Assets ?? [])
            .Where(item => item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (asset is null || string.IsNullOrWhiteSpace(asset.DownloadUrl))
        {
            return UpdateCheckResult.Failed($"发现新版本 {release.TagName}，但 Release 没有可下载的 zip 更新包。");
        }

        return UpdateCheckResult.GitHubUpdateAvailable(release.TagName, currentVersion, asset.DownloadUrl, asset.Name);
    }

    public async Task<string> DownloadPackageAsync(UpdateCheckResult update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!update.HasUpdate)
        {
            throw new InvalidOperationException("没有可用的更新包。");
        }

        if (!string.IsNullOrWhiteSpace(update.PackagePath))
        {
            progress?.Report(1);
            return update.PackagePath;
        }

        if (!string.IsNullOrWhiteSpace(update.ProjectPath))
        {
            return await PublishLocalProjectAsync(update, progress, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
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

        var isolatedUpdaterPath = CreateIsolatedUpdaterCopy(updaterPath);
        var currentProcess = Process.GetCurrentProcess();
        var targetDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var executableName = Path.GetFileName(Environment.ProcessPath) ?? "MangaReader.Native.exe";

        Process.Start(new ProcessStartInfo
        {
            FileName = isolatedUpdaterPath,
            Arguments = $"--package \"{packagePath}\" --target \"{targetDirectory}\" --exe \"{executableName}\" --pid {currentProcess.Id}",
            WorkingDirectory = Path.GetDirectoryName(isolatedUpdaterPath),
            UseShellExecute = true
        });
    }

    private static string CreateIsolatedUpdaterCopy(string updaterPath)
    {
        var updaterDirectory = Path.Combine(Path.GetTempPath(), "MangaReader_Updater_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updaterDirectory);

        var isolatedUpdaterPath = Path.Combine(updaterDirectory, Path.GetFileName(updaterPath));
        File.Copy(updaterPath, isolatedUpdaterPath, overwrite: true);
        return isolatedUpdaterPath;
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

    private UpdateCheckResult? CheckLocalUpdate()
    {
        var currentVersion = GetCurrentVersion();
        var bestPackage = FindBestLocalPackage(currentVersion);
        if (bestPackage is not null)
        {
            return UpdateCheckResult.LocalPackageAvailable(
                FormatVersion(bestPackage.Version),
                currentVersion,
                bestPackage.Path,
                bestPackage.DisplayName);
        }

        var projectPath = FindLocalProjectPath();
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            var projectVersion = ReadProjectVersion(projectPath);
            if (projectVersion is not null && projectVersion > currentVersion)
            {
                return UpdateCheckResult.LocalSourceAvailable(
                    FormatVersion(projectVersion),
                    currentVersion,
                    projectPath,
                    $"本地源码 {FormatVersion(projectVersion)}");
            }
        }

        return null;
    }

    private async Task<string> PublishLocalProjectAsync(UpdateCheckResult update, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(update.ProjectPath))
        {
            throw new InvalidOperationException("没有可发布的本地项目。");
        }

        var updateDirectory = Path.Combine(_storage.Root, "updates");
        Directory.CreateDirectory(updateDirectory);

        var outputDirectory = Path.Combine(updateDirectory, $"local-build-{SanitizeFileName(update.LatestVersion)}");
        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
        Directory.CreateDirectory(outputDirectory);

        progress?.Report(0.05);
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"publish \"{update.ProjectPath}\" -c Release -r win-x64 --self-contained true -o \"{outputDirectory}\"",
            WorkingDirectory = Path.GetDirectoryName(update.ProjectPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 dotnet publish。本地源码更新需要安装 .NET 8 SDK。");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var output = await outputTask;
            var error = await errorTask;
            throw new InvalidOperationException($"本地发布更新包失败，请确认已安装 .NET 8 SDK。\n{output}\n{error}".Trim());
        }

        var executablePath = Path.Combine(outputDirectory, "MangaReader.Native.exe");
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("本地发布完成，但未找到 MangaReader.Native.exe。", executablePath);
        }

        progress?.Report(1);
        return outputDirectory;
    }

    private static LocalUpdatePackage? FindBestLocalPackage(Version currentVersion)
    {
        return EnumerateLocalPackages()
            .Where(package => package.Version > currentVersion)
            .OrderByDescending(package => package.Version)
            .FirstOrDefault();
    }

    private static IEnumerable<LocalUpdatePackage> EnumerateLocalPackages()
    {
        foreach (var root in EnumerateSearchRoots())
        {
            var releaseDirectory = Path.Combine(root, "_release");
            if (Directory.Exists(releaseDirectory))
            {
                foreach (var directory in Directory.EnumerateDirectories(releaseDirectory))
                {
                    var version = ParseVersion(Path.GetFileName(directory));
                    if (version is not null && File.Exists(Path.Combine(directory, "MangaReader.Native.exe")))
                    {
                        yield return new LocalUpdatePackage(version, directory, $"本地发布目录 {Path.GetFileName(directory)}");
                    }
                }

                foreach (var zip in Directory.EnumerateFiles(releaseDirectory, "*.zip"))
                {
                    var version = ParseVersionFromFileName(Path.GetFileNameWithoutExtension(zip));
                    if (version is not null)
                    {
                        yield return new LocalUpdatePackage(version, zip, Path.GetFileName(zip));
                    }
                }
            }

            var updatesDirectory = Path.Combine(root, "updates");
            if (!Directory.Exists(updatesDirectory))
            {
                continue;
            }

            foreach (var zip in Directory.EnumerateFiles(updatesDirectory, "*.zip"))
            {
                var version = ParseVersionFromFileName(Path.GetFileNameWithoutExtension(zip));
                if (version is not null)
                {
                    yield return new LocalUpdatePackage(version, zip, Path.GetFileName(zip));
                }
            }
        }
    }

    private static string? FindLocalProjectPath()
    {
        foreach (var root in EnumerateSearchRoots())
        {
            var projectPath = Path.Combine(root, "MangaReader.Native", "MangaReader.Native.csproj");
            if (File.Exists(projectPath))
            {
                return projectPath;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in EnumerateAncestors(AppContext.BaseDirectory).Append(Directory.GetCurrentDirectory()))
        {
            var normalized = Path.GetFullPath(root);
            if (seen.Add(normalized))
            {
                yield return normalized;
            }
        }
    }

    private static IEnumerable<string> EnumerateAncestors(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        while (directory is not null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
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

    private static Version? ParseVersionFromFileName(string fileName)
    {
        var match = Regex.Match(fileName, @"v?(\d+\.\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        return match.Success ? ParseVersion(match.Groups[1].Value) : null;
    }

    public static string FormatVersion(Version version)
    {
        return version.Revision >= 0 ? version.ToString(4) : version.ToString(3);
    }

    private static Version? ReadProjectVersion(string projectPath)
    {
        var text = File.ReadAllText(projectPath);
        var match = Regex.Match(text, @"<Version>\s*([^<]+)\s*</Version>", RegexOptions.IgnoreCase);
        return match.Success ? ParseVersion(match.Groups[1].Value) : null;
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
        [property: System.Text.Json.Serialization.JsonPropertyName("assets")] IReadOnlyList<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("browser_download_url")] string DownloadUrl);

    private sealed record LocalUpdatePackage(Version Version, string Path, string DisplayName);
}

public sealed record UpdateCheckResult(
    bool HasUpdate,
    bool IsCurrent,
    string LatestVersion,
    Version CurrentVersion,
    string? DownloadUrl,
    string? PackagePath,
    string? ProjectPath,
    string? AssetName,
    string Source,
    string Message)
{
    public static UpdateCheckResult GitHubUpdateAvailable(string latestVersion, Version currentVersion, string downloadUrl, string assetName)
    {
        return new UpdateCheckResult(true, false, latestVersion, currentVersion, downloadUrl, null, null, assetName, "GitHub", $"发现 GitHub 新版本 {latestVersion}。");
    }

    public static UpdateCheckResult LocalPackageAvailable(string latestVersion, Version currentVersion, string packagePath, string assetName)
    {
        return new UpdateCheckResult(true, false, latestVersion, currentVersion, null, packagePath, null, assetName, "本地更新包", $"发现本地更新 {latestVersion}。");
    }

    public static UpdateCheckResult LocalSourceAvailable(string latestVersion, Version currentVersion, string projectPath, string assetName)
    {
        return new UpdateCheckResult(true, false, latestVersion, currentVersion, null, null, projectPath, assetName, "本地源码", $"发现本地源码版本 {latestVersion}。");
    }

    public static UpdateCheckResult UpToDate(string latestVersion, Version currentVersion)
    {
        return new UpdateCheckResult(false, true, latestVersion, currentVersion, null, null, null, null, "无更新", $"当前已是最新版本：{UpdateService.FormatVersion(currentVersion)}。");
    }

    public static UpdateCheckResult Failed(string message)
    {
        return new UpdateCheckResult(false, false, "", new Version(0, 0, 0), null, null, null, null, "失败", message);
    }
}
