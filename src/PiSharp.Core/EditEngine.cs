using System.Text;

namespace PiSharp.Core;

/// <summary>The result of applying safe text replacements to a file.</summary>
public sealed record EditResult(
    string BaseContent,
    string UpdatedContent,
    string Diff,
    string Patch,
    int? FirstChangedLine,
    bool UsedFuzzyMatch);

/// <summary>
/// Applies Pi-compatible exact and conservative fuzzy replacements. Matching
/// happens against one immutable base snapshot, so edits cannot shift one
/// another or accidentally target content written by an earlier replacement.
/// </summary>
public sealed class EditEngine
{
    private const int DiffContextLines = 4;
    private static readonly IReadOnlyDictionary<char, char> QuoteReplacements =
        new Dictionary<char, char>
        {
            ['\u2018'] = '\'', ['\u2019'] = '\'', ['\u201a'] = '\'', ['\u201b'] = '\'',
            ['\u201c'] = '"', ['\u201d'] = '"', ['\u201e'] = '"', ['\u201f'] = '"',
        };
    private static readonly IReadOnlyDictionary<char, char> DashReplacements =
        new Dictionary<char, char>
        {
            ['\u2010'] = '-', ['\u2011'] = '-', ['\u2012'] = '-', ['\u2013'] = '-',
            ['\u2014'] = '-', ['\u2015'] = '-', ['\u2212'] = '-',
        };

    /// <summary>Applies the supplied replacements to file content.</summary>
    public EditResult Apply(string path, string content, IReadOnlyList<EditOperation> edits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
        {
            throw new ArgumentException("At least one edit is required.", nameof(edits));
        }

        var (bom, body) = SplitBom(content);
        var normalizedBody = NormalizeToLf(body);
        var normalizedEdits = edits.Select(NormalizeEdit).ToArray();
        ValidateOldText(normalizedEdits, path);
        var matchSet = FindMatches(normalizedBody, normalizedEdits, path);
        ValidateNonOverlapping(matchSet.Matches, path);
        var updatedBody = ApplyMatches(
            normalizedBody,
            matchSet.BaseContent,
            normalizedEdits,
            matchSet.Matches,
            matchSet.UsedFuzzyMatch);
        if (updatedBody == normalizedBody)
        {
            throw new InvalidOperationException($"No changes made to {path}. The replacement content is identical.");
        }

        var usedFuzzyMatch = matchSet.UsedFuzzyMatch;
        var displayBase = normalizedBody;
        var displayUpdated = updatedBody;
        var diff = CreateDisplayDiff(displayBase, displayUpdated, out var firstChangedLine);
        var patch = CreateUnifiedPatch(path, displayBase, displayUpdated);
        var ending = DetectLineEnding(body);
        var finalContent = bom + RestoreLineEndings(updatedBody, ending);
        return new EditResult(content, finalContent, diff, patch, firstChangedLine, usedFuzzyMatch);
    }

    /// <summary>Normalizes CRLF/CR input to the LF representation used for matching.</summary>
    public static string NormalizeToLf(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Normalizes common typography differences used by model-generated edits.</summary>
    public static string NormalizeForFuzzyMatch(string text)
    {
        var normalized = NormalizeToLf(text).Normalize(NormalizationForm.FormKC);
        var lines = normalized.Split('\n').Select(line => line.TrimEnd());
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            foreach (var character in line)
            {
                builder.Append(QuoteReplacements.TryGetValue(character, out var quote)
                    ? quote
                    : DashReplacements.TryGetValue(character, out var dash)
                        ? dash
                        : IsSpecialSpace(character) ? ' ' : character);
            }
        }

        return builder.ToString();
    }

    /// <summary>Detects the first newline style, defaulting to LF.</summary>
    public static string DetectLineEnding(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static EditOperation NormalizeEdit(EditOperation edit) =>
        new(NormalizeToLf(edit.OldText), NormalizeToLf(edit.NewText));

    private static void ValidateOldText(IReadOnlyList<EditOperation> edits, string path)
    {
        for (var index = 0; index < edits.Count; index++)
        {
            if (string.IsNullOrEmpty(edits[index].OldText))
            {
                throw new InvalidOperationException($"edits[{index}].oldText must not be empty in {path}.");
            }
        }
    }

    private static MatchSet FindMatches(
        string content,
        IReadOnlyList<EditOperation> edits,
        string path)
    {
        var fuzzyContent = NormalizeForFuzzyMatch(content);
        var initial = edits.Select(edit => FindOne(content, fuzzyContent, edit.OldText)).ToArray();
        var useFuzzyContent = initial.Any(match => match.UsedFuzzyMatch);
        var baseContent = useFuzzyContent ? fuzzyContent : content;
        var matches = new List<EditMatch>(edits.Count);
        for (var index = 0; index < edits.Count; index++)
        {
            var match = FindOne(baseContent, baseContent, edits[index].OldText);
            if (!match.Found)
            {
                throw new InvalidOperationException($"Could not find edits[{index}] in {path}. The old text must match exactly including whitespace and newlines.");
            }

            var occurrences = CountOccurrences(baseContent, edits[index].OldText);
            if (occurrences != 1)
            {
                throw new InvalidOperationException($"Found {occurrences} occurrences of edits[{index}] in {path}. Each oldText must be unique; replacement is not unique.");
            }

            matches.Add(match with { EditIndex = index });
        }

        return new MatchSet(baseContent, matches, useFuzzyContent);
    }

    private static EditMatch FindOne(string content, string fuzzyContent, string oldText)
    {
        var exactIndex = content.IndexOf(oldText, StringComparison.Ordinal);
        if (exactIndex >= 0)
        {
            return new EditMatch(0, exactIndex, oldText.Length, false, true);
        }

        var fuzzyOld = NormalizeForFuzzyMatch(oldText);
        var fuzzyIndex = fuzzyContent.IndexOf(fuzzyOld, StringComparison.Ordinal);
        return fuzzyIndex < 0
            ? new EditMatch(0, -1, 0, true, false)
            : new EditMatch(0, fuzzyIndex, fuzzyOld.Length, true, true);
    }

    private static int CountOccurrences(string content, string oldText)
    {
        var normalizedOld = NormalizeForFuzzyMatch(oldText);
        var count = 0;
        var offset = 0;
        while ((offset = content.IndexOf(normalizedOld, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += Math.Max(1, normalizedOld.Length);
        }

        return count;
    }

    private static void ValidateNonOverlapping(IReadOnlyList<EditMatch> matches, string path)
    {
        var ordered = matches.OrderBy(match => match.Index).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            if (previous.Index + previous.Length > ordered[index].Index)
            {
                throw new InvalidOperationException($"edits[{previous.EditIndex}] and edits[{ordered[index].EditIndex}] overlap in {path}.");
            }
        }
    }

    private static string ApplyMatches(
        string originalContent,
        string replacementBaseContent,
        IReadOnlyList<EditOperation> edits,
        IReadOnlyList<EditMatch> matches,
        bool usedFuzzyMatch)
    {
        if (!usedFuzzyMatch)
        {
            return ApplyRaw(replacementBaseContent, edits, matches);
        }

        return ApplyMatchesPreservingUnchangedLines(originalContent, replacementBaseContent, edits, matches);
    }

    private static string ApplyRaw(
        string content,
        IReadOnlyList<EditOperation> edits,
        IReadOnlyList<EditMatch> matches,
        int offset = 0)
    {
        var result = content;
        foreach (var match in matches.OrderByDescending(match => match.Index))
        {
            var index = match.Index - offset;
            var replacement = edits[match.EditIndex].NewText;
            result = result[..index] + replacement + result[(index + match.Length)..];
        }

        return result;
    }

    private static string ApplyMatchesPreservingUnchangedLines(
        string originalContent,
        string replacementBaseContent,
        IReadOnlyList<EditOperation> edits,
        IReadOnlyList<EditMatch> matches)
    {
        var originalLines = SplitLinesWithEndings(originalContent);
        var baseSpans = GetLineSpans(replacementBaseContent);
        var groups = BuildLineGroups(baseSpans, matches);
        var output = new StringBuilder();
        var originalLine = 0;
        foreach (var group in groups)
        {
            output.Append(string.Concat(originalLines.Skip(originalLine).Take(group.StartLine - originalLine)));
            var start = baseSpans[group.StartLine].Start;
            var end = baseSpans[group.EndLine - 1].End;
            output.Append(ApplyRaw(
                replacementBaseContent[start..end],
                edits,
                group.Matches,
                start));
            originalLine = group.EndLine;
        }

        output.Append(string.Concat(originalLines.Skip(originalLine)));
        return output.ToString();
    }

    private static IReadOnlyList<LineGroup> BuildLineGroups(
        IReadOnlyList<LineSpan> spans,
        IReadOnlyList<EditMatch> matches)
    {
        var groups = new List<LineGroup>();
        foreach (var match in matches.OrderBy(match => match.Index))
        {
            var startLine = FindLine(spans, match.Index);
            var endLine = FindLine(spans, match.Index + match.Length - 1) + 1;
            var current = groups.LastOrDefault();
            if (current is not null && startLine < current.EndLine)
            {
                current.EndLine = Math.Max(current.EndLine, endLine);
                current.Matches.Add(match);
                continue;
            }

            groups.Add(new LineGroup(startLine, endLine, [match]));
        }

        return groups;
    }

    private static int FindLine(IReadOnlyList<LineSpan> spans, int index) =>
        spans.Select((span, line) => (span, line)).First(item => index >= item.span.Start && index < item.span.End).line;

    private static IReadOnlyList<LineSpan> GetLineSpans(string content)
    {
        var spans = new List<LineSpan>();
        var start = 0;
        foreach (var line in SplitLinesWithEndings(content))
        {
            spans.Add(new LineSpan(start, start + line.Length));
            start += line.Length;
        }

        return spans;
    }

    private static IReadOnlyList<string> SplitLinesWithEndings(string content)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var lines = content.Split('\n');
        return lines.Select((line, index) => index < lines.Length - 1 ? line + "\n" : line).ToArray();
    }

    private static string CreateDisplayDiff(string oldContent, string newContent, out int? firstChangedLine)
    {
        var oldLines = SplitLines(oldContent);
        var newLines = SplitLines(newContent);
        var window = FindChangeWindow(oldLines, newLines);
        firstChangedLine = window.OldPrefix + 1;
        var width = Math.Max(oldLines.Count, newLines.Count).ToString().Length;
        var output = new List<string>();
        AddContext(output, oldLines, window.OldPrefix - window.ContextBefore, window.OldPrefix, width);
        AddRemoved(output, oldLines, window.OldPrefix, window.OldChangedEnd, width);
        AddAdded(output, newLines, window.OldPrefix, window.NewChangedEnd, width);
        AddContext(output, newLines, window.NewChangedEnd, window.NewChangedEnd + window.ContextAfter, width);
        return string.Join('\n', output);
    }

    private static string CreateUnifiedPatch(string path, string oldContent, string newContent)
    {
        var oldLines = SplitLines(oldContent);
        var newLines = SplitLines(newContent);
        var window = FindChangeWindow(oldLines, newLines);
        var oldStart = window.OldPrefix + 1 - window.ContextBefore;
        var newStart = window.OldPrefix + 1 - window.ContextBefore;
        var oldCount = window.OldChangedEnd - window.OldPrefix + window.ContextBefore + window.ContextAfter;
        var newCount = window.NewChangedEnd - window.OldPrefix + window.ContextBefore + window.ContextAfter;
        var lines = new List<string>
        {
            $"--- {path}",
            $"+++ {path}",
            $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@",
        };
        AddPatchContext(lines, oldLines, window.OldPrefix - window.ContextBefore, window.OldPrefix);
        lines.AddRange(oldLines.Skip(window.OldPrefix).Take(window.OldChangedEnd - window.OldPrefix).Select(line => $"-{line}"));
        lines.AddRange(newLines.Skip(window.OldPrefix).Take(window.NewChangedEnd - window.OldPrefix).Select(line => $"+{line}"));
        AddPatchContext(lines, newLines, window.NewChangedEnd, window.NewChangedEnd + window.ContextAfter);
        return string.Join('\n', lines);
    }

    private static ChangeWindow FindChangeWindow(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        var prefix = 0;
        while (prefix < oldLines.Count && prefix < newLines.Count && oldLines[prefix] == newLines[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Count - prefix && suffix < newLines.Count - prefix &&
               oldLines[oldLines.Count - suffix - 1] == newLines[newLines.Count - suffix - 1])
        {
            suffix++;
        }

        var oldEnd = oldLines.Count - suffix;
        var newEnd = newLines.Count - suffix;
        return new ChangeWindow(
            prefix,
            oldEnd,
            newEnd,
            Math.Min(DiffContextLines, prefix),
            Math.Min(DiffContextLines, Math.Min(oldLines.Count - oldEnd, newLines.Count - newEnd)));
    }

    private static void AddContext(List<string> output, IReadOnlyList<string> lines, int start, int end, int width)
    {
        for (var index = Math.Max(0, start); index < Math.Min(lines.Count, end); index++)
        {
            output.Add($" {(index + 1).ToString().PadLeft(width)} {lines[index]}");
        }
    }

    private static void AddRemoved(List<string> output, IReadOnlyList<string> lines, int start, int end, int width)
    {
        for (var index = start; index < Math.Min(lines.Count, end); index++)
        {
            output.Add($"-{(index + 1).ToString().PadLeft(width)} {lines[index]}");
        }
    }

    private static void AddAdded(List<string> output, IReadOnlyList<string> lines, int start, int end, int width)
    {
        for (var index = start; index < Math.Min(lines.Count, end); index++)
        {
            output.Add($"+{(index + 1).ToString().PadLeft(width)} {lines[index]}");
        }
    }

    private static void AddPatchContext(List<string> output, IReadOnlyList<string> lines, int start, int end)
    {
        for (var index = Math.Max(0, start); index < Math.Min(lines.Count, end); index++)
        {
            output.Add($" {lines[index]}");
        }
    }

    private static IReadOnlyList<string> SplitLines(string content) => content.Split('\n');

    private static (string Bom, string Body) SplitBom(string content) =>
        content.StartsWith('\uFEFF') ? ("\uFEFF", content[1..]) : (string.Empty, content);

    private static bool IsSpecialSpace(char character) => character is '\u00A0' or >= '\u2002' and <= '\u200A' or '\u202F' or '\u205F' or '\u3000';

    private sealed record EditMatch(int EditIndex, int Index, int Length, bool UsedFuzzyMatch, bool Found);

    private sealed record LineSpan(int Start, int End);

    private sealed class LineGroup(int startLine, int endLine, List<EditMatch> matches)
    {
        public int StartLine { get; } = startLine;
        public int EndLine { get; set; } = endLine;
        public List<EditMatch> Matches { get; } = matches;
    }

    private sealed record MatchSet(string BaseContent, IReadOnlyList<EditMatch> Matches, bool UsedFuzzyMatch);

    private sealed record ChangeWindow(
        int OldPrefix,
        int OldChangedEnd,
        int NewChangedEnd,
        int ContextBefore,
        int ContextAfter);

    private static string RestoreLineEndings(string text, string ending) =>
        ending == "\r\n" ? text.Replace("\n", "\r\n") : text;
}
