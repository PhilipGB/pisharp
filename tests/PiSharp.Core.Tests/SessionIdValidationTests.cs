using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

/// <summary>
/// Pinned assertValidSessionId: ids are ASCII alphanumerics plus '-', '_', '.', starting and
/// ending with an alphanumeric. Also the v3-only rename guard.
/// </summary>
public sealed class SessionIdValidationTests
{
    [Theory]
    [InlineData("x")]
    [InlineData("good_id-1")]
    [InlineData("a.b-c_d")]
    [InlineData("A1")]
    [InlineData("9")]
    public void AcceptsValidIds(string id)
    {
        Assert.Equal(id, PiSessionHeader.ValidateId(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("trailing.")]
    [InlineData("bad id")]
    [InlineData("bang!")]
    [InlineData("slash/ed")]
    // é is not an ASCII alphanumeric: the pinned regex is ASCII-only.
    [InlineData("café")]
    public void RejectsInvalidIds(string id)
    {
        Assert.Throws<ArgumentException>(() => PiSessionHeader.ValidateId(id));
    }

    [Fact]
    public async Task CreateWithAValidCustomIdUsesIt()
    {
        using var temp = TempDirectory.Create();
        var store = new SessionStore(temp.Path, Path.Combine(temp.Path, "sessions"));
        var document = await store.CreatePiAsync(CancellationToken.None, null, "my-session_1");
        Assert.Equal("my-session_1", document.PiHeader!.Id);
        // Deferred file: <timestamp>_<id>.jsonl in the workspace directory.
        Assert.Equal(store.WorkspaceDirectory, Path.GetDirectoryName(document.FilePath));
        Assert.EndsWith("_my-session_1.jsonl", Path.GetFileName(document.FilePath));
    }

    [Fact]
    public async Task CreateRejectsAnInvalidCustomId()
    {
        using var temp = TempDirectory.Create();
        var store = new SessionStore(temp.Path, Path.Combine(temp.Path, "sessions"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.CreatePiAsync(CancellationToken.None, null, "-bad"));
    }

    [Fact]
    public async Task RenameIsRejectedForLegacySessions()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var path = Path.Combine(store.WorkspaceDirectory, "legacy.jsonl");
        Directory.CreateDirectory(store.WorkspaceDirectory);

        // Legacy PiSharp v1 layout: session header (sessionId/version 1) plus one turn.
        var header = new SessionHeader(
            "session", 1, Guid.NewGuid().ToString("N"), workspace, "test-model", DateTimeOffset.UtcNow);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var state = JsonSerializer.SerializeToElement(new { model = "test-model" });
        await File.WriteAllLinesAsync(path,
        [
            JsonSerializer.Serialize(header, options),
            JsonSerializer.Serialize(SessionTurn.Create(null, "hi", "hello", state), options),
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RenameSessionAsync(path, "A Name", CancellationToken.None));
    }
}
