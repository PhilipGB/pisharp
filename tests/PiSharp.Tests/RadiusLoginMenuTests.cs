using System.Text.RegularExpressions;

namespace PiSharp.Tests;

public sealed class RadiusLoginMenuTests
{
    [Fact]
    public async Task SelectedRadiusAnimatesInTheActualTerminalAndStopsWhenDeselected()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        await using var server = new RadiusLoginServer();
        await using var terminal = new RadiusLoginTerminal(server.Origin, "{}", trueColor: true);
        await terminal.WaitEditorAsync();
        var mark = terminal.Mark;
        await terminal.SendAsync("/login\n");
        await terminal.WaitTextAsync("Select authentication method:", mark);
        mark = terminal.Mark;
        await terminal.SendAsync("\u001b[B\u001b[B");
        var shimmer = new Regex("(?:\u001b\\[38;2;[0-9]+;[0-9]+;[0-9]+m.){19}\u001b\\[39m");
        var first = await terminal.WaitFrameAsync(frame => shimmer.IsMatch(frame), mark, preserveAnsi: true);
        var initial = shimmer.Match(first).Value;
        mark = terminal.Mark;
        await terminal.WaitFrameAsync(frame => shimmer.IsMatch(frame) && shimmer.Match(frame).Value != initial,
            mark, preserveAnsi: true);
        mark = terminal.Mark;
        await terminal.SendAsync("\u001b[A");
        await terminal.WaitFrameAsync(frame => !shimmer.IsMatch(frame), mark, preserveAnsi: true);
        mark = terminal.Mark;
        await terminal.SendAsync("\u001b");
        await terminal.WaitEditorAsync(mark);
        await terminal.QuitAsync();
    }

    [Fact]
    public async Task LoginOffersRadiusDirectlyFromTheAuthenticationMenu()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/script")) return;
        await using var server = new RadiusLoginServer();
        await using var terminal = new RadiusLoginTerminal(server.Origin, "{}");
        await terminal.WaitEditorAsync();
        var mark = terminal.Mark;
        await terminal.SendAsync("/login\n");
        var menu = await terminal.WaitFrameAsync(frame => frame.Contains("Sign in with Radius", StringComparison.Ordinal), mark);
        Assert.Contains("Select authentication method:", menu);
        Assert.Contains("Sign in with an account", menu);
        Assert.Contains("Sign in with an API key", menu);
        Assert.Contains("Sign in with Radius • not configured", menu);
        mark = terminal.Mark;
        await terminal.SendAsync("\u001b");
        await terminal.WaitEditorAsync(mark);
        await terminal.QuitAsync();
    }
}
