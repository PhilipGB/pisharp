using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class PiJsonlSessionFileStoreTests
{
    [Fact]
    public async Task SavesPiJsonlAndRejectsExternalChangesBeforeReplacingThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pi-jsonl-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "session.jsonl");
            var session = new ConversationSession(root, "fixture", null, "fixture");
            session.Append(new ChatMessage(ChatRole.User, "first"));
            var store = new PiJsonlSessionFileStore(path);

            await store.SaveAsync(session);

            var initial = await File.ReadAllTextAsync(path);
            Assert.Equal("session", System.Text.Json.JsonDocument.Parse(initial.Split('\n')[0])
                .RootElement.GetProperty("type").GetString());
            await File.AppendAllTextAsync(path, "{\"type\":\"external\"}\n");
            var externallyChanged = await File.ReadAllTextAsync(path);
            session.Append(new ChatMessage(ChatRole.Assistant, "second"));

            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(session));

            Assert.Equal(externallyChanged, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FirstSaveDoesNotReplaceAnExistingPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-pi-jsonl-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "session.jsonl");
            await File.WriteAllTextAsync(path, "external file");
            var session = new ConversationSession(root, "fixture", null);

            await Assert.ThrowsAsync<InvalidDataException>(() => new PiJsonlSessionFileStore(path).SaveAsync(session));

            Assert.Equal("external file", await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
