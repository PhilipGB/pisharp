using System.Buffers.Binary;
using SkiaSharp;

namespace PiSharp.Runtime.Tools;

internal static class ReadImageOrientation
{
    public static SKBitmap? Apply(SKBitmap original, ReadOnlySpan<byte> encoded, CancellationToken cancellationToken)
    {
        var orientation = Orientation(encoded);
        if (orientation == 1) return null;
        var swap = orientation >= 5;
        var result = new SKBitmap(new SKImageInfo(swap ? original.Height : original.Width,
            swap ? original.Width : original.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        try
        {
            var source = original.GetPixelSpan();
            var target = result.GetPixelSpan();
            for (var y = 0; y < original.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < original.Width; x++)
                {
                    if ((x & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var (dx, dy) = orientation switch
                    {
                        2 => (original.Width - 1 - x, y),
                        3 => (original.Width - 1 - x, original.Height - 1 - y),
                        4 => (x, original.Height - 1 - y),
                        5 => (y, x),
                        6 => (original.Height - 1 - y, x),
                        7 => (original.Height - 1 - y, original.Width - 1 - x),
                        _ => (y, original.Width - 1 - x)
                    };
                    source.Slice(y * original.RowBytes + x * 4, 4).CopyTo(target.Slice(dy * result.RowBytes + dx * 4, 4));
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static int Orientation(ReadOnlySpan<byte> bytes)
    {
        var start = -1;
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            var offset = 2;
            while (offset + 1 < bytes.Length)
            {
                if (bytes[offset] != 0xff) break;
                var marker = bytes[offset + 1];
                if (marker == 0xff) { offset++; continue; }
                if (offset + 4 > bytes.Length) break;
                if (marker == 0xe1 && HasExif(bytes, offset + 4)) { start = offset + 10; break; }
                offset += 2 + BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 2)..]);
            }
        }
        else if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            long offset = 12;
            while (offset + 8 <= bytes.Length)
            {
                var chunk = bytes[(int)offset..];
                var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                var dataStart = offset + 8;
                if (chunk[..4].SequenceEqual("EXIF"u8))
                {
                    if (dataStart + size > bytes.Length) return 1;
                    start = (int)dataStart + (size >= 6 && HasExif(bytes, (int)dataStart) ? 6 : 0);
                    break;
                }
                offset = dataStart + size + size % 2;
            }
        }
        if (start < 0 || (long)start + 8 > bytes.Length) return 1;
        var tiff = bytes[start..];
        var little = tiff[..2].SequenceEqual("II"u8);
        var directory = Read32(tiff[4..], little);
        if ((long)directory + 2 > tiff.Length) return 1;
        var entries = Read16(tiff[(int)directory..], little);
        for (var index = 0; index < entries; index++)
        {
            var entry = (long)directory + 2 + index * 12;
            if (entry + 12 > tiff.Length) return 1;
            if (Read16(tiff[(int)entry..], little) != 0x112) continue;
            var value = Read16(tiff[((int)entry + 8)..], little);
            return value is >= 1 and <= 8 ? value : 1;
        }
        return 1;
    }

    private static bool HasExif(ReadOnlySpan<byte> bytes, int offset) =>
        offset + 6 <= bytes.Length && bytes.Slice(offset, 6).SequenceEqual("Exif\0\0"u8);
    private static ushort Read16(ReadOnlySpan<byte> bytes, bool little) => little
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
    private static uint Read32(ReadOnlySpan<byte> bytes, bool little) => little
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
}
