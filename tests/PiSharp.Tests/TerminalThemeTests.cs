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
        Assert.Equal(new TerminalTheme.Rgb(192, 192, 192), reported.GetConcreteColor("text"));
        Assert.Equal(new TerminalTheme.Rgb(0, 0, 0), reported.GetConcreteColor("userMessageBg"));
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
}
