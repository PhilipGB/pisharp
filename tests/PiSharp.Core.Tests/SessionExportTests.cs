using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

/// <summary>
/// JSONL export (pinned exportSessionToJsonl): fresh header with the same id and no
/// parentSession, the active branch re-chained sequentially, default
/// session-&lt;timestamp&gt;.jsonl name, missing directories created, source untouched.
/// </summary>
public sealed class SessionExportTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ExportWritesTheActiveBranchWithASequentialParentChain()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));

        // u1 -> a1 -> u2 -> a2 is the active branch; "side" is an abandoned fork of u1.
        var document = await store.CreatePiAsync(CancellationToken.None);
        await store.AppendEntriesAsync(document,
        [
            User("u1", null, "first"),
            Assistant("a1", "u1", "first answer"),
            User("side", "u1", "abandoned"),
            User("u2", "a1", "second"),
            Assistant("a2", "u2", "second answer"),
        ], CancellationToken.None);
        var sourceId = document.PiHeader!.Id;

        var output = Path.Combine(temp.Path, "exported.jsonl");
        var written = await store.ExportJsonlAsync(document, output, CancellationToken.None);
        Assert.Equal(output, written);

        var lines = (await File.ReadAllLinesAsync(output)).Where(l => l.Length > 0).ToList();
        // Header plus the four active-branch entries (the abandoned side branch is excluded).
        Assert.Equal(5, lines.Count);

        var header = JsonDocument.Parse(lines[0]).RootElement;
        Assert.Equal("session", header.GetProperty("type").GetString());
        Assert.Equal(3, header.GetProperty("version").GetInt32());
        Assert.Equal(sourceId, header.GetProperty("id").GetString());
        Assert.Equal(Path.GetFullPath(workspace), header.GetProperty("cwd").GetString());
        // Pinned export header: no parentSession, even for a session that has one.
        Assert.False(header.TryGetProperty("parentSession", out _));

        var exported = lines.Skip(1)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
        Assert.Equal(new[] { "u1", "a1", "u2", "a2" }, exported.Select(e => e.GetProperty("id").GetString()).ToArray());

        // Sequential re-chaining: first parentId omitted (null), then the previous id.
        Assert.Equal(new string?[] { null, "u1", "a1", "u2" }, exported.Select(ParentOf).ToArray());

        // The source session file is untouched.
        var sourceLines = (await File.ReadAllLinesAsync(document.FilePath)).Where(l => l.Length > 0).ToList();
        Assert.Equal(6, sourceLines.Count);
        Assert.Contains(sourceLines, line => line.Contains("\"side\""));
    }

    [Fact]
    public async Task ExportDefaultsToATimestampedSessionFileInTheWorkingDirectory()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var document = await store.CreatePiAsync(CancellationToken.None);
        await store.AppendEntriesAsync(document, [User("u1", null, "hi")], CancellationToken.None);
        await store.AppendEntriesAsync(document, [Assistant("a1", "u1", "yo")], CancellationToken.None);

        var written = await store.ExportJsonlAsync(document, null, CancellationToken.None);

        Assert.Equal(workspace, Path.GetDirectoryName(written));
        var name = Path.GetFileName(written);
        Assert.StartsWith("session-", name);
        Assert.EndsWith(".jsonl", name);
        // Pinned name shape: session-<ISO timestamp with ':' and '.' as '-'>.jsonl.
        Assert.Matches(@"^session-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-\d+Z\.jsonl$", name);
    }

    [Fact]
    public async Task ExportCreatesMissingParentDirectories()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var document = await store.CreatePiAsync(CancellationToken.None);
        await store.AppendEntriesAsync(document, [User("u1", null, "hi"), Assistant("a1", "u1", "yo")], CancellationToken.None);

        var nested = Path.Combine(temp.Path, "a", "b", "out.jsonl");
        var written = await store.ExportJsonlAsync(document, nested, CancellationToken.None);

        Assert.Equal(nested, written);
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public async Task ExportOfAnUnflushedSessionWritesTheInMemoryEntries()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var document = await store.CreatePiAsync(CancellationToken.None);
        await store.AppendEntriesAsync(document, [User("u1", null, "only a prompt")], CancellationToken.None);

        Assert.False(document.IsFileFlushed);
        var output = await store.ExportJsonlAsync(document, Path.Combine(temp.Path, "x.jsonl"), CancellationToken.None);

        var lines = (await File.ReadAllLinesAsync(output)).Where(l => l.Length > 0).ToList();
        Assert.Equal(2, lines.Count); // header + the in-memory user entry
        // The session itself is still unflushed: no file, no side effect.
        Assert.False(document.IsFileFlushed);
    }

    [Fact]
    public async Task ExportIsRejectedForLegacySessions()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var document = await store.CreateAsync("test-model", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ExportJsonlAsync(document, Path.Combine(temp.Path, "legacy.jsonl"), CancellationToken.None));
    }

    // --- fixtures ---------------------------------------------------------------

    private static string? ParentOf(JsonElement entry) =>
        entry.TryGetProperty("parentId", out var parent) ? parent.GetString() : null;

    private static MessageEntry User(string id, string? parentId, string text) =>
        new(id, parentId, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new { role = "user", content = text }));

    private static MessageEntry Assistant(string id, string? parentId, string text) =>
        new(id, parentId, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new { role = "assistant", content = new object[] { new { type = "text", text } } }));
}
