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
        return await SelectDetailedAsync(stream, displayPath, offset, limit, cancellationToken);
    }

    public static async Task<ReadTextSelection> SelectDetailedAsync(FileStream stream, string displayPath, double offset,
        double? limit, CancellationToken cancellationToken)
    {
        var totalFileLines = await CountFileLinesAsync(stream, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var window = ReadTextPlanner.PlanWindow(totalFileLines, offset, limit);
        if (window.Start == window.End)
            return ReadTextPlanner.FormatSelection(displayPath, totalFileLines, window, TextOutputTruncator.Head(""), 0);
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

        bool IsSelected() => fileLines >= window.Start && fileLines < window.End;
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
        var reachedEnd = false;
        while (!reachedEnd && (count = await reader.ReadAsync(block.AsMemory(), cancellationToken)) > 0)
        {
            for (var index = 0; index < count; index++)
            {
                var character = block[index];
                if (character == '\n')
                {
                    FinishLine();
                    if (fileLines >= window.End) { reachedEnd = true; break; }
                    continue;
                }
                // Count surrogate pairs across decoder chunks as one four-byte scalar.
                lineBytes += char.IsLowSurrogate(character) && previousHighSurrogate ? 1 :
                    character <= 0x7f ? 1 : character <= 0x7ff ? 2 : 3;
                previousHighSurrogate = char.IsHighSurrogate(character);
                if (IsSelected()) { StartLine(); Append(character); }
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        if (!reachedEnd) FinishLine(); // split('\n') includes a final row even after the final newline.
        var truncation = TextOutputTruncator.Head(prefix.ToString()) with
        {
            TotalLines = selectedBytes == 0 ? 0 : selectedLines - (lastEmpty ? 1 : 0),
            TotalBytes = selectedBytes
        };
        cancellationToken.ThrowIfCancellationRequested();
        return ReadTextPlanner.FormatSelection(displayPath, totalFileLines, window, truncation, firstLineBytes);
    }

    private static async Task<long> CountFileLinesAsync(FileStream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var block = new byte[64 * 1024];
        long lines = 1;
        int count;
        while ((count = await stream.ReadAsync(block.AsMemory(), cancellationToken)) > 0)
        {
            for (var index = 0; index < count; index++)
                if (block[index] == '\n') lines++;
        }
        stream.Position = 0;
        return lines;
    }
}
