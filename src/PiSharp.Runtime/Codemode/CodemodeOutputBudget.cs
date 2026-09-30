using System.Text;

namespace PiSharp.Runtime.Codemode;

internal static class CodemodeOutputBudget
{
    public static async Task<(string Text, string? FullOutputPath)> ApplyAsync(string text, long tokens,
        CancellationToken cancellationToken)
    {
        var budget = tokens * 4;
        if (text.Length <= budget) return (text, null);
        var head = (int)(budget / 2);
        var tail = (int)(budget - head);
        var path = Path.Combine(Path.GetTempPath(), "pi-codemode-" + Guid.NewGuid().ToString("N") + ".txt");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var file = new FileStream(path, options))
        await using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
            await writer.WriteAsync(text.AsMemory(), cancellationToken);
        var rendered = $"Warning: truncated output (original token count: {(text.Length + 3) / 4})\n" +
            $"Total output lines: {text.Count(character => character == '\n') + 1}\n\n" +
            text[..head] + $"…{(text.Length - head - tail + 3) / 4} tokens truncated…" +
            (tail == 0 ? "" : text[^tail..]) + $"\n\n[Full output: {path} (read with offset/limit)]";
        return (rendered, path);
    }
}
