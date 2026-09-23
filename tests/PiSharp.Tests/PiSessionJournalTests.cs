using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Tests;

public sealed class PiSessionJournalTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Theory]
    [InlineData("f6666666")]
    [InlineData("daaaaaaa")]
    public void ProjectionMatchesPinnedPiV3ForBothBranches(string leaf)
    {
        var journal = PiSessionJournal.Parse(Fixture("session-v3-branch.jsonl"));
        journal.Tree.Select(leaf);
        var projected = PiSessionProjection.Build(journal.Tree);
        using var expected = JsonDocument.Parse(Fixture("session-v3-context.json"));
        var context = expected.RootElement.GetProperty(leaf);
        Assert.Equal(context.GetProperty("thinkingLevel").GetString(), projected.ThinkingLevel);
        var model = context.GetProperty("model");
        Assert.Equal(model.ValueKind == JsonValueKind.Null ? null : model.GetProperty("provider").GetString(), projected.Provider);
        Assert.Equal(model.ValueKind == JsonValueKind.Null ? null : model.GetProperty("modelId").GetString(), projected.ModelId);
        var messages = JsonSerializer.SerializeToElement(projected.Messages);
        Assert.True(JsonElement.DeepEquals(context.GetProperty("messages"), messages), $"Expected: {context.GetProperty("messages")}; actual: {messages}");
        Assert.Equal(11, journal.ToJsonLines().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(leaf, journal.Tree.HeadId);
    }

    [Fact]
    public void RoundTripPreservesRawEntriesAndBranchingWithoutChangingPriorHistory()
    {
        var journal = PiSessionJournal.Parse(Fixture("session-v3-branch.jsonl"));
        journal.Tree.Select("b2222222");
        var added = journal.Append("message", JsonSerializer.SerializeToElement(new { message = new { role = "user", content = "new branch", timestamp = 1733234411000L } }));
        Assert.Equal("b2222222", added.ParentId);
        var reloaded = PiSessionJournal.Parse(journal.ToJsonLines());
        Assert.Equal(11, reloaded.Tree.Entries.Count);
        Assert.Equal(added.Id, reloaded.Tree.HeadId);
        Assert.DoesNotContain(reloaded.Tree.ActivePath(), node => node.Id == "f6666666");
        reloaded.Tree.Select("f6666666");
        Assert.Equal("next", PiSessionProjection.Build(reloaded.Tree).Messages.Last().GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("session-v1-linear.jsonl")]
    [InlineData("session-v2-linear.jsonl")]
    public void MigratesLegacyLinearHistoryAndCompactionLikePinnedPi(string fixture)
    {
        var journal = PiSessionJournal.Parse(Fixture(fixture));
        var entries = journal.Tree.Entries;
        Assert.Equal(3, entries.Count);
        Assert.Null(entries[0].ParentId);
        Assert.Equal(entries[0].Id, entries[1].ParentId);
        Assert.Equal(entries[1].Id, entries[2].ParentId);
        Assert.Equal("custom", entries[1].Payload.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal(entries[0].Id, entries[2].Payload.GetProperty("firstKeptEntryId").GetString());
        Assert.False(entries[2].Payload.TryGetProperty("firstKeptEntryIndex", out _));
        using var expected = JsonDocument.Parse("[" +
            "{\"role\":\"compactionSummary\",\"summary\":\"compact\",\"tokensBefore\":100,\"timestamp\":1733234403000}," +
            "{\"role\":\"user\",\"content\":\"hello\",\"timestamp\":1733234401000}," +
            "{\"role\":\"custom\",\"content\":\"legacy\",\"timestamp\":1733234402000}]");
        Assert.True(JsonElement.DeepEquals(expected.RootElement, JsonSerializer.SerializeToElement(PiSessionProjection.Build(journal.Tree).Messages)));
        Assert.Contains("\"version\":3", journal.ToJsonLines());
        Assert.Equal(3, PiSessionJournal.Parse(journal.ToJsonLines()).Tree.Entries.Count);
    }

    [Fact]
    public async Task SavedJsonlIsPrivateAndReloadable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pisharp-session-test-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(directory, "nested", "session.jsonl");
        try
        {
            var journal = PiSessionJournal.Parse(Fixture("session-v3-branch.jsonl"));
            await PiSharp.Runtime.PiSessionFiles.SaveAsync(journal, file);
            if (OperatingSystem.IsLinux())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            var reloaded = await PiSharp.Runtime.PiSessionFiles.LoadAsync(file);
            Assert.Equal(journal.Id, reloaded.Id);
            Assert.Equal(journal.Tree.Entries.Count, reloaded.Tree.Entries.Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void MalformedAndUnsupportedVersionsAreRejectedRatherThanWrittenOver()
    {
        Assert.Throws<InvalidDataException>(() => PiSessionJournal.Parse(""));
        var fresh = new PiSessionJournal("/project");
        Assert.Throws<ArgumentException>(() => fresh.Append("message", JsonSerializer.SerializeToElement(new { id = "spoofed" })));
        Assert.Empty(fresh.Tree.Entries);
        Assert.Throws<InvalidDataException>(() => PiSessionJournal.Parse(Fixture("session-v3-branch.jsonl").Replace("\"version\":3", "\"version\":4", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => PiSessionJournal.Parse(Fixture("session-v3-branch.jsonl") + "{bad json}\n"));
        Assert.Throws<InvalidDataException>(() => PiSessionJournal.Parse(Fixture("session-v3-branch.jsonl").Replace("\"parentId\":\"a1111111\"", "\"parentId\":\"missing\"", StringComparison.Ordinal)));
    }
}
