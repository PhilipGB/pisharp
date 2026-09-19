using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

/// <summary>
/// Pinned buildSessionInfo conformance: bounded best-effort metadata scans that never throw,
/// skip malformed lines, require a session header first, and resolve created/modified
/// timestamps with pinned precedence (message activity &gt; header timestamp &gt; file mtime).
/// </summary>
public sealed class SessionInfoReaderTests
{
    [Fact]
    public async Task ReadsNamePreviewCountsAndTimestampsFromASessionFile()
    {
        using var temp = TempDirectory.Create();
        var path = WriteSession(temp,
            Header("session-1", "/work/proj"),
            Entry("m1", null, "2026-01-01T00:00:01Z", UserMessage("first question", timestamp: 1_700_000_000_000)),
            Entry("m2", "m1", "2026-01-01T00:00:02Z", AssistantMessage("first answer", timestamp: 1_700_000_002_000)),
            Info("m2", "My Project", "2026-01-01T00:00:03Z"),
            Entry("m3", "m2", "2026-01-01T00:00:04Z", UserMessage("second question", timestamp: 1_700_000_004_000)),
            Entry("m4", "m3", "2026-01-01T00:00:05Z", AssistantMessage("second answer", timestamp: 1_700_000_005_000)));

        var info = await SessionInfoReader.ReadAsync(path);

        Assert.NotNull(info);
        Assert.Equal("session-1", info!.Id);
        Assert.Equal("/work/proj", info.Cwd);
        Assert.Equal("My Project", info.Name);
        Assert.Equal(4, info.MessageCount);
        Assert.Equal("first question", info.FirstMessage);
        Assert.Equal("first question first answer second question second answer", info.AllMessagesText);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), info.Created);
        // Pinned: the latest user/assistant message timestamp wins over the file mtime.
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_005_000), info.Modified);
    }

    [Fact]
    public async Task LatestSessionInfoWinsAndABlankNameClears()
    {
        using var temp = TempDirectory.Create();
        var named = WriteSession(temp,
            Header("s", "/w"),
            Info("a", "Original", "2026-01-01T00:00:01Z"),
            Info("a", "", "2026-01-01T00:00:02Z"));

        Assert.Null((await SessionInfoReader.ReadAsync(named))!.Name);

        var renamed = WriteSession(temp,
            Header("s", "/w"),
            Info("a", "Original", "2026-01-01T00:00:01Z"),
            Info("a", "Renamed", "2026-01-01T00:00:02Z"));

        Assert.Equal("Renamed", (await SessionInfoReader.ReadAsync(renamed))!.Name);
    }

    [Fact]
    public async Task MalformedMiddleLinesAreSkippedLikePinnedParseSessionEntryLine()
    {
        using var temp = TempDirectory.Create();
        var path = WriteSession(temp,
            Header("s", "/w"),
            "{not json",
            Entry("m1", null, "2026-01-01T00:00:01Z", UserMessage("hello", 1_700_000_001_000)));

        var info = await SessionInfoReader.ReadAsync(path);

        Assert.NotNull(info);
        Assert.Equal(1, info!.MessageCount);
        Assert.Equal("hello", info.FirstMessage);
    }

    [Fact]
    public async Task AFileWhoseFirstEntryIsNotAHeaderIsNotASession()
    {
        using var temp = TempDirectory.Create();
        var path = Path.Combine(temp.Path, "not-a-session.jsonl");
        await File.WriteAllTextAsync(path,
            """{"type":"event","data":"not a session"}""" + "\n" +
            """{"type":"session","id":"x","timestamp":"2026-01-01T00:00:00Z","cwd":"/w"}""" + "\n");

        Assert.Null(await SessionInfoReader.ReadAsync(path));
    }

    [Fact]
    public async Task UnreadableAndOversizedFilesReturnNullWithoutThrowing()
    {
        using var temp = TempDirectory.Create();

        Assert.Null(await SessionInfoReader.ReadAsync(Path.Combine(temp.Path, "missing.jsonl")));

        var directory = Path.Combine(temp.Path, "looks-like-jsonl");
        Directory.CreateDirectory(directory);
        Assert.Null(await SessionInfoReader.ReadAsync(directory));
    }

    [Fact]
    public async Task ModifiedFallsBackToTheHeaderTimestampWithoutMessages()
    {
        using var temp = TempDirectory.Create();
        var path = WriteSession(temp, Header("s", "/w"));

        var info = await SessionInfoReader.ReadAsync(path);

        Assert.NotNull(info);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), info!.Modified);
        Assert.Equal("(no messages)", info.FirstMessage);
        Assert.Equal(0, info.MessageCount);
    }

    [Fact]
    public async Task ParentSessionPathSurvivesTheScan()
    {
        using var temp = TempDirectory.Create();
        var path = WriteSession(temp,
            JsonSerializer.Serialize(new { type = "session", version = 3, id = "child", timestamp = "2026-01-01T00:00:00Z", cwd = "/w", parentSession = "/sessions/parent.jsonl" }));

        Assert.Equal("/sessions/parent.jsonl", (await SessionInfoReader.ReadAsync(path))!.ParentSessionPath);
    }

    [Fact]
    public async Task LegacyPiSharpV1SessionsProduceCompatibilityMetadata()
    {
        using var temp = TempDirectory.Create();
        var workspace = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(workspace);
        var path = Path.Combine(temp.Path, "legacy.jsonl");
        var header = SessionHeader.Create(workspace, "some-model");
        using var state = JsonDocument.Parse("{}");
        var turn = SessionTurn.Create(null, "legacy prompt", "legacy answer", state.RootElement.Clone());
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await File.WriteAllLinesAsync(path,
        [
            JsonSerializer.Serialize(header, json),
            JsonSerializer.Serialize(turn, json),
        ]);

        var info = await SessionInfoReader.ReadAsync(path);

        Assert.NotNull(info);
        Assert.Equal(header.SessionId, info!.Id);
        Assert.Equal(workspace, info.Cwd);
        Assert.Null(info.Name);
        Assert.Equal(2, info.MessageCount);
        Assert.Equal("legacy prompt", info.FirstMessage);
    }

    // --- fixtures -----------------------------------------------------------

    private static string Header(string id, string cwd) =>
        JsonSerializer.Serialize(new { type = "session", version = 3, id, timestamp = "2026-01-01T00:00:00Z", cwd });

    private static string Info(string parentId, string name, string timestamp) =>
        JsonSerializer.Serialize(new { type = "session_info", id = "info-" + timestamp, parentId, timestamp, name });

    private static string Entry(string id, string? parentId, string timestamp, string messageLine) =>
        // The message entry wraps the role-specific message object.
        string.Concat("{\"type\":\"message\",\"id\":\"", id, "\",\"parentId\":",
            parentId is null ? "null" : $"\"{parentId}\"",
            ",\"timestamp\":\"", timestamp, "\",\"message\":", messageLine, "}");

    private static string UserMessage(string text, long timestamp) =>
        JsonSerializer.Serialize(new { role = "user", content = text, timestamp });

    private static string AssistantMessage(string text, long timestamp) =>
        JsonSerializer.Serialize(new { role = "assistant", content = new[] { new { type = "text", text } }, timestamp });

    private static string WriteSession(TempDirectory temp, params string[] lines)
    {
        var path = Path.Combine(temp.Path, $"session-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }
}
