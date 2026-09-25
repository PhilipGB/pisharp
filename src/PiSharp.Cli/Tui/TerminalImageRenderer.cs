using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using SkiaSharp;

namespace PiSharp.Cli.Tui;

/// <summary>Owns validated image attachments and maps transcript image rows to terminal protocol output.</summary>
internal sealed class TerminalImageRenderer
{
    private const string MarkerPrefix = "\uE000psimg-";
    private const char MarkerEnd = '\uE001';
    private const int MaximumImageBytes = 20 * 1024 * 1024;
    private const int MaximumImagePixels = 32_000_000;
    private const int MaximumCachedImageBytes = 32 * 1024 * 1024;
    private const long MaximumCachedDecodedBytes = 128L * 1024 * 1024;
    private const int MaximumCachedImages = 64;
    private const int DefaultCellWidthPixels = 9;
    private const int DefaultCellHeightPixels = 18;
    private const int MaximumImageColumns = 60;
    private readonly Dictionary<Guid, ImageEntry> _images = [];
    private readonly HashSet<int> _pendingKittyDeletes = [];
    private readonly TerminalImageProtocol _protocol;
    private int _cachedBytes;
    private long _cachedDecodedBytes;
    private bool _kittyPlacementsVisible;

    public TerminalImageRenderer(Func<string, string?>? environment = null)
    {
        _protocol = TerminalImageCapabilities.Detect(environment ?? Environment.GetEnvironmentVariable);
    }

    public TerminalImageProtocol Protocol => _protocol;

    public string? Register(DataContent image, out string fallback)
    {
        ArgumentNullException.ThrowIfNull(image);
        var mimeType = image.MediaType?.ToLowerInvariant();
        if (mimeType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
        {
            fallback = "[Image omitted: unsupported image type]";
            return null;
        }

        if (image.Data.Length is <= 0 or > MaximumImageBytes)
        {
            fallback = "[Image omitted: image is invalid or exceeds the display limit]";
            return null;
        }

        var bytes = image.Data.ToArray();
        if (!TryGetDimensions(bytes, out var dimensions))
        {
            fallback = "[Image omitted: image is invalid or exceeds the display limit]";
            return null;
        }

        fallback = $"[Image: [{mimeType}] {dimensions.Width}x{dimensions.Height}]";
        if (_protocol == TerminalImageProtocol.None) return null;
        var decodedBytes = (long)dimensions.Width * dimensions.Height * 4;
        if (_images.Count >= MaximumCachedImages || _cachedBytes + bytes.Length > MaximumCachedImageBytes ||
            _cachedDecodedBytes + decodedBytes > MaximumCachedDecodedBytes)
        {
            fallback = "[Image omitted: terminal image cache is full]";
            return null;
        }

        var id = Guid.NewGuid();
        var kittyId = _protocol == TerminalImageProtocol.Kitty ? RandomNumberGenerator.GetInt32(1, int.MaxValue) : 0;
        while (kittyId != 0 && _images.Values.Any(entry => entry.KittyImageId == kittyId))
            kittyId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        _images.Add(id, new(kittyId, bytes, dimensions, fallback, decodedBytes));
        _cachedBytes += bytes.Length;
        _cachedDecodedBytes += decodedBytes;
        return CreateMarker(id, 0);
    }

    /// <summary>Expands stored images into zero-width row markers for the active transcript layout.</summary>
    public string LayoutTranscript(string text, int terminalColumns, int maximumImageRows)
    {
        ArgumentNullException.ThrowIfNull(text);
        var width = Math.Max(1, Math.Min(MaximumImageColumns, terminalColumns - 2));
        var defaultHeight = Math.Max(1, (int)Math.Ceiling(width * (double)DefaultCellWidthPixels / DefaultCellHeightPixels));
        var height = Math.Max(1, Math.Min(maximumImageRows, defaultHeight));
        var result = new System.Text.StringBuilder(text.Length);
        for (var offset = 0; offset < text.Length;)
        {
            if (TryReadMarker(text, offset, out var markerLength, out var id, out var row))
            {
                if (!_images.TryGetValue(id, out var image))
                {
                    result.Append("[Image omitted]");
                }
                else if (row != 0 || _protocol == TerminalImageProtocol.None)
                {
                    result.Append(image.Fallback);
                }
                else
                {
                    var size = TerminalImageLayout.Fit(image.Dimensions, width, height,
                        new(DefaultCellWidthPixels, DefaultCellHeightPixels), _protocol == TerminalImageProtocol.Kitty);
                    image.Cells = size;
                    for (var imageRow = 0; imageRow < size.Rows; imageRow++)
                    {
                        if (imageRow > 0) result.Append('\n');
                        result.Append(CreateMarker(id, imageRow));
                    }
                }
                offset += markerLength;
                continue;
            }
            result.Append(text[offset++]);
        }
        return result.ToString();
    }

    public void PruneUnreferenced(string retainedTranscriptText)
    {
        ArgumentNullException.ThrowIfNull(retainedTranscriptText);
        var retained = new HashSet<Guid>();
        for (var offset = 0; offset < retainedTranscriptText.Length;)
        {
            if (!TryReadMarker(retainedTranscriptText, offset, out var markerLength, out var id, out _))
            {
                offset++;
                continue;
            }
            retained.Add(id);
            offset += markerLength;
        }

        foreach (var (id, image) in _images.ToArray())
        {
            if (retained.Contains(id)) continue;
            if (image.KittyUploaded) _pendingKittyDeletes.Add(image.KittyImageId);
            _cachedBytes -= image.Bytes.Length;
            _cachedDecodedBytes -= image.DecodedBytes;
            _images.Remove(id);
        }
    }

    /// <summary>Prepares visible rows, emits Kitty placement cleanup, and substitutes only registered markers.</summary>
    public TerminalImageFrame PrepareFrame(IReadOnlyList<string> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var visible = new Dictionary<Guid, HashSet<int>>();
        foreach (var line in rows)
        {
            for (var offset = 0; offset < line.Length;)
            {
                if (!TryReadMarker(line, offset, out var markerLength, out var id, out var row))
                {
                    offset++;
                    continue;
                }
                if (_images.ContainsKey(id))
                {
                    if (!visible.TryGetValue(id, out var imageRows)) visible.Add(id, imageRows = []);
                    imageRows.Add(row);
                }
                offset += markerLength;
            }
        }

        var complete = visible.Where(pair => _images.TryGetValue(pair.Key, out var image) &&
            pair.Value.Count == image.Cells.Rows && Enumerable.Range(0, image.Cells.Rows).All(pair.Value.Contains))
            .Select(pair => pair.Key).ToHashSet();
        var hasKittyImage = _protocol == TerminalImageProtocol.Kitty && complete.Count > 0;
        var preamble = new System.Text.StringBuilder();
        if (_protocol == TerminalImageProtocol.Kitty)
        {
            foreach (var kittyId in _pendingKittyDeletes)
                preamble.Append($"\u001b_Ga=d,d=I,i={kittyId},q=2\u001b\\");
            _pendingKittyDeletes.Clear();
            if (_kittyPlacementsVisible || hasKittyImage) preamble.Append(HideKittyPlacements());
        }
        _kittyPlacementsVisible = hasKittyImage;

        var renderedRows = new List<string>(rows.Count);
        var fallbackAdded = new HashSet<Guid>();
        foreach (var line in rows)
        {
            var result = new System.Text.StringBuilder(line.Length);
            for (var offset = 0; offset < line.Length;)
            {
                if (!TryReadMarker(line, offset, out var markerLength, out var id, out var row))
                {
                    result.Append(line[offset++]);
                    continue;
                }

                if (!_images.TryGetValue(id, out var image))
                    result.Append("[Image omitted]");
                else if (!complete.Contains(id))
                {
                    if (fallbackAdded.Add(id)) result.Append(image.Fallback);
                }
                else if (_protocol == TerminalImageProtocol.Kitty && row == 0)
                    result.Append(RenderKitty(image));
                else if (_protocol == TerminalImageProtocol.ITerm2 && row == image.Cells.Rows - 1)
                    result.Append(RenderITerm2(image));

                offset += markerLength;
            }
            renderedRows.Add(result.ToString());
        }

        return new(preamble.ToString(), renderedRows);
    }

    public string CleanupControlSequence() => _protocol == TerminalImageProtocol.Kitty && _images.Values.Any(image => image.KittyUploaded)
        ? "\u001b_Ga=d,d=A,q=2\u001b\\"
        : "";

    public string HidePlacements()
    {
        if (_protocol != TerminalImageProtocol.Kitty || !_kittyPlacementsVisible) return "";
        _kittyPlacementsVisible = false;
        return HideKittyPlacements();
    }

    public void Clear()
    {
        _images.Clear();
        _pendingKittyDeletes.Clear();
        _cachedBytes = 0;
        _cachedDecodedBytes = 0;
        _kittyPlacementsVisible = false;
    }

    internal static bool TryReadMarker(string text, int offset, out int length, out Guid id, out int row)
    {
        length = 0;
        id = default;
        row = 0;
        if (offset < 0 || offset + MarkerPrefix.Length + 35 > text.Length ||
            !text.AsSpan(offset).StartsWith(MarkerPrefix, StringComparison.Ordinal)) return false;
        var idStart = offset + MarkerPrefix.Length;
        if (!Guid.TryParseExact(text.AsSpan(idStart, 32), "N", out id) || text[idStart + 32] != '-') return false;
        var end = text.IndexOf(MarkerEnd, idStart + 33);
        if (end < 0 || !int.TryParse(text.AsSpan(idStart + 33, end - idStart - 33), NumberStyles.None,
                CultureInfo.InvariantCulture, out row) || row < 0) return false;
        length = end + 1 - offset;
        return true;
    }

    private string RenderKitty(ImageEntry image)
    {
        if (image.KittyUploaded)
            return $"\u001b_Ga=p,q=2,C=1,c={image.Cells.Columns},r={image.Cells.Rows},i={image.KittyImageId}\u001b\\";
        image.KittyUploaded = true;
        return EncodeKitty(Convert.ToBase64String(image.Bytes), image.Cells.Columns, image.Cells.Rows, image.KittyImageId);
    }

    private static string RenderITerm2(ImageEntry image)
    {
        var encoded = Convert.ToBase64String(image.Bytes);
        var payload = $"\u001b]1337;File=inline=1;size={image.Bytes.Length};width={image.Cells.Columns};height=auto:{encoded}\u0007";
        return (image.Cells.Rows > 1 ? $"\u001b[{image.Cells.Rows - 1}A" : "") + payload;
    }

    private static string EncodeKitty(string base64, int columns, int rows, int imageId)
    {
        const int chunkSize = 4096;
        var parameters = $"a=T,f=100,q=2,C=1,c={columns},r={rows},i={imageId}";
        if (base64.Length <= chunkSize) return $"\u001b_G{parameters};{base64}\u001b\\";
        var chunks = new System.Text.StringBuilder(base64.Length + base64.Length / chunkSize * 32);
        for (var offset = 0; offset < base64.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, base64.Length - offset);
            if (offset == 0) chunks.Append($"\u001b_G{parameters},m=1;");
            else chunks.Append(offset + count == base64.Length ? "\u001b_Gm=0;" : "\u001b_Gm=1;");
            chunks.Append(base64, offset, count).Append("\u001b\\");
        }
        return chunks.ToString();
    }

    private static string CreateMarker(Guid id, int row) => $"{MarkerPrefix}{id:N}-{row}\uE001";

    private static string HideKittyPlacements() => "\u001b_Ga=d,d=a,q=2\u001b\\";

    private static bool TryGetDimensions(byte[] bytes, out TerminalImagePixels dimensions)
    {
        dimensions = default;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var codec = SKCodec.Create(stream);
            if (codec is null) return false;
            var width = codec.Info.Width;
            var height = codec.Info.Height;
            if (width <= 0 || height <= 0 || (long)width * height > MaximumImagePixels) return false;
            dimensions = new(width, height);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return false;
        }
    }

    internal sealed record TerminalImageFrame(string Preamble, IReadOnlyList<string> Rows);

    private sealed class ImageEntry(int kittyImageId, byte[] bytes, TerminalImagePixels dimensions, string fallback,
        long decodedBytes)
    {
        public int KittyImageId { get; } = kittyImageId;
        public byte[] Bytes { get; } = bytes;
        public TerminalImagePixels Dimensions { get; } = dimensions;
        public string Fallback { get; } = fallback;
        public long DecodedBytes { get; } = decodedBytes;
        public TerminalImageCells Cells { get; set; }
        public bool KittyUploaded { get; set; }
    }
}
