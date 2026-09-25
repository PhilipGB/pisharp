using System.Text;

namespace PiSharp.Core;

public sealed record FileEditDetails(string Diff, string Patch, int? FirstChangedLine);

/// <summary>Builds the edit preview and patch from the same LF-normalized plan that was written.</summary>
public static class FileEditDiff
{
    private const int ContextLines = 4;
    // Bound Myers trace growth; highly divergent files still get a complete, linear remove/add preview.
    private const int MaxEditDistance = 1024;

    private enum ChangeKind { Equal, Added, Removed }

    private sealed record ChangePart(ChangeKind Kind, List<string> Lines);
    private sealed record Frontier(int Start, int[] Values)
    {
        public int Get(int diagonal) => diagonal < Start || diagonal >= Start + Values.Length
            ? 0 : Values[diagonal - Start];
    }

    public static FileEditDetails Create(string path, string originalContent, string newContent)
    {
        var changes = DiffLines(originalContent, newContent);
        var parts = Group(changes);
        var (diff, firstChangedLine) = CreateDisplayDiff(parts, originalContent, newContent);
        return new FileEditDetails(diff, CreateUnifiedPatch(path, changes), firstChangedLine);
    }

    private static List<(ChangeKind Kind, string Line)> DiffLines(string original, string updated)
    {
        var oldLines = SplitLines(original);
        var newLines = SplitLines(updated);
        var maximum = oldLines.Count + newLines.Count;
        var offset = maximum + 1;
        var frontier = new int[2 * maximum + 3];
        frontier[offset + 1] = 0;
        var trace = new List<Frontier>();
        var distanceLimit = Math.Min(maximum, MaxEditDistance);
        var distance = -1;

        for (var depth = 0; depth <= distanceLimit; depth++)
        {
            trace.Add(Snapshot(frontier, offset, depth));
            for (var diagonal = -depth; diagonal <= depth; diagonal += 2)
            {
                int x;
                if (diagonal == -depth || diagonal != depth && frontier[offset + diagonal - 1] < frontier[offset + diagonal + 1])
                    x = frontier[offset + diagonal + 1];
                else
                    x = frontier[offset + diagonal - 1] + 1;

                var y = x - diagonal;
                while (x < oldLines.Count && y < newLines.Count && oldLines[x] == newLines[y])
                {
                    x++;
                    y++;
                }
                frontier[offset + diagonal] = x;
                if (x >= oldLines.Count && y >= newLines.Count)
                {
                    distance = depth;
                    break;
                }
            }
            if (distance >= 0) break;
        }

        if (distance < 0)
            return oldLines.Select(line => (ChangeKind.Removed, line))
                .Concat(newLines.Select(line => (ChangeKind.Added, line))).ToList();

        var reversed = new List<(ChangeKind Kind, string Line)>(oldLines.Count + newLines.Count);
        var oldIndex = oldLines.Count;
        var newIndex = newLines.Count;
        for (var depth = distance; depth > 0; depth--)
        {
            var previous = trace[depth];
            var diagonal = oldIndex - newIndex;
            var previousDiagonal = diagonal == -depth ||
                diagonal != depth && previous.Get(diagonal - 1) < previous.Get(diagonal + 1)
                ? diagonal + 1 : diagonal - 1;
            var previousX = previous.Get(previousDiagonal);
            var previousY = previousX - previousDiagonal;

            while (oldIndex > previousX && newIndex > previousY)
            {
                reversed.Add((ChangeKind.Equal, oldLines[--oldIndex]));
                newIndex--;
            }

            if (oldIndex == previousX)
                reversed.Add((ChangeKind.Added, newLines[--newIndex]));
            else
                reversed.Add((ChangeKind.Removed, oldLines[--oldIndex]));
        }

        while (oldIndex > 0 && newIndex > 0 && oldLines[oldIndex - 1] == newLines[newIndex - 1])
        {
            reversed.Add((ChangeKind.Equal, oldLines[--oldIndex]));
            newIndex--;
        }
        while (oldIndex > 0) reversed.Add((ChangeKind.Removed, oldLines[--oldIndex]));
        while (newIndex > 0) reversed.Add((ChangeKind.Added, newLines[--newIndex]));
        reversed.Reverse();
        return reversed;
    }

    private static Frontier Snapshot(int[] values, int offset, int depth)
    {
        var start = -depth;
        var copy = new int[depth * 2 + 1];
        for (var diagonal = start; diagonal <= depth; diagonal++) copy[diagonal - start] = values[offset + diagonal];
        return new Frontier(start, copy);
    }

    private static List<string> SplitLines(string content)
    {
        if (content.Length == 0) return [];
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] != '\n') continue;
            lines.Add(content[start..(index + 1)]);
            start = index + 1;
        }
        if (start < content.Length) lines.Add(content[start..]);
        return lines;
    }

    private static List<ChangePart> Group(List<(ChangeKind Kind, string Line)> changes)
    {
        var parts = new List<ChangePart>();
        foreach (var change in changes)
        {
            if (parts.Count == 0 || parts[^1].Kind != change.Kind) parts.Add(new ChangePart(change.Kind, []));
            parts[^1].Lines.Add(change.Line);
        }
        return parts;
    }

    private static (string Diff, int? FirstChangedLine) CreateDisplayDiff(List<ChangePart> parts,
        string original, string updated)
    {
        var output = new List<string>();
        var lineNumberWidth = Math.Max(original.Split('\n').Length, updated.Split('\n').Length).ToString().Length;
        var oldLine = 1;
        var newLine = 1;
        var lastWasChange = false;
        int? firstChangedLine = null;

        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            var lines = part.Lines.Select(line => line.EndsWith('\n') ? line[..^1] : line).ToArray();
            if (part.Kind is ChangeKind.Added or ChangeKind.Removed)
            {
                firstChangedLine ??= newLine;
                foreach (var line in lines)
                {
                    if (part.Kind == ChangeKind.Added)
                        output.Add($"+{newLine++.ToString().PadLeft(lineNumberWidth)} {line}");
                    else
                        output.Add($"-{oldLine++.ToString().PadLeft(lineNumberWidth)} {line}");
                }
                lastWasChange = true;
                continue;
            }

            var nextPartIsChange = index < parts.Count - 1 && parts[index + 1].Kind != ChangeKind.Equal;
            if (lastWasChange && nextPartIsChange)
            {
                if (lines.Length <= ContextLines * 2)
                    AddContext(lines);
                else
                {
                    AddContext(lines[..ContextLines]);
                    AddOmission(lines.Length - ContextLines * 2);
                    AddContext(lines[^ContextLines..]);
                }
            }
            else if (lastWasChange)
            {
                var shown = lines.Take(ContextLines).ToArray();
                AddContext(shown);
                AddOmission(lines.Length - shown.Length);
            }
            else if (nextPartIsChange)
            {
                var skipped = Math.Max(0, lines.Length - ContextLines);
                AddOmission(skipped);
                AddContext(lines.Skip(skipped).ToArray());
            }
            else
            {
                oldLine += lines.Length;
                newLine += lines.Length;
            }
            lastWasChange = false;
        }

        return (string.Join('\n', output), firstChangedLine);

        void AddContext(IEnumerable<string> lines)
        {
            foreach (var line in lines)
            {
                output.Add($" {oldLine++.ToString().PadLeft(lineNumberWidth)} {line}");
                newLine++;
            }
        }

        void AddOmission(int count)
        {
            if (count <= 0) return;
            output.Add($" {"".PadLeft(lineNumberWidth)} ...");
            oldLine += count;
            newLine += count;
        }
    }

    private static string CreateUnifiedPatch(string path, List<(ChangeKind Kind, string Line)> changes)
    {
        var hunks = GetHunks(changes);
        var oldLinesBefore = new int[changes.Count + 1];
        var newLinesBefore = new int[changes.Count + 1];
        for (var index = 0; index < changes.Count; index++)
        {
            oldLinesBefore[index + 1] = oldLinesBefore[index] + (changes[index].Kind == ChangeKind.Added ? 0 : 1);
            newLinesBefore[index + 1] = newLinesBefore[index] + (changes[index].Kind == ChangeKind.Removed ? 0 : 1);
        }
        var patch = new StringBuilder().Append("--- ").Append(path).Append('\n')
            .Append("+++ ").Append(path).Append('\n');
        foreach (var (start, end) in hunks)
        {
            var oldBefore = oldLinesBefore[start];
            var newBefore = newLinesBefore[start];
            var oldCount = oldLinesBefore[end] - oldBefore;
            var newCount = newLinesBefore[end] - newBefore;
            var oldStart = oldCount == 0 ? oldBefore : oldBefore + 1;
            var newStart = newCount == 0 ? newBefore : newBefore + 1;
            patch.Append("@@ -").Append(oldStart).Append(',').Append(oldCount)
                .Append(" +").Append(newStart).Append(',').Append(newCount).Append(" @@\n");
            for (var index = start; index < end; index++)
            {
                var change = changes[index];
                var prefix = change.Kind switch
                {
                    ChangeKind.Equal => ' ',
                    ChangeKind.Removed => '-',
                    _ => '+'
                };
                patch.Append(prefix).Append(change.Line);
                if (!change.Line.EndsWith('\n')) patch.Append("\n\\ No newline at end of file\n");
            }
        }
        return patch.ToString();
    }

    private static List<(int Start, int End)> GetHunks(List<(ChangeKind Kind, string Line)> changes)
    {
        var hunks = new List<(int Start, int End)>();
        for (var index = 0; index < changes.Count; index++)
        {
            if (changes[index].Kind == ChangeKind.Equal) continue;
            var start = Math.Max(0, index - ContextLines);
            var end = Math.Min(changes.Count, index + ContextLines + 1);
            if (hunks.Count > 0 && start <= hunks[^1].End)
                hunks[^1] = (hunks[^1].Start, Math.Max(hunks[^1].End, end));
            else
                hunks.Add((start, end));
        }
        return hunks;
    }
}
