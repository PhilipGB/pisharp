using System.Text.Json;

namespace PiSharp.Cli.Tui;

public sealed class EditorKeymap
{
    private sealed record Definition(string Description, string[] DefaultKeys);
    private readonly record struct KeyStroke(ConsoleKey Key, ConsoleModifiers Modifiers);
    private sealed record BoundKey(KeyStroke Stroke, string Name);

    private static readonly IReadOnlyDictionary<string, Definition> s_definitions = new Dictionary<string, Definition>(StringComparer.Ordinal)
    {
        ["tui.input.submit"] = new("Submit input", ["enter"]),
        ["tui.input.newLine"] = new("Insert a new line", ["shift+enter", "ctrl+j"]),
        ["tui.input.tab"] = new("Complete input", ["tab"]),
        ["tui.editor.cursorUp"] = new("Move up or browse older history", ["up"]),
        ["tui.editor.cursorDown"] = new("Move down or browse newer history", ["down"]),
        ["tui.editor.cursorLeft"] = new("Move cursor left", ["left", "ctrl+b"]),
        ["tui.editor.cursorRight"] = new("Move cursor right", ["right", "ctrl+f"]),
        ["tui.editor.cursorWordLeft"] = new("Move cursor left by word", ["alt+left", "ctrl+left", "alt+b"]),
        ["tui.editor.cursorWordRight"] = new("Move cursor right by word", ["alt+right", "ctrl+right", "alt+f"]),
        ["tui.editor.cursorLineStart"] = new("Move cursor to line start", ["home", "ctrl+home", "ctrl+a"]),
        ["tui.editor.cursorLineEnd"] = new("Move cursor to line end", ["end", "ctrl+end", "ctrl+e"]),
        ["tui.editor.deleteCharBackward"] = new("Delete backward", ["backspace"]),
        ["tui.editor.deleteCharForward"] = new("Delete forward", ["delete", "ctrl+d"]),
        ["tui.editor.deleteWordBackward"] = new("Delete previous word", ["ctrl+w", "alt+backspace"]),
        ["tui.editor.deleteWordForward"] = new("Delete next word", ["alt+d", "alt+delete"]),
        ["tui.editor.deleteToLineStart"] = new("Delete to line start", ["ctrl+u"]),
        ["tui.editor.deleteToLineEnd"] = new("Delete to line end", ["ctrl+k"]),
        ["app.interrupt"] = new("Cancel or abort", ["escape"]),
        ["app.clear"] = new("Clear editor", ["ctrl+c"]),
        ["app.exit"] = new("Exit when editor is empty", ["ctrl+d"]),
        ["app.message.followUp"] = new("Queue a follow-up message", IsWindowsBindings() ? ["ctrl+q"] : ["alt+enter"]),
        ["app.message.dequeue"] = new("Restore queued messages", IsWindowsBindings() ? ["alt+q"] : ["alt+up"])
    };

    private readonly string? _path;
    private Dictionary<string, BoundKey[]> _bindings = new(StringComparer.Ordinal);

    public EditorKeymap(string? agentDirectory = null)
    {
        _path = string.IsNullOrWhiteSpace(agentDirectory) ? null : Path.Combine(Path.GetFullPath(agentDirectory), "keybindings.json");
        Reload();
    }

    public bool Matches(string action, ConsoleKeyInfo key) =>
        _bindings.TryGetValue(action, out var bindings) && bindings.Any(binding =>
            binding.Stroke.Key == key.Key && binding.Stroke.Modifiers == key.Modifiers);

    public void Reload()
    {
        var next = s_definitions.ToDictionary(pair => pair.Key,
            pair => ParseBindings(pair.Value.DefaultKeys), StringComparer.Ordinal);
        if (_path is null || !File.Exists(_path))
        {
            _bindings = next;
            return;
        }

        try
        {
            var info = new FileInfo(_path);
            if (info.Length > 256 * 1024)
            {
                _bindings = next;
                return;
            }
            var json = File.ReadAllText(_path).TrimStart('\uFEFF');
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                _bindings = next;
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
        _bindings = next;
    }

    public string FormatHotkeys()
    {
        return string.Join(Environment.NewLine, s_definitions.Select(pair =>
        {
            var keys = _bindings[pair.Key];
            var display = keys.Length == 0 ? "disabled" : string.Join(", ", keys.Select(key => key.Name));
            return $"{display,-32} {pair.Value.Description} ({pair.Key})";
        }));
    }

    private static BoundKey[] ParseBindings(IEnumerable<string> keys)
    {
        var parsed = new List<BoundKey>();
        foreach (var key in keys)
            if (TryParse(key, out var stroke)) parsed.Add(new(stroke, key));
        return parsed.ToArray();
    }

    private static bool TryParse(string value, out KeyStroke stroke)
    {
        stroke = default;
        var parts = value.Split('+');
        var modifiers = ConsoleModifiers.None;
        var index = 0;
        while (index < parts.Length - 1)
        {
            var modifier = parts[index].ToLowerInvariant();
            var flag = modifier switch
            {
                "ctrl" => ConsoleModifiers.Control,
                "shift" => ConsoleModifiers.Shift,
                "alt" => ConsoleModifiers.Alt,
                _ => (ConsoleModifiers?)null
            };
            if (flag is null) break;
            if (modifiers.HasFlag(flag.Value)) return false;
            modifiers |= flag.Value;
            index++;
        }
        var keyName = string.Join('+', parts.Skip(index));
        if (!TryParseKey(keyName, out var key, out var impliedModifiers)) return false;
        stroke = new(key, modifiers | impliedModifiers);
        return true;
    }

    private static bool TryParseKey(string name, out ConsoleKey key, out ConsoleModifiers modifiers)
    {
        modifiers = ConsoleModifiers.None;
        var normalized = name.ToLowerInvariant();
        var special = normalized switch
        {
            "esc" or "escape" => nameof(ConsoleKey.Escape),
            "enter" or "return" => nameof(ConsoleKey.Enter),
            "tab" => nameof(ConsoleKey.Tab),
            "space" => nameof(ConsoleKey.Spacebar),
            "backspace" => nameof(ConsoleKey.Backspace),
            "delete" => nameof(ConsoleKey.Delete),
            "insert" => nameof(ConsoleKey.Insert),
            "clear" => nameof(ConsoleKey.Clear),
            "home" => nameof(ConsoleKey.Home),
            "end" => nameof(ConsoleKey.End),
            "pageup" => nameof(ConsoleKey.PageUp),
            "pagedown" => nameof(ConsoleKey.PageDown),
            "up" => nameof(ConsoleKey.UpArrow),
            "down" => nameof(ConsoleKey.DownArrow),
            "left" => nameof(ConsoleKey.LeftArrow),
            "right" => nameof(ConsoleKey.RightArrow),
            _ => null
        };
        if (special is not null) return Enum.TryParse(special, out key);

        if (name.Length == 1 && char.IsAsciiLetter(name[0]))
            return Enum.TryParse(name, ignoreCase: true, out key);
        if (name.Length == 1 && char.IsAsciiDigit(name[0]))
            return Enum.TryParse("D" + name, out key);
        if (normalized.Length is 2 or 3 && normalized[0] == 'f' && int.TryParse(normalized.AsSpan(1), out var functionKey) &&
            functionKey is >= 1 and <= 12)
            return Enum.TryParse(name, ignoreCase: true, out key);

        var symbol = name switch
        {
            "`" => (ConsoleKey.Oem3, false),
            "-" => (ConsoleKey.OemMinus, false),
            "=" => (ConsoleKey.OemPlus, false),
            "[" => (ConsoleKey.Oem4, false),
            "]" => (ConsoleKey.Oem6, false),
            "\\" => (ConsoleKey.Oem5, false),
            ";" => (ConsoleKey.Oem1, false),
            "'" => (ConsoleKey.Oem7, false),
            "," => (ConsoleKey.OemComma, false),
            "." => (ConsoleKey.OemPeriod, false),
            "/" => (ConsoleKey.Oem2, false),
            "!" => (ConsoleKey.D1, true),
            "@" => (ConsoleKey.D2, true),
            "#" => (ConsoleKey.D3, true),
            "$" => (ConsoleKey.D4, true),
            "%" => (ConsoleKey.D5, true),
            "^" => (ConsoleKey.D6, true),
            "&" => (ConsoleKey.D7, true),
            "*" => (ConsoleKey.D8, true),
            "(" => (ConsoleKey.D9, true),
            ")" => (ConsoleKey.D0, true),
            "_" => (ConsoleKey.OemMinus, true),
            "+" => (ConsoleKey.OemPlus, true),
            "|" => (ConsoleKey.Oem5, true),
            "~" => (ConsoleKey.Oem3, true),
            "{" => (ConsoleKey.Oem4, true),
            "}" => (ConsoleKey.Oem6, true),
            ":" => (ConsoleKey.Oem1, true),
            "<" => (ConsoleKey.OemComma, true),
            ">" => (ConsoleKey.OemPeriod, true),
            "?" => (ConsoleKey.Oem2, true),
            _ => (ConsoleKey.NoName, false)
        };
        key = symbol.Item1;
        if (key == ConsoleKey.NoName) return false;
        if (symbol.Item2) modifiers |= ConsoleModifiers.Shift;
        return true;
    }

    private static bool IsWindowsBindings() => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() &&
        (Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is not null ||
         Environment.GetEnvironmentVariable("WSL_INTEROP") is not null);
}
