using System.Globalization;
using PiSharp.Runtime.Providers;
using SkiaSharp;

namespace PiSharp.Runtime.Tools;

internal static class ReadImageProcessor
{
    private const int MaxDimension = 2000;
    internal const int MaxBase64Bytes = 4_718_592;
    private static readonly SKSamplingOptions s_sampling = new(new SKCubicResampler(0, 0.5f));

    public static ReadToolOutput Process(byte[] input, string sourceMimeType,
        CancellationToken cancellationToken = default, ModelImageResizeOptions? resizeOptions = null)
    {
        var normalizedMimeType = NormalizeMimeType(sourceMimeType);
        var convertedFrom = normalizedMimeType is null ? sourceMimeType : null;
        var maxWidth = resizeOptions?.MaxWidth ?? MaxDimension;
        var maxHeight = resizeOptions?.MaxHeight ?? MaxDimension;
        var maxBase64Bytes = resizeOptions?.MaxBytes ?? MaxBase64Bytes;
        var jpegQualities = new[] { resizeOptions?.JpegQuality ?? 80, 85, 70, 55, 40 }.Distinct().ToArray();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var encodedStream = new MemoryStream(input, writable: false);
            using var codec = SKCodec.Create(encodedStream);
            if (codec is null) return Omitted(sourceMimeType, convertedFrom is not null);
            var encodedInfo = codec.Info;
            if (encodedInfo.Width <= 0 || encodedInfo.Height <= 0)
                return Omitted(sourceMimeType, convertedFrom is not null);
            if ((long)encodedInfo.Width * encodedInfo.Height > 32_000_000)
                return Omitted(sourceMimeType, conversionFailed: false, resizeFailed: true);
            using var original = SKBitmap.Decode(codec);
            cancellationToken.ThrowIfCancellationRequested();
            if (original is null || original.Width <= 0 || original.Height <= 0)
                return Omitted(sourceMimeType, convertedFrom is not null);
            if ((long)original.Width * original.Height > 32_000_000)
                return Omitted(sourceMimeType, conversionFailed: false, resizeFailed: true);

            var imageMimeType = normalizedMimeType ?? "image/png";
            var imageBytes = normalizedMimeType is null ? Encode(original, SKEncodedImageFormat.Png, 100) : input;
            if (imageBytes is null) return Omitted(sourceMimeType, convertedFrom is not null);

            var originalWidth = original.Width;
            var originalHeight = original.Height;
            var payloadLength = Base64Length(imageBytes.Length);
            var wasResized = originalWidth > maxWidth || originalHeight > maxHeight || payloadLength >= maxBase64Bytes;
            if (!wasResized)
                return CreateOutput(imageBytes, imageMimeType, originalWidth, originalHeight, originalWidth, originalHeight,
                    wasResized: false, convertedFrom, maxBase64Bytes);

            var (width, height) = Fit(originalWidth, originalHeight, maxWidth, maxHeight);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var resized = width == originalWidth && height == originalHeight
                    ? null : original.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), s_sampling);
                var target = resized ?? (width == originalWidth && height == originalHeight ? original : null);
                if (target is not null)
                {
                    var png = Encode(target, SKEncodedImageFormat.Png, 100);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (png is not null && Base64Length(png.Length) < maxBase64Bytes)
                        return CreateOutput(png, "image/png", originalWidth, originalHeight, width, height, true,
                            convertedFrom, maxBase64Bytes);

                    foreach (var quality in jpegQualities)
                    {
                        var jpeg = Encode(target, SKEncodedImageFormat.Jpeg, quality);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (jpeg is not null && Base64Length(jpeg.Length) < maxBase64Bytes)
                            return CreateOutput(jpeg, "image/jpeg", originalWidth, originalHeight, width, height, true,
                                convertedFrom, maxBase64Bytes);
                    }
                }

                if (width == 1 && height == 1) break;
                var nextWidth = width == 1 ? 1 : Math.Max(1, (int)Math.Floor(width * 0.75));
                var nextHeight = height == 1 ? 1 : Math.Max(1, (int)Math.Floor(height * 0.75));
                if (nextWidth == width && nextHeight == height) break;
                width = nextWidth;
                height = nextHeight;
            }

            return Omitted(sourceMimeType, conversionFailed: false, resizeFailed: true);
        }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        {
            return Omitted(sourceMimeType, convertedFrom is not null);
        }
    }

    private static ReadToolOutput CreateOutput(byte[] bytes, string mimeType, int originalWidth, int originalHeight,
        int width, int height, bool wasResized, string? convertedFrom, int maxBase64Bytes)
    {
        var hints = new List<string>(2);
        if (convertedFrom is not null && convertedFrom != mimeType)
            hints.Add($"[Image converted from {convertedFrom} to {mimeType}.]");
        if (wasResized)
        {
            var scale = (double)originalWidth / width;
            hints.Add($"[Image: original {originalWidth}x{originalHeight}, displayed at {width}x{height}. Multiply coordinates by {scale.ToString("F2", CultureInfo.InvariantCulture)} to map to original image.]");
        }
        var text = $"Read image file [{mimeType}]" + (hints.Count == 0 ? "" : "\n" + string.Join("\n", hints));
        return new ReadToolOutput(text, mimeType, Convert.ToBase64String(bytes), maxBase64Bytes);
    }

    private static ReadToolOutput Omitted(string mimeType, bool conversionFailed, bool resizeFailed = false)
    {
        var reason = conversionFailed
            ? "[Image omitted: could not be converted to a supported inline image format.]"
            : resizeFailed || mimeType is "image/png" or "image/jpeg" or "image/gif" or "image/webp"
                ? "[Image omitted: could not be resized below the inline image size limit.]"
                : "[Image omitted: could not be converted to a supported inline image format.]";
        return new ReadToolOutput($"Read image file [{mimeType}]\n{reason}");
    }

    private static byte[]? Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data?.ToArray();
    }

    private static long Base64Length(int byteLength) => ((long)byteLength + 2) / 3 * 4;

    private static (int Width, int Height) Fit(int width, int height, int maxWidth, int maxHeight)
    {
        if (width <= maxWidth && height <= maxHeight) return (width, height);
        if (width > maxWidth)
        {
            height = Math.Max(1, (int)Math.Floor(height * (maxWidth / (double)width) + 0.5));
            width = maxWidth;
        }
        if (height > maxHeight)
        {
            width = Math.Max(1, (int)Math.Floor(width * (maxHeight / (double)height) + 0.5));
            height = maxHeight;
        }
        return (width, height);
    }

    private static string? NormalizeMimeType(string mimeType) => mimeType switch
    {
        "image/png" => "image/png",
        "image/jpeg" or "image/jpg" => "image/jpeg",
        "image/gif" => "image/gif",
        "image/webp" => "image/webp",
        _ => null
    };
}
