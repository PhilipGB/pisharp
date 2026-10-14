using PiSharp.Cli.Tui;
using System.Text.RegularExpressions;

namespace PiSharp.Tests;

public sealed class TerminalExtensionErrorTextTests
{
    [Fact]
    public void DeferredErrorAppearsWithItsUserPromptInTheNextScreenFrame()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 120, () => 40,
            new TerminalImageRenderer(_ => null),
            TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor), queryTerminalColors: false);
        output.GetStringBuilder().Clear();

        screen.AppendExtensionError("/guide/SKILL.md", "Bad YAML\n", render: false);
        Assert.Equal("", output.ToString());
        screen.AppendUserMessage("/skill:guide args");

        var frame = output.ToString();
        Assert.Single(Regex.Matches(frame, @"\x1b\[\?2026h"));
        Assert.Contains("Extension \"/guide/SKILL.md\" error: Bad YAML", frame);
        Assert.Contains("/skill:guide args", frame);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public void ErrorBlockPreservesQuotedPathAndMultilineContextWithinSourceMargins(int columns)
    {
        var message = TerminalExtensionErrorText.Message("/workspace/guide/SKILL.md",
            "Flow sequence in block collection\n\ndescription: [broken\n                    ^\n");
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var rendered = TerminalExtensionErrorText.Render(message, columns, theme);
        var rows = rendered.Split('\n');

        var colorPrefix = theme.Style("error", "marker").Split("marker", StringSplitOptions.None)[0];
        Assert.StartsWith(" " + colorPrefix + "Extension", rendered);
        Assert.Contains("Extension \"/workspace/guide/SKILL.md\" error:", rendered);
        Assert.Contains("description: [broken", rendered);
        Assert.Contains("                    ^", rendered);
        Assert.All(rows.Where(row => row.Length > 0), row =>
        {
            Assert.StartsWith(" ", row);
            Assert.EndsWith(" ", Regex.Replace(row, @"\x1b\[[0-9;]*m", ""));
            Assert.InRange(TerminalTextLayout.Width(row), 0, columns);
        });
    }

    [Fact]
    public void TabsUseThreeSpacesAndNarrowTerminalHasNoMargin()
    {
        var rendered = TerminalExtensionErrorText.Render("one\ttwo", 20, TerminalTheme.Default);
        Assert.Contains("one   two", rendered);
        Assert.DoesNotContain('\t', rendered);
        var narrow = TerminalExtensionErrorText.Render("x", 1, TerminalTheme.Default);
        Assert.Equal(1, TerminalTextLayout.Width(narrow));
    }

    [Fact]
    public void ErrorContextRetainsTrailingBlankRowBeforeUserAndReply()
    {
        var transcript = new TerminalTranscriptBuffer();
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var presentation = new TerminalConversationPresentation(transcript,
            new TerminalImageRenderer(_ => null), () => 120, () => theme);

        presentation.AppendExtensionError("/guide/SKILL.md", "Bad YAML\n\ndescription: [broken\n                    ^\n");
        Assert.True(presentation.HasContent);
        presentation.AppendUserMessage("/skill:guide args", null);
        presentation.CommitAssistantText("RECOVERED");
        var rows = TerminalTextLayout.Wrap(transcript.GetText(120), 120)
            .Select(row => Regex.Replace(row, @"\x1b\[[0-9;]*m", "")).ToArray();

        Assert.Equal(new string(' ', 120), rows[4]);
        Assert.Equal("", rows[5]);
        Assert.Equal(new string(' ', 120), rows[6]);
        Assert.StartsWith(" /skill:guide args", rows[7]);
        Assert.Contains(rows, row => row.Contains("RECOVERED", StringComparison.Ordinal));
    }

    [Fact]
    public void RetainedErrorRewrapsAndRecolorsWhenPresentationChanges()
    {
        var columns = 80;
        var theme = TerminalThemeCatalog.LoadBuiltIn("dark", TerminalColorMode.TrueColor);
        var transcript = new TerminalTranscriptBuffer();
        var presentation = new TerminalConversationPresentation(transcript,
            new TerminalImageRenderer(_ => null), () => columns, () => theme);
        presentation.AppendExtensionError("/workspace/long-guide-name/SKILL.md", "Bad YAML at line 1, column 21\n");
        var original = transcript.GetText(columns);

        columns = 40;
        theme = TerminalThemeCatalog.LoadBuiltIn("light", TerminalColorMode.TrueColor);
        presentation.ReRender();
        var resized = transcript.GetText(columns);

        Assert.NotEqual(original, resized);
        Assert.Contains(theme.Style("error", "marker").Split("marker", StringSplitOptions.None)[0], resized);
        Assert.All(resized.Split('\n').Where(row => row.Length > 0), row =>
            Assert.Equal(columns, TerminalTextLayout.Width(row)));
        Assert.Contains("/workspace/long-guide-name/SKILL.md", Regex.Replace(resized, @"\x1b\[[0-9;]*m|\n| ", ""));
    }
}
