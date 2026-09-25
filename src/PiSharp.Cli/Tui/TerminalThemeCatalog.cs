using System.Text.Json;

namespace PiSharp.Cli.Tui;

/// <summary>Discovers and loads built-in, user, and trusted-project Pi-compatible theme files.</summary>
internal sealed class TerminalThemeCatalog
{
    private static readonly string[] s_builtinNames = ["dark", "light"];
    private readonly string? _projectThemeDirectory;
    private readonly string? _userThemeDirectory;
    private readonly TerminalColorMode _mode;
    private readonly Func<string, string?> _environment;

    public TerminalThemeCatalog(string agentDirectory, string? trustedProjectDirectory = null,
        Func<string, string?>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDirectory);
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _mode = TerminalColorModeExtensions.Detect(_environment);
        _userThemeDirectory = Path.Combine(Path.GetFullPath(agentDirectory), "themes");
        _projectThemeDirectory = trustedProjectDirectory is null
            ? null
            : Path.Combine(Path.GetFullPath(trustedProjectDirectory), ".pi", "themes");
    }

    public IReadOnlyList<string> GetAvailableNames()
    {
        var names = new HashSet<string>(s_builtinNames, StringComparer.Ordinal);
        foreach (var path in ThemeFiles())
        {
            try { names.Add(ReadTheme(path).Name); }
            catch (Exception error) when (IsThemeLoadError(error)) { }
        }
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    public TerminalTheme Load(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/'))
            throw new InvalidDataException("Theme names must be nonempty and cannot contain '/'.");
        foreach (var path in ThemeFiles())
        {
            try
            {
                var theme = ReadTheme(path);
                if (theme.Name.Equals(name, StringComparison.Ordinal)) return theme;
            }
            catch (Exception error) when (IsThemeLoadError(error))
            {
                // Invalid custom themes do not hide valid themes with another name.
            }
        }
        if (s_builtinNames.Contains(name, StringComparer.Ordinal)) return LoadBuiltIn(name, _mode, _environment);
        throw new FileNotFoundException($"Theme '{name}' was not found in built-in, user, or trusted project themes.");
    }

    public TerminalTheme Resolve(string? themeSetting, TerminalTheme.Rgb? terminalForeground = null,
        TerminalTheme.Rgb? terminalBackground = null)
    {
        var appearance = terminalBackground is { } background && background.Luminance >= 0.5
            ? "light"
            : terminalBackground is not null ? "dark" : DetectAppearance(_environment);
        if (string.IsNullOrWhiteSpace(themeSetting))
            return LoadBuiltIn(appearance, _mode, _environment).WithTerminalColors(terminalForeground, terminalBackground);
        var slash = themeSetting.IndexOf('/');
        var name = slash < 0
            ? themeSetting
            : themeSetting.IndexOf('/', slash + 1) < 0
                ? (appearance == "light" ? themeSetting[..slash] : themeSetting[(slash + 1)..]).Trim()
                : "";
        if (name.Length == 0) throw new InvalidDataException($"Invalid theme setting '{themeSetting}'.");
        return Load(name).WithTerminalColors(terminalForeground, terminalBackground);
    }

    internal static TerminalTheme LoadBuiltIn(string name, TerminalColorMode mode,
        Func<string, string?>? environment = null)
    {
        if (!s_builtinNames.Contains(name, StringComparer.Ordinal)) throw new ArgumentOutOfRangeException(nameof(name));
        return TerminalTheme.FromEmbedded(name, mode, environment);
    }

    internal static string DetectAppearance(Func<string, string?> environment)
    {
        var background = BackgroundFromEnvironment(environment);
        return background is { } color && color.Luminance >= 0.5 ? "light" : "dark";
    }

    internal static TerminalTheme.Rgb? BackgroundFromEnvironment(Func<string, string?> environment)
    {
        var colorfgbg = environment("COLORFGBG");
        if (string.IsNullOrWhiteSpace(colorfgbg)) return null;
        foreach (var part in colorfgbg.Split(';').Reverse())
        {
            if (int.TryParse(part.Trim(), out var index) && index is >= 0 and <= 255)
                return IndexedRgb(index);
        }
        return null;
    }

    internal static TerminalTheme.Rgb? ForegroundFromEnvironment(Func<string, string?> environment)
    {
        var colorfgbg = environment("COLORFGBG");
        if (string.IsNullOrWhiteSpace(colorfgbg)) return null;
        foreach (var part in colorfgbg.Split(';'))
        {
            if (int.TryParse(part.Trim(), out var index) && index is >= 0 and <= 255)
                return IndexedRgb(index);
        }
        return null;
    }

    private IEnumerable<string> ThemeFiles()
    {
        foreach (var directory in new[] { _projectThemeDirectory, _userThemeDirectory })
        {
            if (directory is null || !Directory.Exists(directory)) continue;
            string[] files;
            try { files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly); }
            catch (Exception error) when (IsThemeLoadError(error)) { continue; }
            foreach (var path in files.Order(StringComparer.Ordinal)) yield return path;
        }
    }

    private TerminalTheme ReadTheme(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 64 * 1024) throw new InvalidDataException($"Theme '{path}' exceeds 64KB.");
        return TerminalTheme.Parse(path, File.ReadAllText(path), _mode, _environment);
    }

    private static TerminalTheme.Rgb IndexedRgb(int index)
    {
        ReadOnlySpan<TerminalTheme.Rgb> basic =
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

    private static bool IsThemeLoadError(Exception error) => error is IOException or UnauthorizedAccessException or
        InvalidDataException or JsonException or ArgumentException or FormatException or OverflowException;
}
