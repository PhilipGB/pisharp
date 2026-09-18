using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class PiSessionStoreTests
{
    [Fact]
    public async Task PiV3SessionRoundTripsTypedEntriesAndTurnProjection()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var document = await store.CreatePiAsync();
        using var stateDocument = JsonDocument.Parse("{\"messages\":[1]}");
        var userId = Guid.NewGuid().ToString("N");
        var assistantId = Guid.NewGuid().ToString("N");
        var user = new MessageEntry(
            userId,
            null,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new { role = "user", content = "hello" }));
        var assistant = new MessageEntry(
            assistantId,
            userId,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new
            {
                role = "assistant",
                content = new[] { new { type = "text", text = "world" } },
            }));
        var state = new CustomEntry(
            Guid.NewGuid().ToString("N"),
            assistantId,
            DateTimeOffset.UtcNow,
            SessionEntryTypes.AgentStateCache,
            stateDocument.RootElement.Clone());
        await store.AppendEntriesAsync(document, [user, assistant, state]);

        var loaded = await store.LoadAsync(document.FilePath);
        var turn = Assert.Single(loaded.Turns);

        Assert.True(loaded.IsPiV3);
        Assert.Equal(3, loaded.PiHeader!.Version);
        Assert.Equal(3, loaded.Entries.Count);
        Assert.Equal("hello", turn.UserMessage);
        Assert.Equal("world", turn.AssistantMessage);
        Assert.Equal(1, turn.AgentState.GetProperty("messages")[0].GetInt32());
        var statistics = loaded.GetStatistics();
        Assert.Equal(1, statistics.UserMessages);
        Assert.Equal(1, statistics.AssistantMessages);
    }

    [Fact]
    public async Task PiV3ChronologyTracksToolResultsCacheAndActiveLeaf()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var document = await store.CreatePiAsync();
        var user = UserMessage("user", null, "inspect");
        var toolCall = AssistantMessage("assistant-tool", user.Id, new[]
        {
            new { type = "toolCall", id = "call-1", name = "read", arguments = (object)new { path = "README.md" } },
        });
        var toolResult = new MessageEntry(
            "tool-result",
            toolCall.Id,
            DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new
            {
                role = "toolResult",
                toolCallId = "call-1",
                toolName = "read",
                content = new[] { new { type = "text", text = "ok" } },
                isError = false,
            }));
        var final = AssistantMessage("assistant-final", toolResult.Id, new[]
        {
            new { type = "text", text = "done" },
        });
        var cache = new CustomEntry(
            "cache",
            final.Id,
            DateTimeOffset.UtcNow,
            SessionEntryTypes.AgentStateCache,
            JsonSerializer.SerializeToElement(new { restored = true }));
        var nextUser = UserMessage("next-user", cache.Id, "continue");
        await store.AppendEntriesAsync(document, [user, toolCall, toolResult, final, cache, nextUser]);

        var loaded = await store.LoadAsync(document.FilePath);

        Assert.Equal(nextUser.Id, loaded.LatestEntryId);
        Assert.Equal(new[] { user.Id, toolCall.Id, toolResult.Id, final.Id, cache.Id, nextUser.Id },
            loaded.GetActiveEntryPath(loaded.LatestEntryId).Select(entry => entry.Id));
        Assert.Equal(cache.Id, loaded.GetTurnLeafEntryId(final.Id));
        Assert.Equal(2, loaded.Turns.Count);
        Assert.True(loaded.LatestTurn!.AgentState.GetProperty("restored").GetBoolean());
        Assert.Equal(1, loaded.GetStatistics().ToolCalls);
    }

    [Fact]
    public async Task PiV3WriterOmitsOptionalNullAndFalseFields()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var document = await store.CreatePiAsync();
        var compaction = new CompactionEntry(
            "compact",
            null,
            DateTimeOffset.UtcNow,
            "summary",
            "first",
            10);
        await store.AppendEntriesAsync(document, [compaction]);

        var lines = await File.ReadAllLinesAsync(document.FilePath);

        Assert.DoesNotContain("parentSession", lines[0]);
        Assert.DoesNotContain("details", lines[1]);
        Assert.DoesNotContain("usage", lines[1]);
        Assert.DoesNotContain("fromHook", lines[1]);
    }

    [Fact]
    public async Task PiV3LabelEntriesRoundTripAndClear()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var document = await store.CreatePiAsync();
        var user = new MessageEntry("user", null, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { role = "user", content = "hi" }));
        var set = new LabelEntry("set", "user", DateTimeOffset.UtcNow, "user", "bookmark");
        await store.AppendEntriesAsync(document, [user, set]);

        var withLabel = await store.LoadAsync(document.FilePath);
        Assert.Equal("bookmark", withLabel.GetLabel("user"));

        var clear = new LabelEntry("clear", "set", DateTimeOffset.UtcNow, "user", null);
        await store.AppendEntriesAsync(withLabel, [clear]);

        var cleared = await store.LoadAsync(document.FilePath);
        Assert.Null(cleared.GetLabel("user"));
        var clearedLine = (await File.ReadAllLinesAsync(document.FilePath)).Last();
        Assert.Contains("\"targetId\":\"user\"", clearedLine);
        // The cleared label omits the label property (WhenWritingNull); "type":"label" must not trip this.
        Assert.DoesNotContain("\"label\":", clearedLine);
    }

    [Fact]
    public async Task PiV3ReaderRetainsLegacyStandaloneBashEntries()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var sessions = Path.Combine(temp.Path, "sessions");
        var store = new SessionStore(workspace, sessions);
        var path = Path.Combine(sessions, "legacy.jsonl");
        Directory.CreateDirectory(sessions);
        var header = PiSessionHeader.Create(workspace);
        var bash = new BashExecutionEntry("bash", null, DateTimeOffset.UtcNow, "pwd", "/workspace", 0, false);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await File.WriteAllLinesAsync(path, [
            JsonSerializer.Serialize(header, jsonOptions),
            JsonSerializer.Serialize(bash, jsonOptions),
        ]);

        var loaded = await store.LoadAsync(path);

        Assert.IsType<BashExecutionEntry>(Assert.Single(loaded.Entries));
    }

    [Fact]
    public async Task PiV3ForkPreservesSelectedPathAndStateCache()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var source = await store.CreatePiAsync();
        var user = new MessageEntry("user", null, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { role = "user", content = "hello" }));
        var assistant = new MessageEntry("assistant", "user", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { role = "assistant", content = "world" }));
        var state = new CustomEntry("state", "assistant", DateTimeOffset.UtcNow, SessionEntryTypes.AgentStateCache, JsonSerializer.SerializeToElement(new { cached = true }));
        var abandoned = new MessageEntry("abandoned", "user", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { role = "assistant", content = "other" }));
        await store.AppendEntriesAsync(source, [user, assistant, state, abandoned]);

        var fork = await store.ForkPiAsync(source, assistant.Id);

        Assert.Equal(new[] { "user", "assistant", "state" }, fork.Entries.Select(entry => entry.Id));
        Assert.Equal(source.FilePath, fork.PiHeader!.ParentSession);
    }

    [Fact]
    public async Task AppendEntries_FailedCommitDoesNotAdvanceTheInMemoryGraph()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var header = PiSessionHeader.Create(workspace);
        // The parent directory does not exist, so the durable commit (the file append)
        // fails. The in-memory graph must stay exactly where it was.
        var document = new SessionDocument(
            Path.Combine(temp.Path, "missing-dir", "session.jsonl"),
            header,
            []);

        var user = UserMessage("user", null, "hello");
        var assistant = AssistantMessage("assistant", user.Id, [new { type = "text", text = "world" }]);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            store.AppendEntriesAsync(document, [user, assistant]));

        Assert.Empty(document.Entries);
        Assert.Empty(document.Turns);
    }

    [Fact]
    public async Task AppendTurn_FailedCommitDoesNotAdvanceTheInMemoryGraph()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var header = SessionHeader.Create(temp.Path, "model");
        var document = new SessionDocument(Path.Combine(temp.Path, "missing-dir", "session.jsonl"), header);
        using var json = JsonDocument.Parse("{\"state\":true}");
        var root = SessionTurn.Create(null, "first", "assistant", json.RootElement);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            store.AppendTurnAsync(document, root));

        Assert.Empty(document.Turns);
    }

    private static MessageEntry UserMessage(string id, string? parentId, string content) =>
        new(id, parentId, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new { role = "user", content }));

    private static MessageEntry AssistantMessage(string id, string? parentId, object[] content) =>
        new(id, parentId, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new { role = "assistant", content }));
}
