using System.Text;
using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TerminalInputHandoffTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("é")]
    [InlineData("x")]
    public void CancellationDuringReadRetainsCompleteEventForIdleConsumer(string text)
    {
        using var ended = new CancellationTokenSource();
        using var stream = new CancelOnReadStream(Encoding.UTF8.GetBytes(text), ended.Cancel);
        var input = new TerminalInput(stream);

        Assert.False(input.TryReadActive(0, ended.Token, out _));

        Assert.True(input.TryRead(0, out var retained));
        AssertEvent(text, retained);
        Assert.False(input.TryRead(0, out _));
    }

    [Fact]
    public void AlreadyEndedActivityDoesNotReadIdleInput()
    {
        using var ended = new CancellationTokenSource();
        ended.Cancel();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x"));
        var input = new TerminalInput(stream);

        Assert.False(input.TryReadActive(0, ended.Token, out _));

        Assert.Equal(0, stream.Position);
        Assert.True(input.TryRead(0, out var retained));
        AssertEvent("x", retained);
    }

    [Fact]
    public void PushedBackEventPrecedesAlreadyRetainedInput()
    {
        using var stream = new MemoryStream();
        var input = new TerminalInput(stream);
        input.PushBack(new(new ConsoleKeyInfo('b', ConsoleKey.B, false, false, false), null));
        input.PushBack(new(new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false), null));

        Assert.True(input.TryRead(0, out var first));
        AssertEvent("a", first);
        Assert.True(input.TryRead(0, out var second));
        AssertEvent("b", second);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("x")]
    public void ActiveReaderReceivesInputBeforeActivityEnds(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var input = new TerminalInput(stream);

        Assert.True(input.TryReadActive(0, CancellationToken.None, out var next));

        AssertEvent(text, next);
    }

    private static void AssertEvent(string text, TerminalInputEvent value)
    {
        if (text == "\n") Assert.Equal(ConsoleKey.Enter, value.Key!.Value.Key);
        else
        {
            Assert.True(TerminalInput.TryGetText(value, out var actual));
            Assert.Equal(text, actual);
        }
    }

    private sealed class CancelOnReadStream(byte[] bytes, Action cancel) : MemoryStream(bytes)
    {
        public override int ReadByte()
        {
            var result = base.ReadByte();
            cancel();
            return result;
        }
    }
}
