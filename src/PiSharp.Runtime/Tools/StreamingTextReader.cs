using System.Globalization;
using System.Text;

namespace PiSharp.Runtime.Tools;

/// <summary>Large UTF-8 text files: count all lines while retaining only the bounded read window.</summary>
internal static class StreamingTextReader
{
    private const int MaxBytes = 50 * 1024;
    private const int MaxLines = 2000;

    public static async Task<string> SelectAsync(string absolute, string displayPath, int offset, int? limit,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var block = new byte[64 * 1024];
        var line = new List<byte>(MaxBytes + 1);
        var captured = new List<byte[]>();
        long total = 0;
        long length = 0;
        long firstLength = 0;
        long selectedBytes = 0;
        bool lastEmpty = false;
        var bom = new byte[3];
        var bomCount = await stream.ReadAsync(bom, cancellationToken);
        if (bomCount != 3 || bom[0] != 0xEF || bom[1] != 0xBB || bom[2] != 0xBF) stream.Position = 0;

        void FinishLine()
        {
            total++;
            if (total >= offset && (limit is null || total - offset < limit.Value))
            {
                if (total == offset) firstLength = length;
                selectedBytes += length + (total == offset ? 0 : 1);
                if (captured.Count < MaxLines + 1) captured.Add(line.ToArray());
            }
            lastEmpty = length == 0;
            line.Clear();
            length = 0;
        }

        int count;
        while ((count = await stream.ReadAsync(block, cancellationToken)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (block[i] == '\n') { FinishLine(); continue; }
                length++;
                if (total + 1 >= offset && captured.Count < MaxLines + 1 && line.Count <= MaxBytes)
                    line.Add(block[i]);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        FinishLine(); // Split('\n') always includes a final line, even after the final newline.
        if (offset > total) throw new ArgumentOutOfRangeException(nameof(offset), $"Offset {offset} is beyond end of file ({total} lines total)");
        var selectedCount = Math.Min(limit ?? long.MaxValue, total - offset + 1);
        var effectiveLines = selectedCount - (selectedCount > 1 && lastEmpty && offset + selectedCount - 1 == total ? 1 : 0);
        if (effectiveLines <= MaxLines && selectedBytes <= MaxBytes)
        {
            var complete = string.Join('\n', captured.Take((int)selectedCount).Select(Decode));
            if (limit.HasValue && offset + selectedCount - 1 < total)
                return $"{complete}\n\n[{total - offset - selectedCount + 1} more lines in file. Use offset={offset + selectedCount} to continue.]";
            return complete;
        }
        if (firstLength > MaxBytes)
            return $"[Line {offset} is {Size(firstLength)}, exceeds 50.0KB limit. Use bash: sed -n '{offset}p' {displayPath} | head -c {MaxBytes}]";
        var results = new List<string>();
        var used = 0;
        var byBytes = false;
        for (var i = 0; i < Math.Min(effectiveLines, MaxLines); i++)
        {
            var candidate = captured[i];
            var next = candidate.Length + (i == 0 ? 0 : 1);
            if (used + next > MaxBytes) { byBytes = true; break; }
            results.Add(Decode(candidate));
            used += next;
        }
        var end = offset + results.Count - 1;
        return string.Join('\n', results) + $"\n\n[Showing lines {offset}-{end} of {total}{(byBytes ? " (50.0KB limit)" : "")}. Use offset={end + 1} to continue.]";
    }

    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes);
    private static string Size(long bytes) => bytes < 1024 ? $"{bytes}B" : bytes < 1024 * 1024
        ? $"{(bytes / 1024d).ToString("F1", CultureInfo.InvariantCulture)}KB"
        : $"{(bytes / (1024d * 1024)).ToString("F1", CultureInfo.InvariantCulture)}MB";
}
