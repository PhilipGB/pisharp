using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class OutputTruncatorTests
{
    [Fact]
    public void TailKeepsLatestLinesAndReportsTruncation()
    {
        var result = OutputTruncator.Tail("one\ntwo\nthree\nfour", maxLines: 2, maxBytes: 100);

        Assert.Equal("three\nfour", result.Content);
        Assert.True(result.Truncated);
        Assert.Equal("lines", result.TruncatedBy);
        Assert.Equal(4, result.TotalLines);
        Assert.Equal(2, result.OutputLines);
    }

    [Fact]
    public void HeadHonorsUtf8ByteLimitWithoutSplittingLines()
    {
        var result = OutputTruncator.Head("alpha\nééé\nomega", maxLines: 10, maxBytes: 7);

        Assert.Equal("alpha", result.Content);
        Assert.True(result.Truncated);
        Assert.Equal("bytes", result.TruncatedBy);
    }

    [Fact]
    public void TailCanReturnUtf8SuffixForOneOversizedLine()
    {
        var result = OutputTruncator.Tail("0123456789", maxLines: 2, maxBytes: 4);

        Assert.Equal("6789", result.Content);
        Assert.True(result.LastLinePartial);
    }
}
