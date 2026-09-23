using PiSharp.Cli;

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
        Assert.Contains("ambiguous", await tools.Edit("file", "x", "y"));
        Assert.Contains("Could not find", await tools.Edit("file", "z", "y"));
        Assert.Equal("x x", await tools.Read("file"));
    }

    [Fact]
    public async Task BashReportsExitAndCanTimeOut()
    {
        var tools = new CodingTools(_dir);
        Assert.Contains("Process exited with code 7", await tools.Bash("exit 7"));
        Assert.Contains("timeout", await tools.Bash("sleep 10", timeout: 1));
    }
}
