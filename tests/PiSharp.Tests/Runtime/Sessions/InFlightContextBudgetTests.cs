using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class InFlightContextBudgetTests
{
    [Fact]
    public async Task PresentationDetailsDoNotConsumeTheProviderContinuationBudget()
    {
        var summaries = 0;
        var budget = new InFlightContextBudget(() => new AutoCompactionPolicy(2700, 300),
            (_, _) => Task.FromResult(new PiAgent.CompactionSummary($"summary #{++summaries}", null)),
            (_, _) => Task.CompletedTask);
        var outcome = new PiSharpToolResult("changed one line", Details: new { patch = new string('x', 100_000) });
        var messages = new ChatMessage[]
        {
            new(ChatRole.User, "edit the file"),
            new(ChatRole.Assistant,
                [new FunctionCallContent("edit-1", "edit", new Dictionary<string, object?> { ["path"] = "file.txt" })]),
            new(ChatRole.Tool, [new FunctionResultContent("edit-1", outcome)])
        };

        var projected = await budget.ProjectAsync(messages, force: false, CancellationToken.None);

        Assert.Same(messages, projected);
        Assert.Equal(0, summaries);
        Assert.Same(outcome, messages[^1].Contents.OfType<FunctionResultContent>().Single().Result);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("call")]
    [InlineData("result")]
    public async Task CacheInvalidatesOnChangedPrefixContentButNotChangedRecentPrompt(string changedPart)
    {
        var summaries = 0;
        var budget = new InFlightContextBudget(() => new AutoCompactionPolicy(2700, 300),
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

    [Fact]
    public async Task OversizedActiveToolResultIsSummarizedInsteadOfKeptInTransientRequest()
    {
        var summaries = 0;
        var budget = new InFlightContextBudget(() => new AutoCompactionPolicy(2700, 300),
            (_, _) => Task.FromResult(new PiAgent.CompactionSummary($"summary #{++summaries}", null)),
            (_, _) => Task.CompletedTask);
        var raw = new ChatMessage[]
        {
            new(ChatRole.User, "old history"),
            new(ChatRole.Assistant, "old answer"),
            new(ChatRole.User, "read the large file"),
            new(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "read", new Dictionary<string, object?> { ["path"] = "big.txt" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", new string('x', 8000))])
        };

        var projected = await budget.ProjectAsync(raw, force: true, CancellationToken.None);
        Assert.Equal(1, summaries);
        Assert.Single(projected);
        Assert.Contains("summary #1", projected[0].Text);
        Assert.Single(raw.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Single(raw.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
    }

    [Fact]
    public async Task SteeringAfterOversizedToolResultDoesNotHideTheActiveTurnFromCompaction()
    {
        var budget = new InFlightContextBudget(() => new AutoCompactionPolicy(2700, 300, 200),
            (_, _) => Task.FromResult(new PiAgent.CompactionSummary("older tool output summarized", null)),
            (_, _) => Task.CompletedTask);
        var messages = new ChatMessage[]
        {
            new(ChatRole.User, "older question"),
            new(ChatRole.Assistant, "older answer"),
            new(ChatRole.User, "read the large file"),
            new(ChatRole.Assistant,
                [new FunctionCallContent("call-1", "read", new Dictionary<string, object?> { ["path"] = "big.txt" })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", new string('x', 20_000))]),
            new(ChatRole.User, "change direction")
        };

        var projected = await budget.ProjectAsync(messages, force: true, CancellationToken.None);

        Assert.Contains(projected, message => message.Text == "change direction");
        Assert.DoesNotContain(projected.SelectMany(message => message.Contents), content => content is FunctionCallContent);
        Assert.DoesNotContain(projected.SelectMany(message => message.Contents), content => content is FunctionResultContent);
        Assert.Contains(projected, message => message.Text.Contains("older tool output summarized", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActivePolicyChangeAppliesToTheNextProviderRequest()
    {
        var policy = new AutoCompactionPolicy(100_000, 100);
        var summaries = 0;
        var budget = new InFlightContextBudget(() => policy,
            (_, _) => Task.FromResult(new PiAgent.CompactionSummary($"summary #{++summaries}", null)),
            (_, _) => Task.CompletedTask);
        var history = new ChatMessage[]
        {
            new(ChatRole.User, new string('x', 3000)),
            new(ChatRole.Assistant, "old answer"),
            new(ChatRole.User, "continue")
        };

        var unchanged = await budget.ProjectAsync(history, force: false, CancellationToken.None);
        policy = new AutoCompactionPolicy(2200, 500);
        var projected = await budget.ProjectAsync(history, force: false, CancellationToken.None);

        Assert.Same(history, unchanged);
        Assert.Equal(1, summaries);
        Assert.Contains("summary #1", projected[0].Text);
        Assert.Equal("continue", projected[^1].Text);
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
