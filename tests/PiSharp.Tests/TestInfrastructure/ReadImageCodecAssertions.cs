using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Tools;
using SkiaSharp;

namespace PiSharp.Tests;

internal static class ReadImageCodecAssertions
{
    public static void Equivalent(PiSharpToolImage image, JsonElement policy, byte[] input)
    {
        var referenceBytes = Convert.FromBase64String(policy.GetProperty("referenceImageBase64").GetString()!);
        var nativeBytes = Convert.FromBase64String(image.DataBase64);
        var reference = Decode(referenceBytes);
        var native = Decode(nativeBytes);
        Assert.Equal((reference.Width, reference.Height), (native.Width, native.Height));
        Assert.Equal(reference.Pixels.Where((_, index) => index % 4 == 3), native.Pixels.Where((_, index) => index % 4 == 3));
        var differences = reference.Pixels.Zip(native.Pixels).Where((_, index) => index % 4 != 3)
            .Select(pair => Math.Abs(pair.First - pair.Second)).ToArray();
        if (policy.GetProperty("kind").GetString() == "jpeg_decoder")
        {
            Assert.True(differences.Max() <= policy.GetProperty("maximumChannelDelta").GetInt32());
            Assert.True(differences.Average() <= policy.GetProperty("maximumMeanAbsoluteDelta").GetDouble());
            if (policy.TryGetProperty("referenceDecodedInputRgbaBase64", out var original))
            {
                var referenceInput = Convert.FromBase64String(original.GetString()!);
                var nativeInput = Decode(input).Pixels;
                Assert.Equal(referenceInput.Length, nativeInput.Length);
                Assert.True(referenceInput.Zip(nativeInput).Max(pair => Math.Abs(pair.First - pair.Second)) <= 2);
            }
            return;
        }
        Assert.Equal("jpeg_encoder", policy.GetProperty("kind").GetString());
        Assert.Equal("image/jpeg", image.MimeType);
        Assert.Equal(Segments(referenceBytes, 0xdb).Select(Convert.ToHexString), Segments(nativeBytes, 0xdb).Select(Convert.ToHexString));
        foreach (var encoded in new[] { referenceBytes, nativeBytes })
        {
            var frame = Assert.Single(Segments(encoded, 0xc0));
            Assert.Equal(new byte[] { 0x11, 0x11, 0x11 }, new[] { frame[7], frame[10], frame[13] });
        }
        var originalPixels = Convert.FromBase64String(policy.GetProperty("referenceRgbaBase64").GetString()!);
        Assert.Equal(policy.GetProperty("referenceRgbaSha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(originalPixels)));
        using (var codec = SKCodec.Create(new MemoryStream(input)))
        using (var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul)))
        {
            Assert.Equal(SKCodecResult.Success, codec.GetPixels(bitmap.Info, bitmap.GetPixels()));
            using var resized = ReadImageResampler.Resize(bitmap, native.Width, native.Height, CancellationToken.None);
            Assert.Equal(originalPixels, resized.GetPixelSpan().ToArray());
        }
        var referenceMse = Mse(reference.Pixels, originalPixels);
        var nativeMse = Mse(native.Pixels, originalPixels);
        var pairedMse = differences.Average(value => (double)value * value);
        Assert.True(nativeMse <= referenceMse * policy.GetProperty("maximumNativeDistortionRatio").GetDouble());
        Assert.True(pairedMse <= referenceMse * policy.GetProperty("maximumPairedDistortionRatio").GetDouble());
    }

    private static double Mse(byte[] pixels, byte[] reference)
    {
        Assert.Equal(reference.Length, pixels.Length);
        return pixels.Zip(reference).Where((_, index) => index % 4 != 3)
            .Average(pair => (double)(pair.First - pair.Second) * (pair.First - pair.Second));
    }

    private static (int Width, int Height, byte[] Pixels) Decode(byte[] encoded)
    {
        using var codec = SKCodec.Create(new MemoryStream(encoded));
        Assert.NotNull(codec);
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = new byte[info.BytesSize];
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, pixels));
        return (info.Width, info.Height, pixels);
    }

    private static IEnumerable<byte[]> Segments(byte[] encoded, byte marker)
    {
        for (var offset = 2; offset + 4 <= encoded.Length;)
        {
            var current = encoded[offset + 1];
            var length = (encoded[offset + 2] << 8) | encoded[offset + 3];
            if (current == marker) yield return encoded.AsSpan(offset + 4, length - 2).ToArray();
            if (current == 0xda) yield break;
            offset += 2 + length;
        }
    }
}
