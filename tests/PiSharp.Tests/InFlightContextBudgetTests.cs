using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class InFlightContextBudgetTests
{
    [Theory]
    [InlineData("user")]
    [InlineData("call")]
    [InlineData("result")]
    public async Task CacheInvalidatesOnChangedPrefixContentButNotChangedRecentPrompt(string changedPart)
    {
        var summaries = 0;
        var budget = new InFlightContextBudget(new AutoCompactionPolicy(2700, 300),
            (_, _) => Task.FromResult(new PiAgent.CompactionSummary($"summary #{++summaries}", null)),
            (_, _) => Task.CompletedTask);

        var first = await budget.ProjectAsync(History(null, "new prompt 1"), force: true, CancellationToken.None);
        var reused = await budget.ProjectAsync(History(null, "new prompt 2"), force: true, CancellationToken.None);
        var changed = await budget.ProjectAsync(History(changedPart, "new prompt 3"), force: true, CancellationToken.None);
        var reusedChanged = await budget.ProjectAsync(History(changedPart, "new prompt 4"), force: true, CancellationToken.None);

        Assert.Equal(2, summaries);
        Assert.Contains("summary #1", first[0].Text);
        Assert.Contains("summary #1", reused[0].Text);
        Assert.Contains("summary #2", changed[0].Text);
        Assert.Contains("summary #2", reusedChanged[0].Text);
        Assert.Equal("new prompt 4", reusedChanged[^1].Text);
    }

    private static ChatMessage[] History(string? changedPart, string recentPrompt) =>
    [
        new(ChatRole.User, changedPart == "user" ? "text Z" : "text A"),
        new(ChatRole.Assistant,
        [
            new FunctionCallContent("call-1", "read", new Dictionary<string, object?>
            {
                ["path"] = changedPart == "call" ? "b.txt" : "a.txt"
            })
        ]),
        new(ChatRole.Tool, [new FunctionResultContent("call-1", changedPart == "result" ? "value Z" : "value A")]),
        new(ChatRole.User, recentPrompt)
    ];
}
