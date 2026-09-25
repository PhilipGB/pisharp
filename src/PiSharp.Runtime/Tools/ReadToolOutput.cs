using System.Buffers.Binary;

namespace PiSharp.Runtime.Tools;

internal sealed record ReadToolOutput(string Text, string? ImageMimeType = null, string? ImageDataBase64 = null,
    int? MaxBase64Bytes = null)
{
    public override string ToString() => Text;
}

internal static class ReadImageDetector
{
    private const int SniffByteCount = 4100;
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    public static async Task<string?> DetectAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[SniffByteCount];
        var length = 0;
        while (length < header.Length)
        {
            var read = await stream.ReadAsync(header.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }
        stream.Position = 0;
        return Detect(header.AsSpan(0, length));
    }

    internal static string? Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff &&
            (bytes.Length < 4 || bytes[3] != 0xf7)) return "image/jpeg";
        if (IsPng(bytes) && !IsAnimatedPng(bytes)) return "image/png";
        if (StartsWithAscii(bytes, 0, "GIF87a") || StartsWithAscii(bytes, 0, "GIF89a")) return "image/gif";
        if (StartsWithAscii(bytes, 0, "RIFF") && StartsWithAscii(bytes, 8, "WEBP")) return "image/webp";
        if (IsBmp(bytes)) return "image/bmp";
        return null;
    }

    private static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(PngSignature) && bytes.Length >= 16 &&
        BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) == 13 && StartsWithAscii(bytes, 12, "IHDR");

    private static bool IsAnimatedPng(ReadOnlySpan<byte> bytes)
    {
        var offset = PngSignature.Length;
        while (offset + 8 <= bytes.Length)
        {
            var chunkLength = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            var chunkTypeOffset = offset + 4;
            if (StartsWithAscii(bytes, chunkTypeOffset, "acTL")) return true;
            if (StartsWithAscii(bytes, chunkTypeOffset, "IDAT")) return false;
            var nextOffset = offset + 8L + chunkLength + 4;
            if (nextOffset <= offset || nextOffset > bytes.Length) return false;
            offset = (int)nextOffset;
        }
        return false;
    }

    private static bool IsBmp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 26 || !StartsWithAscii(bytes, 0, "BM")) return false;
        var declaredFileSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
        if (declaredFileSize != 0 && declaredFileSize < 26) return false;
        if (pixelOffset < 14L + headerSize) return false;
        if (declaredFileSize != 0 && pixelOffset >= declaredFileSize) return false;

        ushort planes;
        ushort bitsPerPixel;
        if (headerSize == 12)
        {
            planes = BinaryPrimitives.ReadUInt16LittleEndian(bytes[22..]);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(bytes[24..]);
        }
        else if (headerSize is >= 40 and <= 124 && bytes.Length >= 30)
        {
            planes = BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]);
            bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]);
        }
        else return false;

        return planes == 1 && bitsPerPixel is 1 or 4 or 8 or 16 or 24 or 32;
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> bytes, int offset, string text)
    {
        if (bytes.Length < offset + text.Length) return false;
        for (var index = 0; index < text.Length; index++)
            if (bytes[offset + index] != text[index]) return false;
        return true;
    }
}
