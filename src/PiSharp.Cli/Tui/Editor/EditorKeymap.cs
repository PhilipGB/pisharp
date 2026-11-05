using System.Text.Json;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Cli.Tui;

public sealed class EditorKeymap
{
    private sealed record Definition(string Description, string[] DefaultKeys);
    private sealed record BoundKey(PiSharpShortcutKey Stroke, string Name);
    private sealed record BuiltInBinding(string Action, bool IsReserved);
    internal sealed record ShortcutDiagnostic(string SourcePath, string Message);
    private static readonly HashSet<string> s_reservedShortcutActions = new(StringComparer.Ordinal)
    {
        "app.interrupt", "app.clear", "app.exit", "app.suspend", "app.thinking.cycle",
        "app.model.cycleForward", "app.model.cycleBackward", "app.model.select", "app.tools.expand",
        "app.thinking.toggle", "app.thinking.save", "app.editor.external", "app.message.copy",
        "app.message.followUp", "tui.input.submit", "tui.input.tab", "tui.select.up",
        "tui.select.down", "tui.select.pageUp", "tui.select.pageDown", "tui.select.confirm",
        "tui.select.cancel", "tui.input.copy", "tui.editor.deleteToLineEnd"
    };
    private static readonly string[] IdleApplicationActions = ["app.suspend", "app.thinking.cycle", "app.thinking.toggle", "app.model.cycleForward", "app.model.cycleBackward", "app.model.select", "app.tools.expand", "app.editor.external", "app.message.copy", "app.clipboard.pasteImage", "app.settings.open", "app.session.tree", "app.session.fork", "app.session.resume"];

    private readonly IReadOnlyDictionary<string, Definition> _definitions;
    private readonly Func<bool> _windowsBindings;

    private static readonly IReadOnlyDictionary<string, Definition> s_definitions = CreateDefinitions(IsWindowsBindings);

    private static IReadOnlyDictionary<string, Definition> CreateDefinitions(Func<bool> windowsBindings) =>
        new Dictionary<string, Definition>(StringComparer.Ordinal)
        {
            ["tui.input.submit"] = new("Submit input", ["enter"]),
            ["tui.input.newLine"] = new("Insert a new line", ["shift+enter", "ctrl+j"]),
            ["tui.input.tab"] = new("Tab / autocomplete", ["tab"]),
            ["tui.editor.cursorUp"] = new("Move up or browse older history", ["up"]),
            ["tui.editor.cursorDown"] = new("Move down or browse newer history", ["down"]),
            ["tui.editor.historyPrevious"] = new("Browse older prompt history", []),
            ["tui.editor.historyNext"] = new("Browse newer prompt history", []),
            ["tui.editor.undo"] = new("Undo the last editor change",
            OperatingSystem.IsWindows() ? ["ctrl+z"] : windowsBindings() ? ["alt+z"] : ["ctrl+-"]),
            ["tui.editor.cursorLeft"] = new("Move cursor left", ["left", "ctrl+b"]),
            ["tui.editor.cursorRight"] = new("Move cursor right", ["right", "ctrl+f"]),
            ["tui.editor.cursorWordLeft"] = new("Move cursor left by word", ["alt+left", "ctrl+left", "alt+b"]),
            ["tui.editor.cursorWordRight"] = new("Move cursor right by word", ["alt+right", "ctrl+right", "alt+f"]),
            ["tui.editor.cursorLineStart"] = new("Move cursor to line start", ["home", "ctrl+a"]),
            ["tui.editor.cursorLineEnd"] = new("Move cursor to line end", ["end", "ctrl+e"]),
            ["tui.editor.jumpForward"] = new("Jump forward to character", ["ctrl+]"]),
            ["tui.editor.jumpBackward"] = new("Jump backward to character", ["ctrl+alt+]"]),
            ["tui.editor.pageUp"] = new("Scroll up by page", ["pageup", "ctrl+pageup"]),
            ["tui.editor.pageDown"] = new("Scroll down by page", ["pagedown", "ctrl+pagedown"]),
            ["tui.editor.selectLeft"] = new("Extend selection left", ["shift+left"]),
            ["tui.editor.selectRight"] = new("Extend selection right", ["shift+right"]),
            ["tui.editor.selectWordLeft"] = new("Extend selection left by word", ["alt+shift+left", "ctrl+shift+left"]),
            ["tui.editor.selectWordRight"] = new("Extend selection right by word", ["alt+shift+right", "ctrl+shift+right"]),
            ["tui.editor.selectLineStart"] = new("Extend selection to line start", ["shift+home"]),
            ["tui.editor.selectLineEnd"] = new("Extend selection to line end", ["shift+end"]),
            ["tui.editor.selectUp"] = new("Extend selection up", ["shift+up"]),
            ["tui.editor.selectDown"] = new("Extend selection down", ["shift+down"]),
            ["tui.editor.deleteCharBackward"] = new("Delete backward", ["backspace"]),
            ["tui.editor.deleteCharForward"] = new("Delete forward", ["delete", "ctrl+d"]),
            ["tui.editor.deleteWordBackward"] = new("Delete previous word", ["ctrl+w", "alt+backspace"]),
            ["tui.editor.deleteWordForward"] = new("Delete next word", ["alt+d", "alt+delete"]),
            ["tui.editor.deleteToLineStart"] = new("Delete to line start", ["ctrl+u"]),
            ["tui.editor.deleteToLineEnd"] = new("Delete to line end", ["ctrl+k"]),
            ["tui.editor.yank"] = new("Yank last killed text", ["ctrl+y"]),
            ["tui.editor.yankPop"] = new("Cycle to the previous killed text", ["alt+y"]),
            ["app.interrupt"] = new("Cancel or abort", ["escape"]),
            ["app.clear"] = new("Clear editor", ["ctrl+c"]),
            ["app.exit"] = new("Exit when editor is empty", ["ctrl+d"]),
            ["app.suspend"] = new("Suspend to background", OperatingSystem.IsWindows() ? [] : ["ctrl+z"]),
            ["app.model.cycleForward"] = new("Cycle to next model", ["ctrl+p"]),
            ["app.model.cycleBackward"] = new("Cycle to previous model", windowsBindings() ? ["alt+p"] : ["shift+ctrl+p"]),
            ["app.model.select"] = new("Open model selector", ["ctrl+l"]),
            ["app.editor.external"] = new("Open external editor", ["ctrl+g"]),
            ["app.message.copy"] = new("Copy selection or last assistant message", ["ctrl+x"]),
            ["app.clipboard.pasteImage"] = new("Paste clipboard image or text", windowsBindings() ? ["alt+v"] : ["ctrl+v"]),
            ["app.settings.open"] = new("Open settings", []),
            ["app.session.tree"] = new("Open session tree", []),
            ["app.session.resume"] = new("Open session selector", []),
            ["app.session.fork"] = new("Open fork selector", []),
            ["app.session.rename"] = new("Rename selected session", ["ctrl+r"]),
            ["app.session.toggleSort"] = new("Toggle session sort mode", ["ctrl+s"]),
            ["app.session.togglePath"] = new("Toggle session path display", ["ctrl+p"]),
            ["app.session.toggleNamedFilter"] = new("Toggle named session filter", ["ctrl+n"]),
            ["app.session.delete"] = new("Delete selected session", ["ctrl+d"]),
            ["app.session.deleteNoninvasive"] = new("Delete session when the search query is empty", ["ctrl+backspace"]),
            ["app.models.save"] = new("Save scoped model selection", ["ctrl+s"]),
            ["app.models.enableAll"] = new("Enable all scoped models", ["ctrl+a"]),
            ["app.models.clearAll"] = new("Clear scoped models", ["ctrl+x"]),
            ["app.models.toggleProvider"] = new("Toggle all models for this provider", ["ctrl+p"]),
            ["app.models.reorderUp"] = new("Move model earlier in the scope", ["alt+up"]),
            ["app.models.reorderDown"] = new("Move model later in the scope", ["alt+down"]),
            ["app.tree.foldOrUp"] = new("Fold a tree branch or jump up",
                OperatingSystem.IsMacOS() ? ["alt+left", "ctrl+left"] : ["ctrl+left", "alt+left"]),
            ["app.tree.unfoldOrDown"] = new("Unfold a tree branch or jump down",
                OperatingSystem.IsMacOS() ? ["alt+right", "ctrl+right"] : ["ctrl+right", "alt+right"]),
            ["app.tree.editLabel"] = new("Edit the selected tree label", ["shift+l"]),
            ["app.tree.toggleLabelTimestamp"] = new("Toggle tree label timestamps", ["shift+t"]),
            ["app.tree.filter.default"] = new("Show the default tree view", ["ctrl+d"]),
            ["app.tree.filter.noTools"] = new("Toggle the tree filter that hides tool results", ["ctrl+t"]),
            ["app.tree.filter.userOnly"] = new("Toggle the tree filter that shows only user messages", ["ctrl+u"]),
            ["app.tree.filter.labeledOnly"] = new("Toggle the tree filter that shows only labeled entries", ["ctrl+l"]),
            ["app.tree.filter.all"] = new("Toggle the tree filter that shows all entries", ["ctrl+a"]),
            ["app.tree.filter.cycleForward"] = new("Cycle the tree filter forward", ["ctrl+o"]),
            ["app.tree.filter.cycleBackward"] = new("Cycle the tree filter backward", ["shift+ctrl+o"]),
            ["app.thinking.cycle"] = new("Cycle thinking level", ["shift+tab"]),
            ["app.thinking.save"] = new("Save thinking level", ["ctrl+s"]),
            ["app.thinking.toggle"] = new("Toggle thinking block visibility", ["ctrl+t"]),
            ["app.tools.expand"] = new("Expand or collapse tool output", ["ctrl+o"]),
            ["app.message.followUp"] = new("Queue a follow-up message", windowsBindings() ? ["ctrl+q"] : ["alt+enter"]),
            ["app.message.dequeue"] = new("Restore queued messages", windowsBindings() ? ["alt+q"] : ["alt+up"]),
            ["tui.select.up"] = new("Move selection up", ["up"]),
            ["tui.select.down"] = new("Move selection down", ["down"]),
            ["tui.select.pageUp"] = new("Selection page up", ["pageup"]),
            ["tui.select.pageDown"] = new("Selection page down", ["pagedown"]),
            ["tui.select.confirm"] = new("Confirm selection", ["enter"]),
            ["tui.select.cancel"] = new("Cancel selection", ["escape", "ctrl+c"]),
            ["tui.altScreen.pageUp"] = new("Scroll transcript up one page", ["pageup"]),
            ["tui.altScreen.pageDown"] = new("Scroll transcript down one page", ["pagedown"]),
            ["tui.altScreen.previousPrompt"] = new("Jump to previous semantic prompt",
            windowsBindings() ? ["ctrl+up"] : ["ctrl+shift+up", "ctrl+up"]),
            ["tui.altScreen.nextPrompt"] = new("Jump to next semantic prompt",
            windowsBindings() ? ["ctrl+down"] : ["ctrl+shift+down", "ctrl+down"]),
            ["tui.altScreen.top"] = new("Scroll to the start of the transcript", ["ctrl+home"]),
            ["tui.altScreen.bottom"] = new("Return to the live transcript", ["ctrl+end"]),
            ["tui.altScreen.search"] = new("Search the rendered transcript", windowsBindings() ? ["ctrl+f"] : ["ctrl+shift+f"]),
            ["tui.altScreen.searchNext"] = new("Select the next transcript search match", ["enter", "ctrl+g"]),
            ["tui.altScreen.searchPrevious"] = new("Select the previous transcript search match", ["shift+enter", "ctrl+shift+g"]),
            ["tui.altScreen.searchClose"] = new("Close transcript search", ["escape"])
        };

    private readonly string? _path;
    private Dictionary<string, BoundKey[]> _bindings = new(StringComparer.Ordinal);
    private IReadOnlyList<ExtensionShortcutInfo> _registeredExtensionShortcuts = [];
    private Dictionary<PiSharpShortcutKey, ExtensionShortcutInfo> _activeExtensionShortcuts = [];
    private IReadOnlyList<ShortcutDiagnostic> _shortcutDiagnostics = [];

    public EditorKeymap(string? agentDirectory = null)
        : this(agentDirectory, windowsBindings: null)
    {
    }

    internal EditorKeymap(string? agentDirectory, bool? windowsBindings)
    {
        _windowsBindings = windowsBindings is null ? IsWindowsBindings : () => windowsBindings.Value;
        _definitions = windowsBindings is null ? s_definitions : CreateDefinitions(_windowsBindings);
        _path = string.IsNullOrWhiteSpace(agentDirectory) ? null : Path.Combine(Path.GetFullPath(agentDirectory), "keybindings.json");
        Reload();
    }

    public bool Matches(string action, ConsoleKeyInfo key) =>
        _bindings.TryGetValue(action, out var bindings) && bindings.Any(binding =>
            binding.Stroke.Key == key.Key && binding.Stroke.Modifiers == key.Modifiers);

    internal IReadOnlyList<ShortcutDiagnostic> ShortcutDiagnostics => _shortcutDiagnostics;

    internal IReadOnlyCollection<ExtensionShortcutInfo> ActiveExtensionShortcuts =>
        _activeExtensionShortcuts.Values.ToArray();

    internal ExtensionShortcutInfo? MatchExtensionShortcut(ConsoleKeyInfo key) =>
        key.Key != ConsoleKey.NoName &&
        _activeExtensionShortcuts.TryGetValue(new PiSharpShortcutKey(key.Key, key.Modifiers), out var shortcut)
            ? shortcut
            : null;

    internal void SetExtensionShortcuts(IReadOnlyList<ExtensionShortcutInfo> shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        _registeredExtensionShortcuts = shortcuts.ToArray();
        ResolveExtensionShortcuts();
    }

    public string? GetDisplayKeys(string action) =>
        _bindings.TryGetValue(action, out var bindings) && bindings.Length > 0
            ? string.Join("/", bindings.Select(binding => string.Join("+", binding.Name.Split('+')
                .Select(part => part.ToLowerInvariant() switch
                {
                    "ctrl" => "Ctrl",
                    "shift" => "Shift",
                    "alt" => "Alt",
                    "esc" or "escape" => "Escape",
                    "pageup" => "PageUp",
                    "pagedown" => "PageDown",
                    _ => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]
                }))))
            : null;

    public string? MatchIdleApplicationAction(ConsoleKeyInfo key)
    {
        foreach (var action in IdleApplicationActions)
            if (Matches(action, key)) return action;
        return null;
    }

    public bool MatchesEditorAction(ConsoleKeyInfo key) => _bindings.Any(pair =>
        (pair.Key.StartsWith("tui.input.", StringComparison.Ordinal) ||
         pair.Key.StartsWith("tui.editor.", StringComparison.Ordinal)) &&
        pair.Value.Any(binding => binding.Stroke.Key == key.Key && binding.Stroke.Modifiers == key.Modifiers));

    public void Reload()
    {
        var next = _definitions.ToDictionary(pair => pair.Key,
            pair => ParseBindings(pair.Value.DefaultKeys), StringComparer.Ordinal);
        if (_path is null || !File.Exists(_path))
        {
            SetBindings(next);
            return;
        }

        try
        {
            var info = new FileInfo(_path);
            if (info.Length > 256 * 1024)
            {
                SetBindings(next);
                return;
            }
            var json = File.ReadAllText(_path).TrimStart('\uFEFF');
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                SetBindings(next);
                return;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!_definitions.ContainsKey(property.Name)) continue;
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    next[property.Name] = ParseBindings([property.Value.GetString()!]);
                }
                else if (property.Value.ValueKind == JsonValueKind.Array && property.Value.EnumerateArray()
                    .All(item => item.ValueKind == JsonValueKind.String))
                {
                    next[property.Name] = ParseBindings(property.Value.EnumerateArray().Select(item => item.GetString()!).ToArray());
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            next = _definitions.ToDictionary(pair => pair.Key,
                pair => ParseBindings(pair.Value.DefaultKeys), StringComparer.Ordinal);
        }
        SetBindings(next);
    }

    public string FormatHotkeys()
    {
        var lines = _definitions.Select(pair =>
        {
            var keys = _bindings[pair.Key];
            var display = keys.Length == 0 ? "disabled" : string.Join(", ", keys.Select(key => key.Name));
            return $"{display,-32} {pair.Value.Description} ({pair.Key})";
        }).ToList();
        if (_activeExtensionShortcuts.Count > 0)
        {
            lines.Add("");
            lines.Add("Extensions:");
            lines.AddRange(_activeExtensionShortcuts.Select(pair =>
                $"{FormatKeyDisplay(pair.Key),-32} {pair.Value.Description ?? pair.Value.SourceInfo.Path}"));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static BoundKey[] ParseBindings(IEnumerable<string> keys)
    {
        var parsed = new List<BoundKey>();
        foreach (var key in keys)
            if (PiSharpShortcutKey.TryParse(key, out var stroke)) parsed.Add(new(stroke, key));
        return parsed.ToArray();
    }

    private void SetBindings(Dictionary<string, BoundKey[]> bindings)
    {
        _bindings = bindings;
        ResolveExtensionShortcuts();
    }

    private void ResolveExtensionShortcuts()
    {
        var builtIns = new Dictionary<PiSharpShortcutKey, BuiltInBinding>();
        foreach (var (action, bindings) in _bindings)
        {
            // Context-local picker and tree actions do not compete with global editor shortcuts.
            if (action.StartsWith("app.models.", StringComparison.Ordinal) ||
                action.StartsWith("app.tree.", StringComparison.Ordinal) ||
                action is "app.session.togglePath" or "app.session.toggleSort" or
                    "app.session.toggleNamedFilter" or "app.session.rename" or
                    "app.session.delete" or "app.session.deleteNoninvasive") continue;
            var isReserved = s_reservedShortcutActions.Contains(action);
            foreach (var binding in bindings)
            {
                if (builtIns.TryGetValue(binding.Stroke, out var existing) && existing.IsReserved && !isReserved)
                    continue;
                builtIns[binding.Stroke] = new(action, isReserved);
            }
        }

        var active = new Dictionary<PiSharpShortcutKey, ExtensionShortcutInfo>();
        var diagnostics = new List<ShortcutDiagnostic>();
        foreach (var shortcut in _registeredExtensionShortcuts)
        {
            if (builtIns.TryGetValue(shortcut.Shortcut, out var builtIn))
            {
                if (builtIn.IsReserved)
                {
                    diagnostics.Add(new(shortcut.SourceInfo.Path,
                        $"Extension shortcut '{shortcut.Shortcut}' from {shortcut.SourceInfo.Path} conflicts with built-in shortcut. Skipping."));
                    continue;
                }
                diagnostics.Add(new(shortcut.SourceInfo.Path,
                    $"Extension shortcut conflict: '{shortcut.Shortcut}' is built-in shortcut for {builtIn.Action} and {shortcut.SourceInfo.Path}. Using {shortcut.SourceInfo.Path}."));
            }

            if (active.TryGetValue(shortcut.Shortcut, out var existing))
            {
                diagnostics.Add(new(shortcut.SourceInfo.Path,
                    $"Extension shortcut conflict: '{shortcut.Shortcut}' registered by both {existing.SourceInfo.Path} and {shortcut.SourceInfo.Path}. Using {shortcut.SourceInfo.Path}."));
            }
            active[shortcut.Shortcut] = shortcut;
        }
        _activeExtensionShortcuts = active;
        _shortcutDiagnostics = diagnostics;
    }

    private static string FormatKeyDisplay(PiSharpShortcutKey shortcut) =>
        string.Join("+", shortcut.ToString().Split('+').Select(part => part.ToLowerInvariant() switch
        {
            "ctrl" => "Ctrl",
            "shift" => "Shift",
            "alt" => "Alt",
            "esc" => "Escape",
            "pageup" => "PageUp",
            "pagedown" => "PageDown",
            _ => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]
        }));

    private static bool IsWindowsBindings() => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() &&
        (Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is not null ||
         Environment.GetEnvironmentVariable("WSL_INTEROP") is not null);
}
