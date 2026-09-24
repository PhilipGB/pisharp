using System.Buffers.Binary;
using PiSharp.Runtime.Tools;
using SkiaSharp;

namespace PiSharp.Tests;

public sealed class ReadImageProcessorTests
{
    [Fact]
    public void SmallPngKeepsItsOriginalEncoding()
    {
        var bytes = CreatePng(3, 2);

        var output = ReadImageProcessor.Process(bytes, "image/png");

        Assert.Equal("Read image file [image/png]", output.Text);
        Assert.Equal("image/png", output.ImageMimeType);
        Assert.Equal(bytes, Convert.FromBase64String(output.ImageDataBase64!));
    }

    [Fact]
    public void LargeDimensionsResizeWithCoordinateHint()
    {
        var bytes = CreatePng(2400, 20);

        var output = ReadImageProcessor.Process(bytes, "image/png");
        using var resized = SKBitmap.Decode(Convert.FromBase64String(output.ImageDataBase64!));

        Assert.Equal((2000, 17), (resized!.Width, resized.Height));
        Assert.Contains("[Image: original 2400x20, displayed at 2000x17. Multiply coordinates by 1.20 to map to original image.]", output.Text);
        Assert.True(output.ImageDataBase64!.Length < ReadImageProcessor.MaxBase64Bytes);
    }

    [Fact]
    public void BmpIsNormalizedToPngBeforeProviderUse()
    {
        var output = ReadImageProcessor.Process(CreateBmp(), "image/bmp");
        var png = Convert.FromBase64String(output.ImageDataBase64!);
        using var decoded = SKBitmap.Decode(png);

        Assert.Equal("image/png", output.ImageMimeType);
        Assert.Contains("[Image converted from image/bmp to image/png.]", output.Text);
        Assert.Equal((1, 1), (decoded!.Width, decoded.Height));
    }

    [Fact]
    public void CorruptDetectedImageProducesOmissionTextInsteadOfSendingBadBytes()
    {
        var output = ReadImageProcessor.Process([0x89, 0x50, 0x4e, 0x47], "image/png");

        Assert.Null(output.ImageDataBase64);
        Assert.Contains("[Image omitted: could not be resized below the inline image size limit.]", output.Text);
    }

    [Fact]
    public void CancellationIsObservedBeforeImageDecoding()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => ReadImageProcessor.Process([0x89, 0x50], "image/png", cancelled.Token));
    }

    [Fact]
    public void ExcessiveDimensionsAreRejectedBeforePixelDecode()
    {
        var bytes = CreateBmp();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 10_000);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 10_000);

        var output = ReadImageProcessor.Process(bytes, "image/bmp");

        Assert.Null(output.ImageDataBase64);
        Assert.Contains("[Image omitted: could not be resized below the inline image size limit.]", output.Text);
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] CreateBmp()
    {
        var bytes = new byte[58];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 24);
        return bytes;
    }
}
