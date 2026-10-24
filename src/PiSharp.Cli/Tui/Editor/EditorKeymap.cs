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
        "app.thinking.toggle", "app.editor.external", "app.message.copy", "app.message.followUp",
        "tui.input.submit", "tui.select.confirm", "tui.select.cancel", "tui.input.copy",
        "tui.editor.deleteToLineEnd"
    };
    private static readonly string[] IdleApplicationActions = ["app.suspend", "app.thinking.cycle", "app.thinking.toggle", "app.model.cycleForward", "app.model.cycleBackward", "app.model.select", "app.tools.expand", "app.editor.external", "app.message.copy", "app.clipboard.pasteImage", "app.settings.open", "app.session.fork", "app.session.resume"];

    private static readonly IReadOnlyDictionary<string, Definition> s_definitions = new Dictionary<string, Definition>(StringComparer.Ordinal)
    {
        ["tui.input.submit"] = new("Submit input", ["enter"]),
        ["tui.input.newLine"] = new("Insert a new line", ["shift+enter", "ctrl+j"]),
        ["tui.input.tab"] = new("Complete input", ["tab", "shift+tab"]),
        ["tui.editor.cursorUp"] = new("Move up or browse older history", ["up"]),
        ["tui.editor.cursorDown"] = new("Move down or browse newer history", ["down"]),
        ["tui.editor.historyPrevious"] = new("Browse older prompt history", []),
        ["tui.editor.historyNext"] = new("Browse newer prompt history", []),
        ["tui.editor.undo"] = new("Undo the last editor change",
            OperatingSystem.IsWindows() ? ["ctrl+z"] : IsWindowsBindings() ? ["alt+z"] : ["ctrl+-"]),
        ["tui.editor.cursorLeft"] = new("Move cursor left", ["left", "ctrl+b"]),
        ["tui.editor.cursorRight"] = new("Move cursor right", ["right", "ctrl+f"]),
        ["tui.editor.cursorWordLeft"] = new("Move cursor left by word", ["alt+left", "ctrl+left", "alt+b"]),
        ["tui.editor.cursorWordRight"] = new("Move cursor right by word", ["alt+right", "ctrl+right", "alt+f"]),
        ["tui.editor.cursorLineStart"] = new("Move cursor to line start", ["home", "ctrl+home", "ctrl+a"]),
        ["tui.editor.cursorLineEnd"] = new("Move cursor to line end", ["end", "ctrl+end", "ctrl+e"]),
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
        ["app.model.cycleBackward"] = new("Cycle to previous model", IsWindowsBindings() ? ["alt+p"] : ["ctrl+shift+p", "alt+p"]),
        ["app.model.select"] = new("Open model selector", ["ctrl+l"]),
        ["app.editor.external"] = new("Open external editor", ["ctrl+g"]),
        ["app.message.copy"] = new("Copy selection or last assistant message", ["ctrl+x"]),
        ["app.clipboard.pasteImage"] = new("Paste clipboard image or text", IsWindowsBindings() ? ["alt+v"] : ["ctrl+v"]),
        ["app.settings.open"] = new("Open settings", []),
        ["app.session.resume"] = new("Open session selector", []),
        ["app.session.fork"] = new("Open fork selector", []),
        ["app.session.rename"] = new("Rename selected session", ["ctrl+r"]),
        ["app.session.toggleSort"] = new("Toggle session sort mode", ["ctrl+s"]),
        ["app.models.save"] = new("Save scoped model selection", ["ctrl+s"]),
        ["app.models.enableAll"] = new("Enable all scoped models", ["ctrl+a"]),
        ["app.models.clearAll"] = new("Clear scoped models", ["ctrl+x"]),
        ["app.models.toggleProvider"] = new("Toggle all models for this provider", ["ctrl+p"]),
        ["app.models.reorderUp"] = new("Move model earlier in the scope", ["alt+up"]),
        ["app.models.reorderDown"] = new("Move model later in the scope", ["alt+down"]),
        ["app.thinking.cycle"] = new("Cycle thinking level", ["shift+tab"]),
        ["app.thinking.toggle"] = new("Toggle thinking block visibility", ["ctrl+t"]),
        ["app.tools.expand"] = new("Expand or collapse tool output", ["ctrl+o"]),
        ["app.message.followUp"] = new("Queue a follow-up message", IsWindowsBindings() ? ["ctrl+q"] : ["alt+enter"]),
        ["app.message.dequeue"] = new("Restore queued messages", IsWindowsBindings() ? ["alt+q"] : ["alt+up"]),
        ["tui.altScreen.pageUp"] = new("Scroll transcript up one page", ["pageup"]),
        ["tui.altScreen.pageDown"] = new("Scroll transcript down one page", ["pagedown"]),
        ["tui.altScreen.top"] = new("Scroll to the start of the transcript", ["ctrl+home"]),
        ["tui.altScreen.bottom"] = new("Return to the live transcript", ["ctrl+end"]),
        ["tui.altScreen.search"] = new("Search the rendered transcript", IsWindowsBindings() ? ["ctrl+f"] : ["ctrl+shift+f", "ctrl+f"]),
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
    {
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
                    "esc" or "escape" => "Esc",
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
        var next = s_definitions.ToDictionary(pair => pair.Key,
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
                if (!s_definitions.ContainsKey(property.Name)) continue;
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
            next = s_definitions.ToDictionary(pair => pair.Key,
                pair => ParseBindings(pair.Value.DefaultKeys), StringComparer.Ordinal);
        }
        SetBindings(next);
    }

    public string FormatHotkeys()
    {
        var lines = s_definitions.Select(pair =>
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
            if (action.StartsWith("app.models.", StringComparison.Ordinal)) continue;
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
            "esc" => "Esc",
            "pageup" => "PageUp",
            "pagedown" => "PageDown",
            _ => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]
        }));

    private static bool IsWindowsBindings() => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() &&
        (Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is not null ||
         Environment.GetEnvironmentVariable("WSL_INTEROP") is not null);
}
