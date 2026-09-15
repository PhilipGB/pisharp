using PiSharp.Cli;

namespace PiSharp.Core.Tests;

public sealed class TerminalPromptReaderTests
{
    [Fact]
    public void ReadPrompt_NormalLine_ReturnsSingleLine()
    {
        using var input = new StringReader("hello world\n");
        using var output = new StringWriter();
        using var reader = new TerminalPromptReader(input, output, enableBracketedPaste: false);

        var result = reader.ReadPrompt();

        Assert.Equal("hello world", result);
        Assert.Equal("> ", output.ToString());
    }

    [Fact]
    public void ReadPrompt_BracketedMultilinePaste_ReturnsOnePrompt()
    {
        const string start = "\u001b[200~";
        const string end = "\u001b[201~";
        using var input = new StringReader($"{start}first line\nsecond line{end}\n");
        using var output = new StringWriter();
        using var reader = new TerminalPromptReader(input, output, enableBracketedPaste: false);

        var result = reader.ReadPrompt();

        Assert.Equal("first line\nsecond line", result);
    }

    [Fact]
    public void Dispose_WhenBracketedPasteEnabled_RestoresTerminalMode()
    {
        using var input = new StringReader(string.Empty);
        using var output = new StringWriter();
        var reader = new TerminalPromptReader(input, output, enableBracketedPaste: true);

        reader.Dispose();

        Assert.Equal("\u001b[?2004h\u001b[?2004l", output.ToString());
    }
}
