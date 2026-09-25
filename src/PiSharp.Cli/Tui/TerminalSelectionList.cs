using System.Globalization;

namespace PiSharp.Cli.Tui;

internal sealed record TerminalSelectionOption<T>(string Key, T Value, string Label,
    string? Description = null, string? SearchText = null, bool IsCurrent = false);

internal sealed record TerminalSelection<T>(TerminalSelectionOption<T> Option, bool IsScoped);

internal enum TerminalSelectionAction { Continue, Accept, Cancel }

/// <summary>Searchable, width-aware list state shared by application pickers.</summary>
internal sealed class TerminalSelectionList<T>
{
    private readonly string _title;
    private readonly IReadOnlyList<TerminalSelectionOption<T>> _allOptions;
    private readonly IReadOnlyList<TerminalSelectionOption<T>>? _scopedOptions;
    private readonly string _emptyMessage;
    private readonly string? _allLabel;
    private readonly string? _scopedLabel;
    private IReadOnlyList<TerminalSelectionOption<T>> _activeOptions;
    private IReadOnlyList<TerminalSelectionOption<T>> _filteredOptions;
    private string _query = "";
    private int _selectedIndex;
    private int _lastHeight = 24;
    private int _optionLineStart = -1;
    private int _optionStartIndex;
    private int _optionLineCount;
    private int? _mousePressedIndex;
    private int _mousePressColumn;
    private int _mousePressRow;

    public TerminalSelectionList(string title, IReadOnlyList<TerminalSelectionOption<T>> allOptions,
        string? selectedKey = null, IReadOnlyList<TerminalSelectionOption<T>>? scopedOptions = null,
        string allLabel = "All", string scopedLabel = "Scoped", string emptyMessage = "No matching items")
    {
        _title = Clean(title);
        _allOptions = Sanitize(allOptions);
        _scopedOptions = scopedOptions is null ? null : Sanitize(scopedOptions);
        _emptyMessage = Clean(emptyMessage);
        _allLabel = _scopedOptions is null ? null : Clean(allLabel);
        _scopedLabel = _scopedOptions is null ? null : Clean(scopedLabel);
        IsScoped = _scopedOptions is not null;
        _activeOptions = IsScoped ? _scopedOptions! : _allOptions;
        _filteredOptions = _activeOptions;
        _selectedIndex = IndexOf(selectedKey, _filteredOptions);
    }

    public bool IsScoped { get; private set; }
    public TerminalSelection<T>? Selected
    {
        get
        {
            if (_filteredOptions.Count == 0) return null;
            var selected = _filteredOptions[Math.Clamp(_selectedIndex, 0, _filteredOptions.Count - 1)];
            return new(SanitizeOption(selected), IsScoped);
        }
    }

    public TerminalSelectionAction HandleInput(TerminalInputEvent input, int? mouseContentLine = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Mouse is { } mouse)
        {
            var delta = mouse.WheelScrollDelta;
            if (delta != 0 && _filteredOptions.Count > 0)
            {
                _selectedIndex = Math.Clamp(_selectedIndex - delta, 0, _filteredOptions.Count - 1);
                _mousePressedIndex = null;
                return TerminalSelectionAction.Continue;
            }
            if (mouse.IsRelease)
            {
                var action = _mousePressedIndex is { } pressed &&
                    mouse.Column == _mousePressColumn && mouse.Row == _mousePressRow
                    ? AcceptMouseSelection(pressed)
                    : TerminalSelectionAction.Continue;
                _mousePressedIndex = null;
                return action;
            }
            if (!mouse.IsMotion && (mouse.Button & 3) == 0)
            {
                _mousePressedIndex = OptionIndexAt(mouseContentLine);
                _mousePressColumn = mouse.Column;
                _mousePressRow = mouse.Row;
                if (_mousePressedIndex is { } selected) _selectedIndex = selected;
            }
            return TerminalSelectionAction.Continue;
        }
        if (input.Key is { } key)
        {
            if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                return TerminalSelectionAction.Cancel;
            if (key.Key == ConsoleKey.Enter) return Selected is null ? TerminalSelectionAction.Continue : TerminalSelectionAction.Accept;
            if (key.Key == ConsoleKey.Tab && _scopedOptions is not null)
            {
                ToggleScope();
                return TerminalSelectionAction.Continue;
            }
            if (key.Key == ConsoleKey.UpArrow) { MoveSelection(-1); return TerminalSelectionAction.Continue; }
            if (key.Key == ConsoleKey.DownArrow) { MoveSelection(1); return TerminalSelectionAction.Continue; }
            if (key.Key == ConsoleKey.PageUp) { MoveSelection(-VisibleItemCount(_lastHeight)); return TerminalSelectionAction.Continue; }
            if (key.Key == ConsoleKey.PageDown) { MoveSelection(VisibleItemCount(_lastHeight)); return TerminalSelectionAction.Continue; }
            if (key.Key == ConsoleKey.Home) { _selectedIndex = 0; return TerminalSelectionAction.Continue; }
            if (key.Key == ConsoleKey.End) { _selectedIndex = Math.Max(0, _filteredOptions.Count - 1); return TerminalSelectionAction.Continue; }
            if (key.Key == ConsoleKey.Backspace) { RemoveLastQueryElement(); return TerminalSelectionAction.Continue; }
            if (key.Key == ConsoleKey.U && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                SetQuery("");
                return TerminalSelectionAction.Continue;
            }
            if (!char.IsControl(key.KeyChar) && key.KeyChar != '\0') AppendQuery(key.KeyChar.ToString());
            return TerminalSelectionAction.Continue;
        }

        if (input.Text is { Length: > 0 } text) AppendQuery(text);
        return TerminalSelectionAction.Continue;
    }

    public IReadOnlyList<string> Render(int width, int height)
    {
        width = Math.Max(12, width - 8);
        height = Math.Max(3, height);
        _lastHeight = height;
        var lines = new List<string> { _title };
        if (_scopedOptions is not null)
            lines.Add($"Scope: {(IsScoped ? _scopedLabel : _allLabel)} · Tab to switch");
        lines.Add($"Search: {_query}");
        lines.Add(new string('-', Math.Min(width, Math.Max(1, TerminalTextLayout.Width(_title)))));

        var visibleCount = VisibleItemCount(height);
        _optionLineStart = -1;
        _optionLineCount = 0;
        if (_filteredOptions.Count == 0)
        {
            lines.Add(_emptyMessage);
        }
        else
        {
            var start = Math.Max(0, Math.Min(_selectedIndex - visibleCount / 2, _filteredOptions.Count - visibleCount));
            var end = Math.Min(start + visibleCount, _filteredOptions.Count);
            _optionLineStart = lines.Count;
            _optionStartIndex = start;
            _optionLineCount = end - start;
            for (var index = start; index < end; index++)
            {
                var option = _filteredOptions[index];
                var selected = index == _selectedIndex;
                var marker = selected ? "> " : "  ";
                var current = option.IsCurrent ? "✓ " : "  ";
                var description = string.IsNullOrWhiteSpace(option.Description) ? "" : $" · {option.Description}";
                var line = TerminalSafeText.Normalize($"{marker}{current}{option.Label}{description}");
                lines.Add(TerminalTranscriptViewport.Clip(line, width));
            }
            if (start > 0 || end < _filteredOptions.Count)
                lines.Add($"({_selectedIndex + 1}/{_filteredOptions.Count})");
        }

        lines.Add(_scopedOptions is not null
            ? "↑↓ move · type to filter · Tab scope · Enter select · Esc close"
            : "↑↓ move · type to filter · Enter select · Esc close");
        var maximumContentLines = Math.Max(1, height - 2);
        return lines.Take(maximumContentLines).Select(line => TerminalTranscriptViewport.Clip(line, width)).ToArray();
    }

    private int? OptionIndexAt(int? contentLine)
    {
        if (contentLine is not { } line || _optionLineStart < 0) return null;
        var index = line - _optionLineStart;
        return index >= 0 && index < _optionLineCount ? _optionStartIndex + index : null;
    }

    private TerminalSelectionAction AcceptMouseSelection(int index)
    {
        if (index < 0 || index >= _filteredOptions.Count) return TerminalSelectionAction.Continue;
        _selectedIndex = index;
        return TerminalSelectionAction.Accept;
    }

    private void ToggleScope()
    {
        var selectedKey = Selected?.Option.Key;
        IsScoped = !IsScoped;
        _activeOptions = IsScoped ? _scopedOptions! : _allOptions;
        FilterOptions();
        var restoredIndex = IndexOf(selectedKey, _filteredOptions);
        _selectedIndex = restoredIndex >= 0 ? restoredIndex : Math.Clamp(_selectedIndex, 0, Math.Max(0, _filteredOptions.Count - 1));
    }

    private void MoveSelection(int offset)
    {
        if (_filteredOptions.Count == 0) return;
        _selectedIndex = (_selectedIndex + offset % _filteredOptions.Count + _filteredOptions.Count) % _filteredOptions.Count;
    }

    private int VisibleItemCount(int height) => Math.Max(1, height - 8 - (_scopedOptions is not null ? 1 : 0));

    private void AppendQuery(string value)
    {
        value = Clean(value).Replace('\n', ' ').Replace('\r', ' ');
        if (value.Length == 0) return;
        SetQuery(_query + value);
    }

    private void RemoveLastQueryElement()
    {
        if (_query.Length == 0) return;
        var starts = StringInfo.ParseCombiningCharacters(_query);
        SetQuery(_query[..starts[^1]]);
    }

    private void SetQuery(string query)
    {
        _query = Clean(query).Replace('\n', ' ').Replace('\r', ' ');
        FilterOptions();
        _selectedIndex = 0;
    }

    private void FilterOptions()
    {
        var tokens = _query.Trim().Split([' ', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            _filteredOptions = _activeOptions;
            _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, _filteredOptions.Count - 1));
            return;
        }

        _filteredOptions = _activeOptions.Select((option, index) => (Option: option, Index: index,
                Score: MatchScore(tokens, option.SearchText ?? $"{option.Label} {option.Description}")))
            .Where(match => match.Score is not null)
            .OrderBy(match => match.Score)
            .ThenBy(match => match.Index)
            .Select(match => SanitizeOption(match.Option))
            .ToArray();
        _selectedIndex = 0;
    }

    private static double? MatchScore(IReadOnlyList<string> tokens, string text)
    {
        var score = 0d;
        foreach (var token in tokens)
        {
            var tokenScore = FuzzyTokenScore(token, text);
            if (tokenScore is null) return null;
            score += tokenScore.Value;
        }
        return score;
    }

    private static double? FuzzyTokenScore(string query, string text)
    {
        var normalizedQuery = query.ToLowerInvariant();
        var normalizedText = text.ToLowerInvariant();
        if (normalizedQuery.Length > normalizedText.Length) return null;
        var queryIndex = 0;
        var score = 0d;
        var lastMatchIndex = -1;
        var consecutiveMatches = 0;
        while (queryIndex < normalizedQuery.Length)
        {
            var index = normalizedText.IndexOf(normalizedQuery[queryIndex], lastMatchIndex + 1);
            if (index < 0) return null;
            var boundary = index == 0 || normalizedText[index - 1] is ' ' or '-' or '_' or '.' or '/' or ':';
            if (lastMatchIndex == index - 1)
            {
                consecutiveMatches++;
                score -= consecutiveMatches * 5;
            }
            else
            {
                consecutiveMatches = 0;
                if (lastMatchIndex >= 0) score += (index - lastMatchIndex - 1) * 2;
            }
            if (boundary) score -= 10;
            score += index * 0.1;
            lastMatchIndex = index;
            queryIndex++;
        }
        if (normalizedQuery == normalizedText) score -= 100;
        return score;
    }

    private static int IndexOf(string? key, IReadOnlyList<TerminalSelectionOption<T>> options) =>
        key is null ? 0 : options.Select((option, index) => (option, index))
            .Where(candidate => candidate.option.Key.Equals(key, StringComparison.Ordinal))
            .Select(candidate => candidate.index).FirstOrDefault(-1);

    private static IReadOnlyList<TerminalSelectionOption<T>> Sanitize(IReadOnlyList<TerminalSelectionOption<T>> options) =>
        options.Select(SanitizeOption).ToArray();

    private static TerminalSelectionOption<T> SanitizeOption(TerminalSelectionOption<T> option) => option with
    {
        Label = Clean(option.Label),
        Description = option.Description is null ? null : Clean(option.Description),
        SearchText = option.SearchText is null ? null : Clean(option.SearchText)
    };

    private static string Clean(string value) => TerminalSafeText.Normalize(value).Replace('\t', ' ');
}
