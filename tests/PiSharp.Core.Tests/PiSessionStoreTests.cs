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
        Assert.Equal(source.PiHeader!.Id, fork.PiHeader!.ParentSession);
    }
}
