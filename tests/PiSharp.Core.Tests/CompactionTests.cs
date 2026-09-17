using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class CompactionTests
{
    [Fact]
    public void FindCutPointNeverStartsAtToolResult()
    {
        var entries = new SessionEntry[]
        {
            Message("user-1", null, "first"),
            Message("assistant-1", "user-1", "response"),
            Message("tool-1", "assistant-1", "result", "toolResult"),
            Message("user-2", "tool-1", "second"),
            Message("assistant-2", "user-2", "response"),
        };

        var result = PiCompactionPlanner.FindCutPoint(entries, 0, entries.Length, 1);

        Assert.NotEqual(2, result.FirstKeptEntryIndex);
        Assert.True(result.FirstKeptEntryIndex >= 3);
    }

    [Fact]
    public void ContextEntriesReplaceHistoryBeforeLatestCompaction()
    {
        var entries = new SessionEntry[]
        {
            Message("user-1", null, "old"),
            Message("assistant-1", "user-1", "old answer"),
            Message("user-2", "assistant-1", "kept"),
            new CompactionEntry("compact", "user-2", DateTimeOffset.UtcNow, "checkpoint", "user-2", 100),
            Message("assistant-2", "compact", "new answer"),
        };

        var context = PiCompactionPlanner.BuildContextEntries(entries);

        Assert.Equal(new[] { "compact", "user-2", "assistant-2" }, context.Select(entry => entry.Id));
    }

    [Fact]
    public void PrepareCompactionCarriesFileOperationsAndPreviousSummary()
    {
        var previousDetails = JsonSerializer.SerializeToElement(new
        {
            readFiles = new[] { "README.md" },
            modifiedFiles = new[] { "src/Program.cs" },
        });
        var entries = new SessionEntry[]
        {
            Message("user-1", null, "old"),
            new CompactionEntry("compact", "user-1", DateTimeOffset.UtcNow, "previous", "user-1", 50, previousDetails),
            Message("user-2", "compact", "continue"),
            Message("assistant-2", "user-2", "done", content: [ToolCall("read", "read", "docs.md")]),
        };

        var plan = PiCompactionPlanner.PrepareCompaction(
            entries,
            new CompactionSettings(keepRecentTokens: 1));

        Assert.NotNull(plan);
        Assert.Equal("previous", plan.PreviousSummary);
        Assert.Contains("README.md", plan.FileOperations.ReadFiles);
        Assert.Contains("src/Program.cs", plan.FileOperations.ModifiedFiles);
    }

    [Fact]
    public async Task BranchCollectionStopsAtCommonAncestorAndPreservesChronology()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var document = await store.CreatePiAsync();
        var root = Message("root", null, "root");
        var old = Message("old", "root", "old branch");
        var target = Message("target", "root", "target branch");
        var oldLeaf = Message("old-leaf", "old", "last old");
        await store.AppendEntriesAsync(document, [root, old, target, oldLeaf]);

        var plan = PiCompactionPlanner.CollectBranchSummary(document, oldLeaf.Id, target.Id);

        Assert.Equal("root", plan.CommonAncestorId);
        Assert.Equal(new[] { "old", "old-leaf" }, plan.Entries.Select(entry => entry.Id));
    }

    [Fact]
    public void RepeatedCompactionExcludesPreviousCompactionEntryFromConversation()
    {
        var entries = new SessionEntry[]
        {
            Message("user-1", null, "first"),
            Message("assistant-1", "user-1", "answer", "assistant"),
            new CompactionEntry("compact", "assistant-1", DateTimeOffset.UtcNow, "previous summary", "user-1", 100),
            Message("user-2", "compact", "second"),
            Message("assistant-2", "user-2", "second answer", "assistant"),
        };

        var plan = PiCompactionPlanner.PrepareCompaction(entries, new CompactionSettings(keepRecentTokens: 1))
            ?? throw new InvalidOperationException("expected a compaction plan");

        Assert.Equal("previous summary", plan.PreviousSummary);
        // The previous CompactionEntry is metadata for the update prompt, never conversation
        // content: it must not be serialized a second time into the new conversation.
        Assert.DoesNotContain(plan.MessagesToSummarize, entry => entry is CompactionEntry);
        Assert.DoesNotContain(plan.TurnPrefixMessages, entry => entry is CompactionEntry);
        var conversation = PiCompactionPlanner.SerializeForSummary(
            plan.MessagesToSummarize.Concat(plan.TurnPrefixMessages));
        Assert.DoesNotContain("previous summary", conversation);
        Assert.Contains("second", conversation);
    }

    [Fact]
    public void EstimateAnchorsToLastValidUsageAndSkipsAbortedOrZero()
    {
        var entries = new SessionEntry[]
        {
            Message("user-1", null, "old"),
            Message("assistant-1", "user-1", "old answer", "assistant",
                extra: new { usage = new { totalTokens = 1000 }, stopReason = "aborted" }),
            Message("user-2", "assistant-1", "new"),
            Message("assistant-2", "user-2", "new answer", "assistant",
                extra: new { usage = new { totalTokens = 5000 } }),
            Message("user-3", "assistant-2", new string('x', 400)),
        };

        var estimate = PiCompactionPlanner.EstimateContextTokens(entries);

        // Anchored to the 5000-token response; trailing user message adds 400 chars / 4.
        Assert.Equal(5000, estimate.UsageTokens);
        Assert.Equal(100, estimate.TrailingTokens);
        Assert.Equal(5100, estimate.Tokens);
        Assert.Equal(3, estimate.LastUsageEntryIndex);
    }

    [Fact]
    public void EstimateCountsImagesAsFixedCharacters()
    {
        var withImage = Message("user-1", null, string.Empty,
            content: [new { type = "text", text = "look" }, new { type = "image", data = "AAAA", mimeType = "image/png" }]);
        var withoutImage = Message("user-2", null, "look");

        var with = PiCompactionPlanner.EstimateEntryTokens(withImage);
        var without = PiCompactionPlanner.EstimateEntryTokens(withoutImage);

        // 4800 image chars + 4 text chars vs 4 text chars: exactly 1200 tokens of difference.
        Assert.Equal(1200, with - without);
    }

    [Fact]
    public void EstimateCountsAssistantThinkingAndToolCallArguments()
    {
        var plain = Message("assistant-1", null, "done", "assistant");
        var rich = Message("assistant-2", null, string.Empty, "assistant", content:
        [
            new { type = "text", text = "done" },
            new { type = "thinking", thinking = new string('t', 400) },
            ToolCall("toolCall", "read", "src/A.cs"),
        ]);

        var plainTokens = PiCompactionPlanner.EstimateEntryTokens(plain);
        var richTokens = PiCompactionPlanner.EstimateEntryTokens(rich);

        // 400 thinking chars + "read" (4) + {\"path\":\"src/A.cs\"} (20) = 424 chars = 106 tokens.
        Assert.Equal(plainTokens + 106, richTokens);
    }

    [Fact]
    public void EstimateCountsBashCommandPlusOutput()
    {
        var bash = new BashExecutionEntry("bash-1", null, DateTimeOffset.UtcNow, "dotnet build", new string('o', 400), 0, false);

        // "dotnet build" (12) + 400 output chars = 412 chars = 103 tokens.
        Assert.Equal(103, PiCompactionPlanner.EstimateEntryTokens(bash));
    }

    [Fact]
    public void ShouldCompactUsesStrictThresholdAboveWindowMinusReserve()
    {
        var settings = new CompactionSettings(reserveTokens: 1000);

        Assert.False(PiCompactionPlanner.ShouldCompact(9000, 10_000, settings));
        Assert.True(PiCompactionPlanner.ShouldCompact(9001, 10_000, settings));
        Assert.False(PiCompactionPlanner.ShouldCompact(int.MaxValue, 10_000, new CompactionSettings(enabled: false)));
    }

    [Fact]
    public void SplitTurnCutKeepsTurnStartForPrefixSummarization()
    {
        var entries = new SessionEntry[]
        {
            Message("user-1", null, "first"),
            Message("assistant-1", "user-1", "first answer", "assistant"),
            Message("user-2", "assistant-1", "second"),
            Message("assistant-2", "user-2", "second answer", "assistant"),
        };

        var wholeTurn = PiCompactionPlanner.FindCutPoint(entries, 0, entries.Length, 100);

        Assert.False(wholeTurn.IsSplitTurn);
        Assert.Equal(-1, wholeTurn.TurnStartIndex);

        // keepRecent of 1 token pushes the cut into the middle of the second turn: the plan
        // then exposes the turn prefix for separate summarization.
        var plan = PiCompactionPlanner.PrepareCompaction(entries, new CompactionSettings(keepRecentTokens: 1))
            ?? throw new InvalidOperationException("expected a compaction plan");
        if (plan.IsSplitTurn)
        {
            Assert.True(plan.TurnPrefixMessages.Count > 0);
            Assert.Equal("user-2", plan.TurnPrefixMessages[0].Id);
        }
    }

    [Fact]
    public void BranchSelectionExcludesToolResultsAndAppliesSummaryBudgetRule()
    {
        var compact = new CompactionEntry("compact", null, DateTimeOffset.UtcNow, new string('s', 2000), "root", 100);
        var oldUser = Message("old-user", null, new string('a', 4000));
        var oldAssistant = Message("old-assistant", "old-user", "work", "assistant",
            content: [ToolCall("toolCall", "read", "docs/spec.md")]);
        var toolResult = Message("tool-result", "old-assistant", new string('r', 8000), "toolResult");
        var recent = Message("recent", "tool-result", new string('z', 1000));
        var entries = new SessionEntry[] { compact, oldUser, oldAssistant, toolResult, recent };

        // Budget of 256 keeps only the recent message (250). The tool result is skipped
        // entirely (raw tool output is never summarized) even though it fits in no budget,
        // and the walk breaks at oldAssistant — whose tool call still contributes file ops.
        var plan = PiCompactionPlanner.PrepareBranchEntries(entries, "root", 256);

        Assert.DoesNotContain(plan.Entries, entry => entry is MessageEntry { Message: var m } && IsRole(m, "toolResult"));
        Assert.Contains(plan.Entries, entry => entry.Id == "recent");
        Assert.DoesNotContain(plan.Entries, entry => entry.Id == "old-user");
        Assert.DoesNotContain(plan.Entries, entry => entry.Id == "old-assistant");
        // File operations are extracted even from entries the budget walk breaks at.
        Assert.Contains("docs/spec.md", plan.FileOperations.ReadFiles);
    }

    [Fact]
    public void BranchSelectionForceIncludesSummariesUnderNinetyPercentBudget()
    {
        var compact = new CompactionEntry("compact", null, DateTimeOffset.UtcNow, new string('s', 4000), "root", 100);
        var oldUser = Message("old-user", null, new string('a', 4000));
        var entries = new SessionEntry[] { compact, oldUser };

        // Budget 1000: oldUser (1000 tokens) fills the budget; the 1000-token summary would
        // break it and the accumulated total (1000) is not below 90% of the budget, so the
        // summary is dropped. With budget 1800 the accumulated total (1000) is below 1620 and
        // the summary is force-included even though it breaks the budget.
        var tight = PiCompactionPlanner.PrepareBranchEntries(entries, "root", 1000);
        Assert.DoesNotContain(tight.Entries, entry => entry is CompactionEntry);

        var loose = PiCompactionPlanner.PrepareBranchEntries(entries, "root", 1800);
        Assert.Contains(loose.Entries, entry => entry is CompactionEntry);
    }

    [Fact]
    public void BranchSelectionCarriesNestedSummaryFileMetadataOnlyFromPiGeneratedSummaries()
    {
        var details = JsonSerializer.SerializeToElement(new
        {
            readFiles = new[] { "pi/read.md" },
            modifiedFiles = new[] { "pi/write.md" },
        });
        var hookDetails = JsonSerializer.SerializeToElement(new
        {
            readFiles = new[] { "hook/read.md" },
            modifiedFiles = new[] { "hook/write.md" },
        });
        var piSummary = new BranchSummaryEntry(
            "pi-summary", null, DateTimeOffset.UtcNow, "root", "summary", details, null, FromHook: false);
        var hookSummary = new BranchSummaryEntry(
            "hook-summary", null, DateTimeOffset.UtcNow, "root", "summary", hookDetails, null, FromHook: true);
        var entry = Message("entry", null, "work");

        var plan = PiCompactionPlanner.PrepareBranchEntries(
            [piSummary, hookSummary, entry], "root", tokenBudget: 0);

        Assert.Contains("pi/read.md", plan.FileOperations.ReadFiles);
        Assert.Contains("pi/write.md", plan.FileOperations.ModifiedFiles);
        Assert.DoesNotContain("hook/read.md", plan.FileOperations.ReadFiles);
        Assert.DoesNotContain("hook/write.md", plan.FileOperations.ModifiedFiles);
    }

    [Fact]
    public void CutPointNeverSplitsToolCallFromResult()
    {
        var entries = new SessionEntry[]
        {
            Message("user-1", null, "first"),
            Message("assistant-1", "user-1", string.Empty, "assistant",
                content: [ToolCall("toolCall", "read", "a.txt")]),
            Message("tool-1", "assistant-1", "data", "toolResult"),
            Message("user-2", "tool-1", "second"),
            Message("assistant-2", "user-2", "done", "assistant"),
        };

        // The first kept entry must never be the tool result for any retention budget: a cut
        // there would strand the result without its tool call.
        foreach (var keepRecent in new[] { 1, 5, 10, 20, 50 })
        {
            var result = PiCompactionPlanner.FindCutPoint(entries, 0, entries.Length, keepRecent);
            Assert.NotEqual("tool-1", entries[result.FirstKeptEntryIndex].Id);
        }
    }

    private static bool IsRole(JsonElement message, string role) =>
        message.TryGetProperty("role", out var value) &&
        string.Equals(value.GetString(), role, StringComparison.Ordinal);

    private static MessageEntry Message(
        string id,
        string? parentId,
        string text,
        string role = "user",
        object[]? content = null,
        object? extra = null)
    {
        var message = new Dictionary<string, object?>
        {
            ["role"] = role,
            ["content"] = content ?? [new { type = "text", text }],
        };
        if (extra is not null)
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(extra));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                message[property.Name] = property.Value.Clone();
            }
        }
        return new MessageEntry(id, parentId, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(message));
    }

    private static object ToolCall(string type, string name, string path) =>
        new { type = "toolCall", name, arguments = new { path } };
}
