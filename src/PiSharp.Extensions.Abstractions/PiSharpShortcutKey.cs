namespace PiSharp.Extensions.Abstractions;

/// <summary>A key and modifier combination accepted by PiSharp's interactive terminal keymap.</summary>
public readonly record struct PiSharpShortcutKey
{
    private const ConsoleModifiers SupportedModifiers = ConsoleModifiers.Control | ConsoleModifiers.Shift |
        ConsoleModifiers.Alt;

    /// <summary>Creates a shortcut from a terminal key and its modifiers.</summary>
    public PiSharpShortcutKey(ConsoleKey key, ConsoleModifiers modifiers = ConsoleModifiers.None)
    {
        if (!Enum.IsDefined(key) || key == ConsoleKey.NoName)
            throw new ArgumentOutOfRangeException(nameof(key), key, "A shortcut must use a named terminal key.");
        if ((modifiers & ~SupportedModifiers) != 0)
            throw new ArgumentOutOfRangeException(nameof(modifiers), modifiers,
                "Shortcuts support only Ctrl, Shift, and Alt modifiers.");

        Key = key;
        Modifiers = modifiers;
    }

    /// <summary>The terminal key pressed by the shortcut.</summary>
    public ConsoleKey Key { get; }

    /// <summary>The Ctrl, Shift, and Alt modifiers required by the shortcut.</summary>
    public ConsoleModifiers Modifiers { get; }

    /// <summary>Whether this value represents a usable key and supported modifier combination.</summary>
    public bool IsValid => Key != default && Enum.IsDefined(Key) && Key != ConsoleKey.NoName &&
        (Modifiers & ~SupportedModifiers) == 0;

    /// <summary>Parses PiSharp keymap syntax such as <c>ctrl+shift+p</c> or <c>f8</c>.</summary>
    public static PiSharpShortcutKey Parse(string value) =>
        TryParse(value, out var shortcut)
            ? shortcut
            : throw new ArgumentException($"Invalid PiSharp shortcut key: {value}", nameof(value));

    /// <summary>Tries to parse PiSharp keymap syntax.</summary>
    public static bool TryParse(string? value, out PiSharpShortcutKey shortcut)
    {
        shortcut = default;
        if (string.IsNullOrEmpty(value)) return false;

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
        shortcut = new(key, modifiers | impliedModifiers);
        return true;
    }

    /// <summary>Returns the canonical PiSharp keymap spelling for this combination.</summary>
    public override string ToString()
    {
        var modifiers = new List<string>(3);
        if (Modifiers.HasFlag(ConsoleModifiers.Control)) modifiers.Add("ctrl");
        if (Modifiers.HasFlag(ConsoleModifiers.Alt)) modifiers.Add("alt");
        if (Modifiers.HasFlag(ConsoleModifiers.Shift)) modifiers.Add("shift");
        modifiers.Add(GetKeyName(Key, Modifiers));
        return string.Join('+', modifiers);
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
        if (normalized.Length is 2 or 3 && normalized[0] == 'f' &&
            int.TryParse(normalized.AsSpan(1), out var functionKey) && functionKey is >= 1 and <= 12)
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

    private static string GetKeyName(ConsoleKey key, ConsoleModifiers modifiers)
    {
        var shifted = modifiers.HasFlag(ConsoleModifiers.Shift);
        var name = key switch
        {
            ConsoleKey.Escape => "esc",
            ConsoleKey.Enter => "enter",
            ConsoleKey.Spacebar => "space",
            ConsoleKey.UpArrow => "up",
            ConsoleKey.DownArrow => "down",
            ConsoleKey.LeftArrow => "left",
            ConsoleKey.RightArrow => "right",
            ConsoleKey.Oem3 => shifted ? "~" : "`",
            ConsoleKey.OemMinus => shifted ? "_" : "-",
            ConsoleKey.OemPlus => shifted ? "+" : "=",
            ConsoleKey.Oem4 => shifted ? "{" : "[",
            ConsoleKey.Oem6 => shifted ? "}" : "]",
            ConsoleKey.Oem5 => shifted ? "|" : "\\",
            ConsoleKey.Oem1 => shifted ? ":" : ";",
            ConsoleKey.Oem7 => "'",
            ConsoleKey.OemComma => shifted ? "<" : ",",
            ConsoleKey.OemPeriod => shifted ? ">" : ".",
            ConsoleKey.Oem2 => shifted ? "?" : "/",
            >= ConsoleKey.D0 and <= ConsoleKey.D9 => shifted
                ? key switch
                {
                    ConsoleKey.D0 => ")",
                    ConsoleKey.D1 => "!",
                    ConsoleKey.D2 => "@",
                    ConsoleKey.D3 => "#",
                    ConsoleKey.D4 => "$",
                    ConsoleKey.D5 => "%",
                    ConsoleKey.D6 => "^",
                    ConsoleKey.D7 => "&",
                    ConsoleKey.D8 => "*",
                    ConsoleKey.D9 => "(",
                    _ => key.ToString()
                }
                : ((int)key - (int)ConsoleKey.D0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            >= ConsoleKey.A and <= ConsoleKey.Z => key.ToString().ToLowerInvariant(),
            _ => key.ToString().ToLowerInvariant()
        };
        return name;
    }
}
