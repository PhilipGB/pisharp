using System.Globalization;
using System.Text;

namespace PiSharp.Core;

/// <summary>One replacement, matched against the same original file as all other replacements.</summary>
public sealed record TextEdit(string OldText, string NewText);

/// <summary>Pure edit planning. Validation happens before any file is modified.</summary>
public static class FileEdits
{
    public static string Apply(string source, IReadOnlyList<TextEdit> edits, string path)
    {
        if (edits.Count == 0) throw new ArgumentException("Edit tool input is invalid. edits must contain at least one replacement.");
        var original = NormalizeLf(source);
        var replacements = edits.Select(e => new TextEdit(NormalizeLf(e.OldText), NormalizeLf(e.NewText))).ToArray();
        for (var i = 0; i < replacements.Length; i++)
            if (string.IsNullOrEmpty(replacements[i].OldText))
                throw new ArgumentException(edits.Count == 1 ? $"oldText must not be empty in {path}." : $"edits[{i}].oldText must not be empty in {path}.");

        // Pi tries exact matches first. If one requires fuzzy matching, match every edit
        // against the same normalized snapshot, never the output of an earlier edit.
        var fuzzy = replacements.Any(e => !original.Contains(e.OldText, StringComparison.Ordinal) &&
            Fuzzy(original).Contains(Fuzzy(e.OldText), StringComparison.Ordinal));
        var baseContent = fuzzy ? Fuzzy(original) : original;
        var spans = new List<(int Start, int Length, int Index, string Replacement)>();
        for (var i = 0; i < replacements.Length; i++)
        {
            var needle = fuzzy ? Fuzzy(replacements[i].OldText) : replacements[i].OldText;
            var first = baseContent.IndexOf(needle, StringComparison.Ordinal);
            if (first < 0)
                throw new ArgumentException(edits.Count == 1
                    ? $"Could not find the exact text in {path}. The old text must match exactly including all whitespace and newlines."
                    : $"Could not find edits[{i}] in {path}. The oldText must match exactly including all whitespace and newlines.");
            var next = baseContent.IndexOf(needle, first + 1, StringComparison.Ordinal);
            if (next >= 0)
            {
                var count = 2;
                while ((next = baseContent.IndexOf(needle, next + 1, StringComparison.Ordinal)) >= 0) count++;
                throw new ArgumentException(edits.Count == 1
                    ? $"Found {count} occurrences of the text in {path}. The text must be unique. Please provide more context to make it unique."
                    : $"Found {count} occurrences of edits[{i}] in {path}. Each oldText must be unique. Please provide more context to make it unique.");
            }
            spans.Add((first, needle.Length, i, replacements[i].NewText));
        }
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < spans.Count; i++)
            if (spans[i - 1].Start + spans[i - 1].Length > spans[i].Start)
                throw new ArgumentException($"edits[{spans[i - 1].Index}] and edits[{spans[i].Index}] overlap in {path}. Merge them into one edit or target disjoint regions.");

        var output = new StringBuilder(baseContent);
        foreach (var span in spans.AsEnumerable().Reverse())
        {
            output.Remove(span.Start, span.Length);
            output.Insert(span.Start, span.Replacement);
        }
        var changed = fuzzy ? OverlayChangedLines(original, baseContent, spans) : output.ToString();
        if (changed == original)
            throw new ArgumentException(edits.Count == 1
                ? $"No changes made to {path}. The replacement produced identical content. This might indicate an issue with special characters or the text not existing as expected."
                : $"No changes made to {path}. The replacements produced identical content.");
        return changed;
    }

    private static string NormalizeLf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    // Matches Pi's progressive whitespace/Unicode normalization. This is not a general
    // approximate search: the normalized oldText must still occur uniquely.
    private static string Fuzzy(string text)
    {
        var lines = text.Normalize(NormalizationForm.FormKC).Split('\n');
        for (var i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd();
        return string.Join("\n", lines)
            .Replace('‘', '\'').Replace('’', '\'').Replace('‚', '\'').Replace('‛', '\'')
            .Replace('“', '"').Replace('”', '"').Replace('„', '"').Replace('‟', '"')
            .Replace('‐', '-').Replace('‑', '-').Replace('‒', '-').Replace('–', '-').Replace('—', '-').Replace('―', '-').Replace('−', '-')
            .Replace('\u00A0', ' ').Replace('\u202F', ' ').Replace('\u205F', ' ').Replace('\u3000', ' ');
    }

    private static string OverlayChangedLines(string original, string basis, List<(int Start, int Length, int Index, string Replacement)> spans)
    {
        // Pi preserves untouched original lines even when fuzzy matching normalizes them.
        // Group replacements that touch the same line; replace only those groups.
        var originalLines = SplitLines(original);
        var baseLines = SplitLines(basis);
        if (originalLines.Count != baseLines.Count) throw new ArgumentException("Fuzzy normalization changed the line count.");
        var starts = new int[baseLines.Count];
        for (var i = 1; i < starts.Length; i++) starts[i] = starts[i - 1] + baseLines[i - 1].Length;
        var groups = new List<(int First, int Last, List<(int Start, int Length, int Index, string Replacement)> Spans)>();
        foreach (var span in spans)
        {
            var first = Array.FindLastIndex(starts, offset => offset <= span.Start);
            var last = Array.FindLastIndex(starts, offset => offset < span.Start + span.Length);
            var previous = groups.LastOrDefault();
            if (groups.Count > 0 && first < previous.Last)
            {
                previous.Spans.Add(span);
                groups[^1] = (previous.First, Math.Max(last + 1, previous.Last), previous.Spans);
            }
            else groups.Add((first, last + 1, [span]));
        }
        var result = new StringBuilder();
        var cursor = 0;
        foreach (var group in groups)
        {
            for (; cursor < group.First; cursor++) result.Append(originalLines[cursor]);
            var start = starts[group.First];
            var segment = new StringBuilder(string.Concat(baseLines.Skip(group.First).Take(group.Last - group.First)));
            foreach (var span in group.Spans.AsEnumerable().Reverse())
            {
                segment.Remove(span.Start - start, span.Length);
                segment.Insert(span.Start - start, span.Replacement);
            }
            result.Append(segment);
            cursor = group.Last;
        }
        for (; cursor < originalLines.Count; cursor++) result.Append(originalLines[cursor]);
        return result.ToString();
    }

    private static List<string> SplitLines(string text)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') { parts.Add(text[start..(i + 1)]); start = i + 1; }
        if (start < text.Length) parts.Add(text[start..]);
        return parts;
    }
}
