using System.Text.Json;

namespace PiSharp.Core.Settings;

/// <summary>A key or key combination in Pi's "modifier+key" notation (e.g. "ctrl+shift+p").</summary>
public sealed record KeyId(string Value);

/// <summary>A keybinding assignment: one key or an ordered list of alternative keys.</summary>
public sealed class KeybindingValue
{
    private KeybindingValue(IReadOnlyList<string> keys) => Keys = keys;

    /// <summary>Creates a single-key value.</summary>
    public static KeybindingValue Of(string key) => new([key]);

    /// <summary>Creates a multi-key value.</summary>
    public static KeybindingValue Of(IReadOnlyList<string> keys) => new(keys);

    /// <summary>Gets the alternative keys in order.</summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>Gets the primary key (first alternative).</summary>
    public string Primary => Keys.Count > 0 ? Keys[0] : string.Empty;
}

/// <summary>A keybinding definition with default keys and a user-facing description.</summary>
public sealed record KeybindingDefinition(IReadOnlyList<string> DefaultKeys, string Description);

/// <summary>
/// Resolves effective keybindings: Pi's built-in Linux defaults (TUI + application
/// bindings from the pinned keybindings.ts tables) overridden by the user's
/// ~/.pi/agent/keybindings.json, with Pi's legacy keybinding-name migration.
/// </summary>
public sealed class KeybindingsManager
{
    private readonly Dictionary<string, KeybindingValue> _userByKey;
    private readonly string? _configPath;
    private IReadOnlyList<string> _orderedNames;

    private KeybindingsManager(
        IReadOnlyDictionary<string, KeybindingDefinition> defaults,
        IReadOnlyList<string> orderedNames,
        IReadOnlyDictionary<string, KeybindingValue> userBindings,
        string? configPath)
    {
        Defaults = defaults;
        _orderedNames = orderedNames;
        _userByKey = new Dictionary<string, KeybindingValue>(userBindings);
        _configPath = configPath;
    }

    /// <summary>Gets the built-in keybinding definitions (defaults for unresolved bindings).</summary>
    public IReadOnlyDictionary<string, KeybindingDefinition> Defaults { get; }

    /// <summary>Gets the built-in keybinding definitions in canonical display order.</summary>
    public IReadOnlyList<string> KeybindingNames => _orderedNames;

    /// <summary>Creates a manager from the user keybindings file under the agent directory.</summary>
    public static async Task<KeybindingsManager> CreateAsync(
        string? homeDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var path = SettingsPaths.GetKeybindingsPath(homeDirectory);
        var config = await LoadConfigFileAsync(path, cancellationToken);
        return Create(config, path);
    }

    /// <summary>Creates a manager over an in-memory configuration (tests, SDK hosts).</summary>
    public static KeybindingsManager Create(
        IReadOnlyDictionary<string, KeybindingValue>? userBindings = null,
        string? configPath = null)
    {
        var defaults = GetDefaultDefinitions();
        var ordered = BuildOrder(defaults, userBindings?.Keys ?? []);
        return new KeybindingsManager(
            defaults,
            ordered,
            userBindings ?? new Dictionary<string, KeybindingValue>(),
            configPath);
    }

    /// <summary>Re-reads the user keybindings file (Pi's KeybindingsManager.reload).</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (_configPath is null)
        {
            return;
        }
        var config = await LoadConfigFileAsync(_configPath, cancellationToken);
        ReplaceUserBindings(config);
    }

    private void ReplaceUserBindings(IReadOnlyDictionary<string, KeybindingValue> config)
    {
        _userByKey.Clear();
        foreach (var (key, value) in config)
        {
            _userByKey[key] = value;
        }
        RebuildOrder();
    }

    private void RebuildOrder()
    {
        // Order is stable across reloads: canonical defaults first, unknown keys sorted.
        var unknown = _userByKey.Keys
            .Where(key => !Defaults.ContainsKey(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
        _orderedNames = [.. Defaults.Keys, .. unknown];
    }

    /// <summary>Gets the effective keys for a keybinding (user override, else default).</summary>
    public IReadOnlyList<string> GetKeys(string keybinding)
    {
        if (_userByKey.TryGetValue(keybinding, out var user))
        {
            return user.Keys;
        }
        return Defaults.TryGetValue(keybinding, out var definition)
            ? definition.DefaultKeys
            : [];
    }

    /// <summary>Gets the effective keybinding configuration in canonical order.</summary>
    public IReadOnlyDictionary<string, KeybindingValue> GetEffectiveConfig()
    {
        var result = new Dictionary<string, KeybindingValue>();
        foreach (var name in _orderedNames)
        {
            if (_userByKey.TryGetValue(name, out var user))
            {
                result[name] = user;
            }
            else if (Defaults.TryGetValue(name, out var definition))
            {
                result[name] = KeybindingValue.Of(definition.DefaultKeys);
            }
        }
        return result;
    }

    /// <summary>
    /// Reads and migrates a keybindings.json file. Missing or malformed files yield an
    /// empty configuration (Pi's loadRawConfig silently ignores problems).
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, KeybindingValue>> LoadConfigFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, KeybindingValue>();
        }

        try
        {
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            using var document = JsonDocument.Parse(content.TrimStart('\uFEFF'));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, KeybindingValue>();
            }

            var raw = new Dictionary<string, object?>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                raw[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Array when property.Value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String)
                        => property.Value.EnumerateArray().Select(e => e.GetString()!).ToArray(),
                    _ => null,
                };
            }

            var migrated = MigrateLegacyNames(raw);
            var config = new Dictionary<string, KeybindingValue>();
            foreach (var (key, value) in migrated)
            {
                config[key] = value is string single
                    ? KeybindingValue.Of(single)
                    : KeybindingValue.Of((string[])value!);
            }
            return config;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, KeybindingValue>();
        }
    }

    /// <summary>
    /// Maps Pi's legacy flat keybinding names onto the current namespaced names (Pi's
    /// KEYBINDING_NAME_MIGRATIONS). A migrated name never wins over an already-present
    /// current name.
    /// </summary>
    internal static Dictionary<string, object?> MigrateLegacyNames(IReadOnlyDictionary<string, object?> rawConfig)
    {
        var config = new Dictionary<string, object?>();
        foreach (var (key, value) in rawConfig)
        {
            var nextKey = LegacyNameMigrations.GetValueOrDefault(key) ?? key;
            if (nextKey != key && rawConfig.ContainsKey(nextKey))
            {
                continue;
            }
            config[nextKey] = value;
        }
        return config;
    }

    private static readonly Dictionary<string, string> LegacyNameMigrations = new()
    {
        ["cursorUp"] = "tui.editor.cursorUp",
        ["cursorDown"] = "tui.editor.cursorDown",
        ["cursorLeft"] = "tui.editor.cursorLeft",
        ["cursorRight"] = "tui.editor.cursorRight",
        ["cursorWordLeft"] = "tui.editor.cursorWordLeft",
        ["cursorWordRight"] = "tui.editor.cursorWordRight",
        ["cursorLineStart"] = "tui.editor.cursorLineStart",
        ["cursorLineEnd"] = "tui.editor.cursorLineEnd",
        ["jumpForward"] = "tui.editor.jumpForward",
        ["jumpBackward"] = "tui.editor.jumpBackward",
        ["pageUp"] = "tui.editor.pageUp",
        ["pageDown"] = "tui.editor.pageDown",
        ["deleteCharBackward"] = "tui.editor.deleteCharBackward",
        ["deleteCharForward"] = "tui.editor.deleteCharForward",
        ["deleteWordBackward"] = "tui.editor.deleteWordBackward",
        ["deleteWordForward"] = "tui.editor.deleteWordForward",
        ["deleteToLineStart"] = "tui.editor.deleteToLineStart",
        ["deleteToLineEnd"] = "tui.editor.deleteToLineEnd",
        ["yank"] = "tui.editor.yank",
        ["yankPop"] = "tui.editor.yankPop",
        ["undo"] = "tui.editor.undo",
        ["newLine"] = "tui.input.newLine",
        ["submit"] = "tui.input.submit",
        ["tab"] = "tui.input.tab",
        ["copy"] = "tui.input.copy",
        ["selectUp"] = "tui.select.up",
        ["selectDown"] = "tui.select.down",
        ["selectPageUp"] = "tui.select.pageUp",
        ["selectPageDown"] = "tui.select.pageDown",
        ["selectConfirm"] = "tui.select.confirm",
        ["selectCancel"] = "tui.select.cancel",
        ["interrupt"] = "app.interrupt",
        ["clear"] = "app.clear",
        ["exit"] = "app.exit",
        ["suspend"] = "app.suspend",
        ["cycleThinkingLevel"] = "app.thinking.cycle",
        ["cycleModelForward"] = "app.model.cycleForward",
        ["cycleModelBackward"] = "app.model.cycleBackward",
        ["selectModel"] = "app.model.select",
        ["expandTools"] = "app.tools.expand",
        ["toggleThinking"] = "app.thinking.toggle",
        ["toggleSessionNamedFilter"] = "app.session.toggleNamedFilter",
        ["externalEditor"] = "app.editor.external",
        ["followUp"] = "app.message.followUp",
        ["dequeue"] = "app.message.dequeue",
        ["pasteImage"] = "app.clipboard.pasteImage",
        ["newSession"] = "app.session.new",
        ["tree"] = "app.session.tree",
        ["fork"] = "app.session.fork",
        ["resume"] = "app.session.resume",
        ["treeFoldOrUp"] = "app.tree.foldOrUp",
        ["treeUnfoldOrDown"] = "app.tree.unfoldOrDown",
        ["treeEditLabel"] = "app.tree.editLabel",
        ["treeToggleLabelTimestamp"] = "app.tree.toggleLabelTimestamp",
        ["toggleSessionPath"] = "app.session.togglePath",
        ["toggleSessionSort"] = "app.session.toggleSort",
        ["renameSession"] = "app.session.rename",
        ["deleteSession"] = "app.session.delete",
        ["deleteSessionNoninvasive"] = "app.session.deleteNoninvasive",
    };

    private static IReadOnlyList<string> BuildOrder(
        IReadOnlyDictionary<string, KeybindingDefinition> defaults,
        IEnumerable<string> userKeys)
    {
        var ordered = defaults.Keys.ToList();
        var unknown = userKeys
            .Where(key => !defaults.ContainsKey(key))
            .Distinct()
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
        ordered.AddRange(unknown);
        return ordered;
    }

    /// <summary>
    /// Builds Pi's full default keybinding table (TUI editor/input/select/alt-screen plus
    /// the application bindings) for Linux, mirroring pinned keybindings.ts.
    /// </summary>
    public static IReadOnlyDictionary<string, KeybindingDefinition> GetDefaultDefinitions()
    {
        var definitions = new Dictionary<string, KeybindingDefinition>();

        void Add(string name, string[] keys, string description) =>
            definitions[name] = new KeybindingDefinition(keys, description);

        // TUI editor (pi-tui TUI_KEYBINDINGS)
        Add("tui.editor.cursorUp", ["up"], "Move cursor up");
        Add("tui.editor.cursorDown", ["down"], "Move cursor down");
        Add("tui.editor.historyPrevious", [], "Select previous prompt history entry");
        Add("tui.editor.historyNext", [], "Select next prompt history entry");
        Add("tui.editor.cursorLeft", ["left", "ctrl+b"], "Move cursor left");
        Add("tui.editor.cursorRight", ["right", "ctrl+f"], "Move cursor right");
        Add("tui.editor.cursorWordLeft", ["alt+left", "ctrl+left", "alt+b"], "Move cursor word left");
        Add("tui.editor.cursorWordRight", ["alt+right", "ctrl+right", "alt+f"], "Move cursor word right");
        Add("tui.editor.cursorLineStart", ["home", "ctrl+home", "ctrl+a"], "Move to line start");
        Add("tui.editor.cursorLineEnd", ["end", "ctrl+end", "ctrl+e"], "Move to line end");
        Add("tui.editor.jumpForward", ["ctrl+]"], "Jump forward to character");
        Add("tui.editor.jumpBackward", ["ctrl+alt+]"], "Jump backward to character");
        Add("tui.editor.pageUp", ["pageUp", "ctrl+pageUp"], "Page up");
        Add("tui.editor.pageDown", ["pageDown", "ctrl+pageDown"], "Page down");
        Add("tui.editor.deleteCharBackward", ["backspace"], "Delete character backward");
        Add("tui.editor.deleteCharForward", ["delete", "ctrl+d"], "Delete character forward");
        Add("tui.editor.deleteWordBackward", ["ctrl+w", "alt+backspace"], "Delete word backward");
        Add("tui.editor.deleteWordForward", ["alt+d", "alt+delete"], "Delete word forward");
        Add("tui.editor.deleteToLineStart", ["ctrl+u"], "Delete to line start");
        Add("tui.editor.deleteToLineEnd", ["ctrl+k"], "Delete to line end");
        Add("tui.editor.yank", ["ctrl+y"], "Yank");
        Add("tui.editor.yankPop", ["alt+y"], "Yank pop");
        Add("tui.editor.undo", ["ctrl+-"], "Undo");
        Add("tui.input.newLine", ["shift+enter", "ctrl+j"], "Insert newline");
        Add("tui.input.submit", ["enter"], "Submit input");
        Add("tui.input.tab", ["tab"], "Tab / autocomplete");
        Add("tui.input.copy", ["ctrl+c"], "Copy selection");
        Add("tui.select.up", ["up"], "Move selection up");
        Add("tui.select.down", ["down"], "Move selection down");
        Add("tui.select.pageUp", ["pageUp"], "Selection page up");
        Add("tui.select.pageDown", ["pageDown"], "Selection page down");
        Add("tui.select.confirm", ["enter"], "Confirm selection");
        Add("tui.select.cancel", ["escape", "ctrl+c"], "Cancel selection");
        Add("tui.altScreen.pageUp", ["pageUp"], "Scroll viewport up one page");
        Add("tui.altScreen.pageDown", ["pageDown"], "Scroll viewport down one page");
        Add("tui.altScreen.halfPageUp", [], "Scroll viewport up half a page");
        Add("tui.altScreen.halfPageDown", [], "Scroll viewport down half a page");
        Add("tui.altScreen.lineUp", [], "Scroll viewport up one line");
        Add("tui.altScreen.lineDown", [], "Scroll viewport down one line");
        Add("tui.altScreen.previousPrompt", ["ctrl+shift+up", "ctrl+up"], "Jump to previous semantic prompt");
        Add("tui.altScreen.nextPrompt", ["ctrl+shift+down", "ctrl+down"], "Jump to next semantic prompt");
        Add("tui.altScreen.search", ["ctrl+shift+f"], "Search the primary scroll view");
        Add("tui.altScreen.searchNext", ["enter", "ctrl+g"], "Select the next search match");
        Add("tui.altScreen.searchPrevious", ["shift+enter", "ctrl+shift+g"], "Select the previous search match");
        Add("tui.altScreen.searchClose", ["escape"], "Close transcript search");
        Add("tui.altScreen.top", ["home"], "Scroll viewport to top");
        Add("tui.altScreen.bottom", ["end"], "Scroll viewport to bottom");

        // Application bindings (packages/coding-agent keybindings.ts)
        Add("app.interrupt", ["escape"], "Cancel or abort");
        Add("app.clear", ["ctrl+c"], "Clear editor");
        Add("app.exit", ["ctrl+d"], "Exit when editor is empty");
        Add("app.suspend", ["ctrl+z"], "Suspend to background");
        Add("app.thinking.cycle", ["shift+tab"], "Cycle thinking level");
        Add("app.thinking.save", ["ctrl+s"], "Save thinking level");
        Add("app.model.cycleForward", ["ctrl+p"], "Cycle to next model");
        Add("app.model.cycleBackward", ["shift+ctrl+p"], "Cycle to previous model");
        Add("app.model.select", ["ctrl+l"], "Open model selector");
        Add("app.tools.expand", ["ctrl+o"], "Toggle tool output");
        Add("app.thinking.toggle", ["ctrl+t"], "Toggle thinking blocks");
        Add("app.session.toggleNamedFilter", ["ctrl+n"], "Toggle named session filter");
        Add("app.editor.external", ["ctrl+g"], "Open external editor");
        Add("app.message.copy", ["ctrl+x"], "Copy message to clipboard");
        Add("app.message.followUp", ["alt+enter"], "Queue follow-up message");
        Add("app.message.dequeue", ["alt+up"], "Restore queued messages");
        Add("app.clipboard.pasteImage", ["ctrl+v"], "Paste image from clipboard (text fallback)");
        Add("app.session.new", [], "Start a new session");
        Add("app.session.tree", [], "Open session tree");
        Add("app.session.fork", [], "Fork current session");
        Add("app.session.resume", [], "Resume a session");
        Add("app.tree.foldOrUp", ["ctrl+left", "alt+left"], "Fold tree branch or move up");
        Add("app.tree.unfoldOrDown", ["ctrl+right", "alt+right"], "Unfold tree branch or move down");
        Add("app.tree.editLabel", ["shift+l"], "Edit tree label");
        Add("app.tree.toggleLabelTimestamp", ["shift+t"], "Toggle tree label timestamps");
        Add("app.session.togglePath", ["ctrl+p"], "Toggle session path display");
        Add("app.session.toggleSort", ["ctrl+s"], "Toggle session sort mode");
        Add("app.session.rename", ["ctrl+r"], "Rename session");
        Add("app.session.delete", ["ctrl+d"], "Delete session");
        Add("app.session.deleteNoninvasive", ["ctrl+backspace"], "Delete session when query is empty");
        Add("app.models.save", ["ctrl+s"], "Save model selection");
        Add("app.models.enableAll", ["ctrl+a"], "Enable all models");
        Add("app.models.clearAll", ["ctrl+x"], "Clear all models");
        Add("app.models.toggleProvider", ["ctrl+p"], "Toggle all models for provider");
        Add("app.models.reorderUp", ["alt+up"], "Move model up in order");
        Add("app.models.reorderDown", ["alt+down"], "Move model down in order");
        Add("app.tree.filter.default", ["ctrl+d"], "Tree filter: default view");
        Add("app.tree.filter.noTools", ["ctrl+t"], "Tree filter: hide tool results");
        Add("app.tree.filter.userOnly", ["ctrl+u"], "Tree filter: user messages only");
        Add("app.tree.filter.labeledOnly", ["ctrl+l"], "Tree filter: labeled entries only");
        Add("app.tree.filter.all", ["ctrl+a"], "Tree filter: show all entries");
        Add("app.tree.filter.cycleForward", ["ctrl+o"], "Tree filter: cycle forward");
        Add("app.tree.filter.cycleBackward", ["shift+ctrl+o"], "Tree filter: cycle backward");

        return definitions;
    }
}
