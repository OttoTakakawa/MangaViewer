namespace MangaReader.Native.Services;

public sealed class CoverQualityProfile
{
    public required string SettingValue { get; init; }
    public required int CacheDecodeWidth { get; init; }
    public required int DisplayDecodeWidth { get; init; }
}

public static class CoverQualitySettings
{
    public const string SettingKey = "app.cover_quality";
    public const string DefaultValue = "standard";

    public static CoverQualityProfile Resolve(LibraryDatabase database)
    {
        var setting = database.LoadSetting(SettingKey, DefaultValue);
        return Resolve(setting);
    }

    public static CoverQualityProfile Resolve(string? settingValue)
    {
        return (settingValue ?? DefaultValue).Trim().ToLowerInvariant() switch
        {
            "low" => new CoverQualityProfile
            {
                SettingValue = "low",
                CacheDecodeWidth = 240,
                DisplayDecodeWidth = 160
            },
            "high" => new CoverQualityProfile
            {
                SettingValue = "high",
                CacheDecodeWidth = 560,
                DisplayDecodeWidth = 360
            },
            _ => new CoverQualityProfile
            {
                SettingValue = "standard",
                CacheDecodeWidth = 360,
                DisplayDecodeWidth = 240
            }
        };
    }
}
