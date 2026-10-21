using PiSharp.Cli.Tui;

namespace PiSharp.Tests.Cli.Tui.Rendering;

public sealed class TerminalPathDisplayTests
{
    [Fact]
    public void FooterReplacesHomeDirectoryWithTilde()
    {
        var home = Path.Combine(Path.GetTempPath(), "home", "alex");
        var workingDirectory = Path.Combine(home, "Projects", "pisharp");
        var actual = TerminalPathDisplay.FormatForFooter(workingDirectory, home);

        Assert.Equal("~" + Path.DirectorySeparatorChar + "Projects" + Path.DirectorySeparatorChar + "pisharp", actual);
    }

    [Fact]
    public void FooterDisplaysHomeDirectoryAsTilde()
    {
        var home = Path.Combine(Path.GetTempPath(), "home", "alex");

        Assert.Equal("~", TerminalPathDisplay.FormatForFooter(home, home));
    }

    [Fact]
    public void FooterDoesNotAbbreviateSiblingOfHomeDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "home", "alexander", "Projects", "pisharp");
        var home = Path.Combine(Path.GetTempPath(), "home", "alex");

        Assert.Equal(path, TerminalPathDisplay.FormatForFooter(path, home));
    }
}
