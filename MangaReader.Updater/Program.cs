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
                ZipFile.ExtractToDirectory(options.PackagePath, extractRoot, overwriteFiles: true);
                var sourceRoot = ResolvePackageRoot(extractRoot);
                CopyDirectory(sourceRoot, options.TargetDirectory);
            }
            finally
            {
                TryDeleteDirectory(extractRoot);
                TryDeleteFile(options.PackagePath);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(options.TargetDirectory, options.ExecutableName),
                WorkingDirectory = options.TargetDirectory,
                UseShellExecute = true
            });

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
            using var process = Process.GetProcessById(processId);
            process.WaitForExit(30000);
        }
        catch
        {
            // The process may already be gone, which is the desired state.
        }
    }

    private static string ResolvePackageRoot(string extractRoot)
    {
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
            File.Copy(file, destination, overwrite: true);
        }
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
