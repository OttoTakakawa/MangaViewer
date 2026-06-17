using System.Diagnostics;
using System.IO.Compression;

namespace MangaReader.Updater;

internal static class Program
{
    private static readonly string[] ProtectedNames =
    [
        "MangaReader_Data",
        "MangaReader_DataLocation.txt"
    ];

    private static int Main(string[] args)
    {
        try
        {
            var options = UpdateOptions.Parse(args);
            WaitForMainProcess(options.ProcessId);

            var extractRoot = Path.Combine(Path.GetTempPath(), "MangaReader_Update_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractRoot);

            try
            {
                var sourceRoot = ResolvePackageRoot(options.PackagePath, extractRoot);
                CopyDirectory(sourceRoot, options.TargetDirectory);
            }
            finally
            {
                TryDeleteDirectory(extractRoot);
                if (File.Exists(options.PackagePath))
                {
                    TryDeleteFile(options.PackagePath);
                }
            }

            StartMainExecutableWithRetry(options);

            return 0;
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "MangaReader_Updater_Error.log");
            File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {ex}\n\n");
            return 1;
        }
    }

    private static void WaitForMainProcess(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            var process = Process.GetProcessById(processId);

            // Try graceful shutdown signal first
            try
            {
                process.CloseMainWindow();
            }
            catch
            {
                // May not have a message loop (self-contained host), ignore
            }

            // Wait for graceful exit (short grace period)
            process.WaitForExit(5000);

            // If still running, terminate forcefully
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                    // Process may have already exited between Kill call and exception
                }
            }

            // Final wait for full teardown
            process.WaitForExit(60000);
        }
        catch
        {
            // The process may already be gone, which is the desired state.
        }
    }

    private static void StartMainExecutableWithRetry(UpdateOptions options)
    {
        var executablePath = Path.Combine(options.TargetDirectory, options.ExecutableName);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("更新完成，但未找到要重启的主程序。", executablePath);
        }

        Exception? lastError = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = executablePath,
                    WorkingDirectory = options.TargetDirectory,
                    UseShellExecute = true
                });
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                lastError = ex;
                Thread.Sleep(1000 + attempt * 500);
            }
        }

        throw new InvalidOperationException($"更新已完成，但自动重启主程序失败：{executablePath}", lastError);
    }

    private static string ResolvePackageRoot(string packagePath, string extractRoot)
    {
        if (Directory.Exists(packagePath))
        {
            var directoryExe = Path.Combine(packagePath, "MangaReader.Native.exe");
            if (File.Exists(directoryExe))
            {
                return packagePath;
            }

            throw new FileNotFoundException("本地更新目录内未找到 MangaReader.Native.exe。", directoryExe);
        }

        ZipFile.ExtractToDirectory(packagePath, extractRoot, overwriteFiles: true);
        var directExe = Path.Combine(extractRoot, "MangaReader.Native.exe");
        if (File.Exists(directExe))
        {
            return extractRoot;
        }

        var nestedExe = Directory
            .EnumerateFiles(extractRoot, "MangaReader.Native.exe", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(nestedExe))
        {
            throw new FileNotFoundException("更新包内未找到 MangaReader.Native.exe。");
        }

        return Path.GetDirectoryName(nestedExe) ?? extractRoot;
    }

    private static readonly object CopyLock = new();

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            if (IsProtectedPath(relativePath))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            if (IsProtectedPath(relativePath))
            {
                continue;
            }

            var destination = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyFileWithRetry(file, destination);
        }
    }

    private static void CopyFileWithRetry(string sourceFile, string destinationFile)
    {
        const int maxRetries = 5;
        var delayMs = 2000;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                lock (CopyLock)
                {
                    File.Copy(sourceFile, destinationFile, overwrite: true);
                }
                return;
            }
            catch (IOException) when (attempt < maxRetries)
            {
                // File is likely still locked by .NET Native JIT image or lingering handle
                var fileName = Path.GetFileName(sourceFile);
                Console.Error.WriteLine(
                    $"[Retry-{attempt + 1}] Locked file '{fileName}', retrying in {delayMs}ms...");
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 16000);
            }
            catch (UnauthorizedAccessException) when (attempt < maxRetries)
            {
                var fileName = Path.GetFileName(sourceFile);
                Console.Error.WriteLine(
                    $"[RetryAuth-{attempt + 1}] Access denied for '{fileName}', retrying in {delayMs}ms...");
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 16000);
            }
        }

        // Final attempt without retry — let it bubble up as an unhandled error
        File.Copy(sourceFile, destinationFile, overwrite: true);
    }

    private static bool IsProtectedPath(string relativePath)
    {
        var firstSegment = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return ProtectedNames.Any(name => string.Equals(firstSegment, name, StringComparison.OrdinalIgnoreCase));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Cleanup failure should not invalidate a completed update.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup failure should not invalidate a completed update.
        }
    }
}

internal sealed record UpdateOptions(string PackagePath, string TargetDirectory, string ExecutableName, int ProcessId)
{
    public static UpdateOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length - 1; i += 2)
        {
            values[args[i]] = args[i + 1];
        }

        var packagePath = Require(values, "--package");
        var targetDirectory = Require(values, "--target");
        var executableName = values.TryGetValue("--exe", out var exe) ? exe : "MangaReader.Native.exe";
        var processId = values.TryGetValue("--pid", out var pidText) && int.TryParse(pidText, out var pid) ? pid : 0;

        return new UpdateOptions(
            Path.GetFullPath(packagePath),
            Path.GetFullPath(targetDirectory),
            executableName,
            processId);
    }

    private static string Require(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"缺少更新参数：{key}");
        }

        return value;
    }
}
