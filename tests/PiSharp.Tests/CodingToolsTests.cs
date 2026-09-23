using PiSharp.Cli;
using PiSharp.Runtime;

namespace PiSharp.Tests;

public sealed class CodingToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pisharp-test-" + Guid.NewGuid());
    public CodingToolsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task WriteReadAndEditResolveAgainstWorkingDirectory()
    {
        var tools = new CodingTools(_dir);
        Assert.Equal("Successfully wrote to nested/file.txt", await tools.Write("nested/file.txt", "one\r\ntwo\r\n"));
        Assert.Contains("one\r\ntwo", await tools.Read("nested/file.txt"));
        Assert.Equal("Successfully replaced 1 block(s) in nested/file.txt.", await tools.Edit("nested/file.txt", "one\ntwo", "three\nfour"));
        Assert.Equal("three\r\nfour\r\n", await File.ReadAllTextAsync(Path.Combine(_dir, "nested/file.txt")));
    }

    [Fact]
    public async Task EditFailsWithoutMutatingWhenAmbiguousOrAbsent()
    {
        var tools = new CodingTools(_dir);
        await tools.Write("file", "x x");
        Assert.Contains("2 occurrences", await tools.Edit("file", "x", "y"));
        Assert.Contains("Could not find", await tools.Edit("file", "z", "y"));
        Assert.Equal("x x", await tools.Read("file"));
    }

    [Fact]
    public async Task BashReportsExitAndCanTimeOut()
    {
        var tools = new CodingTools(_dir);
        Assert.Contains("Error: Command exited with code 7", await tools.Bash("exit 7"));
        Assert.Contains("timed out after 1 seconds", await tools.Bash("sleep 10", timeout: 1));
    }

    [Fact]
    public async Task BashCancellationAndTimeoutKillDescendantsAfterParentExits()
    {
        if (!OperatingSystem.IsLinux()) return;
        var tools = new CodingTools(_dir);
        using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Assert.Contains("Command aborted", await tools.Bash("sleep 10", cancellationToken: abort.Token));

        // The shell exits immediately, but its child still owns stdout/stderr.
        // The timeout must cover stream draining too and kill the entire process group.
        var result = await tools.Bash("(sleep 3; touch escaped) &", timeout: 1);
        Assert.Contains("timed out after 1 seconds", result);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.False(File.Exists(Path.Combine(_dir, "escaped")));
    }
    [Fact]
    public async Task BashBoundsOutputAndRetainsPrivateCompleteLog()
    {
        var tools = new CodingTools(_dir);
        var output = await tools.Bash("seq 1 3000");
        Assert.Contains("3000", output);
        Assert.Contains("Full output: ", output);
        var path = output.Split("Full output: ")[1].Split(']')[0];
        try
        {
            Assert.Contains("1\n2\n", await File.ReadAllTextAsync(path));
            Assert.Contains("2999\n3000", await File.ReadAllTextAsync(path));
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { File.Delete(path); }
    }
}
