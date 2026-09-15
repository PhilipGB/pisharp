using System.Text.Json;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

public sealed class SessionStoreTests
{
    [Fact]
    public async Task Store_RoundTripsAppendOnlySession()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var sessions = Path.Combine(temp.Path, "sessions");
        var store = new SessionStore(workspace, sessions);
        var document = await store.CreateAsync("model");

        using var state = JsonDocument.Parse("{\"messages\":[1]}");
        var turn = SessionTurn.Create(null, "hello", "world", state.RootElement);
        await store.AppendTurnAsync(document, turn);

        var loaded = await store.LoadAsync(document.FilePath);

        Assert.Equal(document.Header.SessionId, loaded.Header.SessionId);
        var loadedTurn = Assert.Single(loaded.Turns);
        Assert.Equal("hello", loadedTurn.UserMessage);
        Assert.Equal("world", loadedTurn.AssistantMessage);
        Assert.True(loadedTurn.AgentState.GetProperty("messages")[0].GetInt32() == 1);
    }

    [Fact]
    public async Task Fork_CopiesOnlySelectedActivePath()
    {
        using var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        var source = await store.CreateAsync("model");

        using var state = JsonDocument.Parse("{}");
        var root = SessionTurn.Create(null, "root", "a", state.RootElement);
        await store.AppendTurnAsync(source, root);
        var left = SessionTurn.Create(root.Id, "left", "a", state.RootElement);
        await store.AppendTurnAsync(source, left);
        var right = SessionTurn.Create(root.Id, "right", "a", state.RootElement);
        await store.AppendTurnAsync(source, right);

        var fork = await store.ForkAsync(source, left.Id, "model");

        Assert.Equal(new[] { root.Id, left.Id }, fork.Turns.Select(turn => turn.Id));
        Assert.DoesNotContain(fork.Turns, turn => turn.Id == right.Id);
    }
}
