using PiSharp.Core;
using PiSharp.Runtime;

namespace PiSharp.Tests;

public sealed class ReadTextConformanceTests
{
    [Theory]
    [InlineData("a\nb\nc\n", 1, 2, "a\nb\n\n[2 more lines in file. Use offset=3 to continue.]")]
    [InlineData("a\nb\nc\n", 2, null, "b\nc\n")]
    [InlineData("a\nb\n", 3, null, "")]
    public void LineSelectionMatchesPinnedReadTool(string input, int offset, int? limit, string expected) =>
        Assert.Equal(expected, ReadTextPlanner.Select(input, "input.txt", offset, limit));

    [Fact]
    public async Task TruncationAndFirstLineFallbackMatchPinnedReadTool()
    {
        var lines = string.Concat(Enumerable.Repeat("a\n", 2002));
        var expected = string.Join("\n", Enumerable.Repeat("a", 2000)) + "\n\n[Showing lines 1-2000 of 2003. Use offset=2001 to continue.]";
        Assert.Equal(expected, ReadTextPlanner.Select(lines, "input.txt"));
        Assert.Equal("[Line 1 is 50.0KB, exceeds 50.0KB limit. Use bash: sed -n '1p' input.txt | head -c 51200]",
            ReadTextPlanner.Select(new string('x', 51201) + "\nlast", "input.txt"));
        Assert.Equal(new string('a', 30000) + "\n\n[Showing lines 1-1 of 3 (50.0KB limit). Use offset=2 to continue.]",
            ReadTextPlanner.Select(new string('a', 30000) + "\n" + new string('b', 30000) + "\nthird", "input.txt"));

        var directory = Path.Combine(Path.GetTempPath(), "pisharp-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "input.txt"), lines);
            Assert.Equal(expected, await new CodingTools(directory).Read("input.txt"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
