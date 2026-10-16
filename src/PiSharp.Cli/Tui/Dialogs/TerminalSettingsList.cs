namespace PiSharp.Cli.Tui;

internal sealed record TerminalSettingsListItem(string Key, string Label, string Value,
    string Description, string SearchText);

internal sealed record TerminalSettingsListSelection(TerminalSettingsListItem? Item, bool ChangeScope = false,
    bool ResetValue = false);

internal enum TerminalSettingsListAction { Continue, Activate, ChangeScope, ResetValue, Cancel }

/// <summary>Compact in-flow settings list with filtering and a bounded, resize-aware viewport.</summary>
internal sealed class TerminalSettingsList
{
    private const int MaximumVisibleItems = 10;
    private readonly string _title;
    private IReadOnlyList<TerminalSettingsListItem> _items;
    private IReadOnlyList<TerminalSettingsListItem> _filteredItems;
    private readonly bool _canChangeScope;
    private string _scopeLabel;
    private string _query = "";
    private int _selectedIndex;

    public TerminalSettingsList(string title, IReadOnlyList<TerminalSettingsListItem> items,
        string? selectedKey = null, string scopeLabel = "User settings", bool canChangeScope = false)
    {
        _title = Clean(title);
        _items = Sanitize(items);
        _filteredItems = _items;
        _scopeLabel = Clean(scopeLabel);
        _canChangeScope = canChangeScope;
        _selectedIndex = FindIndex(selectedKey, _filteredItems);
    }

    public TerminalSettingsListItem? Selected => _filteredItems.Count == 0
        ? null : _filteredItems[Math.Clamp(_selectedIndex, 0, _filteredItems.Count - 1)];
    public bool IsFiltering => _query.Length > 0;
    public bool ShouldActivateSpace(TerminalInputEvent input) =>
        input.Key is { Key: ConsoleKey.Spacebar, Modifiers: 0 } && !IsFiltering;

    public TerminalSettingsListAction HandleInput(TerminalInputEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Key is { } key)
        {
            if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                return TerminalSettingsListAction.Cancel;
            if (key.Key == ConsoleKey.Enter) return Selected is null
                ? TerminalSettingsListAction.Continue : TerminalSettingsListAction.Activate;
            if (key.Key == ConsoleKey.Tab && _canChangeScope)
                return TerminalSettingsListAction.ChangeScope;
            if (key.Key == ConsoleKey.Delete)
                return Selected is null ? TerminalSettingsListAction.Continue : TerminalSettingsListAction.ResetValue;
            if (key.Key == ConsoleKey.UpArrow) { Move(-1); return TerminalSettingsListAction.Continue; }
            if (key.Key == ConsoleKey.DownArrow) { Move(1); return TerminalSettingsListAction.Continue; }
            if (key.Key == ConsoleKey.PageUp) { Move(-MaximumVisibleItems); return TerminalSettingsListAction.Continue; }
            if (key.Key == ConsoleKey.PageDown) { Move(MaximumVisibleItems); return TerminalSettingsListAction.Continue; }
            if (key.Key == ConsoleKey.Home) { _selectedIndex = 0; return TerminalSettingsListAction.Continue; }
            if (key.Key == ConsoleKey.End) { _selectedIndex = Math.Max(0, _filteredItems.Count - 1); return TerminalSettingsListAction.Continue; }
            if (key.Key == ConsoleKey.Backspace) { RemoveLastQueryElement(); return TerminalSettingsListAction.Continue; }
            if (key.Key == ConsoleKey.U && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                SetQuery("");
                return TerminalSettingsListAction.Continue;
            }
            if (key.Key == ConsoleKey.Spacebar && _query.Length == 0)
                return Selected is null ? TerminalSettingsListAction.Continue : TerminalSettingsListAction.Activate;
            if (!char.IsControl(key.KeyChar) && key.KeyChar != '\0') AppendQuery(key.KeyChar.ToString());
            return TerminalSettingsListAction.Continue;
        }
        if (input.Text is { Length: > 0 } text) AppendQuery(text);
        return TerminalSettingsListAction.Continue;
    }

    public void ReplaceItems(IReadOnlyList<TerminalSettingsListItem> items, string scopeLabel)
    {
        ArgumentNullException.ThrowIfNull(items);
        var selectedKey = Selected?.Key;
        _items = Sanitize(items);
        _scopeLabel = Clean(scopeLabel);
        FilterOptions();
        var restoredIndex = FindIndex(selectedKey, _filteredItems);
        _selectedIndex = restoredIndex >= 0 ? restoredIndex : Math.Clamp(_selectedIndex, 0, Math.Max(0, _filteredItems.Count - 1));
    }

    public void ChangeScope(string scopeLabel, IReadOnlyList<TerminalSettingsListItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _scopeLabel = Clean(scopeLabel);
        _items = Sanitize(items);
        SetQuery("");
    }

    public IReadOnlyList<string> Render(int width, int height, TerminalTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        width = Math.Max(16, width);
        height = Math.Max(3, height);
        var panelRows = Math.Max(1, height - 2);
        var heading = _canChangeScope ? $"{_title} · {_scopeLabel}" : _title;
        var lines = new List<string> { theme.Style("accent", heading, bold: true) };
        if (_query.Length > 0 && panelRows > 1)
            lines.Add(theme.Style("muted", "Filter: " + _query));

        // Keep the panel inside the screen's bottom margin. Reduce the description first
        // as the terminal shrinks so the selected row, count and key hints stay visible.
        var descriptionRows = Math.Clamp(Math.Min(width < 50 ? 3 : 2, panelRows - lines.Count - 3), 0, 3);
        var fixedRows = lines.Count + descriptionRows + 2;
        var visibleCount = Math.Clamp(panelRows - fixedRows, 0, MaximumVisibleItems);
        if (visibleCount == 0 && panelRows > lines.Count + 2) visibleCount = 1;
        var start = Math.Max(0, Math.Min(_selectedIndex - visibleCount / 2, _filteredItems.Count - visibleCount));
        var end = Math.Min(start + visibleCount, _filteredItems.Count);
        var labelWidth = Math.Min(_filteredItems.Count == 0 ? 12 : _filteredItems.Max(item => TerminalTextLayout.Width(item.Label)),
            Math.Max(1, (width - 6) / 2));
        var valueWidth = Math.Max(1, width - labelWidth - 6);

        for (var index = start; index < end; index++)
        {
            var item = _filteredItems[index];
            var selected = index == _selectedIndex;
            var label = TerminalTranscriptViewport.Clip(TerminalSafeText.Normalize(item.Label), labelWidth);
            label += new string(' ', Math.Max(0, labelWidth - TerminalTextLayout.Width(label)));
            var value = TerminalTranscriptViewport.Clip(TerminalSafeText.Normalize(item.Value), valueWidth);
            var row = $"{(selected ? ">" : " ")} {label}  {value}";
            lines.Add(selected ? theme.Style("accent", row, bold: true) : row);
        }
        if (_filteredItems.Count == 0)
            lines.Add(theme.Style("muted", "No matching settings"));

        lines.Add(theme.Style("muted", $"({(_filteredItems.Count == 0 ? 0 : _selectedIndex + 1)}/{_filteredItems.Count})"));
        var selectedDescription = Selected?.Description ?? "Type to filter the available settings.";
        foreach (var line in TerminalTextLayout.Wrap(TerminalSafeText.Normalize(selectedDescription), width)
                     .Take(descriptionRows))
            lines.Add(theme.Style("muted", line));
        if (lines.Count < panelRows)
            lines.Add(theme.Style("dim", _canChangeScope
            ? "Type to filter · Enter/Space change · Delete reset · Tab scope · Esc cancel"
            : "Type to filter · Enter/Space change · Delete reset · Esc cancel"));

        return lines.Take(panelRows).Select(line => TerminalTranscriptViewport.Clip(line, width)).ToArray();
    }

    private void Move(int offset)
    {
        if (_filteredItems.Count == 0) return;
        _selectedIndex = (_selectedIndex + offset % _filteredItems.Count + _filteredItems.Count) % _filteredItems.Count;
    }

    private void AppendQuery(string value)
    {
        if (_query.Length + value.Length > 256) return;
        SetQuery(_query + value);
    }

    private void RemoveLastQueryElement()
    {
        if (_query.Length == 0) return;
        var starts = System.Globalization.StringInfo.ParseCombiningCharacters(_query);
        SetQuery(_query[..starts[^1]]);
    }

    private void SetQuery(string query)
    {
        _query = query;
        FilterOptions();
    }

    private void FilterOptions()
    {
        if (_query.Length == 0)
        {
            _filteredItems = _items;
            _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, _filteredItems.Count - 1));
            return;
        }
        var terms = _query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _filteredItems = _items.Where(item =>
        {
            var haystack = item.SearchText + " " + item.Label + " " + item.Value + " " + item.Description;
            return terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
        }).ToArray();
        _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, _filteredItems.Count - 1));
    }

    private static int FindIndex(string? key, IReadOnlyList<TerminalSettingsListItem> items)
    {
        if (key is null) return 0;
        var index = 0;
        foreach (var item in items)
        {
            if (item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return index;
            index++;
        }
        return 0;
    }

    private static IReadOnlyList<TerminalSettingsListItem> Sanitize(IReadOnlyList<TerminalSettingsListItem> items) =>
        items.Select(item => item with
        {
            Key = Clean(item.Key),
            Label = Clean(item.Label),
            Value = Clean(item.Value),
            Description = Clean(item.Description),
            SearchText = Clean(item.SearchText)
        }).ToArray();

    private static string Clean(string? value) => TerminalSafeText.Normalize(value ?? "");
}
