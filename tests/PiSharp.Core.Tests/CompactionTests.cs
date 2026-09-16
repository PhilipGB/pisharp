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

    private static MessageEntry Message(
        string id,
        string? parentId,
        string text,
        string role = "user",
        object[]? content = null) =>
        new(
            id,
            parentId,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new
            {
                role,
                content = content ?? [new { type = "text", text }],
            }));

    private static object ToolCall(string type, string name, string path) =>
        new { type = "toolCall", name, arguments = new { path } };
}
