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
    public void DecodesSgrMouseCoordinatesMotionReleaseAndWheelDirection()
    {
        var reader = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes(
            "\u001b[<0;4;2M\u001b[<32;5;3M\u001b[<3;5;3m\u001b[<72;7;1M\u001b[<67;7;1M")));

        var press = reader.Read().Mouse!.Value;
        Assert.Equal((4, 2), (press.Column, press.Row));
        Assert.False(press.IsRelease);
        Assert.False(press.IsMotion);

        var motion = reader.Read().Mouse!.Value;
        Assert.True(motion.IsMotion);
        Assert.Equal((5, 3), (motion.Column, motion.Row));

        Assert.True(reader.Read().Mouse!.Value.IsRelease);
        Assert.Equal(5, reader.Read().Mouse!.Value.WheelScrollDelta);
        Assert.Equal(0, reader.Read().Mouse!.Value.WheelScrollDelta); // horizontal wheel
    }

    [Fact]
    public void DecodesCtrlMinusUndoBindingFromKittyCsiU()
    {
        var reader = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b[45;5u")));

        var key = reader.Read().Key!.Value;

        Assert.Equal(ConsoleKey.OemMinus, key.Key);
        Assert.True(key.Modifiers.HasFlag(ConsoleModifiers.Control));
    }

    [Fact]
    public void DecodesModifiedCursorAndPageKeysForConfigurableBindings()
    {
        var reader = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes(
            "\u001b[1;5D\u001b[1;3C\u001b[6;2~\u001b[112;6u")));

        var controlLeft = reader.Read().Key!.Value;
        Assert.Equal(ConsoleKey.LeftArrow, controlLeft.Key);
        Assert.True(controlLeft.Modifiers.HasFlag(ConsoleModifiers.Control));
        var altRight = reader.Read().Key!.Value;
        Assert.Equal(ConsoleKey.RightArrow, altRight.Key);
        Assert.True(altRight.Modifiers.HasFlag(ConsoleModifiers.Alt));
        var shiftPageDown = reader.Read().Key!.Value;
        Assert.Equal(ConsoleKey.PageDown, shiftPageDown.Key);
        Assert.True(shiftPageDown.Modifiers.HasFlag(ConsoleModifiers.Shift));
        var controlShiftP = reader.Read().Key!.Value;
        Assert.Equal(ConsoleKey.P, controlShiftP.Key);
        Assert.True(controlShiftP.Modifiers.HasFlag(ConsoleModifiers.Control));
        Assert.True(controlShiftP.Modifiers.HasFlag(ConsoleModifiers.Shift));
        var windowsBindings = OperatingSystem.IsWindows() || OperatingSystem.IsLinux() &&
            (Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is not null ||
             Environment.GetEnvironmentVariable("WSL_INTEROP") is not null);
        if (!windowsBindings)
            Assert.Equal("app.model.cycleBackward", new EditorKeymap().MatchIdleApplicationAction(controlShiftP));
    }

    [Fact]
    public void DecodedShiftArrowExtendsPromptSelection()
    {
        var reader = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b[1;2D")));
        var key = reader.Read().Key!.Value;
        var editor = new EditorBuffer();
        editor.SetText("draft");

        Assert.Equal(ConsoleKey.LeftArrow, key.Key);
        Assert.True(key.Modifiers.HasFlag(ConsoleModifiers.Shift));
        Assert.Equal(EditorAction.Render, editor.Handle(key));
        Assert.Equal("t", editor.SelectedText);
    }

    [Theory]
    [InlineData("\u001b]10;rgb:aaaa/bbbb/cccc\aX")]
    [InlineData("\u001b]11;rgb:0000/1111/2222\u001b\\X")]
    public void TerminalColorOscRepliesAreConsumedWithoutEditingTheDraft(string input)
    {
        var reader = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes(input)));
        var reply = reader.Read();
        Assert.Null(reply.Key);
        Assert.Null(reply.Text);
        Assert.Equal('X', reader.Read().Key?.KeyChar);
    }

    [Fact]
    public void TruncatedOscIsDiscardedAndOversizedOscIsRejected()
    {
        var truncated = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b]11;rgb:abcd")));
        Assert.Null(truncated.Read().Text);
        Assert.Null(truncated.Read().Key);
        var oversized = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b]11;" + new string('x', 4096) + "\a")));
        Assert.Throws<InvalidDataException>(() => oversized.Read());
    }

    [Fact]
    public void ActiveRunKeysDecodeFollowUpDequeueAndRestorePendingDraft()
    {
        var reader = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b[13;3u\u001b[1;3A")));
        Assert.True(reader.TryRead(0, out var followUp));
        Assert.Equal(ConsoleKey.Enter, followUp.Key?.Key);
        Assert.True(followUp.Key?.Modifiers.HasFlag(ConsoleModifiers.Alt));
        Assert.True(reader.TryRead(0, out var dequeue));
        Assert.Equal(ConsoleKey.UpArrow, dequeue.Key?.Key);
        Assert.True(dequeue.Key?.Modifiers.HasFlag(ConsoleModifiers.Alt));
        Assert.False(reader.TryRead(0, out _));

        var editor = new TerminalEditor();
        editor.Prefill("draft");
        editor.RestorePending(["steer", "follow up"]);
        Assert.Equal("steer\n\nfollow up\n\ndraft", editor.Draft);
    }

    [Fact]
    public async Task ActiveEditorRoutesEnterAltEnterAndEscapeWithPendingRestoration()
    {
        var editor = new TerminalEditor();
        var queued = new List<(string Text, bool FollowUp)>();
        Task<bool> Queue(string text, bool followUp, CancellationToken _) { queued.Add((text, followUp)); return Task.FromResult(true); }
        var aborted = false;
        IReadOnlyList<string> Clear() => ["pending steer", "pending follow up"];
        static TerminalInputEvent Key(ConsoleKey key, ConsoleModifiers modifiers = 0) => new(
            new ConsoleKeyInfo('\0', key, modifiers.HasFlag(ConsoleModifiers.Shift),
                modifiers.HasFlag(ConsoleModifiers.Alt), modifiers.HasFlag(ConsoleModifiers.Control)), null);

        editor.Prefill("steer now");
        Assert.True(await editor.HandleActiveInputAsync(Key(ConsoleKey.Enter), Queue, Clear, () => aborted = true));
        editor.Prefill("do later");
        Assert.True(await editor.HandleActiveInputAsync(Key(ConsoleKey.Enter, ConsoleModifiers.Alt), Queue, Clear,
            () => aborted = true));
        editor.Prefill("unfinished draft");
        Assert.False(await editor.HandleActiveInputAsync(Key(ConsoleKey.Escape), Queue, Clear, () => aborted = true));

        Assert.Equal([("steer now", false), ("do later", true)], queued);
        Assert.True(aborted);
        Assert.Equal("pending steer\n\npending follow up\n\nunfinished draft", editor.Draft);
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
