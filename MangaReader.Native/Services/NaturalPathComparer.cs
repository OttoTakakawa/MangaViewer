using System.Text.RegularExpressions;

namespace MangaReader.Native.Services;

public sealed partial class NaturalPathComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        if (x is null) return y is null ? 0 : -1;
        if (y is null) return 1;

        var ax = Tokenize(Path.GetFileNameWithoutExtension(x));
        var ay = Tokenize(Path.GetFileNameWithoutExtension(y));
        var count = Math.Min(ax.Count, ay.Count);

        for (var i = 0; i < count; i++)
        {
            var left = ax[i];
            var right = ay[i];
            var leftIsNumber = long.TryParse(left, out var leftNumber);
            var rightIsNumber = long.TryParse(right, out var rightNumber);

            var result = leftIsNumber && rightIsNumber
                ? leftNumber.CompareTo(rightNumber)
                : string.Compare(left, right, StringComparison.CurrentCultureIgnoreCase);

            if (result != 0) return result;
        }

        return ax.Count.CompareTo(ay.Count);
    }

    private static List<string> Tokenize(string value)
    {
        return TokenRegex().Matches(value).Select(match => match.Value).ToList();
    }

    [GeneratedRegex(@"\d+|\D+")]
    private static partial Regex TokenRegex();
}
