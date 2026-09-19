using System.Text.Json;

namespace PiSharp.Core.Tests;

/// <summary>
/// Pinned session-manager.ts getDefaultSessionDirPath conformance: the encoded cwd keeps one
/// leading separator stripped, turns every /, \, and : into -, and wraps the result in
/// --…-- under the agent directory's sessions root.
/// </summary>
public sealed class SessionDirectoryTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("/home/user/proj", "--home-user-proj--")]
    [InlineData("/", "----")]
    [InlineData("/a/b/c/d", "--a-b-c-d--")]
    [InlineData("/home/user/with:colon", "--home-user-with-colon--")]
    [InlineData("/home/user/proj/", "--home-user-proj--")]
    public void EncodeCwdMatchesThePinnedEncodingForUnixPaths(string cwd, string expected)
    {
        Assert.Equal(expected, SessionDirectory.EncodeCwd(Path.GetFullPath(cwd)));
    }

    [Fact]
    public void DefaultSessionDirSitsUnderTheAgentSessionsRoot()
    {
        var path = SessionDirectory.GetDefaultSessionDirPath("/home/user/proj", agentDirectory: "/tmp/agent");
        Assert.Equal(Path.Combine("/tmp/agent", "sessions", "--home-user-proj--"), Path.GetFullPath(path));
    }

    [Fact]
    public void StoreDefaultsToTheCanonicalLayoutWithoutAnOverride()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var agentDir = Path.Combine(temp.Path, "agent");

        var store = new SessionStore(workspace, sessionsRoot: null, agentDirectory: agentDir);

        Assert.Equal(
            Path.Combine(agentDir, "sessions", SessionDirectory.EncodeCwd(Path.GetFullPath(workspace))),
            Path.GetFullPath(store.WorkspaceDirectory));
        Assert.Equal(store.WorkspaceDirectory, store.CanonicalDirectory);
        Assert.False(store.UsesExplicitSessionDir);
    }

    [Fact]
    public void ExplicitSessionDirOverridesTheCanonicalLayout()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var explicitDir = Path.Combine(temp.Path, "elsewhere");

        var store = new SessionStore(workspace, sessionsRoot: explicitDir, agentDirectory: Path.Combine(temp.Path, "agent"));

        Assert.Equal(Path.GetFullPath(explicitDir), store.WorkspaceDirectory);
        Assert.NotEqual(store.CanonicalDirectory, store.WorkspaceDirectory);
        Assert.True(store.UsesExplicitSessionDir);
    }

    [Fact]
    public async Task NewSessionsGoToTheCanonicalDirectoryAndLegacySessionsStayDiscoverable()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var agentDir = Path.Combine(temp.Path, "agent");
        var legacyRoot = Path.Combine(temp.Path, "legacy");
        var store = new SessionStore(
            workspace,
            sessionsRoot: null,
            agentDirectory: agentDir,
            legacySessionsRoot: legacyRoot);

        // The legacy compatibility directory must stay inside the temp directory so the test
        // never writes into the executing user's real ~/.pisharp profile.
        Assert.StartsWith(
            Path.GetFullPath(temp.Path),
            Path.GetFullPath(store.LegacyWorkspaceDirectory));

        // A pre-Phase-3 session living under <legacySessionsRoot>/<key>/ for this workspace.
        // Its timestamps are pinned in the past so the newest-first ordering is deterministic.
        var legacyPath = Path.Combine(store.LegacyWorkspaceDirectory, "legacy.jsonl");
        Directory.CreateDirectory(store.LegacyWorkspaceDirectory);
        var legacyTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var header = PiSessionHeader.Create(workspace);
        await File.WriteAllLinesAsync(legacyPath,
        [
            System.Text.Json.JsonSerializer.Serialize(header, Json),
            System.Text.Json.JsonSerializer.Serialize(new MessageEntry(
                "u", null, legacyTime,
                System.Text.Json.JsonSerializer.SerializeToElement(new { role = "user", content = "legacy prompt" })), Json),
            System.Text.Json.JsonSerializer.Serialize(new MessageEntry(
                "a", "u", legacyTime,
                System.Text.Json.JsonSerializer.SerializeToElement(new { role = "assistant", content = "legacy answer" })), Json),
        ]);

        var document = await store.CreatePiAsync();
        await store.AppendEntriesAsync(document,
        [
            new MessageEntry("u2", null, DateTimeOffset.UtcNow,
                System.Text.Json.JsonSerializer.SerializeToElement(new { role = "user", content = "new prompt" })),
            new MessageEntry("a2", "u2", DateTimeOffset.UtcNow,
                System.Text.Json.JsonSerializer.SerializeToElement(new { role = "assistant", content = "new answer" })),
        ]);

        // The new session's file is under the canonical directory, not the legacy one.
        Assert.StartsWith(store.CanonicalDirectory + Path.DirectorySeparatorChar, document.FilePath);
        var infos = await store.ListInfosAsync();
        Assert.Equal(2, infos.Count);
        Assert.Contains(infos, info => info.Path == document.FilePath);
        Assert.Contains(infos, info => info.Path == legacyPath);
        // Newest-first: the just-flushed session sorts before the legacy one.
        Assert.Equal(document.FilePath, infos[0].Path);
    }
}
