using PiSharp.Cli.Tui;

namespace PiSharp.Tests;

public sealed class TranscriptSearchControllerTests
{
    [Fact]
    public void QueryEditsSelectTheFirstMatchAtOrAfterTheCurrentTranscriptAnchor()
    {
        const string transcript = "alpha\nalpha\nalpha";
        var secondLineStart = transcript.IndexOf("alpha", transcript.IndexOf('\n') + 1, StringComparison.Ordinal);
        var search = new TranscriptSearchController();

        search.SetQuery("a", direction: 0, anchorOffset: secondLineStart);
        var firstQuery = search.Highlight(transcript);
        Assert.Equal(secondLineStart, firstQuery.SelectedTextStart);
        Assert.Equal(3, search.State.SelectedMatch);

        search.SetQuery("alpha", direction: 0, anchorOffset: secondLineStart);
        var refinedQuery = search.Highlight(transcript);

        Assert.Equal(secondLineStart, refinedQuery.SelectedTextStart);
        Assert.Equal(2, search.State.SelectedMatch);
    }
}
