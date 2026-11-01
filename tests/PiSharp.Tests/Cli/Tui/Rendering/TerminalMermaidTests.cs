using System.Text.RegularExpressions;
using PiSharp.Cli;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

/// <summary>Source-pinned Mermaid cases mirroring Pi's mermaid.test.ts at b06c1f2.</summary>
public sealed class TerminalMermaidTests
{
    private static string StripSgr(string text) => Regex.Replace(text, "\\u001b\\[[0-9;]*m", "");

    private static string Render(string markdown, string? mode = "streaming", bool isStreaming = false,
        bool isThinking = false, int width = 100, TerminalTheme? theme = null) =>
        TerminalMarkdownRenderer.Render(markdown, availableWidth: width, theme: theme ?? TerminalTheme.Default,
            mermaidMode: mode, isStreaming: isStreaming, isThinking: isThinking);

    private static string RenderStreaming(string markdown, string? mode = "streaming", int width = 100,
        TerminalTheme? theme = null, bool isThinking = false) =>
        TerminalMarkdownRenderer.RenderStreaming(markdown, availableWidth: width, theme: theme ?? TerminalTheme.Default,
            mermaidMode: mode, isThinking: isThinking);

    [Fact]
    public void BasicFlowchartRendersUnicodeDiagram()
    {
        const string markdown = "Before\n\n```mermaid\nflowchart LR\n  A[Start] --> B[Done]\n```\nAfter";

        var rendered = StripSgr(Render(markdown));

        Assert.Contains("Before", rendered);
        Assert.Contains("┌───────┐", rendered);
        Assert.Contains("│ Start ├───▶│ Done │", rendered);
        Assert.Contains("└───────┘    └──────┘", rendered);
        Assert.DoesNotContain("```mermaid", rendered);
        Assert.Contains("After", rendered);
    }

    [Fact]
    public void SurroundingMarkdownIsPreserved()
    {
        const string markdown = "# Title\n\nBefore\n\n```mermaid\nflowchart LR\n  A --> B\n```\n\nAfter";

        var rendered = StripSgr(Render(markdown));

        Assert.Contains("Title", rendered);
        Assert.Contains("Before", rendered);
        Assert.Contains("┌───┐", rendered);
        Assert.Contains("After", rendered);
    }

    [Fact]
    public void UnsupportedSyntaxFallsBackToCodeBlock()
    {
        const string unsupported = "```mermaid\npie\n  title Pets\n  \"Dogs\" : 4\n```";

        var rendered = StripSgr(Render(unsupported));
        var themed = Render(unsupported);

        Assert.Contains("```mermaid", rendered);
        Assert.Contains("pie", rendered);
        Assert.DoesNotContain("┌", rendered);
        Assert.Contains("```mermaid", themed);
    }

    [Fact]
    public void OversizedDiagramFallsBackWhenItExceedsAvailableWidth()
    {
        const string markdown = "```mermaid\nflowchart LR\n  A[Start] --> B[Done]\n```";

        var narrow = StripSgr(Render(markdown, width: 10));
        var wide = StripSgr(Render(markdown, width: 100));

        Assert.Contains("```mermaid", narrow);
        Assert.DoesNotContain("┌───────┐", narrow);
        Assert.Contains("┌───────┐", wide);
    }

    [Fact]
    public void IncompleteDiagramRendersWhileStreaming()
    {
        const string partial = "```mermaid\nflowchart LR\n  A --> B";

        var streaming = StripSgr(RenderStreaming(partial, isThinking: false));
        var final = StripSgr(Render(partial));

        Assert.Contains("───▶", streaming);
        Assert.DoesNotContain("```mermaid", streaming);
        // Final (non-streaming) also renders when the source is a complete diagram without its fence.
        Assert.Contains("───▶", final);
    }

    [Fact]
    public void ClassAssignmentsRenderWithoutWarnings()
    {
        const string markdown = "```mermaid\nflowchart LR\n  A[Foo]:::highlight --> B[Bar]\n```";

        var rendered = StripSgr(Render(markdown));
        var themed = Render(markdown);

        Assert.DoesNotContain("```mermaid", rendered);
        Assert.DoesNotContain("Mermaid diagram not rendered", rendered);
        Assert.Contains("│ Foo ├───▶│ Bar │", rendered);
        Assert.Contains("Foo", themed);
    }

    [Fact]
    public void PartiallyInvalidFinalDiagramFallsBackWithWarning()
    {
        const string markdown = "```mermaid\nflowchart LR\n  A[Foo] invalid\n```";

        var final = Render(markdown);
        var stripped = StripSgr(final);
        var streaming = RenderStreaming(markdown);
        var strippedStreaming = StripSgr(streaming);
        var followedByText = Render(markdown + "\nFollowing text");

        Assert.Contains("```mermaid", stripped);
        Assert.Contains("Mermaid diagram not rendered", stripped);
        Assert.Contains("dropped, expected a link: \"invalid\"", stripped);
        Assert.DoesNotContain("more)", stripped);
        Assert.Contains("Following text", StripSgr(followedByText));
        Assert.DoesNotContain("Mermaid diagram not rendered", strippedStreaming);
        Assert.DoesNotContain("```mermaid", strippedStreaming);
        Assert.Contains("│ Foo │", strippedStreaming);
        Assert.Contains(TerminalTheme.Default.Fg("warning"), final);
    }

    [Fact]
    public void MultiplePartialWarningsAreCondensed()
    {
        const string markdown = "```mermaid\nflowchart LR\n  A[Foo] invalid\n  B[Bar] also-invalid\n```";

        var rendered = StripSgr(Render(markdown));

        Assert.Contains("```mermaid", rendered);
        Assert.Contains("dropped, expected a link: \"invalid\"", rendered);
        Assert.Contains("(+1 more)", rendered);
        Assert.DoesNotContain("dropped, expected a link: \"also-invalid\"", rendered);
    }

    [Theory]
    [InlineData("off", false, true)]
    [InlineData("final", true, true)]
    [InlineData("final", false, false)]
    [InlineData("streaming", true, false)]
    [InlineData("streaming", false, false)]
    public void RenderingModesGateStreamingAndFinalOutput(string mode, bool isStreaming, bool expectFallback)
    {
        const string markdown = "```mermaid\nflowchart LR\n  A --> B\n```";

        var rendered = isStreaming
            ? StripSgr(RenderStreaming(markdown, mode: mode))
            : StripSgr(Render(markdown, mode: mode, isStreaming: false));

        if (expectFallback)
            Assert.Contains("```mermaid", rendered);
        else
            Assert.DoesNotContain("```mermaid", rendered);
    }

    [Fact]
    public void OffModeNeverRenders()
    {
        const string markdown = "```mermaid\nflowchart LR\n  A --> B\n```";

        Assert.Contains("```mermaid", StripSgr(Render(markdown, mode: "off")));
        Assert.Contains("```mermaid", StripSgr(RenderStreaming(markdown, mode: "off")));
    }

    [Fact]
    public void ThinkingBlocksNeverTransformMermaid()
    {
        const string markdown = "```mermaid\nflowchart LR\n  A --> B\n```";

        var thinkingFinal = StripSgr(Render(markdown, mode: "streaming", isThinking: true));
        var thinkingStreaming = StripSgr(RenderStreaming(markdown, mode: "streaming", isThinking: true));

        Assert.Contains("```mermaid", thinkingFinal);
        Assert.Contains("```mermaid", thinkingStreaming);
        Assert.DoesNotContain("┌───┐", thinkingFinal);
    }

    [Fact]
    public void SemanticSpansUsePiThemeTokens()
    {
        const string markdown = "```mermaid\nflowchart LR\n  A --> B\n```";
        var theme = TerminalTheme.Default;

        var rendered = Render(markdown, theme: theme);

        Assert.Contains(theme.Style("borderMuted", "").Substring(0, 7), rendered);
        Assert.Contains(theme.Style("accent", "").Substring(0, 7), rendered);
    }

    [Fact]
    public void TopLevelOnlyTransformsNestedFencesStayAsCode()
    {
        const string quote = "> ```mermaid\n> flowchart LR\n> A --> B\n> ```";
        const string list = "- item\n\n  ```mermaid\n  flowchart LR\n  A --> B\n  ```";

        Assert.Contains("```mermaid", StripSgr(Render(quote)));
        Assert.DoesNotContain("┌───┐", StripSgr(Render(quote)));
        // List fences are nested; they must not render as diagrams.
        var listRendered = StripSgr(Render(list));
        Assert.DoesNotContain("┌───┐    ┌───┐", listRendered);
    }

    [Fact]
    public void LanguageMatchingIsCaseInsensitiveAndIgnoresExtraInfo()
    {
        foreach (var info in new[] { "mermaid", "Mermaid", "mermaid extra", "MERMAID  extra info" })
        {
            var markdown = $"```{info}\nflowchart LR\n  A --> B\n```";
            Assert.DoesNotContain("```", StripSgr(Render(markdown)).TrimStart().Substring(0, 3));
        }
    }

    [Fact]
    public void MermaidSettingDefaultsToStreamingWithProjectPrecedence()
    {
        var unset = new UserSettings();
        Assert.Null(unset.MarkdownMermaid);
        Assert.Equal("streaming", unset.EffectiveMarkdownMermaid);
        Assert.Equal(UserSettings.DefaultMarkdownMermaid, "streaming");

        var user = new UserSettings(MarkdownMermaid: "off");
        var project = new UserSettings(MarkdownMermaid: "final");
        Assert.Equal("final", user.Overlay(project).EffectiveMarkdownMermaid);
        Assert.Equal("off", user.Overlay(new UserSettings()).EffectiveMarkdownMermaid);

        var invalid = new UserSettings(MarkdownMermaid: "sometimes");
        Assert.Equal("streaming", invalid.EffectiveMarkdownMermaid);
    }

    [Fact]
    public async Task MermaidSettingLoadsFromUserAndTrustedProjectFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mermaid-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".pi"));
        try
        {
            var path = Path.Combine(root, "settings.json");
            var unset = await UserSettings.LoadAsync(root, _ => null);
            Assert.Null(unset.MarkdownMermaid);
            Assert.Equal("streaming", unset.EffectiveMarkdownMermaid);

            await File.WriteAllTextAsync(path, "{\"markdown\":{\"mermaid\":\"off\"}}");
            var user = await UserSettings.LoadAsync(root, _ => null);
            Assert.Equal("off", user.MarkdownMermaid);

            await File.WriteAllTextAsync(Path.Combine(root, ".pi", "settings.json"),
                "{\"markdown\":{\"mermaid\":\"final\"}}");
            Assert.Equal("final", user.Overlay(await UserSettings.LoadProjectAsync(root)).EffectiveMarkdownMermaid);
            Assert.Equal("off", user.EffectiveMarkdownMermaid);

            foreach (var invalid in new[]
            {
                "{\"markdown\":null}",
                "{\"markdown\":{\"mermaid\":true}}",
                "{\"markdown\":{\"unknown\":\"value\"}}",
                "{\"markdown\":{\"mermaid\":\"off\",\"mermaid\":\"final\"}}"
            })
            {
                await File.WriteAllTextAsync(path, invalid);
                await Assert.ThrowsAsync<InvalidDataException>(() => UserSettings.LoadAsync(root, _ => null));
            }

            await File.WriteAllTextAsync(path, "{\"markdown\":{\"mermaid\":\"sometimes\"}}");
            Assert.Equal("streaming", (await UserSettings.LoadAsync(root, _ => null)).EffectiveMarkdownMermaid);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MermaidSettingPersistsAndClearsThroughWriter()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mermaid-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            foreach (var mode in new[] { "off", "final", "streaming" })
            {
                await UserSettingsWriter.SetAsync(path, "markdown.mermaid", mode, userScope: true);
                Assert.Equal(mode, (await UserSettings.LoadAsync(root, _ => null)).MarkdownMermaid);
            }
            await UserSettingsWriter.SetAsync(path, "markdown.mermaid", null, userScope: true);
            Assert.Null((await UserSettings.LoadAsync(root, _ => null)).MarkdownMermaid);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                UserSettingsWriter.SetAsync(path, "markdown.mermaid", "sometimes", userScope: true));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MermaidModeChangeRendersPreviouslyCommittedMarkdownAgain()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 24);

        screen.CommitAssistantText("```mermaid\nflowchart LR\n  A --> B\n```");
        var before = output.ToString();
        Assert.Contains("┌───┐", StripSgr(before));

        screen.SetMermaidRenderingMode("off");

        var after = output.ToString();
        var lastFrame = after.LastIndexOf("\u001b[?2026h", StringComparison.Ordinal);
        Assert.True(lastFrame >= 0);
        var visibleFrame = StripSgr(after[lastFrame..]);
        Assert.Contains("```mermaid", visibleFrame);
        Assert.DoesNotContain("┌───┐", visibleFrame);
    }

    [Fact]
    public void MermaidPickerExposesOffFinalStreamingWithInheritDefault()
    {
        var ids = TerminalSettingsPicker.Settings.Select(setting => setting.Id).ToArray();
        Assert.Contains("markdown.mermaid", ids);

        var setting = TerminalSettingsPicker.Settings.Single(s => s.Id == "markdown.mermaid");
        Assert.Equal("Mermaid diagrams", setting.Label);
        Assert.Contains("Unicode", setting.Description);
    }
}
