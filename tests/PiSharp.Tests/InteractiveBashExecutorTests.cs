using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class InteractiveBashExecutorTests
{
    [Theory]
    [InlineData("!pwd", "pwd", false)]
    [InlineData("  ! printf ok  ", "printf ok", false)]
    [InlineData("!!pwd", "pwd", true)]
    [InlineData(" !! printf ok  ", "printf ok", true)]
    public void ParsesBangCommands(string input, string expectedCommand, bool expectedExcludeFromContext)
    {
        Assert.True(InteractiveBashExecutor.TryParse(input, out var command, out var excludeFromContext));
        Assert.Equal(expectedCommand, command);
        Assert.Equal(expectedExcludeFromContext, excludeFromContext);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("/")]
    [InlineData("!")]
    [InlineData("!!   ")]
    public void LeavesNonCommandsAndEmptyBangInputUnchanged(string input)
    {
        Assert.False(InteractiveBashExecutor.TryParse(input, out _, out _));
    }
}
