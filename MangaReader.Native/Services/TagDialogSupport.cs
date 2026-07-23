namespace MangaReader.Native.Services;

internal static class TagDialogSupport
{
    public static bool IsValidHexColor(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length == 7
            && value[0] == '#'
            && value.Skip(1).All(Uri.IsHexDigit);
    }

    public static IReadOnlyList<string> NormalizeCustomColors(
        IEnumerable<string>? colors,
        IReadOnlyCollection<string> presetColors)
    {
        return colors?
            .Where(IsValidHexColor)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(color => !presetColors.Contains(color, StringComparer.OrdinalIgnoreCase))
            .ToList() ?? [];
    }

    public static string GetSelectedCategory(System.Windows.Controls.ComboBox box, string fallback)
    {
        var text = box.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return box.SelectedItem is System.Windows.Controls.ComboBoxItem item
            ? item.Content as string ?? fallback
            : fallback;
    }
}
