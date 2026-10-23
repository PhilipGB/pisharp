using PiSharp.Runtime.Tools;
using SkiaSharp;

namespace PiSharp.Tests;

public sealed class ReadImageJpegEncodingTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(17, 11, 20)]
    [InlineData(64, 48, 40)]
    [InlineData(127, 65, 80)]
    [InlineData(9, 9, 100)]
    public void StandardHuffmanCodingPreservesEveryDecodedPixel(int width, int height, int quality)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var pixels = bitmap.GetPixelSpan();
        for (var index = 0; index < pixels.Length; index++) pixels[index] = (byte)((index * 37 + index / 7 * 13) % 256);
        using var pixmap = bitmap.PeekPixels();
        using var encoded = pixmap.Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.Ignore));
        var original = encoded.ToArray();

        var standardized = ReadImageJpegEncoding.Standardize(original, CancellationToken.None);

        Assert.Equal(Decode(original), Decode(standardized));
        Assert.Equal(Segments(original, 0xdb), Segments(standardized, 0xdb));
        Assert.Equal(Segments(original, 0xc0), Segments(standardized, 0xc0));
        Assert.Equal(new[] { 29, 179, 29, 179 }, Segments(standardized, 0xc4).Select(value => value.Length));
        var base64Bytes = (standardized.Length + 2) / 3 * 4;
        Assert.Null(ReadImageJpegEncoding.StandardizeWithinBudget(original, base64Bytes, CancellationToken.None));
        Assert.Equal(standardized, ReadImageJpegEncoding.StandardizeWithinBudget(original, base64Bytes + 1, CancellationToken.None));
    }

    [Fact]
    public void CancellationIsObservedBeforeEntropyWork()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1));
        using var pixels = bitmap.PeekPixels();
        using var encoded = pixels.Encode(new SKJpegEncoderOptions(80, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.Ignore));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => ReadImageJpegEncoding.Standardize(encoded.ToArray(), cancellation.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/9g=")]
    [InlineData("/9j/xAADAA==")]
    public void InvalidHeadersCannotBecomeSuccessfulJpegPayloads(string input) =>
        Assert.Throws<InvalidDataException>(() => ReadImageJpegEncoding.Standardize(Convert.FromBase64String(input), CancellationToken.None));

    private static byte[] Decode(byte[] encoded)
    {
        using var codec = SKCodec.Create(new MemoryStream(encoded));
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[info.BytesSize];
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, pixels));
        return pixels;
    }

    private static byte[][] Segments(byte[] encoded, byte wanted)
    {
        var segments = new List<byte[]>();
        for (var offset = 2; offset + 4 <= encoded.Length;)
        {
            var marker = encoded[offset + 1];
            var length = (encoded[offset + 2] << 8) | encoded[offset + 3];
            if (marker == wanted) segments.Add(encoded.AsSpan(offset + 4, length - 2).ToArray());
            if (marker == 0xda) break;
            offset += 2 + length;
        }
        return segments.ToArray();
    }
}
