using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Tui;

/// <summary>A validated Pi theme palette prepared for terminal output.</summary>
internal sealed class TerminalTheme
{
    private static readonly string[] s_foregroundTokens =
    [
        "accent", "border", "borderAccent", "borderMuted", "success", "error", "warning", "muted", "dim", "text", "thinkingText",
        "scrollbarTrack", "scrollbarThumb", "searchMatchText", "userMessageText", "customMessageText", "customMessageLabel",
        "toolTitle", "toolOutput", "mdHeading", "mdLink", "mdLinkUrl", "mdCode", "mdCodeBlock", "mdCodeBlockBorder", "mdQuote",
        "mdQuoteBorder", "mdHr", "mdListBullet", "toolDiffAdded", "toolDiffRemoved", "toolDiffContext", "syntaxComment",
        "syntaxKeyword", "syntaxFunction", "syntaxVariable", "syntaxString", "syntaxNumber", "syntaxType", "syntaxOperator",
        "syntaxPunctuation", "thinkingOff", "thinkingMinimal", "thinkingLow", "thinkingMedium", "thinkingHigh", "thinkingXhigh",
        "thinkingMax", "bashMode"
    ];

    private static readonly string[] s_backgroundTokens =
    ["selectedBg", "searchMatchBg", "userMessageBg", "customMessageBg", "toolPendingBg", "toolSuccessBg", "toolErrorBg"];

    private static readonly string[] s_requiredForegroundTokens =
    [
        "accent", "border", "borderAccent", "borderMuted", "success", "error", "warning", "muted", "dim", "text", "thinkingText",
        "userMessageText", "customMessageText", "customMessageLabel", "toolTitle", "toolOutput", "mdHeading", "mdLink", "mdLinkUrl",
        "mdCode", "mdCodeBlock", "mdCodeBlockBorder", "mdQuote", "mdQuoteBorder", "mdHr", "mdListBullet", "toolDiffAdded",
        "toolDiffRemoved", "toolDiffContext", "syntaxComment", "syntaxKeyword", "syntaxFunction", "syntaxVariable", "syntaxString",
        "syntaxNumber", "syntaxType", "syntaxOperator", "syntaxPunctuation", "thinkingOff", "thinkingMinimal", "thinkingLow",
        "thinkingMedium", "thinkingHigh", "thinkingXhigh", "bashMode"
    ];

    private static readonly string[] s_requiredBackgroundTokens =
    ["selectedBg", "userMessageBg", "customMessageBg", "toolPendingBg", "toolSuccessBg", "toolErrorBg"];

    private static readonly Regex s_hexColor = new("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.CultureInvariant);
    private static readonly Regex s_oklch = new(
        "^oklch\\(\\s*([+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:e[+-]?\\d+)?)(%)?\\s+([+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:e[+-]?\\d+)?)\\s+([+-]?(?:\\d+(?:\\.\\d*)?|\\.\\d+)(?:e[+-]?\\d+)?)(?:deg)?\\s*\\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Lazy<TerminalTheme> s_default = new(() => TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorModeExtensions.Detect(Environment.GetEnvironmentVariable)));

    private readonly IReadOnlyDictionary<string, string> _foreground;
    private readonly IReadOnlyDictionary<string, string> _background;
    private readonly IReadOnlyDictionary<string, Rgb> _concrete;
    private readonly Dictionary<string, ColorValue> _palette;
    private readonly Rgb? _terminalForeground;
    private readonly Rgb? _terminalBackground;
    private readonly string? _declaredAppearance;

    private TerminalTheme(string name, string? declaredAppearance, TerminalColorMode mode,
        Dictionary<string, ColorValue> colors, Rgb? terminalForeground, Rgb? terminalBackground)
    {
        Name = name;
        Mode = mode;
        _declaredAppearance = declaredAppearance;
        _palette = new(colors, StringComparer.Ordinal);
        _terminalForeground = terminalForeground;
        _terminalBackground = terminalBackground;
        var backgroundTokens = s_backgroundTokens.ToHashSet(StringComparer.Ordinal);
        var ansi = new Dictionary<string, string>(StringComparer.Ordinal);
        var concrete = new Dictionary<string, Rgb>(StringComparer.Ordinal);
        var foregroundColors = new List<Rgb>();
        var backgroundColors = new List<Rgb>();
        foreach (var (token, value) in colors)
        {
            var isBackground = backgroundTokens.Contains(token);
            ansi[token] = value.IsDefault
                ? $"\u001b[{(isBackground ? 49 : 39)}m"
                : value.ToAnsi(mode, isBackground);
            if (!value.IsDefault)
            {
                var rgb = value.AsRgb();
                concrete[token] = rgb;
                if (!value.IsIndexed || value.Index >= 16)
                    (isBackground ? backgroundColors : foregroundColors).Add(rgb);
            }
        }
        _foreground = s_foregroundTokens.ToDictionary(token => token, token => ansi[token], StringComparer.Ordinal);
        _background = s_backgroundTokens.ToDictionary(token => token, token => ansi[token], StringComparer.Ordinal);
        _concrete = concrete;

        Appearance = declaredAppearance ?? DetectAppearance(foregroundColors, backgroundColors);
    }

    public static TerminalTheme Default => s_default.Value;
    public string Name { get; }
    public string Appearance { get; }
    internal TerminalColorMode Mode { get; }
    internal Rgb? TerminalForeground => _terminalForeground;
    internal Rgb? TerminalBackground => _terminalBackground;

    internal TerminalTheme WithTerminalColors(Rgb? foreground, Rgb? background) =>
        new(Name, _declaredAppearance, Mode, new(_palette, StringComparer.Ordinal),
            foreground ?? _terminalForeground, background ?? _terminalBackground);

    internal string Fg(string token) => _foreground.TryGetValue(token, out var value)
        ? value
        : throw new ArgumentOutOfRangeException(nameof(token), token, "Unknown foreground theme token.");

    internal string Bg(string token) => _background.TryGetValue(token, out var value)
        ? value
        : throw new ArgumentOutOfRangeException(nameof(token), token, "Unknown background theme token.");

    internal string Style(string token, string text, bool background = false, bool bold = false,
        bool dim = false, bool italic = false, bool underline = false, bool strikethrough = false)
    {
        var output = new System.Text.StringBuilder();
        if (bold) output.Append("\u001b[1m");
        if (dim) output.Append("\u001b[2m");
        if (italic) output.Append("\u001b[3m");
        if (underline) output.Append("\u001b[4m");
        if (strikethrough) output.Append("\u001b[9m");
        output.Append(background ? Bg(token) : Fg(token)).Append(text).Append("\u001b[0m");
        return output.ToString();
    }

    internal Rgb GetConcreteColor(string token)
    {
        if (_concrete.TryGetValue(token, out var color)) return color;
        var isBackground = s_backgroundTokens.Contains(token, StringComparer.Ordinal);
        if (isBackground && _terminalBackground is { } background) return background;
        if (!isBackground && _terminalForeground is { } foreground) return foreground;
        return Appearance == "light"
            ? (isBackground ? new(255, 255, 255) : new(0, 0, 0))
            : (isBackground ? new(0, 0, 0) : new(229, 229, 231));
    }

    internal static TerminalTheme Parse(string label, string json, TerminalColorMode mode,
        Func<string, string?>? environment = null)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Theme '{label}' must be a JSON object.");
        var rootNames = new HashSet<string>(StringComparer.Ordinal);
        string? name = null;
        string? appearance = null;
        JsonElement colorsElement = default;
        JsonElement varsElement = default;
        foreach (var property in root.EnumerateObject())
        {
            if (!rootNames.Add(property.Name)) throw new InvalidDataException($"Theme '{label}' contains duplicate property '{property.Name}'.");
            switch (property.Name)
            {
                case "$schema":
                    if (property.Value.ValueKind != JsonValueKind.String) throw Invalid(label, "$schema must be a string.");
                    break;
                case "name":
                    name = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    break;
                case "appearance":
                    appearance = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                    if (appearance is not ("dark" or "light")) throw Invalid(label, "appearance must be 'dark' or 'light'.");
                    break;
                case "colors": colorsElement = property.Value; break;
                case "vars": varsElement = property.Value; break;
                case "export":
                    if (property.Value.ValueKind != JsonValueKind.Object) throw Invalid(label, "export must be an object.");
                    break;
                default: throw Invalid(label, $"unsupported property '{property.Name}'.");
            }
        }
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Contains('/') || name.Any(char.IsControl))
            throw Invalid(label, "name must be a nonempty string of at most 128 characters and cannot contain '/'.");
        if (colorsElement.ValueKind != JsonValueKind.Object) throw Invalid(label, "colors must be an object.");

        var vars = ReadValues(label, varsElement, "vars", optional: true);
        var rawColors = ReadValues(label, colorsElement, "colors", optional: false);
        var allowed = s_foregroundTokens.Concat(s_backgroundTokens).ToHashSet(StringComparer.Ordinal);
        foreach (var token in rawColors.Keys)
            if (!allowed.Contains(token)) throw Invalid(label, $"unknown color token '{token}'.");

        foreach (var token in s_requiredForegroundTokens.Concat(s_requiredBackgroundTokens))
            if (!rawColors.ContainsKey(token)) throw Invalid(label, $"missing required color token '{token}'.");
        AddFallback(rawColors, "scrollbarTrack", "muted");
        AddFallback(rawColors, "scrollbarThumb", "text");
        AddFallback(rawColors, "thinkingMax", "thinkingXhigh");
        AddFallback(rawColors, "searchMatchBg", "selectedBg");
        AddFallback(rawColors, "searchMatchText", "text");

        var resolved = new Dictionary<string, ColorValue>(StringComparer.Ordinal);
        foreach (var (token, value) in rawColors)
            resolved[token] = Resolve(label, value, vars, new HashSet<string>(StringComparer.Ordinal));

        var env = environment ?? Environment.GetEnvironmentVariable;
        var detectedForeground = TerminalThemeCatalog.ForegroundFromEnvironment(env);
        var detectedBackground = TerminalThemeCatalog.BackgroundFromEnvironment(env);
        return new(name, appearance, mode, resolved, detectedForeground, detectedBackground);
    }

    internal static TerminalTheme FromEmbedded(string name, TerminalColorMode mode,
        Func<string, string?>? environment = null)
    {
        var resourceName = $"PiSharp.Cli.Tui.Themes.{name}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded theme resource '{resourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return Parse(name, reader.ReadToEnd(), mode, environment);
    }

    private string DetectAppearance(IReadOnlyList<Rgb> foregroundColors, IReadOnlyList<Rgb> backgroundColors)
    {
        var foregroundLightness = AverageOklabLightness(foregroundColors);
        var backgroundLightness = AverageOklabLightness(backgroundColors);
        if (foregroundLightness is { } fg && backgroundLightness is { } bg) return bg < fg ? "dark" : "light";
        if (backgroundLightness is { } onlyBackground) return onlyBackground < 0.5 ? "dark" : "light";
        if (foregroundLightness is { } onlyForeground) return onlyForeground > 0.5 ? "dark" : "light";
        return _terminalBackground is { } background && background.Luminance >= 0.5 ? "light" : "dark";
    }

    private static double? AverageOklabLightness(IReadOnlyList<Rgb> colors)
    {
        if (colors.Count == 0) return null;
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return colors.Average(color =>
        {
            var red = Linear(color.R);
            var green = Linear(color.G);
            var blue = Linear(color.B);
            var l = Math.Cbrt(0.4122214708 * red + 0.5363325363 * green + 0.0514459929 * blue);
            var m = Math.Cbrt(0.2119034982 * red + 0.6806995451 * green + 0.1073969566 * blue);
            var s = Math.Cbrt(0.0883024619 * red + 0.2817188376 * green + 0.6299787005 * blue);
            return 0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s;
        });
    }

    private static Dictionary<string, ColorValue> ReadValues(string label, JsonElement element, string path, bool optional)
    {
        var result = new Dictionary<string, ColorValue>(StringComparer.Ordinal);
        if (optional && element.ValueKind == JsonValueKind.Undefined) return result;
        if (element.ValueKind != JsonValueKind.Object) throw Invalid(label, $"{path} must be an object.");
        foreach (var property in element.EnumerateObject())
        {
            if (result.ContainsKey(property.Name)) throw Invalid(label, $"{path} contains duplicate key '{property.Name}'.");
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => ColorValue.ParseString(property.Value.GetString()!),
                JsonValueKind.Number when property.Value.TryGetInt32(out var index) && index is >= 0 and <= 255 => ColorValue.Indexed(index),
                _ => throw Invalid(label, $"{path}.{property.Name} must be a color string or an ANSI index from 0 to 255.")
            };
        }
        return result;
    }

    private static void AddFallback(Dictionary<string, ColorValue> colors, string optional, string fallback)
    {
        if (!colors.ContainsKey(optional)) colors[optional] = colors[fallback];
    }

    private static ColorValue Resolve(string label, ColorValue value, Dictionary<string, ColorValue> vars, HashSet<string> visited)
    {
        if (!value.IsReference) return value;
        if (!visited.Add(value.Reference!)) throw Invalid(label, $"circular variable reference '{value.Reference}'.");
        if (!vars.TryGetValue(value.Reference!, out var referenced)) throw Invalid(label, $"variable '{value.Reference}' was not found.");
        return Resolve(label, referenced, vars, visited);
    }

    private static InvalidDataException Invalid(string label, string message) => new($"Invalid theme '{label}': {message}");

    private readonly record struct ColorValue(bool IsDefault, bool IsReference, string? Reference, bool IsIndexed, int Index, Rgb Rgb)
    {
        public static ColorValue Indexed(int index) => new(false, false, null, true, index, default);

        public static ColorValue ParseString(string value)
        {
            if (value.Length == 0) return new(true, false, null, false, 0, default);
            if (s_hexColor.IsMatch(value))
            {
                var digits = value[1..];
                if (digits.Length == 3) digits = string.Concat(digits.Select(digit => new string(digit, 2)));
                return new(false, false, null, false, 0, new(
                    byte.Parse(digits[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(digits[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(digits[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            }
            var match = s_oklch.Match(value);
            if (match.Success)
            {
                var lightness = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) / (match.Groups[2].Success ? 100 : 1);
                var chroma = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                var hue = double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                if (!double.IsFinite(lightness) || !double.IsFinite(chroma) || !double.IsFinite(hue) || lightness is < 0 or > 1 || chroma < 0)
                    throw new InvalidDataException($"Invalid OKLCH color '{value}'.");
                return new(false, false, null, false, 0, FromOklch(lightness, chroma, hue));
            }
            return new(false, true, value, false, 0, default);
        }

        public Rgb AsRgb() => IsDefault ? default : IsIndexed ? IndexedRgb(Index) : Rgb;

        public string ToAnsi(TerminalColorMode mode, bool background)
        {
            var slot = background ? 48 : 38;
            if (mode == TerminalColorMode.None) return background ? "\u001b[49m" : "\u001b[39m";
            if (IsIndexed) return $"\u001b[{slot};5;{Index}m";
            if (mode == TerminalColorMode.TrueColor) return $"\u001b[{slot};2;{Rgb.R};{Rgb.G};{Rgb.B}m";
            return $"\u001b[{slot};5;{Rgb.ToAnsi256()}m";
        }

        private static Rgb FromOklch(double l, double c, double h)
        {
            var radians = h * Math.PI / 180;
            var cosine = Math.Cos(radians);
            var sine = Math.Sin(radians);
            (double R, double G, double B) Convert(double chroma)
            {
                var a = chroma * cosine;
                var b = chroma * sine;
                var lr = Math.Pow(l + 0.3963377774 * a + 0.2158037573 * b, 3);
                var mr = Math.Pow(l - 0.1055613458 * a - 0.0638541728 * b, 3);
                var sr = Math.Pow(l - 0.0894841775 * a - 1.291485548 * b, 3);
                return (4.0767416621 * lr - 3.3077115913 * mr + 0.2309699292 * sr,
                    -1.2684380046 * lr + 2.6097574011 * mr - 0.3413193965 * sr,
                    -0.0041960863 * lr - 0.7034186147 * mr + 1.707614701 * sr);
            }
            var rgb = Convert(c);
            bool InGamut((double R, double G, double B) value) => value.R is >= -1e-7 and <= 1.0000001 &&
                value.G is >= -1e-7 and <= 1.0000001 && value.B is >= -1e-7 and <= 1.0000001;
            if (!InGamut(rgb))
            {
                var low = 0d;
                var high = c;
                rgb = Convert(0);
                for (var step = 0; step < 20; step++)
                {
                    var mid = (low + high) / 2;
                    var candidate = Convert(mid);
                    if (InGamut(candidate)) { low = mid; rgb = candidate; }
                    else high = mid;
                }
            }
            static byte Channel(double value)
            {
                var srgb = value <= 0.0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;
                return (byte)Math.Round(Math.Clamp(srgb, 0, 1) * 255);
            }
            return new(Channel(rgb.R), Channel(rgb.G), Channel(rgb.B));
        }
    }

    internal readonly record struct Rgb(byte R, byte G, byte B)
    {
        public double Luminance
        {
            get
            {
                static double Linear(byte channel)
                {
                    var value = channel / 255d;
                    return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
                }
                return 0.2126 * Linear(R) + 0.7152 * Linear(G) + 0.0722 * Linear(B);
            }
        }

        public int ToAnsi256()
        {
            ReadOnlySpan<byte> levels = [0, 95, 135, 175, 215, 255];
            static int Nearest(ReadOnlySpan<byte> values, byte target)
            {
                var index = 0;
                var distance = int.MaxValue;
                for (var i = 0; i < values.Length; i++)
                {
                    var next = Math.Abs(values[i] - target);
                    if (next < distance) { index = i; distance = next; }
                }
                return index;
            }
            var r = Nearest(levels, R);
            var g = Nearest(levels, G);
            var b = Nearest(levels, B);
            var cube = new Rgb(levels[r], levels[g], levels[b]);
            var gray = (byte)Math.Clamp((int)Math.Round(0.299 * R + 0.587 * G + 0.114 * B), 0, 255);
            var grayIndex = Math.Clamp((int)Math.Round((gray - 8) / 10d), 0, 23);
            var grayValue = (byte)(8 + grayIndex * 10);
            var spread = Math.Max(R, Math.Max(G, B)) - Math.Min(R, Math.Min(G, B));
            var cubeDistance = Distance(this, cube);
            var grayDistance = Distance(this, new(grayValue, grayValue, grayValue));
            return spread < 10 && grayDistance < cubeDistance ? 232 + grayIndex : 16 + 36 * r + 6 * g + b;
        }

        private static double Distance(Rgb first, Rgb second)
        {
            var dr = first.R - second.R;
            var dg = first.G - second.G;
            var db = first.B - second.B;
            return 0.299 * dr * dr + 0.587 * dg * dg + 0.114 * db * db;
        }
    }

    private static Rgb IndexedRgb(int index)
    {
        ReadOnlySpan<Rgb> basic =
        [new(0, 0, 0), new(128, 0, 0), new(0, 128, 0), new(128, 128, 0), new(0, 0, 128), new(128, 0, 128), new(0, 128, 128), new(192, 192, 192),
         new(128, 128, 128), new(255, 0, 0), new(0, 255, 0), new(255, 255, 0), new(0, 0, 255), new(255, 0, 255), new(0, 255, 255), new(255, 255, 255)];
        if (index < 16) return basic[index];
        if (index < 232)
        {
            ReadOnlySpan<byte> levels = [0, 95, 135, 175, 215, 255];
            var cube = index - 16;
            return new(levels[cube / 36], levels[(cube % 36) / 6], levels[cube % 6]);
        }
        var gray = (byte)(8 + (index - 232) * 10);
        return new(gray, gray, gray);
    }
}

internal enum TerminalColorMode { None, Ansi256, TrueColor }

internal static class TerminalColorModeExtensions
{
    public static TerminalColorMode Detect(Func<string, string?> environment)
    {
        if (!string.IsNullOrEmpty(environment("NO_COLOR")) || string.Equals(environment("TERM"), "dumb", StringComparison.OrdinalIgnoreCase))
            return TerminalColorMode.None;
        var forced = environment("PI_TRUE_COLOR");
        if (forced == "1") return TerminalColorMode.TrueColor;
        if (forced == "0") return TerminalColorMode.Ansi256;

        var colorTerm = environment("COLORTERM")?.ToLowerInvariant() ?? "";
        var termProgram = environment("TERM_PROGRAM")?.ToLowerInvariant() ?? "";
        var emulator = environment("TERMINAL_EMULATOR")?.ToLowerInvariant() ?? "";
        var term = environment("TERM")?.ToLowerInvariant() ?? "";
        var trueColorHint = colorTerm is "truecolor" or "24bit" || term.EndsWith("-direct", StringComparison.Ordinal);
        if (Present(environment, "TMUX") || term.StartsWith("tmux", StringComparison.Ordinal) ||
            term.StartsWith("screen", StringComparison.Ordinal))
            return trueColorHint ? TerminalColorMode.TrueColor : TerminalColorMode.Ansi256;

        if (Present(environment, "KITTY_WINDOW_ID") || termProgram is "kitty" or "ghostty" or "wezterm" or "warpterminal" or "iterm.app" or
            "alacritty" or "vscode" or "zed" || term.Contains("ghostty", StringComparison.Ordinal) ||
            Present(environment, "GHOSTTY_RESOURCES_DIR") || Present(environment, "WEZTERM_PANE") ||
            Present(environment, "WARP_SESSION_ID") || Present(environment, "WARP_TERMINAL_SESSION_UUID") ||
            Present(environment, "ITERM_SESSION_ID") || Present(environment, "WT_SESSION") ||
            emulator == "jetbrains-jediterm" || OperatingSystem.IsWindows())
            return TerminalColorMode.TrueColor;
        return trueColorHint ? TerminalColorMode.TrueColor : TerminalColorMode.Ansi256;
    }

    private static bool Present(Func<string, string?> environment, string key) =>
        !string.IsNullOrWhiteSpace(environment(key));
}
