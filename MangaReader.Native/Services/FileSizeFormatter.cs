namespace MangaReader.Native.Services;

internal static class FileSizeFormatter
{
    public static string Format(long bytes)
    {
        if (bytes <= 0)
        {
            return "0MB";
        }

        const double mb = 1024d * 1024d;
        const double gb = 1024d * 1024d * 1024d;
        return bytes >= gb
            ? $"{bytes / gb:0.##}G"
            : $"{Math.Max(1, bytes / mb):0.#}MB";
    }

    public static string FormatWithUnitSuffix(long bytes)
    {
        const double mb = 1024d * 1024d;
        const double gb = 1024d * 1024d * 1024d;
        return bytes >= gb ? $"{bytes / gb:0.##}GB" : $"{Math.Max(1, bytes / mb):0.#}MB";
    }

    public static string FormatBackupFile(long bytes)
    {
        const double kb = 1024d;
        const double mb = kb * 1024d;
        return bytes >= mb ? $"{bytes / mb:F1} MB" : $"{Math.Max(1, bytes / kb):F0} KB";
    }
}
