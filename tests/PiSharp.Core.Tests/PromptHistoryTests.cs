using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class PromptHistoryTests
{
    [Fact]
    public void PreviousAndNextRestoreDraft()
    {
        var history = new PromptHistory();
        history.Add("older");
        history.Add("newer");

        Assert.Equal("newer", history.Previous("draft"));
        Assert.Equal("older", history.Previous("ignored"));
        Assert.Equal("newer", history.Next());
        Assert.Equal("draft", history.Next());
        Assert.Null(history.Next());
    }

    [Fact]
    public void AddSuppressesConsecutiveDuplicatesAndCapsEntries()
    {
        var history = new PromptHistory();
        history.Add("same");
        history.Add("same");
        for (var index = 0; index < 101; index++)
        {
            history.Add($"prompt-{index}");
        }

        Assert.Equal(100, history.Entries.Count);
        Assert.DoesNotContain("same", history.Entries);
    }
}
