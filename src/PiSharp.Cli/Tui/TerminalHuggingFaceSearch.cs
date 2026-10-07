using System.Text.RegularExpressions;

namespace PiSharp.Cli.Tui;

internal sealed class TerminalHuggingFaceSearch(TerminalScreen screen, TerminalInput input, EditorKeymap keymap,
    HuggingFaceClient client, IDictionary<string, IReadOnlyList<HuggingFaceModel>> cache)
{
    private const string Footer = "enter select • escape/ctrl+c back";
    private static readonly Regex s_repositoryInput = new("^[^/\\s]+/[^:\\s]+(?::[^\\s:]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public string? Show()
    {
        var buffer = new EditorBuffer(keymap);
        IReadOnlyList<HuggingFaceModel> results = [];
        IReadOnlyList<HuggingFaceModel> filtered = [];
        var selectedIndex = 0;
        var status = "Type at least 2 characters";
        var renderedWidth = -1;
        var renderedHeight = -1;
        string? scheduledQuery = null;
        DateTimeOffset? searchAt = null;
        string? activeQuery = null;
        CancellationTokenSource? requestCancellation = null;
        Task<IReadOnlyList<HuggingFaceModel>>? request = null;
        var dirty = true;

        using var mode = TerminalMode.Enter(screen);
        try
        {
            while (screen.IsActive)
            {
                screen.RefreshIfResized();
                if (renderedWidth != screen.TerminalWidth || renderedHeight != screen.TerminalHeight)
                    dirty = true;

                if (request is { IsCompleted: true })
                {
                    var completedRequest = request;
                    request = null;
                    try
                    {
                        var found = completedRequest.GetAwaiter().GetResult();
                        if (!string.IsNullOrEmpty(activeQuery)) cache[activeQuery] = found;
                        if (activeQuery == buffer.Text.Trim())
                        {
                            results = found;
                            selectedIndex = 0;
                            status = results.Count == 0 ? "No GGUF models found" : "";
                            FilterResults();
                            dirty = true;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception error)
                    {
                        if (activeQuery == buffer.Text.Trim())
                        {
                            results = [];
                            status = error.Message;
                            FilterResults();
                            dirty = true;
                        }
                    }
                    requestCancellation?.Dispose();
                    requestCancellation = null;
                    activeQuery = null;
                }

                if (scheduledQuery is { } dueQuery && searchAt <= DateTimeOffset.UtcNow)
                {
                    scheduledQuery = null;
                    searchAt = null;
                    status = "Searching Hugging Face…";
                    activeQuery = dueQuery;
                    requestCancellation = new CancellationTokenSource();
                    request = client.SearchAsync(dueQuery, requestCancellation.Token);
                    dirty = true;
                }

                if (dirty)
                {
                    var lines = Render(buffer.Text, buffer.Cursor, filtered, selectedIndex, status,
                        out var cursorRow, out var cursorColumn);
                    screen.SetEditorPanel(lines, cursorRow, cursorColumn, cursorVisible: false, bottomMargin: 2);
                    renderedWidth = screen.TerminalWidth;
                    renderedHeight = screen.TerminalHeight;
                    dirty = false;
                }

                if (!input.TryRead(50, out var next))
                {
                    continue;
                }
                if (next.IsEndOfStream) return null;
                if (next.Key is { } key)
                {
                    if (key.Key == ConsoleKey.Escape || keymap.Matches("app.interrupt", key) ||
                        keymap.Matches("app.clear", key)) return null;
                    if (key.Key == ConsoleKey.Enter)
                    {
                        var query = buffer.Text.Trim();
                        if (s_repositoryInput.IsMatch(query)) return query;
                        if (selectedIndex < filtered.Count) return filtered[selectedIndex].Id;
                        continue;
                    }
                    if (key.Key == ConsoleKey.UpArrow && filtered.Count > 0)
                    {
                        selectedIndex = (selectedIndex + filtered.Count - 1) % filtered.Count;
                        dirty = true;
                        continue;
                    }
                    if (key.Key == ConsoleKey.DownArrow && filtered.Count > 0)
                    {
                        selectedIndex = (selectedIndex + 1) % filtered.Count;
                        dirty = true;
                        continue;
                    }

                    var previous = buffer.Text;
                    _ = buffer.Handle(key);
                    if (previous != buffer.Text) QueryChanged();
                    else dirty = true;
                    continue;
                }

                if (next.Text is { Length: > 0 } text)
                {
                    var previous = buffer.Text;
                    _ = buffer.InsertText(text.Replace("\r", "", StringComparison.Ordinal)
                        .Replace("\n", "", StringComparison.Ordinal).Replace("\t", "    ", StringComparison.Ordinal));
                    if (previous != buffer.Text) QueryChanged();
                }
            }
            return null;
        }
        finally
        {
            requestCancellation?.Cancel();
            if (request is not null) _ = ObserveAsync(request);
            requestCancellation?.Dispose();
            screen.SetEditorPanel(null);
        }

        void QueryChanged()
        {
            var query = buffer.Text.Trim();
            scheduledQuery = null;
            searchAt = null;
            requestCancellation?.Cancel();
            if (request is not null) _ = ObserveAsync(request);
            request = null;
            requestCancellation?.Dispose();
            requestCancellation = null;
            activeQuery = null;
            if (query.Length < 2)
            {
                status = "Type at least 2 characters";
                FilterResults();
                dirty = true;
                return;
            }
            if (cache.TryGetValue(query, out var cached))
            {
                results = cached;
                status = results.Count == 0 ? "No GGUF models found" : "";
                FilterResults();
                dirty = true;
                return;
            }
            status = "Searching Hugging Face…";
            scheduledQuery = query;
            searchAt = DateTimeOffset.UtcNow.AddMilliseconds(500);
            FilterResults();
            dirty = true;
        }

        void FilterResults()
        {
            filtered = FuzzyFilter(results, buffer.Text);
            selectedIndex = Math.Min(selectedIndex, Math.Max(0, filtered.Count - 1));
        }
    }

    private IReadOnlyList<string> Render(string value, int cursor, IReadOnlyList<HuggingFaceModel> results,
        int selectedIndex, string status, out int cursorRow, out int cursorColumn)
    {
        var width = screen.TerminalWidth;
        var theme = screen.CurrentTheme;
        var lines = new List<string>
        {
            Border(theme, width),
            Pad(theme.Style("accent", " Download model", bold: true), width),
            "",
            Pad(theme.Style("dim", " Model name or owner/repository[:quant]"), width)
        };
        cursorRow = lines.Count;
        lines.Add(InputLine(value, cursor, width, out var inputCursorColumn));
        lines.Add("");
        cursorColumn = inputCursorColumn;
        var visibleCount = Math.Min(10, results.Count);
        var start = Math.Max(0, Math.Min(selectedIndex - visibleCount / 2, results.Count - visibleCount));
        var end = Math.Min(start + visibleCount, results.Count);
        for (var index = start; index < end; index++)
        {
            var model = results[index];
            var prefix = index == selectedIndex ? "→ " : "  ";
            var downloads = CompactCount(model.Downloads) + " downloads";
            var line = index == selectedIndex
                ? theme.Style("accent", prefix + model.Id + "  " + downloads)
                : prefix + model.Id + theme.Style("muted", "  " + downloads);
            lines.Add(Pad(line, width));
        }
        if (start > 0 || end < results.Count)
            lines.Add(Pad(theme.Style("dim", $"  ({selectedIndex + 1}/{results.Count})"), width));
        if (results.Count == 0 || status == "Searching Hugging Face…")
            lines.Add(Pad(theme.Style("dim", "  " + status), width));
        lines.Add("");
        lines.Add(Pad(theme.Style("dim", " " + Footer), width));
        lines.Add(Border(theme, width));
        return lines;
    }

    private static string InputLine(string value, int cursor, int width, out int cursorColumn)
    {
        var safe = TerminalSafeText.Normalize(value);
        var position = Math.Clamp(cursor, 0, safe.Length);
        var before = safe[..position];
        var atCursor = position < safe.Length ? safe[position].ToString() : " ";
        var after = position < safe.Length ? safe[(position + 1)..] : "";
        var line = Pad("> " + before + "\u001b[7m" + atCursor + "\u001b[27m" + after, width);
        cursorColumn = Math.Min(width + 1, 3 + TerminalTextLayout.Width(before));
        return line;
    }

    private static IReadOnlyList<HuggingFaceModel> FuzzyFilter(IReadOnlyList<HuggingFaceModel> models, string query)
    {
        var tokens = Regex.Split(query.Trim(), "[\\s/]+", RegexOptions.CultureInvariant)
            .Where(token => token.Length > 0).ToArray();
        if (tokens.Length == 0) return models;
        return models.Select(model => (Model: model, Score: Score(model.Id, tokens)))
            .Where(entry => entry.Score is not null).OrderBy(entry => entry.Score)
            .Select(entry => entry.Model).ToArray();
    }

    private static double? Score(string text, IReadOnlyList<string> tokens)
    {
        var total = 0d;
        foreach (var token in tokens)
        {
            var result = MatchToken(token, text);
            if (result is null) return null;
            total += result.Value;
        }
        return total;
    }

    private static double? MatchToken(string query, string text)
    {
        double? Match(string value)
        {
            var needle = value.ToLowerInvariant();
            var haystack = text.ToLowerInvariant();
            var score = 0d;
            var last = -1;
            var consecutive = 0;
            foreach (var character in needle)
            {
                var index = haystack.IndexOf(character, last + 1);
                if (index < 0) return null;
                var boundary = index == 0 || char.IsWhiteSpace(haystack[index - 1]) || "-_./:".Contains(haystack[index - 1]);
                if (last == index - 1)
                {
                    consecutive++;
                    score -= consecutive * 5;
                }
                else
                {
                    consecutive = 0;
                    if (last >= 0) score += (index - last - 1) * 2;
                }
                if (boundary) score -= 10;
                score += index * 0.1;
                last = index;
            }
            if (needle == haystack) score -= 100;
            return score;
        }

        var primary = Match(query);
        if (primary is not null) return primary;
        var lettersThenDigits = Regex.Match(query, "^(?<letters>[a-z]+)(?<digits>[0-9]+)$", RegexOptions.IgnoreCase);
        var digitsThenLetters = Regex.Match(query, "^(?<digits>[0-9]+)(?<letters>[a-z]+)$", RegexOptions.IgnoreCase);
        var swapped = lettersThenDigits.Success
            ? lettersThenDigits.Groups["digits"].Value + lettersThenDigits.Groups["letters"].Value
            : digitsThenLetters.Success
                ? digitsThenLetters.Groups["letters"].Value + digitsThenLetters.Groups["digits"].Value
                : "";
        var swappedMatch = swapped.Length == 0 ? null : Match(swapped);
        return swappedMatch is null ? null : swappedMatch.Value + 5;
    }

    private static string CompactCount(long value) => value >= 1_000_000
        ? (value / 1_000_000d).ToString(value >= 10_000_000 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + "M"
        : value >= 1_000
            ? (value / 1_000d).ToString(value >= 100_000 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + "k"
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Border(TerminalTheme theme, int width) =>
        theme.Fg("border") + new string('─', Math.Max(1, width)) + "\u001b[0m";

    private static string Pad(string line, int width) =>
        line + new string(' ', Math.Max(0, width - TerminalTextLayout.Width(line)));

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { }
    }
}
