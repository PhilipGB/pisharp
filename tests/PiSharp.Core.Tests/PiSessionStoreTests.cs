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
        // The pinned lazy-flush contract materializes the file with the first assistant
        // message; the compaction entry follows as a plain append.
        await store.AppendEntriesAsync(document, [UserMessage("user", null, "hello"), AssistantMessage("assistant", "user", [new { type = "text", text = "world" }])]);
        var compaction = new CompactionEntry(
            "compact",
            "assistant",
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
        var assistant = new MessageEntry("assistant", "user", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { role = "assistant", content = "there" }));
        var set = new LabelEntry("set", "assistant", DateTimeOffset.UtcNow, "user", "bookmark");
        await store.AppendEntriesAsync(document, [user, assistant, set]);

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

    [Fact]
    public async Task PiV3SessionFileIsDeferredUntilTheFirstAssistantMessage()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var document = await store.CreatePiAsync();

        // Pinned lazy-flush contract: creation, model/thinking entries, and user prompts
        // never materialize the file on disk.
        Assert.False(File.Exists(document.FilePath));
        var modelChange = new ModelChangeEntry("model", null, DateTimeOffset.UtcNow, "openai", "gpt-test");
        await store.AppendEntriesAsync(document, [modelChange]);
        var user = UserMessage("user", "model", "hello");
        await store.AppendEntriesAsync(document, [user]);
        Assert.False(File.Exists(document.FilePath));

        // The first assistant message writes the whole session in one materialization.
        var assistant = AssistantMessage("assistant", "user", [new { type = "text", text = "world" }]);
        await store.AppendEntriesAsync(document, [assistant]);
        Assert.True(File.Exists(document.FilePath));

        var lines = await File.ReadAllLinesAsync(document.FilePath);
        Assert.Equal(4, lines.Length);
        Assert.Contains("\"type\":\"session\"", lines[0]);
        Assert.Contains("\"type\":\"model_change\"", lines[1]);

        // Subsequent entries are plain appends.
        var next = UserMessage("next", "assistant", "again");
        await store.AppendEntriesAsync(document, [next]);
        Assert.Equal(5, (await File.ReadAllLinesAsync(document.FilePath)).Length);
    }

    [Fact]
    public async Task PiV3SessionWithoutAnAssistantResponseNeverAppearsInListings()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var document = await store.CreatePiAsync();
        await store.AppendEntriesAsync(document, [UserMessage("user", null, "never answered")]);

        Assert.Empty(await store.ListInfosAsync());
        Assert.Null(await store.ContinueAsync());
    }

    [Fact]
    public async Task PiV3ExplicitPathIsPreservedForAMissingSessionFile()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var explicitPath = Path.Combine(temp.Path, "elsewhere", "chosen.jsonl");

        var document = await store.CreatePiAtAsync(explicitPath);

        Assert.Equal(Path.GetFullPath(explicitPath), document.FilePath);
        Assert.False(File.Exists(document.FilePath));
        await store.AppendEntriesAsync(document, [UserMessage("user", null, "hi"),
            AssistantMessage("assistant", "user", [new { type = "text", text = "yo" }])]);
        Assert.True(File.Exists(document.FilePath));
    }

    private static MessageEntry UserMessage(string id, string? parentId, string content) =>
        new(id, parentId, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new { role = "user", content }));

    [Fact]
    public async Task ResolveFallsBackToOtherProjectsUnderTheDefaultSessionRoot()
    {
        using var temp = TempDirectory.Create();
        var sessionRoot = Path.Combine(temp.Path, "sessions");
        var currentCwd = Directory.CreateDirectory(Path.Combine(temp.Path, "current")).FullName;
        var foreignCwd = Directory.CreateDirectory(Path.Combine(temp.Path, "foreign")).FullName;

        // A materialized session that belongs to a different project directory. Both stores
        // share a temp agent directory so the canonical layout stays inside the test tree.
        var foreignStore = new SessionStore(foreignCwd, null, temp.Path);
        var foreign = await foreignStore.CreatePiAsync(CancellationToken.None);
        await foreignStore.AppendEntriesAsync(foreign,
        [
            new MessageEntry("fu", null, DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(new { role = "user", content = "foreign prompt" })),
            new MessageEntry("fa", "fu", DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(new { role = "assistant", content = "foreign answer" })),
        ], CancellationToken.None);
        var foreignId = foreign.PiHeader!.Id;

        // The current project has no session with that id... and none at all.
        var store = new SessionStore(currentCwd, null, temp.Path);
        Assert.Empty(await store.ListInfosAsync(CancellationToken.None));

        // ...but the global search finds it and reports the foreign cwd.
        var resolution = await store.ResolveAsync(foreignId, CancellationToken.None);
        Assert.NotNull(resolution);
        Assert.Equal(foreign.FilePath, resolution!.Path);
        Assert.Equal(foreignCwd, resolution.ForeignCwd);
    }

    private static MessageEntry AssistantMessage(string id, string? parentId, object[] content) =>
        new(id, parentId, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new { role = "assistant", content }));

    [Fact]
    public async Task ContinueWithAnExplicitSessionDirIgnoresTheLegacyDirectory()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "custom"));

        var aTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var aPath = Path.Combine(store.WorkspaceDirectory, "a.jsonl");
        await WritePiSessionFileAsync(aPath, "aaaaaaaaaaaa", workspace, aTime);

        // A newer session in the legacy ~/.pisharp/sessions directory for the same
        // workspace: the explicit scope must not see it.
        var bTime = aTime.AddMinutes(5);
        var bPath = Path.Combine(store.LegacyWorkspaceDirectory, "b.jsonl");
        Directory.CreateDirectory(store.LegacyWorkspaceDirectory);
        await WritePiSessionFileAsync(bPath, "bbbbbbbbbbbb", workspace, bTime);

        var continued = await store.ContinueAsync(CancellationToken.None);

        Assert.NotNull(continued);
        Assert.Equal(aPath, continued!.FilePath);
    }

    [Fact]
    public async Task ResolveWithAnExplicitSessionDirCannotSeeTheLegacyDirectory()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "custom"));

        // The session exists only in the legacy directory.
        var bPath = Path.Combine(store.LegacyWorkspaceDirectory, "b.jsonl");
        Directory.CreateDirectory(store.LegacyWorkspaceDirectory);
        await WritePiSessionFileAsync(
            bPath, "cafe0123abcd", workspace, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // Neither the full id nor an id prefix may resolve from the explicit scope.
        Assert.Null(await store.ResolveAsync("cafe0123abcd", CancellationToken.None));
        Assert.Null(await store.ResolveAsync("cafe", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveIdPrefixesAreCaseSensitive()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));

        // An id whose letters make case matter (pinned startsWith is case-sensitive).
        const string id = "AbCd1234EfGh5678";
        var path = Path.Combine(store.WorkspaceDirectory, $"{id}.jsonl");
        await WritePiSessionFileAsync(path, id, workspace, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // The exact id (correct case) resolves.
        Assert.Equal(path, (await store.ResolveAsync(id, CancellationToken.None))!.Path);
        // A correct-case prefix resolves.
        Assert.Equal(path, (await store.ResolveAsync("AbCd", CancellationToken.None))!.Path);
        // Wrong-case prefixes do not resolve (local or global scope).
        Assert.Null(await store.ResolveAsync("abcd", CancellationToken.None));
        Assert.Null(await store.ResolveAsync("ABCD", CancellationToken.None));
    }

    /// <summary>
    /// Writes a minimal materialized Pi v3 session file (header + user/assistant messages)
    /// with a fixed id, cwd, and entry timestamps.
    /// </summary>
    private static async Task WritePiSessionFileAsync(string path, string id, string cwd, DateTimeOffset time)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var header = new PiSessionHeader("session", 3, id, time, Path.GetFullPath(cwd));
        var user = new MessageEntry("u", null, time,
            JsonSerializer.SerializeToElement(new { role = "user", content = "prompt" }));
        var assistant = new MessageEntry("a", "u", time.AddMilliseconds(500),
            JsonSerializer.SerializeToElement(new { role = "assistant", content = "answer" }));
        await File.WriteAllLinesAsync(path, [
            JsonSerializer.Serialize(header, options),
            JsonSerializer.Serialize(user, options),
            JsonSerializer.Serialize(assistant, options),
        ]);
        File.SetLastWriteTimeUtc(path, time.UtcDateTime);
    }
}
