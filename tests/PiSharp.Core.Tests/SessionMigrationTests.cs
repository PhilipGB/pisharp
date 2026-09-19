using System.Text.Json.Nodes;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

/// <summary>
/// Pinned migration.test.ts conformance: v1 entries gain collision-checked 8-hex ids and a
/// linear parentId chain, compaction firstKeptEntryIndex becomes firstKeptEntryId, v2
/// hookMessage roles become custom, and already-current files are left untouched.
/// </summary>
public sealed class SessionMigrationTests
{
    private static readonly System.Text.Json.JsonSerializerOptions Json =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public void V1EntriesGainIdsAndLinearParentChain()
    {
        var entries = ParseLines(
            """{"type":"session","id":"sess-1","timestamp":"2025-01-01T00:00:00Z","cwd":"/tmp"}""",
            """{"type":"message","timestamp":"2025-01-01T00:00:01Z","message":{"role":"user","content":"hi","timestamp":1}}""",
            """{"type":"message","timestamp":"2025-01-01T00:00:02Z","message":{"role":"assistant","content":[{"type":"text","text":"hello"}],"provider":"test","model":"test","usage":{"input":1,"output":1,"cacheRead":0,"cacheWrite":0},"stopReason":"stop","timestamp":2}}""");

        Assert.True(SessionMigration.MigrateEntries(entries));

        Assert.Equal(3, entries[0]!.AsObject()["version"]!.GetValue<int>());
        var first = entries[1]!.AsObject();
        var second = entries[2]!.AsObject();
        Assert.Matches("^[0-9a-f]{8}$", first["id"]!.GetValue<string>());
        Assert.Null(first["parentId"]);
        Assert.Equal(first["id"]!.GetValue<string>(), second["parentId"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{8}$", second["id"]!.GetValue<string>());
    }

    [Fact]
    public void MigrationIsIdempotentForCurrentVersionFiles()
    {
        var entries = ParseLines(
            """{"type":"session","id":"sess-1","version":3,"timestamp":"2025-01-01T00:00:00Z","cwd":"/tmp"}""",
            """{"type":"message","id":"abc12345","parentId":null,"timestamp":"2025-01-01T00:00:01Z","message":{"role":"user","content":"hi"}}""");

        Assert.False(SessionMigration.MigrateEntries(entries));
        Assert.Equal("abc12345", entries[1]!.AsObject()["id"]!.GetValue<string>());
    }

    [Fact]
    public void V2FileRenamesHookMessageToCustomAndKeepsExistingIds()
    {
        var entries = ParseLines(
            """{"type":"session","id":"sess-1","version":2,"timestamp":"2025-01-01T00:00:00Z","cwd":"/tmp"}""",
            """{"type":"message","id":"abc12345","parentId":null,"timestamp":"2025-01-01T00:00:01Z","message":{"role":"hookMessage","content":"hooked"}}""",
            """{"type":"message","id":"def67890","parentId":"abc12345","timestamp":"2025-01-01T00:00:02Z","message":{"role":"user","content":"hi"}}""");

        Assert.True(SessionMigration.MigrateEntries(entries));

        Assert.Equal(3, entries[0]!.AsObject()["version"]!.GetValue<int>());
        Assert.Equal("custom", entries[1]!.AsObject()["message"]!.AsObject()["role"]!.GetValue<string>());
        // v2 already has ids: they survive the version bump.
        Assert.Equal("abc12345", entries[1]!.AsObject()["id"]!.GetValue<string>());
        Assert.Equal("abc12345", entries[2]!.AsObject()["parentId"]!.GetValue<string>());
    }

    [Fact]
    public void CompactionFirstKeptEntryIndexBecomesFirstKeptEntryId()
    {
        var entries = ParseLines(
            """{"type":"session","id":"sess-1","timestamp":"2025-01-01T00:00:00Z","cwd":"/tmp"}""",
            """{"type":"message","timestamp":"2025-01-01T00:00:01Z","message":{"role":"user","content":"hi"}}""",
            """{"type":"compaction","timestamp":"2025-01-01T00:00:03Z","summary":"s","firstKeptEntryIndex":1}""");

        Assert.True(SessionMigration.MigrateEntries(entries));

        var message = entries[1]!.AsObject();
        var compaction = entries[2]!.AsObject();
        Assert.Equal(message["id"]!.GetValue<string>(), compaction["firstKeptEntryId"]!.GetValue<string>());
        Assert.Null(compaction["firstKeptEntryIndex"]);
    }

    [Fact]
    public void LegacyPiSharpV1DocumentsAreNeverMigrated()
    {
        var entries = ParseLines(
            """{"type":"session","version":1,"sessionId":"legacy-1","workingDirectory":"/tmp","model":"m","createdAtUtc":"2025-01-01T00:00:00Z"}""",
            """{"type":"turn","turnId":"t1","userMessage":"hi","assistantMessage":"yo","createdAtUtc":"2025-01-01T00:00:01Z"}""");

        Assert.False(SessionMigration.MigrateEntries(entries));
        Assert.Equal("turn", entries[1]!.AsObject()["type"]!.GetValue<string>());
        Assert.Null(entries[1]!.AsObject()["id"]);
    }

    [Fact]
    public void MigrateSessionLinesRewritesTheFileContentAndSkipsMalformedLines()
    {
        var lines = new[]
        {
            """{"type":"session","id":"sess-1","timestamp":"2025-01-01T00:00:00Z","cwd":"/tmp"}""",
            "{not json",
            """{"type":"message","timestamp":"2025-01-01T00:00:01Z","message":{"role":"user","content":"hi"}}""",
        };

        var migrated = SessionMigration.MigrateSessionLines(lines);

        Assert.NotNull(migrated);
        Assert.Equal(2, migrated!.Length);
        Assert.DoesNotContain("{not json", string.Join("\n", migrated!));
        Assert.Contains("\"version\":3", migrated[0]);
        Assert.Contains("\"id\":\"", migrated[1]);

        // A current-version file needs no rewrite.
        Assert.Null(SessionMigration.MigrateSessionLines(migrated));
    }

    // --- file-level integration (LoadAsync rewrites the file on load) --------

    [Fact]
    public async Task LoadAsyncMigratesAVersion1FileInPlace()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        Directory.CreateDirectory(store.WorkspaceDirectory);
        var path = Path.Combine(store.WorkspaceDirectory, "v1.jsonl");
        var v1User = new MessageEntry("u", null, DateTimeOffset.UtcNow,
            System.Text.Json.JsonSerializer.SerializeToElement(new { role = "user", content = "hi" }));
        var v1Assistant = new MessageEntry("a", "u", DateTimeOffset.UtcNow,
            System.Text.Json.JsonSerializer.SerializeToElement(new { role = "assistant", content = "yo" }));
        // Strip the ids to emulate a pre-v2 Pi file.
        var userLine = StripId(System.Text.Json.JsonSerializer.Serialize(v1User, Json));
        var assistantLine = StripId(System.Text.Json.JsonSerializer.Serialize(v1Assistant, Json));
        await File.WriteAllLinesAsync(path,
        [
            System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "session",
                id = "sess-1",
                timestamp = "2025-01-01T00:00:00Z",
                cwd = workspace,
            }),
            userLine,
            assistantLine,
        ]);

        var document = await store.LoadAsync(path);

        // The file was rewritten to v3 with a real id/parentId chain.
        var rewritten = await File.ReadAllLinesAsync(path);
        Assert.Contains("\"version\":3", rewritten[0]);
        var entries = new List<SessionEntry>();
        for (var i = 1; i < rewritten.Length; i++)
        {
            Assert.Contains($"\"id\":\"", rewritten[i]);
        }

        Assert.Equal(2, document.Entries.Count);
        // The in-memory graph reflects the migrated chain.
        Assert.Equal(document.Entries[0].Id, document.Entries[1].ParentId);

        // Second load is a no-op migration.
        var again = await store.LoadAsync(path);
        Assert.Equal(document.Entries[0].Id, again.Entries[0].Id);
    }

    private static List<JsonNode?> ParseLines(params string[] lines) =>
        lines.Select(line => JsonNode.Parse(line)).ToList();

    private static string StripId(string line)
    {
        var node = JsonNode.Parse(line)!.AsObject();
        node.Remove("id");
        node.Remove("parentId");
        return node.ToJsonString();
    }
}
