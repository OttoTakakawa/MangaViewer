using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace MangaReader.Native.Services;

internal enum ImageContentKind
{
    LineArt,
    Photo
}

internal sealed record AdaptiveResampleResult(
    BitmapSource Bitmap,
    ImageContentKind ContentKind,
    string Sampler,
    double SharpenAmount,
    int SharpenThreshold,
    long ElapsedMs);

internal static class AdaptiveImageResampler
{
    internal const string AlgorithmVersion = "adaptive-cubic-v1";

    public static AdaptiveResampleResult Resize(
        BitmapSource source,
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        targetWidth = Math.Max(1, targetWidth);
        targetHeight = Math.Max(1, targetHeight);

        var converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        if (converted.CanFreeze && !converted.IsFrozen)
        {
            converted.Freeze();
        }

        var sourceStride = converted.PixelWidth * 4;
        var sourcePixels = new byte[sourceStride * converted.PixelHeight];
        converted.CopyPixels(sourcePixels, sourceStride, 0);
        cancellationToken.ThrowIfCancellationRequested();

        var contentKind = Classify(sourcePixels, converted.PixelWidth, converted.PixelHeight, sourceStride);
        var sampler = contentKind == ImageContentKind.LineArt ? "Catmull-Rom" : "Mitchell";
        var sharpenAmount = contentKind == ImageContentKind.LineArt ? 0.18 : 0.02;
        var sharpenThreshold = contentKind == ImageContentKind.LineArt ? 6 : 12;

        using var sourceBitmap = new SKBitmap(
            new SKImageInfo(converted.PixelWidth, converted.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        Marshal.Copy(sourcePixels, 0, sourceBitmap.GetPixels(), sourcePixels.Length);
        using var targetBitmap = new SKBitmap(
            new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(targetBitmap))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            var cubic = contentKind == ImageContentKind.LineArt
                ? SKCubicResampler.CatmullRom
                : SKCubicResampler.Mitchell;
            canvas.DrawBitmap(
                sourceBitmap,
                new SKRect(0, 0, targetWidth, targetHeight),
                new SKSamplingOptions(cubic),
                paint);
            canvas.Flush();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var targetStride = targetWidth * 4;
        var targetPixels = new byte[targetStride * targetHeight];
        Marshal.Copy(targetBitmap.GetPixels(), targetPixels, 0, targetPixels.Length);
        ApplyLuminanceUnsharpMask(
            targetPixels,
            targetWidth,
            targetHeight,
            targetStride,
            sharpenAmount,
            sharpenThreshold,
            cancellationToken);

        var bitmap = BitmapSource.Create(
            targetWidth,
            targetHeight,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            targetPixels,
            targetStride);
        bitmap.Freeze();
        stopwatch.Stop();
        return new AdaptiveResampleResult(
            bitmap,
            contentKind,
            sampler,
            sharpenAmount,
            sharpenThreshold,
            stopwatch.ElapsedMilliseconds);
    }

    private static ImageContentKind Classify(byte[] pixels, int width, int height, int stride)
    {
        var step = Math.Max(1, (int)Math.Sqrt((long)width * height / 65536d));
        long samples = 0;
        long graySamples = 0;
        long edges = 0;
        long edgeTests = 0;
        double saturationTotal = 0;

        for (var y = 0; y < height; y += step)
        {
            var row = y * stride;
            for (var x = 0; x < width; x += step)
            {
                var index = row + x * 4;
                var b = pixels[index];
                var g = pixels[index + 1];
                var r = pixels[index + 2];
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                var chroma = max - min;
                saturationTotal += max == 0 ? 0 : chroma / (double)max;
                if (chroma <= 18)
                {
                    graySamples++;
                }

                var luma = Luma(r, g, b);
                if (x + step < width)
                {
                    var next = index + step * 4;
                    if (Math.Abs(luma - Luma(pixels[next + 2], pixels[next + 1], pixels[next])) >= 30)
                    {
                        edges++;
                    }
                    edgeTests++;
                }
                if (y + step < height)
                {
                    var next = index + step * stride;
                    if (Math.Abs(luma - Luma(pixels[next + 2], pixels[next + 1], pixels[next])) >= 30)
                    {
                        edges++;
                    }
                    edgeTests++;
                }
                samples++;
            }
        }

        var grayRatio = samples == 0 ? 0 : graySamples / (double)samples;
        var averageSaturation = samples == 0 ? 0 : saturationTotal / samples;
        var edgeDensity = edgeTests == 0 ? 0 : edges / (double)edgeTests;
        return grayRatio >= 0.68 && averageSaturation <= 0.16 && edgeDensity >= 0.045
            ? ImageContentKind.LineArt
            : ImageContentKind.Photo;
    }

    private static void ApplyLuminanceUnsharpMask(
        byte[] pixels,
        int width,
        int height,
        int stride,
        double amount,
        int threshold,
        CancellationToken cancellationToken)
    {
        if (width < 3 || height < 3 || amount <= 0)
        {
            return;
        }

        var original = (byte[])pixels.Clone();
        for (var y = 1; y < height - 1; y++)
        {
            if ((y & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var row = y * stride;
            for (var x = 1; x < width - 1; x++)
            {
                var index = row + x * 4;
                var center = Luma(original[index + 2], original[index + 1], original[index]);
                if (center <= 12 || center >= 243)
                {
                    continue;
                }

                var blur = (
                    LumaAt(original, index - 4)
                    + LumaAt(original, index + 4)
                    + LumaAt(original, index - stride)
                    + LumaAt(original, index + stride)) / 4;
                var detail = center - blur;
                if (Math.Abs(detail) < threshold || Math.Abs(detail) > 96)
                {
                    continue;
                }

                var adjustment = Math.Clamp(detail * amount, -10, 10);
                pixels[index] = ClampToByte(original[index] + adjustment);
                pixels[index + 1] = ClampToByte(original[index + 1] + adjustment);
                pixels[index + 2] = ClampToByte(original[index + 2] + adjustment);
            }
        }
    }

    private static int LumaAt(byte[] pixels, int index) =>
        Luma(pixels[index + 2], pixels[index + 1], pixels[index]);

    private static int Luma(byte r, byte g, byte b) => (77 * r + 150 * g + 29 * b) >> 8;

    private static byte ClampToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
