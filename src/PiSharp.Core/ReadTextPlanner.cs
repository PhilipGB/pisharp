using System.Text;

namespace PiSharp.Core;

/// <summary>Pi's text read line/byte selection; image reads and streamed file IO are separate concerns.</summary>
public static class ReadTextPlanner
{
    private const int MaxLines = 2000;
    private const int MaxBytes = 50 * 1024;

    public static string Select(string text, string path, int offset = 1, int? limit = null)
    {
        if (offset < 1 || limit is <= 0) throw new ArgumentOutOfRangeException(nameof(offset), "Offset and limit must be positive.");
        var lines = text.Split('\n');
        if (offset > lines.Length) throw new ArgumentOutOfRangeException(nameof(offset), $"Offset {offset} is beyond end of file ({lines.Length} lines total)");
        var start = offset - 1;
        var count = Math.Min(limit ?? lines.Length, lines.Length - start);
        var selected = string.Join("\n", lines.Skip(start).Take(count));
        var selectedLines = selected.Length == 0 ? 0 : selected.EndsWith('\n') ? count - 1 : count;
        var bytes = Encoding.UTF8.GetByteCount(selected);
        if (selectedLines <= MaxLines && bytes <= MaxBytes)
        {
            if (limit.HasValue && start + count < lines.Length)
            {
                var remaining = lines.Length - start - count;
                return $"{selected}\n\n[{remaining} more lines in file. Use offset={offset + count} to continue.]";
            }
            return selected;
        }
        if (Encoding.UTF8.GetByteCount(lines[start]) > MaxBytes)
            return $"[Line {offset} is {Size(Encoding.UTF8.GetByteCount(lines[start]))}, exceeds 50.0KB limit. Use bash: sed -n '{offset}p' {path} | head -c {MaxBytes}]";

        var output = new List<string>();
        var used = 0;
        var byBytes = false;
        var available = selected.Split('\n');
        var totalSelected = selected.Length == 0 ? 0 : selected.EndsWith('\n') ? available.Length - 1 : available.Length;
        for (var i = 0; i < totalSelected && i < MaxLines; i++)
        {
            var next = Encoding.UTF8.GetByteCount(available[i]) + (i == 0 ? 0 : 1);
            if (used + next > MaxBytes) { byBytes = true; break; }
            output.Add(available[i]);
            used += next;
        }
        var end = offset + output.Count - 1;
        return string.Join("\n", output) + $"\n\n[Showing lines {offset}-{end} of {lines.Length}{(byBytes ? " (50.0KB limit)" : "")}. Use offset={end + 1} to continue.]";
    }

    private static string Size(int bytes) => bytes < 1024 ? $"{bytes}B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:F1}KB" : $"{bytes / (1024d * 1024):F1}MB";
}
