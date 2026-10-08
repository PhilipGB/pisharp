using System.Text;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalInputTests
{
    [Theory]
    [InlineData("\u001b]10;rgb:aaaa/bbbb/cccc\a")]
    [InlineData("\u001b[?61;1;21;22c")]
    [InlineData("\u001b[?997;2n")]
    [InlineData("\u001b[99~")]
    public void BlockingReadSkipsProtocolTrafficAndReturnsTheNextUserInput(string protocol)
    {
        var input = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes(protocol + "X")));
        Assert.Equal('X', input.Read().Key?.KeyChar);
    }

    [Theory]
    [InlineData("\u001b]11;#282a36\a")]
    [InlineData("\u001b[?62;22c")]
    [InlineData("\u001b[?997;1n")]
    [InlineData("\u001b[99~")]
    public void PollingConsumesProtocolTrafficWithoutReportingUserInputOrBlocking(string protocol)
    {
        var input = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes(protocol)));
        Assert.False(input.TryRead(0, out _));
    }

    [Theory]
    [InlineData("X")]
    [InlineData("😀")]
    [InlineData("\u001b[A")]
    [InlineData("\u001b[200~draft\u001b[201~")]
    public void PendingReplyCompletionRetainsGenuineInputAndStopsAtTheDaFence(string userInput)
    {
        var reader = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b]11;#282a36\a" + userInput + "\u001b[?62;22cY")));
        var pending = true;
        reader.TerminalDeviceAttributesReceived += () => pending = false;
        reader.CompletePendingReplies(() => pending, 100);
        Assert.True(pending);
        var actual = reader.Read();
        var expected = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes(userInput))).Read();
        Assert.Equal(expected, actual);
        reader.CompletePendingReplies(() => pending, 100);
        Assert.False(pending);
        Assert.Equal('Y', reader.Read().Key?.KeyChar);
    }

    [Fact]
    public void PendingReplyCompletionConsumesAlreadyAvailableFenceAfterWaitBudgetExpires()
    {
        var reader = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b]11;#282a36\a\u001b[?62;22cY")));
        var pending = true;
        reader.TerminalDeviceAttributesReceived += () => pending = false;
        reader.CompletePendingReplies(() => pending, 0);
        Assert.False(pending);
        Assert.Equal('Y', reader.Read().Key?.KeyChar);
    }

    [Fact]
    public void PendingReplyCompletionBoundsAlreadyQueuedControlTrafficByBytes()
    {
        var controls = string.Concat(Enumerable.Repeat("\u001b[0n", 20000));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(controls + "\u001b[?62;22cY"));
        var reader = new TerminalInput(stream);
        var pending = true;
        reader.TerminalDeviceAttributesReceived += () => pending = false;
        reader.CompletePendingReplies(() => pending, 0);
        Assert.True(pending);
        Assert.InRange(stream.Position, 1, 64 * 1024);
        Assert.Equal('Y', reader.Read().Key?.KeyChar);
        Assert.False(pending);
    }

    [Fact]
    public async Task ActiveEditorStopsOnlyForExplicitEofAndIgnoresEmptyControlEvents()
    {
        var editor = new TerminalEditor();
        Task<bool> Queue(string text, bool followUp, CancellationToken token) => throw new InvalidOperationException();
        Assert.True(await editor.HandleActiveInputAsync(new(null, null), Queue, () => [], () => { }));
        Assert.False(await editor.HandleActiveInputAsync(TerminalInputEvent.EndOfStream, Queue, () => [], () => { }));
    }

    [Fact]
    public void RealEofIsExplicitAndDifferentFromAnEmptyControlEvent()
    {
        var input = new TerminalInput(new MemoryStream());
        Assert.True(input.Read().IsEndOfStream);
        Assert.False(new TerminalInputEvent(null, null).IsEndOfStream);
    }

    [Fact]
    public void PollingSkipsReportsAndStillReturnsAvailableUserInput()
    {
        var input = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b]11;#282a36\a\u001b[?62;22cX")));
        Assert.True(input.TryRead(0, out var next));
        Assert.False(next.IsEndOfStream);
        Assert.Equal('X', next.Key?.KeyChar);
        Assert.False(input.TryRead(0, out _));
    }

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
    public void DecodesKittyKeyEventsAndConsumesNegotiationReplies()
    {
        var reader = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes(
            "\u001b[?7u\u001b[97;1u\u001b[97:65:97;2u\u001b[13;2u\u001b[57352;5u\u001b[57364u\u001b[128512u")));

        var letter = reader.Read().Key!.Value;
        Assert.Equal((ConsoleKey.A, 'a'), (letter.Key, letter.KeyChar));

        var shiftedLetter = reader.Read().Key!.Value;
        Assert.Equal((ConsoleKey.A, 'A'), (shiftedLetter.Key, shiftedLetter.KeyChar));
        Assert.True(shiftedLetter.Modifiers.HasFlag(ConsoleModifiers.Shift));

        var shiftedEnter = reader.Read().Key!.Value;
        Assert.Equal(ConsoleKey.Enter, shiftedEnter.Key);
        Assert.True(shiftedEnter.Modifiers.HasFlag(ConsoleModifiers.Shift));

        var controlUp = reader.Read().Key!.Value;
        Assert.Equal(ConsoleKey.UpArrow, controlUp.Key);
        Assert.True(controlUp.Modifiers.HasFlag(ConsoleModifiers.Control));
        Assert.Equal(ConsoleKey.F1, reader.Read().Key?.Key);
        Assert.Equal("😀", reader.Read().Text);
        Assert.True(reader.Read().IsEndOfStream);
    }

    [Fact]
    public void KittyKeyReleaseIsIgnoredAndPressesRemainReadable()
    {
        var release = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b[97;1:3u")));
        Assert.False(release.TryRead(0, out _));

        var press = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b[97;1u")));
        Assert.True(press.TryRead(0, out var key));
        Assert.Equal(ConsoleKey.A, key.Key?.Key);
        Assert.Equal('a', key.Key?.KeyChar);
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
        Assert.False(reply.IsEndOfStream);
        Assert.Equal('X', reply.Key?.KeyChar);
    }

    [Theory]
    [InlineData("\u001b]10;rgb:aaaa/bbbb/cccc\aX", 10, 170, 187, 204, null)]
    [InlineData("\u001b]11;rgb:0000/1111/2222\u001b\\X", 11, 0, 17, 34, null)]
    [InlineData("\u001b]10;#AABBCC\aX", 10, 170, 187, 204, null)]
    [InlineData("\u001b]4;13;#ff0080\aX", 4, 255, 0, 128, 13)]
    [InlineData("\u001b]4;1;#ffff00000000\aX", 4, 255, 0, 0, 1)]
    public void EmitsValidatedTerminalColorReports(string input, int slot, byte red, byte green, byte blue,
        int? paletteIndex = null)
    {
        var reader = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes(input)));
        TerminalColorResponse? response = null;
        reader.TerminalColorReceived += value => response = value;

        var report = reader.Read();

        Assert.False(report.IsEndOfStream);
        Assert.Equal('X', report.Key?.KeyChar);
        Assert.NotNull(response);
        Assert.Equal(new TerminalColorResponse(slot, new TerminalTheme.Rgb(red, green, blue), paletteIndex), response!.Value);
        Assert.True(reader.Read().IsEndOfStream);
    }

    [Fact]
    public void ConsumesDa1AndTerminalAppearanceReports()
    {
        var reader = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b[?997;2n\u001b[?62;22cX")));
        string? appearance = null;
        var deviceAttributes = 0;
        reader.TerminalColorSchemeReceived += value => appearance = value;
        reader.TerminalDeviceAttributesReceived += () => deviceAttributes++;

        var next = reader.Read();
        Assert.False(next.IsEndOfStream);
        Assert.Equal('X', next.Key?.KeyChar);
        Assert.Equal("light", appearance);
        Assert.Equal(1, deviceAttributes);
        Assert.True(reader.Read().IsEndOfStream);
    }

    [Fact]
    public void TruncatedOscIsDiscardedAndOversizedOscIsRejected()
    {
        var truncated = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("\u001b]11;rgb:abcd")));
        Assert.True(truncated.Read().IsEndOfStream);
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
