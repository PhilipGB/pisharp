using System.Text;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalInputTests
{
    [Fact]
    public void DecodesArrowsModifiersEscapeAndUnicodeWithoutLeakingSequences()
    {
        var bytes = Encoding.UTF8.GetBytes("\u001b[A\u001b[B\u001b[3~\u001b[13;3u\u001bZ\u001b[99~😀");
        var reader = new TerminalInput(new MemoryStream(bytes));
        Assert.Equal(ConsoleKey.UpArrow, reader.Read().Key?.Key);
        Assert.Equal(ConsoleKey.DownArrow, reader.Read().Key?.Key);
        Assert.Equal(ConsoleKey.Delete, reader.Read().Key?.Key);
        var newline = reader.Read().Key;
        Assert.Equal(ConsoleKey.Enter, newline?.Key);
        Assert.True(newline?.Modifiers.HasFlag(ConsoleModifiers.Alt));
        // ESC Z is Alt+Z, whereas CSI Z means Shift+Tab.
        Assert.True(reader.Read().Key?.Modifiers.HasFlag(ConsoleModifiers.Alt));
        Assert.Null(reader.Read().Key); // Unknown CSI is ignored, not inserted into the editor.
        Assert.Equal("😀", reader.Read().Text);
        Assert.Equal(ConsoleKey.Escape, new TerminalInput(new MemoryStream([27])).Read().Key?.Key);
        Assert.Null(reader.Read().Key);
    }

    [Fact]
    public void PastedMultilineTextRemainsOneAtomicEditorInsertion()
    {
        var reader = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes(
            "before\u001b[200~line one\r\nline two\u001b[201~after\n")));
        var buffer = new EditorBuffer();
        foreach (var c in "before") buffer.Handle(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
        for (var i = 0; i < 6; i++) reader.Read();
        var pasted = reader.Read();
        Assert.Equal("line one\nline two", pasted.Text);
        Assert.Equal(EditorAction.Render, buffer.InsertText(pasted.Text!));
        Assert.Equal("beforeline one\nline two", buffer.Text);
        Assert.Equal("after", string.Concat(Enumerable.Range(0, 5).Select(_ => reader.Read().Key!.Value.KeyChar)));
        Assert.Equal(EditorAction.Submit, buffer.Handle(reader.Read().Key!.Value));
        Assert.Single(buffer.History);
        Assert.Equal("beforeline one\nline two", buffer.History.Single());
    }

    [Fact]
    public void PasteStripsTerminalControlsAndDoesNotExecuteCommands()
    {
        var buffer = new EditorBuffer();
        buffer.InsertText("/name safe\u001b[2J\0\nother");
        Assert.Equal("/name safe[2J\nother", buffer.Text);
    }
}
