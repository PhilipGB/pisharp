using System.Text;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalDialogWheelTests
{
    [Theory]
    [InlineData(ConsoleKey.PageUp)]
    [InlineData(ConsoleKey.PageDown)]
    [InlineData(ConsoleKey.Home)]
    [InlineData(ConsoleKey.End)]
    public void WheelFallbackLeavesViewportKeysForTheFocusedComponent(ConsoleKey key)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 40, () => 12);
        Assert.False(TerminalWheelInput.TryRouteUnhandled(screen,
            new TerminalInputEvent(new ConsoleKeyInfo('\0', key, false, false, false), null)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectorWheelFallsBackOnlyWhenTheListIsEmpty(bool empty)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var screen = new TerminalScreen(output, error, () => 40, () => 12);
        screen.Output.Write(string.Join('\n', Enumerable.Range(1, 40).Select(index => "line " + index)) + "\n");
        var initialOffset = screen.TranscriptScrollOffset;
        var offsetWhileFocused = -1;
        const string wheel = "\u001b[<64;3;1M";
        using var bytes = new ObservedInput(Encoding.UTF8.GetBytes(wheel + "\r"), Encoding.UTF8.GetByteCount(wheel),
            () => offsetWhileFocused = screen.TranscriptScrollOffset);
        var input = new TerminalInput(bytes);
        TerminalSelectionOption<string>[] options = empty ? [] :
            [new("first", "first", "First"), new("second", "second", "Second")];
        var selected = new TerminalOverlayHost(input).Select(screen, "Mouse-aware selector", options);

        Assert.Equal(empty ? null : "first", selected?.Option.Value);
        Assert.Equal(empty, offsetWhileFocused > initialOffset);
    }

    private sealed class ObservedInput(byte[] bytes, int observeAt, Action observe) : MemoryStream(bytes)
    {
        public override int ReadByte()
        {
            if (Position == observeAt) observe();
            return base.ReadByte();
        }
    }
}
