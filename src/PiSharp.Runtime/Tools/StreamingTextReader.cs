using System.Text;
using PiSharp.Core;

namespace PiSharp.Runtime.Tools;

/// <summary>Counts decoded UTF-8 while retaining only the bounded read window.</summary>
internal static class StreamingTextReader
{
    private const int MaxBytes = 50 * 1024;
    private const int MaxLines = 2000;

    public static async Task<string> SelectAsync(string absolute, string displayPath, int offset, int? limit,
        CancellationToken cancellationToken) =>
        (await SelectDetailedAsync(absolute, displayPath, offset, limit, cancellationToken)).Text;

    public static async Task<ReadTextSelection> SelectDetailedAsync(string absolute, string displayPath, int offset,
        int? limit, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        // No decoder preamble: Buffer.toString preserves a UTF-8 BOM and replaces malformed bytes.
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
            bufferSize: 32 * 1024, leaveOpen: true);
        var block = new char[32 * 1024];
        var prefix = new StringBuilder();
        long fileLines = 0;
        long lineBytes = 0;
        long firstLineBytes = 0;
        long selectedBytes = 0;
        long selectedLines = 0;
        var lastEmpty = false;
        var lineStarted = false;
        var previousHighSurrogate = false;

        bool IsSelected() => fileLines + 1 >= offset && (limit is null || fileLines + 1 - offset < limit.Value);
        void Append(char value)
        {
            // Two extra rows prove line truncation even for a prefix made entirely of empty rows.
            if (selectedLines < MaxLines + 2 && prefix.Length < MaxBytes + 4) prefix.Append(value);
        }
        void StartLine()
        {
            if (lineStarted) return;
            if (selectedLines > 0) Append('\n');
            lineStarted = true;
        }
        void FinishLine()
        {
            if (IsSelected())
            {
                StartLine();
                if (selectedLines == 0) firstLineBytes = lineBytes;
                selectedBytes += lineBytes + (selectedLines > 0 ? 1 : 0);
                selectedLines++;
                lastEmpty = lineBytes == 0;
            }
            fileLines++;
            lineBytes = 0;
            lineStarted = false;
            previousHighSurrogate = false;
        }

        int count;
        while ((count = await reader.ReadAsync(block.AsMemory(), cancellationToken)) > 0)
        {
            for (var index = 0; index < count; index++)
            {
                var character = block[index];
                if (character == '\n') { FinishLine(); continue; }
                // Count surrogate pairs across decoder chunks as one four-byte scalar.
                lineBytes += char.IsLowSurrogate(character) && previousHighSurrogate ? 1 :
                    character <= 0x7f ? 1 : character <= 0x7ff ? 2 : 3;
                previousHighSurrogate = char.IsHighSurrogate(character);
                if (IsSelected()) { StartLine(); Append(character); }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        FinishLine(); // split('\n') includes a final row even after the final newline.
        if (offset > fileLines)
            throw new ArgumentOutOfRangeException(nameof(offset), $"Offset {offset} is beyond end of file ({fileLines} lines total)");
        var truncation = TextOutputTruncator.Head(prefix.ToString()) with
        {
            TotalLines = selectedBytes == 0 ? 0 : selectedLines - (lastEmpty ? 1 : 0),
            TotalBytes = selectedBytes
        };
        return ReadTextPlanner.FormatSelection(displayPath, fileLines, offset, limit.HasValue ? selectedLines : null,
            truncation, firstLineBytes);
    }
}
