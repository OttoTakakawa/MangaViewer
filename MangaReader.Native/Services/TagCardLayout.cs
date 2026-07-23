using MangaReader.Native.Models;

namespace MangaReader.Native.Services;

internal readonly record struct TagCardLayoutOptions(
    int RowCount,
    int RowCapacity,
    int MaxTextUnits,
    int ChromeUnits,
    bool AllowSpanningLongTag,
    bool ReserveSummarySpace);

internal static class TagCardLayout
{
    public static IReadOnlyList<TagChip> Build(
        IReadOnlyList<string> tags,
        TagCardLayoutOptions options,
        Func<string, string> colorResolver)
    {
        if (tags.Count == 0)
        {
            return [];
        }

        var rows = new int[options.RowCount];
        var visible = new List<(TagChip Chip, int Row, int Units)>();
        foreach (var tag in tags)
        {
            if (!TryPlace(tag, rows, options, out var row, out var units))
            {
                continue;
            }

            visible.Add((new TagChip { Name = tag, Color = colorResolver(tag) }, row, units));
        }

        var hiddenCount = tags.Count - visible.Count;
        if (hiddenCount > 0 && options.ReserveSummarySpace)
        {
            while (visible.Count > 0 && !CanPlaceSummary(rows, options.RowCapacity))
            {
                RemovePlacement(rows, visible[^1].Row, visible[^1].Units);
                visible.RemoveAt(visible.Count - 1);
                hiddenCount++;
            }
        }

        var result = visible.Select(item => item.Chip).ToList();
        if (hiddenCount > 0)
        {
            result.Add(new TagChip { Name = $"+{hiddenCount}", Color = "#E5E7EB" });
        }
        return result;
    }

    private static bool TryPlace(
        string tag,
        int[] rows,
        TagCardLayoutOptions options,
        out int row,
        out int units)
    {
        row = -1;
        var textUnits = CountTextUnits(tag);
        units = textUnits + options.ChromeUnits;
        if (textUnits > options.MaxTextUnits)
        {
            return false;
        }

        if (units <= options.RowCapacity)
        {
            for (var i = 0; i < rows.Length; i++)
            {
                if (rows[i] + units <= options.RowCapacity)
                {
                    rows[i] += units;
                    row = i;
                    return true;
                }
            }
            return false;
        }

        if (!options.AllowSpanningLongTag || rows.Any(value => value > 0) || rows.Length < 2)
        {
            return false;
        }

        rows[0] = options.RowCapacity;
        rows[1] = Math.Min(options.RowCapacity, units - options.RowCapacity);
        return true;
    }

    private static bool CanPlaceSummary(int[] rows, int rowCapacity)
    {
        const int summaryUnits = 6;
        return rows.Any(rowUnits => rowUnits + summaryUnits <= rowCapacity);
    }

    private static void RemovePlacement(int[] rows, int row, int units)
    {
        if (row < 0)
        {
            Array.Clear(rows);
            return;
        }
        rows[row] = Math.Max(0, rows[row] - units);
    }

    private static int CountTextUnits(string text)
    {
        var units = 0;
        foreach (var character in text)
        {
            units += character <= 0x007F ? 1 : 2;
        }
        return units;
    }
}
