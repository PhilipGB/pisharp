using System.Text.Json;
using PiSharp.Runtime.Resources;

namespace PiSharp.Cli.Tui;

internal sealed record TerminalThemeDiagnostic(string Message, string Path);

/// <summary>Discovers and loads built-in, user, and trusted-project Pi-compatible theme files.</summary>
internal sealed class TerminalThemeCatalog
{
    private static readonly string[] s_builtinNames = ["dark", "light"];
    private readonly string? _projectThemeDirectory;
    private readonly string? _userThemeDirectory;
    private readonly string? _projectBaseDirectory;
    private readonly string _userBaseDirectory;
    private readonly IReadOnlyList<string>? _userThemePaths;
    private readonly IReadOnlyList<string>? _projectThemePaths;
    private readonly bool _discoverThemes;
    private readonly IReadOnlyList<string>? _explicitThemePaths;
    private readonly string _explicitThemeBaseDirectory;
    private readonly TerminalColorMode _mode;
    private readonly Func<string, string?> _environment;

    public IReadOnlyList<TerminalThemeDiagnostic> Diagnostics => GetDiagnostics();

    public TerminalThemeCatalog(string agentDirectory, string? trustedProjectDirectory = null,
        Func<string, string?>? environment = null, bool? trueColorOverride = null,
        IReadOnlyList<string>? userThemePaths = null, IReadOnlyList<string>? projectThemePaths = null,
        bool discoverThemes = true, IReadOnlyList<string>? explicitThemePaths = null,
        string? explicitThemeBaseDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDirectory);
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _mode = trueColorOverride switch
        {
            true => TerminalColorMode.TrueColor,
            false => TerminalColorMode.Ansi256,
            null => TerminalColorModeExtensions.Detect(_environment)
        };
        _userBaseDirectory = Path.GetFullPath(agentDirectory);
        _userThemeDirectory = Path.Combine(_userBaseDirectory, "themes");
        _userThemePaths = userThemePaths;
        _projectThemePaths = trustedProjectDirectory is null ? null : projectThemePaths;
        _discoverThemes = discoverThemes;
        _explicitThemePaths = explicitThemePaths;
        _explicitThemeBaseDirectory = Path.GetFullPath(explicitThemeBaseDirectory ?? Environment.CurrentDirectory);
        _projectBaseDirectory = trustedProjectDirectory is null
            ? null
            : Path.Combine(Path.GetFullPath(trustedProjectDirectory), ".pi");
        _projectThemeDirectory = trustedProjectDirectory is null
            ? null
            : Path.Combine(_projectBaseDirectory!, "themes");
    }

    public IReadOnlyList<string> GetAvailableNames()
    {
        var names = new HashSet<string>(s_builtinNames, StringComparer.Ordinal);
        foreach (var path in ThemeFiles())
        {
            try { names.Add(ReadTheme(path).Name); }
            catch (Exception error) when (IsThemeLoadError(error)) { }
        }
        return ["system", .. names.Order(StringComparer.Ordinal)];
    }

    public TerminalTheme Load(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/'))
            throw new InvalidDataException("Theme names must be nonempty and cannot contain '/'.");
        if (name == "system") return TerminalSystemTheme.Create(_mode, new(), DetectAppearance(_environment));
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
        return Resolve(themeSetting, new(terminalForeground, terminalBackground));
    }

    public TerminalTheme Resolve(string? themeSetting, TerminalColorState terminalColors)
    {
        ArgumentNullException.ThrowIfNull(terminalColors);
        var appearance = TerminalSystemTheme.DetectAppearance(terminalColors, _environment("COLORFGBG"));
        if (string.IsNullOrWhiteSpace(themeSetting) || themeSetting == "system")
            return TerminalSystemTheme.Create(_mode, terminalColors, appearance);
        var slash = themeSetting.IndexOf('/');
        var name = slash < 0
            ? themeSetting
            : themeSetting.IndexOf('/', slash + 1) < 0
                ? (appearance == "light" ? themeSetting[..slash] : themeSetting[(slash + 1)..]).Trim()
                : "";
        if (name.Length == 0) throw new InvalidDataException($"Invalid theme setting '{themeSetting}'.");
        return Load(name).WithTerminalColors(terminalColors.Foreground, terminalColors.Background, appearance);
    }

    internal static TerminalTheme LoadBuiltIn(string name, TerminalColorMode mode,
        Func<string, string?>? environment = null)
    {
        if (!s_builtinNames.Contains(name, StringComparer.Ordinal)) throw new ArgumentOutOfRangeException(nameof(name));
        return TerminalTheme.FromEmbedded(name, mode, environment);
    }

    internal static string DetectAppearance(Func<string, string?> environment) =>
        TerminalSystemTheme.DetectColorFgBg(environment("COLORFGBG")) ?? "dark";

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
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (_discoverThemes)
        {
            if (_projectBaseDirectory is not null)
            {
                foreach (var path in ConfiguredThemeFiles(_projectThemePaths, _projectBaseDirectory))
                    if (seen.Add(path)) yield return path;
                foreach (var path in ThemeFilesIn(_projectThemeDirectory, recursive: false))
                    if (seen.Add(path) && LocalResourcePathRules.IsEnabledByOverrides(path, _projectThemePaths, _projectBaseDirectory))
                        yield return path;
            }
            foreach (var path in ConfiguredThemeFiles(_userThemePaths, _userBaseDirectory))
                if (seen.Add(path)) yield return path;
            foreach (var path in ThemeFilesIn(_userThemeDirectory, recursive: false))
                if (seen.Add(path) && LocalResourcePathRules.IsEnabledByOverrides(path, _userThemePaths, _userBaseDirectory))
                    yield return path;
        }
        foreach (var entry in _explicitThemePaths ?? [])
        {
            var path = Path.GetFullPath(entry, _explicitThemeBaseDirectory);
            foreach (var file in ThemeFilesIn(path, recursive: true))
                if (seen.Add(file)) yield return file;
        }
    }

    private IReadOnlyList<TerminalThemeDiagnostic> GetDiagnostics()
    {
        var diagnostics = new List<TerminalThemeDiagnostic>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        AddConfiguredPaths(_userThemePaths, _userBaseDirectory);
        if (_projectBaseDirectory is not null)
            AddConfiguredPaths(_projectThemePaths, _projectBaseDirectory);
        foreach (var entry in _explicitThemePaths ?? [])
        {
            var path = Path.GetFullPath(entry, _explicitThemeBaseDirectory);
            if (seen.Add(path)) AddPathDiagnostic(path);
        }
        return diagnostics;

        void AddConfiguredPaths(IReadOnlyList<string>? entries, string baseDirectory)
        {
            foreach (var entry in LocalResourcePathRules.GetPaths(entries))
            {
                var path = LocalResourcePathRules.ResolvePath(entry, baseDirectory);
                if (!LocalResourcePathRules.IsEnabledByOverrides(path, entries, baseDirectory) || !seen.Add(path))
                    continue;
                AddPathDiagnostic(path);
            }
        }

        void AddPathDiagnostic(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                diagnostics.Add(new("theme path does not exist", path));
            else if (File.Exists(path) && !Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new("theme path is not a json file", path));
        }
    }

    private static IEnumerable<string> ConfiguredThemeFiles(IReadOnlyList<string>? entries, string baseDirectory)
    {
        var candidates = LocalResourcePathRules.GetPaths(entries)
            .SelectMany(entry => ThemeFilesIn(LocalResourcePathRules.ResolvePath(entry, baseDirectory), recursive: true))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return LocalResourcePathRules.ApplyOverrides(candidates, entries, baseDirectory);
    }

    private static IReadOnlyList<string> ThemeFilesIn(string? path, bool recursive)
    {
        if (path is null) return [];
        if (File.Exists(path))
            return Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ? [Path.GetFullPath(path)] : [];
        if (!Directory.Exists(path)) return [];
        try
        {
            return Directory.EnumerateFiles(path, "*.json", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(file => !Path.GetFileName(file).StartsWith(".", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).Take(1000).Select(Path.GetFullPath).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
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
