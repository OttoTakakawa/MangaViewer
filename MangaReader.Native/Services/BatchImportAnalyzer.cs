using MangaReader.Native.Models;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MangaReader.Native.Services;

public sealed class BatchImportAnalyzer
{
    private const int AnalysisDecodePixelWidth = 160;
    private const double LandscapeRatioThreshold = 1.2;
    private const long HighQualityBytes = 2L * 1024 * 1024;
    private const long UltraQualityBytes = 5L * 1024 * 1024;
    private readonly NaturalPathComparer _pathComparer = new();

    public List<BatchImportCandidate> AnalyzeAuthorFolder(string authorFolder)
    {
        if (!Directory.Exists(authorFolder))
        {
            return [];
        }

        return FindBookFolders(authorFolder)
            .Select(CreateCandidate)
            .Where(candidate => candidate.PageCount > 0)
            .OrderBy(candidate => candidate.FolderPath, _pathComparer)
            .ToList();
    }

    public BatchImportCandidate? AnalyzeBookFolder(string bookFolder)
    {
        if (!Directory.Exists(bookFolder) || !HasDirectImages(bookFolder))
        {
            return null;
        }

        var candidate = CreateCandidate(bookFolder);
        return candidate.PageCount > 0 ? candidate : null;
    }

    private static IEnumerable<string> FindBookFolders(string authorFolder)
    {
        var imageFolders = Directory.EnumerateDirectories(authorFolder, "*", SearchOption.AllDirectories)
            .Where(HasDirectImages)
            .ToList();

        if (imageFolders.Count == 0 && HasDirectImages(authorFolder))
        {
            imageFolders.Add(authorFolder);
        }

        return imageFolders;
    }

    private static bool HasDirectImages(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder).Any(ImageLoader.IsSupportedImage);
        }
        catch
        {
            return false;
        }
    }

    private BatchImportCandidate CreateCandidate(string folder)
    {
        var pages = Directory.EnumerateFiles(folder)
            .Where(ImageLoader.IsSupportedImage)
            .OrderBy(path => path, _pathComparer)
            .ToList();

        var tags = InferTags(pages);
        var folderName = Path.GetFileName(folder);
        return new BatchImportCandidate
        {
            FolderPath = folder,
            FolderName = folderName,
            Title = folderName,
            PageCount = pages.Count,
            Pages = pages,
            Tags = string.Join(", ", tags)
        };
    }

    private static List<string> InferTags(IReadOnlyList<string> pages)
    {
        var tags = new List<string>();
        if (pages.Count == 0)
        {
            return tags;
        }

        tags.Add(pages.Count >= 16 ? "单行本" : "杂图合集");

        var wideCount = 0;
        var colorScore = 0;
        var sampleCount = 0;
        foreach (var page in PickSamplePages(pages))
        {
            var sample = AnalyzeImage(page);
            if (sample is null)
            {
                continue;
            }

            sampleCount++;
            if (sample.Value.IsWide)
            {
                wideCount++;
            }
            if (sample.Value.IsColor)
            {
                colorScore++;
            }
        }

        if (sampleCount > 0)
        {
            tags.Add(colorScore >= Math.Max(1, sampleCount / 2) ? "全彩" : "黑白");
            if (wideCount > sampleCount / 2)
            {
                tags[0] = "CG";
            }
        }

        var qualityTag = InferQualityTag(pages);
        if (!string.IsNullOrWhiteSpace(qualityTag))
        {
            tags.Add(qualityTag);
        }

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string InferQualityTag(IReadOnlyList<string> pages)
    {
        var largestSample = PickRandomQualitySamples(pages)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .Select(file => file.Length)
            .DefaultIfEmpty(0)
            .Max();

        return largestSample switch
        {
            >= UltraQualityBytes => "超清",
            >= HighQualityBytes => "高清",
            _ => ""
        };
    }

    private static IEnumerable<string> PickRandomQualitySamples(IReadOnlyList<string> pages)
    {
        if (pages.Count == 0)
        {
            yield break;
        }

        var sampleCount = Math.Min(3, pages.Count);
        var picked = new HashSet<int>();
        while (picked.Count < sampleCount)
        {
            var index = Random.Shared.Next(pages.Count);
            if (picked.Add(index))
            {
                yield return pages[index];
            }
        }
    }

    private static IEnumerable<string> PickSamplePages(IReadOnlyList<string> pages)
    {
        var picked = new HashSet<int>();
        foreach (var index in PickSampleIndices(pages.Count))
        {
            if (picked.Add(index))
            {
                yield return pages[index];
            }
        }
    }

    private static IEnumerable<int> PickSampleIndices(int pageCount)
    {
        if (pageCount <= 0)
        {
            yield break;
        }

        if (pageCount < 20)
        {
            yield return 0;
            yield return pageCount / 2;
            yield return pageCount - 1;
            yield break;
        }

        yield return 9;
        yield return 14;
        yield return 19;
    }

    private static (bool IsColor, bool IsWide)? AnalyzeImage(string path)
    {
        try
        {
            var image = ImageLoader.LoadBitmap(path, AnalysisDecodePixelWidth);
            var isWide = image.PixelWidth > image.PixelHeight * LandscapeRatioThreshold;
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            var colorful = 0;
            var total = 0;
            for (var y = 0; y < converted.PixelHeight; y += 3)
            {
                for (var x = 0; x < converted.PixelWidth; x += 3)
                {
                    var index = y * stride + x * 4;
                    var b = pixels[index];
                    var g = pixels[index + 1];
                    var r = pixels[index + 2];
                    var chroma = Math.Abs(r - g) + Math.Abs(g - b) + Math.Abs(r - b);
                    if (chroma > 38)
                    {
                        colorful++;
                    }
                    total++;
                }
            }

            return (total > 0 && colorful / (double)total > 0.08, isWide);
        }
        catch
        {
            return null;
        }
    }
}
