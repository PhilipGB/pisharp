using PiSharp.Cli;

namespace PiSharp.Core.Tests;

/// <summary>
/// Key-driven prompt input (item 4): masking, Esc cancellation, token observation,
/// backspace, arrow handling, and bracketed-paste reassembly over the IConsoleIO seam.
/// </summary>
public class ConsoleKeyInputTests
{
    [Fact]
    public void ReadLine_TypedCharacters_ReturnsEchoedLine()
    {
        var console = new FakeConsoleIO();
        console.EnqueueText("hello");
        console.EnqueueText("\r");

        var result = ConsoleKeyInput.ReadLine(console, "> ", CancellationToken.None);

        Assert.Equal("hello", result);
        Assert.StartsWith("> ", console.Output);
        Assert.Contains("hello", console.Output);
    }

    [Fact]
    public void ReadLine_Secret_IsMaskedAndNeverEchoed()
    {
        var console = new FakeConsoleIO();
        const string secret = "sk-super-secret-key";
        console.EnqueueText(secret);
        console.EnqueueText("\r");

        var result = ConsoleKeyInput.ReadLine(
            console, "API key: ", CancellationToken.None, new ConsoleKeyInput.Options { Mask = true });

        Assert.Equal(secret, result);
        Assert.DoesNotContain(secret, console.Output);
        Assert.Equal($"API key: {new string('*', secret.Length)}", console.Output);
    }

    [Fact]
    public void ReadLine_Backspace_DeletesLastCharacter()
    {
        var console = new FakeConsoleIO();
        console.EnqueueText("ab");
        console.EnqueueKey(FakeConsoleIO.CharacterKey('\b'));
        console.EnqueueText("c");
        console.EnqueueText("\r");

        var result = ConsoleKeyInput.ReadLine(console, "> ", CancellationToken.None);

        Assert.Equal("ac", result);
    }

    [Fact]
    public void ReadLine_LoneEscape_Cancels()
    {
        var console = new FakeConsoleIO();
        console.EnqueueText("partial");
        console.EnqueueKey(FakeConsoleIO.EscapeKey);

        Assert.Throws<OperationCanceledException>(
            () => ConsoleKeyInput.ReadLine(console, "> ", CancellationToken.None));
    }

    [Fact]
    public void ReadLine_EscFollowedByRealKey_CancelsFirst()
    {
        // A real key buffered behind the Esc: the Esc is reported (cancel), the key is not
        // mistaken for a CSI prefix.
        var console = new FakeConsoleIO();
        console.EnqueueKey(FakeConsoleIO.EscapeKey);
        console.EnqueueText("x");

        Assert.Throws<OperationCanceledException>(
            () => ConsoleKeyInput.ReadLine(console, "> ", CancellationToken.None));
    }

    [Fact]
    public void ReadLine_ArrowKeys_AreConsumedAndIgnored()
    {
        var console = new FakeConsoleIO();
        console.EnqueueKey(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false));
        console.EnqueueKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false));
        console.EnqueueText("a");
        console.EnqueueText("\r");

        var result = ConsoleKeyInput.ReadLine(console, "> ", CancellationToken.None);

        Assert.Equal("a", result);
    }

    [Fact]
    public void ReadLine_BracketedPaste_SplicedAsOneSubmission()
    {
        var console = new FakeConsoleIO();
        console.EnqueueText("pre ");
        console.EnqueuePasteStart();
        console.EnqueueText("pasted value");
        console.EnqueuePasteEnd();
        console.EnqueueText("\r");

        var result = ConsoleKeyInput.ReadLine(console, "> ", CancellationToken.None);

        Assert.Equal("pre pasted value", result);
    }

    [Fact]
    public void ReadLine_BracketedPaste_TriggersEscStateNotCancel()
    {
        // Regression: the Escape that begins ESC[200~ must not be treated as a lone-Esc
        // cancel even though bytes follow it in the buffer.
        var console = new FakeConsoleIO();
        console.EnqueuePasteStart();
        console.EnqueueText("key");
        console.EnqueuePasteEnd();
        console.EnqueueText("\r");

        var result = ConsoleKeyInput.ReadLine(console, "> ", CancellationToken.None);

        Assert.Equal("key", result);
    }

    [Fact]
    public void ReadLine_SingleLinePrompt_PasteNewlineTakesFirstLine()
    {
        var console = new FakeConsoleIO();
        console.EnqueuePasteStart();
        console.EnqueueText("first line\nsecond line");
        console.EnqueuePasteEnd();
        console.EnqueueText("\r");

        var result = ConsoleKeyInput.ReadLine(console, "> ", CancellationToken.None);

        Assert.Equal("first line", result);
    }

    [Fact]
    public void ReadLine_MultilinePrompt_PasteKeepsNewlines()
    {
        var console = new FakeConsoleIO();
        console.EnqueuePasteStart();
        console.EnqueueText("first line\nsecond line");
        console.EnqueuePasteEnd();
        console.EnqueueText("\r");

        var result = ConsoleKeyInput.ReadLine(
            console, "> ", CancellationToken.None, new ConsoleKeyInput.Options { AcceptMultilinePaste = true });

        Assert.Equal("first line\nsecond line", result);
    }

    [Fact]
    public void ReadLine_PreCancelledToken_ThrowsWithoutReading()
    {
        var console = new FakeConsoleIO();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ConsoleKeyInput.ReadLine(console, "> ", cts.Token));
    }

    [Fact]
    public void ReadLine_LoneEscape_WithHandler_ReplacesDraft()
    {
        var console = new FakeConsoleIO();
        console.EnqueueText("abc");
        console.EnqueueKey(FakeConsoleIO.EscapeKey); // Esc: clear the draft
        console.EnqueueText("xy");
        console.EnqueueText("\r");

        var result = ConsoleKeyInput.ReadLine(
            console, "> ", CancellationToken.None,
            new ConsoleKeyInput.Options
            {
                OnLoneEscape = draft => string.IsNullOrWhiteSpace(draft) ? null : string.Empty,
            });

        Assert.Equal("xy", result);
    }

    [Fact]
    public void ReadLine_UpArrow_HistoryCallbackReplacesDraft()
    {
        var console = new FakeConsoleIO();
        console.EnqueueText("draft");
        console.EnqueueKey(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false));
        console.EnqueueText("\r");

        var seen = new List<string>();
        var result = ConsoleKeyInput.ReadLine(
            console, "> ", CancellationToken.None,
            new ConsoleKeyInput.Options
            {
                HistoryPrevious = draft =>
                {
                    seen.Add(draft);
                    return "from-history";
                },
            });

        Assert.Equal("from-history", result);
        Assert.Equal(["draft"], seen);
    }

    [Fact]
    public void ReadLine_RedirectedReader_ReturnsLine()
    {
        var console = new FakeConsoleIO { IsInteractive = false };
        console.EnqueueLine("  piped value  ");

        var result = ConsoleKeyInput.ReadLine(console, "> ", CancellationToken.None);

        Assert.Equal("piped value", result);
        Assert.StartsWith("> ", console.Output);
    }

    [Fact]
    public void ReadLine_RedirectedEof_Cancels()
    {
        var console = new FakeConsoleIO { IsInteractive = false };
        console.EnqueueLine(null);

        Assert.Throws<OperationCanceledException>(
            () => ConsoleKeyInput.ReadLine(console, "> ", CancellationToken.None));
    }
}

/// <summary>
/// Key-mode REPL prompt (TerminalPromptReader over IConsoleIO): paste splicing, Esc clearing,
/// history navigation, and token cancellation.
/// </summary>
public class TerminalPromptReaderKeyModeTests
{
    [Fact]
    public void ReadPrompt_MultilinePaste_ReturnsOnePrompt()
    {
        var console = new FakeConsoleIO();
        using var reader = new TerminalPromptReader(console, enableBracketedPaste: false);
        console.EnqueuePasteStart();
        console.EnqueueText("first line\nsecond line");
        console.EnqueuePasteEnd();
        console.EnqueueText("\r");

        var result = reader.ReadPrompt();

        Assert.Equal("first line\nsecond line", result);
    }

    [Fact]
    public void ReadPrompt_EscClearsDraftWithoutExiting()
    {
        var console = new FakeConsoleIO();
        using var reader = new TerminalPromptReader(console, enableBracketedPaste: false);
        console.EnqueueText("discard this");
        console.EnqueueKey(FakeConsoleIO.EscapeKey);
        console.EnqueueText("keep this");
        console.EnqueueText("\r");

        var result = reader.ReadPrompt();

        Assert.Equal("keep this", result);
    }

    [Fact]
    public void ReadPrompt_UpArrow_RestoresHistoryEntry()
    {
        var console = new FakeConsoleIO();
        using var reader = new TerminalPromptReader(console, enableBracketedPaste: false);

        // Submit one prompt, then reopen the prompt and recall it with Up.
        console.EnqueueText("remembered");
        console.EnqueueText("\r");
        Assert.Equal("remembered", reader.ReadPrompt());

        console.EnqueueKey(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false));
        console.EnqueueText("\r");
        Assert.Equal("remembered", reader.ReadPrompt());
    }

    [Fact]
    public void ReadPrompt_CancelledToken_Throws()
    {
        var console = new FakeConsoleIO();
        using var reader = new TerminalPromptReader(console, enableBracketedPaste: false);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => reader.ReadPrompt("> ", cts.Token));
    }
}
