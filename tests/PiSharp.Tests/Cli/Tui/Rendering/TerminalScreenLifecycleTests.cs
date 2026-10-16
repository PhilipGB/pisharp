namespace PiSharp.Tests.Cli.Tui.Rendering;

public sealed class TerminalScreenLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisposeRestoresTerminalModesWhenFinalRenderFails(bool failOnFlush)
    {
        using var output = new ThrowOnceTextWriter();
        using var error = new StringWriter();
        var screen = new PiSharp.Cli.Tui.TerminalScreen(output, error,
            () => 40, () => 12, new PiSharp.Cli.Tui.TerminalImageRenderer(),
            PiSharp.Cli.Tui.TerminalTheme.Default, queryTerminalColors: false,
            minimumRenderInterval: TimeSpan.FromHours(1));
        screen.SetEditor("deferred render", 15);
        output.FailNext(failOnFlush);

        var failure = Assert.Throws<IOException>(screen.Dispose);

        Assert.Equal(failOnFlush ? "terminal flush failed" : "terminal write failed", failure.Message);
        Assert.False(screen.IsActive);
        var terminalOutput = output.ToString();
        Assert.Contains("\u001b[?25h", terminalOutput);
        Assert.Contains("\u001b[?1049l", terminalOutput);
        Assert.Contains("\u001b[?7h", terminalOutput);
        Assert.Contains("\u001b[?2026l", terminalOutput);
    }

    private sealed class ThrowOnceTextWriter : StringWriter
    {
        private bool _failOnFlush;
        private bool _armed;

        public void FailNext(bool failOnFlush)
        {
            _failOnFlush = failOnFlush;
            _armed = true;
        }

        public override void Write(string? value)
        {
            ThrowIfArmed(failOnFlush: false);
            base.Write(value);
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            ThrowIfArmed(failOnFlush: false);
            base.Write(buffer);
        }

        public override void Flush()
        {
            ThrowIfArmed(failOnFlush: true);
            base.Flush();
        }

        private void ThrowIfArmed(bool failOnFlush)
        {
            if (!_armed || _failOnFlush != failOnFlush) return;
            _armed = false;
            throw new IOException(failOnFlush ? "terminal flush failed" : "terminal write failed");
        }
    }
}
