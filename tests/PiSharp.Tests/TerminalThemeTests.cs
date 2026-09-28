using System.Text.Json.Nodes;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalThemeTests
{
    [Fact]
    public void BuiltInPalettesResolvePiTokensInTrueColorMode()
    {
        var dark = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var light = TerminalThemeCatalog.LoadBuiltIn("light", TerminalColorMode.TrueColor);

        Assert.Equal("\u001b[38;2;240;198;116m", dark.Fg("mdHeading"));
        Assert.Equal("\u001b[38;2;154;115;38m", light.Fg("mdHeading"));
        Assert.Equal("\u001b[48;2;58;58;74m", dark.Bg("selectedBg"));
        Assert.Equal("dark", dark.Appearance);
        Assert.Equal("light", light.Appearance);
    }

    [Fact]
    public void ThemeFilesResolveVariablesOklchAndIndexedColors()
    {
        var root = BuiltIn("nebula");
        root["vars"]!["heading"] = "#123456";
        root["colors"]!["mdHeading"] = "heading";
        root["colors"]!["accent"] = 200;
        root["colors"]!["success"] = "oklch(100% 0.3 150)";

        var theme = TerminalTheme.Parse("nebula", root.ToJsonString(), TerminalColorMode.TrueColor, _ => null);

        Assert.Equal("\u001b[38;2;18;52;86m", theme.Fg("mdHeading"));
        Assert.Equal("\u001b[38;5;200m", theme.Fg("accent"));
        Assert.Equal("\u001b[38;2;255;255;255m", theme.Fg("success"));
    }

    [Fact]
    public void ThemeFilesResolveOkhslColorsLikePi()
    {
        var root = BuiltIn("nebula");
        root["colors"]!["success"] = "okhsl(231 68% 55%)";

        var theme = TerminalTheme.Parse("nebula", root.ToJsonString(), TerminalColorMode.TrueColor, _ => null);

        Assert.Equal("\u001b[38;2;59;142;180m", theme.Fg("success"));
    }

    [Fact]
    public void SystemThemeIsAvailableAsARealTheme()
    {
        var root = TempRoot();
        try
        {
            var catalog = new TerminalThemeCatalog(root, environment: _ => null);
            var theme = catalog.Resolve("system");

            Assert.Equal("system", theme.Name);
            Assert.Equal("dark", theme.Appearance);
            Assert.Equal("\u001b[39m", theme.Fg("text"));
            Assert.StartsWith("\u001b[38;5;", theme.Fg("error"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SystemThemeMatchesThePinnedPiPaletteAndReadabilityVector()
    {
        var root = TempRoot();
        try
        {
            var palette = new[]
            {
                "21222c", "ff5555", "50fa7b", "f1fa8c", "bd93f9", "ff79c6", "8be9fd", "f8f8f2",
                "6272a4", "ff6e6e", "69ff94", "ffffa5", "d6acff", "ff92df", "a4ffff", "ffffff"
            }.Select(HexColor).ToArray();
            var background = HexColor("282a36");
            var foreground = HexColor("f8f8f2");
            var colors = new TerminalColorState(foreground, background, palette);
            var catalog = new TerminalThemeCatalog(root, environment: _ => null, trueColorOverride: true);

            var theme = catalog.Resolve("system", colors);

            Assert.Equal("dark", theme.Appearance);
            Assert.Equal(new TerminalTheme.Rgb(249, 117, 111), theme.GetConcreteColor("error"));
            Assert.Equal(new TerminalTheme.Rgb(69, 35, 108), theme.GetConcreteColor("selectedBg"));
            Assert.Equal(foreground, theme.GetConcreteColor("text"));
            Assert.True(TerminalSystemTheme.Contrast(theme.GetConcreteColor("text"), background) >= 4.5);
            Assert.True(TerminalSystemTheme.Contrast(theme.GetConcreteColor("text"), theme.GetConcreteColor("selectedBg")) >= 4.5);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SystemThemeUsesReportedAppearanceThenColorFgBgThenDarkFallback()
    {
        var root = TempRoot();
        try
        {
            var catalog = new TerminalThemeCatalog(root, environment: key => key == "COLORFGBG" ? "15;0" : null);

            Assert.Equal("dark", catalog.Resolve("system").Appearance);
            Assert.Equal("light", catalog.Resolve("system", new(AppearanceReport: "light")).Appearance);
            Assert.StartsWith("\u001b[38;5;1m", catalog.Resolve("system", new(AppearanceReport: "light")).Fg("error"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SystemThemeMatchesPinnedPiLightAndBackgroundOnlyFixtures()
    {
        var root = TempRoot();
        try
        {
            var catalog = new TerminalThemeCatalog(root, environment: _ => null, trueColorOverride: true);
            var light = catalog.Resolve("system", new(
                HexColor("657b83"), HexColor("fdf6e3")));
            var backgroundOnly = catalog.Resolve("system", new(
                Background: HexColor("1e1e1e")));

            Assert.Equal("light", light.Appearance);
            Assert.Equal(HexColor("4a5a60"), light.GetConcreteColor("text"));
            Assert.Equal(HexColor("c6253c"), light.GetConcreteColor("error"));
            Assert.Equal(HexColor("dfe7eb"), light.GetConcreteColor("selectedBg"));
            Assert.Equal("dark", backgroundOnly.Appearance);
            Assert.Equal(HexColor("dddfe0"), backgroundOnly.GetConcreteColor("text"));
            Assert.Equal(HexColor("eb777a"), backgroundOnly.GetConcreteColor("error"));
            Assert.True(TerminalSystemTheme.Contrast(backgroundOnly.GetConcreteColor("text"), HexColor("1e1e1e")) >= 4.5);
            Assert.True(TerminalSystemTheme.Contrast(backgroundOnly.GetConcreteColor("text"), backgroundOnly.GetConcreteColor("selectedBg")) >= 4.5);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissingAndCircularThemeVariablesAreRejected()
    {
        var missing = BuiltIn("missing-var");
        missing["colors"]!["mdHeading"] = "unknown";
        Assert.Throws<InvalidDataException>(() => TerminalTheme.Parse("missing-var", missing.ToJsonString(), TerminalColorMode.TrueColor, _ => null));

        var circular = BuiltIn("circular-var");
        circular["vars"]!["first"] = "second";
        circular["vars"]!["second"] = "first";
        circular["colors"]!["mdHeading"] = "first";
        Assert.Throws<InvalidDataException>(() => TerminalTheme.Parse("circular-var", circular.ToJsonString(), TerminalColorMode.TrueColor, _ => null));
    }

    [Fact]
    public void CatalogDiscoversByThemeContentAndUsesProjectThemeBeforeUserTheme()
    {
        var root = TempRoot();
        var agent = Path.Combine(root, "agent");
        var project = Path.Combine(root, "workspace");
        var userThemePath = Path.Combine(agent, "themes", "not-the-name.json");
        var projectThemePath = Path.Combine(project, ".pi", "themes", "ocean.json");
        Directory.CreateDirectory(Path.GetDirectoryName(userThemePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(projectThemePath)!);
        var userTheme = BuiltIn("ocean");
        userTheme["colors"]!["mdHeading"] = "#111111";
        var projectTheme = BuiltIn("ocean");
        projectTheme["colors"]!["mdHeading"] = "#222222";
        File.WriteAllText(userThemePath, userTheme.ToJsonString());
        File.WriteAllText(projectThemePath, projectTheme.ToJsonString());
        File.WriteAllText(Path.Combine(agent, "themes", "invalid.json"), "{ not json");

        try
        {
            var catalog = new TerminalThemeCatalog(agent, project, key => key == "COLORTERM" ? "truecolor" : null);
            Assert.Contains("ocean", catalog.GetAvailableNames());
            Assert.DoesNotContain("not-the-name", catalog.GetAvailableNames());
            Assert.Equal("\u001b[38;2;34;34;34m", catalog.Load("ocean").Fg("mdHeading"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ConfiguredThemePathsUseScopedBasesPatternsAndProjectPrecedence()
    {
        var root = TempRoot();
        var agent = Path.Combine(root, "agent");
        var project = Path.Combine(root, "workspace");
        var userCustom = Path.Combine(agent, "custom-themes");
        var userAuto = Path.Combine(agent, "themes");
        var projectCustom = Path.Combine(project, ".pi", "custom-themes");
        Directory.CreateDirectory(userCustom);
        Directory.CreateDirectory(userAuto);
        Directory.CreateDirectory(projectCustom);
        try
        {
            var userOcean = BuiltIn("ocean");
            userOcean["colors"]!["mdHeading"] = "#111111";
            File.WriteAllText(Path.Combine(userCustom, "custom.json"), userOcean.ToJsonString());
            var projectOcean = BuiltIn("ocean");
            projectOcean["colors"]!["mdHeading"] = "#222222";
            File.WriteAllText(Path.Combine(projectCustom, "custom.json"), projectOcean.ToJsonString());
            File.WriteAllText(Path.Combine(userAuto, "keep.json"), BuiltIn("force-kept").ToJsonString());
            File.WriteAllText(Path.Combine(userAuto, "deny.json"), BuiltIn("force-denied").ToJsonString());

            IReadOnlyList<string> userPaths = ["custom-themes", "+themes/keep.json", "+themes/deny.json",
                "!themes/*.json", "-themes/deny.json"];
            IReadOnlyList<string> projectPaths = ["custom-themes", "!themes/blocked/**"];
            var trusted = new TerminalThemeCatalog(agent, project,
                key => key == "COLORTERM" ? "truecolor" : null,
                userThemePaths: userPaths, projectThemePaths: projectPaths);
            Assert.Equal("\u001b[38;2;34;34;34m", trusted.Load("ocean").Fg("mdHeading"));
            var trustedNames = trusted.GetAvailableNames();
            Assert.Contains("force-kept", trustedNames);
            Assert.DoesNotContain("force-denied", trustedNames);

            var untrusted = new TerminalThemeCatalog(agent, userThemePaths: userPaths, projectThemePaths: projectPaths,
                environment: key => key == "COLORTERM" ? "truecolor" : null);
            Assert.Equal("\u001b[38;2;17;17;17m", untrusted.Load("ocean").Fg("mdHeading"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ThemePairTracksTerminalAppearanceAndNoColorIsRespected()
    {
        var root = TempRoot();
        try
        {
            var catalog = new TerminalThemeCatalog(root, environment: key => key switch
            {
                "COLORTERM" => "truecolor",
                "COLORFGBG" => "0;15",
                _ => null
            });
            Assert.Equal("light", catalog.Resolve("light/dark").Name);
            Assert.Equal("light", catalog.Resolve("light/dark", terminalBackground: new TerminalTheme.Rgb(255, 255, 255)).Name);
            Assert.Equal("dark", catalog.Resolve("light/dark", terminalBackground: new TerminalTheme.Rgb(0, 0, 0)).Name);

            var noColor = new TerminalThemeCatalog(root, environment: key => key == "NO_COLOR" ? "1" : null);
            Assert.Equal("\u001b[39m", noColor.Resolve("dark").Fg("mdHeading"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TrueColorDetectionMatchesTerminalHintsAndExplicitOverride()
    {
        Assert.Equal(TerminalColorMode.TrueColor,
            TerminalColorModeExtensions.Detect(key => key == "TERM_PROGRAM" ? "kitty" : null));
        Assert.Equal(TerminalColorMode.TrueColor,
            TerminalColorModeExtensions.Detect(key => key == "TERM" ? "xterm-direct" : null));
        Assert.Equal(TerminalColorMode.Ansi256,
            TerminalColorModeExtensions.Detect(key => key == "PI_TRUE_COLOR" ? "0" : null));
        Assert.Equal(TerminalColorMode.None,
            TerminalColorModeExtensions.Detect(key => key == "NO_COLOR" ? "1" : null));
    }

    [Fact]
    public void CatalogTrueColorOverrideAppliesToBuiltInAndCustomThemes()
    {
        var root = TempRoot();
        try
        {
            var themeDirectory = Path.Combine(root, "themes");
            Directory.CreateDirectory(themeDirectory);
            File.WriteAllText(Path.Combine(themeDirectory, "ocean.json"), BuiltIn("ocean").ToJsonString());

            var enabled = new TerminalThemeCatalog(root, environment: key => key == "TERM" ? "dumb" : null,
                trueColorOverride: true);
            Assert.StartsWith("\u001b[38;2;", enabled.Resolve("dark").Fg("mdHeading"));
            Assert.StartsWith("\u001b[38;2;", enabled.Load("ocean").Fg("mdHeading"));

            var disabled = new TerminalThemeCatalog(root, environment: key => key == "COLORTERM" ? "truecolor" : null,
                trueColorOverride: false);
            Assert.StartsWith("\u001b[38;5;", disabled.Resolve("dark").Fg("mdHeading"));
            Assert.StartsWith("\u001b[38;5;", disabled.Load("ocean").Fg("mdHeading"));

            var automatic = new TerminalThemeCatalog(root, environment: key => key == "TERM_PROGRAM" ? "kitty" : null);
            Assert.StartsWith("\u001b[38;2;", automatic.Resolve("dark").Fg("mdHeading"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EmptyTokensUseTerminalDefaultsAndExposeConcreteAppearanceColors()
    {
        var root = BuiltIn("terminal-defaults");
        root["appearance"] = "light";
        root["colors"]!["text"] = "";
        var theme = TerminalTheme.Parse("terminal-defaults", root.ToJsonString(), TerminalColorMode.TrueColor, _ => null);

        Assert.Equal("\u001b[39m", theme.Fg("text"));
        Assert.Equal(new TerminalTheme.Rgb(0, 0, 0), theme.GetConcreteColor("text"));

        root["colors"]!["userMessageBg"] = "";
        var reported = TerminalTheme.Parse("reported-defaults", root.ToJsonString(), TerminalColorMode.TrueColor,
            key => key == "COLORFGBG" ? "7;0" : null);
        Assert.Equal(new TerminalTheme.Rgb(0, 0, 0), reported.GetConcreteColor("text"));
        Assert.Equal(new TerminalTheme.Rgb(255, 255, 255), reported.GetConcreteColor("userMessageBg"));
    }

    private static JsonObject BuiltIn(string name)
    {
        var resource = typeof(TerminalTheme).Assembly.GetManifestResourceStream("PiSharp.Cli.Tui.Themes.dark.json");
        Assert.NotNull(resource);
        using (resource)
        using (var reader = new StreamReader(resource!))
        {
            var theme = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
            theme["name"] = name;
            return theme;
        }
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-themes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static TerminalTheme.Rgb HexColor(string value) => new(
        byte.Parse(value[..2], System.Globalization.NumberStyles.HexNumber),
        byte.Parse(value[2..4], System.Globalization.NumberStyles.HexNumber),
        byte.Parse(value[4..6], System.Globalization.NumberStyles.HexNumber));
}
