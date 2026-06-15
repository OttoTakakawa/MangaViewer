using MangaReader.Core.Services;

namespace MangaReader.Avalonia.Services;

public static class AvaloniaAppPaths
{
    public static AppStorage CreateStorage()
    {
        return new AppStorage(GetApplicationDataRoot());
    }

    private static string GetApplicationDataRoot()
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "MangaReader");
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            return Path.Combine(appData, "MangaReader");
        }

        return Path.Combine(AppContext.BaseDirectory, "MangaReader_Data");
    }
}
