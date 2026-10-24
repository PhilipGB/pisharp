using System.Buffers.Binary;

namespace PiSharp.Runtime.Tools;

/// <summary>Uses the pinned encoder's standard Huffman coding without changing Skia's quantized image coefficients.</summary>
internal static class ReadImageJpegEncoding
{
    private static readonly byte[][] s_standard =
    [
        Convert.FromBase64String("AAABBQEBAQEBAQAAAAAAAAAAAQIDBAUGBwgJCgs="),
        Convert.FromBase64String("EAACAQMDAgQDBQUEBAAAAX0BAgMABBEFEiExQQYTUWEHInEUMoGRoQgjQrHBFVLR8CQzYnKCCQoWFxgZGiUmJygpKjQ1Njc4OTpDREVGR0hJSlNUVVZXWFlaY2RlZmdoaWpzdHV2d3h5eoOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4eLj5OXm5+jp6vHy8/T19vf4+fo="),
        Convert.FromBase64String("AQADAQEBAQEBAQEBAAAAAAAAAQIDBAUGBwgJCgs="),
        Convert.FromBase64String("EQACAQIEBAMEBwUEBAABAncAAQIDEQQFITEGEkFRB2FxEyIygQgUQpGhscEJIzNS8BVictEKFiQ04SXxFxgZGiYnKCkqNTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqCg4SFhoeIiYqSk5SVlpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY2dri4+Tl5ufo6ery8/T19vf4+fo=")
    ];
    private static readonly IReadOnlyDictionary<int, HuffmanTable> s_tables = s_standard.ToDictionary(
        data => (int)data[0], data => new HuffmanTable(data.AsSpan(1)));

    public static byte[] Standardize(ReadOnlySpan<byte> encoded, CancellationToken cancellationToken) =>
        Transcode(encoded, int.MaxValue, cancellationToken) ?? throw new InvalidDataException("JPEG output exceeds its size limit.");

    public static byte[]? StandardizeWithinBudget(ReadOnlySpan<byte> encoded, int maximumBase64Bytes,
        CancellationToken cancellationToken) => maximumBase64Bytes <= 0 ? null :
        Transcode(encoded, (maximumBase64Bytes - 1) / 4 * 3, cancellationToken);

    private static byte[]? Transcode(ReadOnlySpan<byte> encoded, int maximumBytes, CancellationToken cancellationToken)
    {
        if (encoded.Length < 2 || encoded[0] != 0xff || encoded[1] != 0xd8) throw new InvalidDataException("Invalid JPEG header.");
        using var output = new MemoryStream((int)Math.Min(int.MaxValue, Math.Min((long)encoded.Length + 512, (long)maximumBytes + 1)));
        output.Write(encoded[..2]);
        var tables = new Dictionary<int, HuffmanTable>();
        var offset = 2;
        var width = 0;
        var height = 0;
        while (offset + 4 <= encoded.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (encoded[offset] != 0xff) throw new InvalidDataException("Invalid JPEG marker.");
            var marker = encoded[offset + 1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(encoded[(offset + 2)..]);
            if (length < 2 || (long)offset + 2 + length > encoded.Length) throw new InvalidDataException("Incomplete JPEG segment.");
            var data = encoded.Slice(offset + 4, length - 2);
            if (marker == 0xc4)
            {
                for (var index = 0; index < data.Length;)
                {
                    if (index + 17 > data.Length) throw new InvalidDataException("Incomplete JPEG Huffman table.");
                    var count = 0;
                    for (var value = 1; value <= 16; value++) count += data[index + value];
                    if (index + 17 + count > data.Length) throw new InvalidDataException("Incomplete JPEG Huffman symbols.");
                    tables[data[index]] = new HuffmanTable(data.Slice(index + 1, 16 + count));
                    index += 17 + count;
                }
            }
            else if (marker == 0xda)
            {
                if (width == 0 || height == 0 || data.Length != 10 || data[0] != 3 ||
                    data[7] != 0 || data[8] != 63 || data[9] != 0)
                    throw new InvalidDataException("Unsupported JPEG scan.");
                foreach (var table in s_standard)
                {
                    output.WriteByte(0xff); output.WriteByte(0xc4);
                    var tableLength = table.Length + 2;
                    output.WriteByte((byte)(tableLength >> 8)); output.WriteByte((byte)tableLength);
                    output.Write(table);
                }
                output.Write(encoded.Slice(offset, 2 + length));
                if (output.Length > maximumBytes) return null;
                var reader = new EntropyReader(encoded[(offset + 2 + length)..]);
                var writer = new EntropyWriter(output);
                for (var y = 0; y < height; y += 8)
                    for (var x = 0; x < width; x += 8)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        for (var component = 0; component < 3; component++)
                        {
                            var selector = data[2 + component * 2];
                            var dc = selector >> 4;
                            var ac = 0x10 | (selector & 15);
                            TranscodeBlock(ref reader, writer, tables[dc], tables[ac], s_tables[dc], s_tables[ac]);
                        }
                        if (output.Length > maximumBytes) return null;
                    }
                writer.Finish();
                output.WriteByte(0xff); output.WriteByte(0xd9);
                return output.Length <= maximumBytes ? output.ToArray() : null;
            }
            else
            {
                if (marker == 0xc0)
                {
                    if (data.Length != 15 || data[0] != 8 || data[5] != 3 ||
                        data[7] != 0x11 || data[10] != 0x11 || data[13] != 0x11)
                        throw new InvalidDataException("Unsupported JPEG frame.");
                    height = BinaryPrimitives.ReadUInt16BigEndian(data[1..]);
                    width = BinaryPrimitives.ReadUInt16BigEndian(data[3..]);
                }
                if (marker is 0xc2 or 0xdd) throw new InvalidDataException("Unsupported JPEG coding mode.");
                output.Write(encoded.Slice(offset, 2 + length));
            }
            offset += 2 + length;
            if (output.Length > maximumBytes) return null;
        }
        throw new InvalidDataException("Missing JPEG scan.");
    }

    private static void TranscodeBlock(ref EntropyReader reader, EntropyWriter writer, HuffmanTable dc, HuffmanTable ac,
        HuffmanTable standardDc, HuffmanTable standardAc)
    {
        var category = dc.Read(ref reader);
        if (category > 11) throw new InvalidDataException("Invalid JPEG DC category.");
        standardDc.Write(writer, category);
        writer.Write(reader.Read(category), category);
        for (var position = 1; position < 64;)
        {
            var symbol = ac.Read(ref reader);
            standardAc.Write(writer, symbol);
            if (symbol == 0) break;
            if (symbol == 0xf0) { position += 16; continue; }
            var bits = symbol & 15;
            position += symbol >> 4;
            if (bits == 0 || bits > 10 || position >= 64) throw new InvalidDataException("Invalid JPEG AC category.");
            writer.Write(reader.Read(bits), bits);
            position++;
        }
    }

    private sealed class HuffmanTable
    {
        private readonly int[] _minimum = new int[17];
        private readonly int[] _maximum = new int[17];
        private readonly int[] _offsets = new int[17];
        private readonly byte[] _symbols;
        private readonly ushort[] _codes = new ushort[256];
        private readonly byte[] _lengths = new byte[256];

        public HuffmanTable(ReadOnlySpan<byte> data)
        {
            _symbols = data[16..].ToArray();
            var code = 0;
            var index = 0;
            for (var length = 1; length <= 16; length++)
            {
                var count = data[length - 1];
                _minimum[length] = code; _maximum[length] = code + count - 1; _offsets[length] = index - code;
                for (var item = 0; item < count; item++)
                {
                    var symbol = _symbols[index++];
                    _codes[symbol] = (ushort)code++; _lengths[symbol] = (byte)length;
                }
                code <<= 1;
            }
        }

        public byte Read(ref EntropyReader reader)
        {
            var code = 0;
            for (var length = 1; length <= 16; length++)
            {
                code = (code << 1) | reader.Read(1);
                if (code >= _minimum[length] && code <= _maximum[length]) return _symbols[code + _offsets[length]];
            }
            throw new InvalidDataException("Invalid JPEG Huffman code.");
        }

        public void Write(EntropyWriter writer, byte symbol)
        {
            var length = _lengths[symbol];
            if (length == 0) throw new InvalidDataException("Unsupported JPEG Huffman symbol.");
            writer.Write(_codes[symbol], length);
        }
    }

    private ref struct EntropyReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;
        private int _remaining;
        private int _value;

        public EntropyReader(ReadOnlySpan<byte> data) => _data = data;

        public int Read(int count)
        {
            var result = 0;
            for (var bit = 0; bit < count; bit++)
            {
                if (_remaining == 0)
                {
                    if (_offset >= _data.Length) throw new InvalidDataException("Incomplete JPEG entropy data.");
                    _value = _data[_offset++];
                    if (_value == 255 && (_offset >= _data.Length || _data[_offset++] != 0))
                        throw new InvalidDataException("Unexpected JPEG entropy marker.");
                    _remaining = 8;
                }
                result = (result << 1) | ((_value >> --_remaining) & 1);
            }
            return result;
        }
    }

    private sealed class EntropyWriter(Stream stream)
    {
        private int _value;
        private int _count;

        public void Write(int value, int bits)
        {
            for (var index = bits - 1; index >= 0; index--)
            {
                _value = (_value << 1) | ((value >> index) & 1);
                if (++_count != 8) continue;
                stream.WriteByte((byte)_value);
                if ((_value & 255) == 255) stream.WriteByte(0);
                _count = _value = 0;
            }
        }

        public void Finish() { if (_count > 0) Write((1 << (8 - _count)) - 1, 8 - _count); }
    }
}
