using System.Text;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalKeyboardProtocolTests
{
    [Fact]
    public void SuspendRestoresFallbackAndResumeNegotiatesAgain()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 24,
            new TerminalImageRenderer(), TerminalTheme.Default, queryTerminalColors: false);
        var input = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b[?62;22cX\u001b[?62;22cY")));
        screen.AttachTerminalInput(input);
        Assert.Equal('X', input.Read().Key?.KeyChar);
        screen.Suspend();
        Assert.Contains("\u001b[>4;0m", output.ToString());
        screen.Resume();
        Assert.Equal('Y', input.Read().Key?.KeyChar);
        Assert.Equal(2, output.ToString().Split("\u001b[>4;2m", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ReplacingTheInputReaderDetachesThePreviousProtocolReplies()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 24,
            new TerminalImageRenderer(), TerminalTheme.Default, queryTerminalColors: false);
        var oldInput = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b[?62;22cX")));
        var input = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b[?62;22cY")));
        screen.AttachTerminalInput(oldInput);
        screen.AttachTerminalInput(input);
        Assert.Equal('X', oldInput.Read().Key?.KeyChar);
        Assert.DoesNotContain("\u001b[>4;2m", output.ToString());
        Assert.Equal('Y', input.Read().Key?.KeyChar);
        Assert.Contains("\u001b[>4;2m", output.ToString());
    }

    [Fact]
    public void KeyboardFenceDoesNotEndTheSeparateColorQueryAndRepeatedAttachmentKeepsOneBinding()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 24,
            new TerminalImageRenderer(), TerminalTheme.Default, queryTerminalColors: true);
        var input = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes(
            "\u001b[?62;22c\u001b]11;#ffffff\a\u001b[?62;22cX")));
        screen.AttachTerminalInput(input);
        screen.AttachTerminalInput(input);

        Assert.Equal('X', input.Read().Key?.KeyChar);
        Assert.Equal(new TerminalTheme.Rgb(255, 255, 255), screen.CurrentTheme.TerminalBackground);
    }

    [Theory]
    [InlineData("\u001b[?62;22c", true)]
    [InlineData("\u001b[?0u\u001b[?62;22c", true)]
    [InlineData("\u001b[?7u\u001b[?62;22c", false)]
    public void NegotiationRepliesSelectFallbackWithoutConsumingUserInput(string replies, bool fallback)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 24,
            new TerminalImageRenderer(), TerminalTheme.Default, queryTerminalColors: false);
        var input = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes(replies + "X")));
        screen.AttachTerminalInput(input);

        Assert.Equal('X', input.Read().Key?.KeyChar);
        Assert.Equal(fallback, output.ToString().Contains("\u001b[>4;2m", StringComparison.Ordinal));
        screen.Dispose();
        Assert.Equal(fallback, output.ToString().Contains("\u001b[>4;0m", StringComparison.Ordinal));
    }

    [Fact]
    public void LateKittyReplyDisablesAnAlreadyEnabledFallback()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 80, () => 24,
            new TerminalImageRenderer(), TerminalTheme.Default, queryTerminalColors: false);
        var input = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b[?62;22cX\u001b[?7uY")));
        screen.AttachTerminalInput(input);

        Assert.Equal('X', input.Read().Key?.KeyChar);
        Assert.Contains("\u001b[>4;2m", output.ToString());
        Assert.Equal('Y', input.Read().Key?.KeyChar);
        Assert.Contains("\u001b[>4;0m", output.ToString());
    }
}
