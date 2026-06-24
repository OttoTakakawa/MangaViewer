using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
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
        var localUpdate = await Task.Run(() => CheckLocalUpdate(cancellationToken), cancellationToken);
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

    private UpdateCheckResult? CheckLocalUpdate(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var currentVersion = GetCurrentVersion();
        var currentBuild = GetCurrentBuildInfo(currentVersion);
        AppLogger.Info("update", $"本地更新检查开始：currentVersion={FormatVersion(currentVersion)}, baseDirectory={currentBuild.BaseDirectory}, processPath={Environment.ProcessPath}");
        var bestPackage = FindBestLocalPackage(currentBuild, _storage.Root, cancellationToken);
        if (bestPackage is not null)
        {
            AppLogger.Info("update", $"检测到本地更新：latestVersion={FormatVersion(bestPackage.Version)}, path={bestPackage.Path}, source={bestPackage.DisplayName}");
            return UpdateCheckResult.LocalPackageAvailable(
                FormatVersion(bestPackage.Version),
                currentVersion,
                bestPackage.Path,
                bestPackage.DisplayName);
        }

        var projectPath = FindLocalProjectPath(cancellationToken);
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        AppLogger.Info("update", $"本地没有发现可用更新：currentVersion={FormatVersion(currentVersion)}, baseDirectory={currentBuild.BaseDirectory}");
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

        progress?.Report(0.08);
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

        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        var fakeProgress = 0.08d;
        progress?.Report(fakeProgress);

        var outputTask = Task.Run(async () =>
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                stdoutLines.Add(line);
                fakeProgress = Math.Max(fakeProgress, EstimatePublishProgress(line, fakeProgress));
                progress?.Report(fakeProgress);
            }
        }, cancellationToken);

        var errorTask = Task.Run(async () =>
        {
            while (true)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                stderrLines.Add(line);
            }
        }, cancellationToken);

        var heartbeatTask = Task.Run(async () =>
        {
            while (!process.HasExited)
            {
                await Task.Delay(700, cancellationToken);
                fakeProgress = Math.Min(0.92, fakeProgress + 0.03);
                progress?.Report(fakeProgress);
            }
        }, cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(outputTask, errorTask);
        try
        {
            await heartbeatTask;
        }
        catch (OperationCanceledException)
        {
        }

        if (process.ExitCode != 0)
        {
            var output = string.Join(Environment.NewLine, stdoutLines);
            var error = string.Join(Environment.NewLine, stderrLines);
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

    private static double EstimatePublishProgress(string line, double currentProgress)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return currentProgress;
        }

        if (line.Contains("正在确定要还原的项目", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Determining projects to restore", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(currentProgress, 0.14);
        }

        if (line.Contains("已还原", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Restored", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(currentProgress, 0.28);
        }

        if (line.Contains("->", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(currentProgress, 0.62);
        }

        if (line.Contains("已成功生成", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(currentProgress, 0.82);
        }

        if (line.Contains("Publish", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("publish", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Max(currentProgress, 0.88);
        }

        return currentProgress;
    }

    private static LocalUpdatePackage? FindBestLocalPackage(CurrentBuildInfo currentBuild, string? storageRoot, CancellationToken cancellationToken)
    {
        return EnumerateLocalPackages(storageRoot, currentBuild.Version, cancellationToken)
            .Where(package => IsCandidateLocalPackage(package, currentBuild))
            .OrderByDescending(package => package.Version)
            .ThenByDescending(package => package.ModifiedUtc)
            .FirstOrDefault();
    }

    private static bool IsCandidateLocalPackage(LocalUpdatePackage package, CurrentBuildInfo currentBuild)
    {
        var versionComparison = package.Version.CompareTo(currentBuild.Version);
        if (versionComparison > 0)
        {
            AppLogger.Info("update", $"候选包通过：版本更高。candidateVersion={FormatVersion(package.Version)}, candidatePath={package.Path}");
            return true;
        }

        if (versionComparison < 0)
        {
            AppLogger.Info("update", $"候选包跳过：版本更低。candidateVersion={FormatVersion(package.Version)}, candidatePath={package.Path}");
            return false;
        }

        AppLogger.Info("update", $"版本号一致，开始检查包体变化：candidatePath={package.Path}, currentBaseDirectory={currentBuild.BaseDirectory}, isDirectory={package.IsDirectory}");

        if (package.IsDirectory &&
            string.Equals(NormalizePath(package.Path), currentBuild.BaseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Info("update", $"检测到包体一致，跳过更新：candidatePath={package.Path} 与当前运行目录一致");
            return false;
        }

        if (!package.IsDirectory)
        {
            AppLogger.Info("update", $"检测到同版本 zip 包，允许更新：candidatePath={package.Path}");
            return true;
        }

        var packageBuildSignature = TryGetBuildSignature(package.ExecutablePath);
        var currentBuildSignature = TryGetBuildSignature(currentBuild.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(packageBuildSignature) &&
            !string.IsNullOrWhiteSpace(currentBuildSignature) &&
            !string.Equals(packageBuildSignature, currentBuildSignature, StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Info("update", $"检测到同版本但构建标识不同，允许更新：candidateSignature={packageBuildSignature}, currentSignature={currentBuildSignature}");
            return true;
        }

        var packageContentSignature = TryGetContentSignature(package.ExecutablePath);
        var currentContentSignature = TryGetContentSignature(currentBuild.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(packageContentSignature) &&
            !string.IsNullOrWhiteSpace(currentContentSignature))
        {
            var sameContent = string.Equals(packageContentSignature, currentContentSignature, StringComparison.OrdinalIgnoreCase);
            AppLogger.Info("update", sameContent
                ? $"检测到包体一致，跳过更新：candidatePath={package.Path}"
                : $"检测到同版本但包体不同，允许更新：candidatePath={package.Path}");
            return !sameContent;
        }

        var acceptedByModifiedTime = package.ModifiedUtc > currentBuild.ModifiedUtc.AddSeconds(1);
        AppLogger.Info("update", $"同版本包体时间比较：candidateModifiedUtc={package.ModifiedUtc:O}, currentModifiedUtc={currentBuild.ModifiedUtc:O}, accepted={acceptedByModifiedTime}");
        return acceptedByModifiedTime;
    }

    private static IEnumerable<LocalUpdatePackage> EnumerateLocalPackages(string? storageRoot, Version minimumVersion, CancellationToken cancellationToken)
    {
        var scannedUpdateDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in EnumerateSearchRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var releaseDirectory = Path.Combine(root, "_release");
            if (Directory.Exists(releaseDirectory))
            {
                foreach (var directory in Directory.EnumerateDirectories(releaseDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var version = ParseVersion(Path.GetFileName(directory));
                    if (version is null || version < minimumVersion)
                    {
                        continue;
                    }

                    var executablePath = Path.Combine(directory, "MangaReader.Native.exe");
                    if (File.Exists(executablePath))
                    {
                        yield return new LocalUpdatePackage(
                            version,
                            directory,
                            $"本地发布目录 {Path.GetFileName(directory)}",
                            File.GetLastWriteTimeUtc(executablePath),
                            executablePath,
                            true);
                    }
                }

                foreach (var zip in Directory.EnumerateFiles(releaseDirectory, "*.zip"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var version = ParseVersionFromFileName(Path.GetFileNameWithoutExtension(zip));
                    if (version is not null && version >= minimumVersion)
                    {
                        yield return new LocalUpdatePackage(
                            version,
                            zip,
                            Path.GetFileName(zip),
                            File.GetLastWriteTimeUtc(zip),
                            null,
                            false);
                    }
                }
            }

            foreach (var updatesDirectory in EnumerateUpdateDirectories(root, storageRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalizedDirectory = NormalizePath(updatesDirectory);
                if (!Directory.Exists(updatesDirectory) || !scannedUpdateDirectories.Add(normalizedDirectory))
                {
                    continue;
                }

                foreach (var zip in Directory.EnumerateFiles(updatesDirectory, "*.zip"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var version = ParseVersionFromFileName(Path.GetFileNameWithoutExtension(zip));
                    if (version is not null && version >= minimumVersion)
                    {
                        yield return new LocalUpdatePackage(
                            version,
                            zip,
                            Path.GetFileName(zip),
                            File.GetLastWriteTimeUtc(zip),
                            null,
                            false);
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateUpdateDirectories(string root, string? storageRoot)
    {
        yield return Path.Combine(root, "updates");
        yield return Path.Combine(root, "MangaReader_Data", "updates");

        if (!string.IsNullOrWhiteSpace(storageRoot))
        {
            yield return Path.Combine(storageRoot, "updates");
        }

        yield return Path.Combine(AppStorage.DefaultRoot, "updates");
    }

    private static string? FindLocalProjectPath(CancellationToken cancellationToken)
    {
        foreach (var root in EnumerateSearchRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private static CurrentBuildInfo GetCurrentBuildInfo(Version currentVersion)
    {
        var executablePath = Environment.ProcessPath;
        var modifiedUtc = !string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath)
            ? File.GetLastWriteTimeUtc(executablePath)
            : DateTime.MinValue;

        return new CurrentBuildInfo(
            currentVersion,
            NormalizePath(AppContext.BaseDirectory),
            modifiedUtc,
            executablePath);
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

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? TryGetBuildSignature(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        var productVersion = FileVersionInfo.GetVersionInfo(executablePath).ProductVersion;
        return string.IsNullOrWhiteSpace(productVersion) ? null : productVersion.Trim();
    }

    private static string? TryGetContentSignature(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(executablePath);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record GitHubRelease(
        [property: System.Text.Json.Serialization.JsonPropertyName("tag_name")] string TagName,
        [property: System.Text.Json.Serialization.JsonPropertyName("assets")] IReadOnlyList<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("browser_download_url")] string DownloadUrl);

    private sealed record CurrentBuildInfo(Version Version, string BaseDirectory, DateTime ModifiedUtc, string? ExecutablePath);

    private sealed record LocalUpdatePackage(
        Version Version,
        string Path,
        string DisplayName,
        DateTime ModifiedUtc,
        string? ExecutablePath,
        bool IsDirectory);
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
