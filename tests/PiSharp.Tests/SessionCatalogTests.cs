using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class SessionCatalogTests
{
    [Fact]
    public async Task ListsProjectSessionsAndResolvesUniqueIdOrNameWithoutResettingStaleWriterGuard()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var first = new ConversationSession(cwd, "fixture", null);
            first.Rename("project notes");
            first.Append(new ChatMessage(ChatRole.User, "first"));
            var firstPath = store.NewPath(first);
            await store.SaveAsync(first, firstPath);
            var second = new ConversationSession(cwd, "fixture", null);
            second.Append(new ChatMessage(ChatRole.User, "second"));
            var secondPath = store.NewPath(second);
            await store.SaveAsync(second, secondPath);
            var list = await SessionCatalog.ListAsync(store);
            Assert.Equal(2, list.Count);
            Assert.Equal(first.Id, SessionCatalog.Resolve(list, "project notes").Id);
            Assert.Equal(second.Id, SessionCatalog.Resolve(list, second.Id[..12]).Id);
            Assert.Throws<ArgumentException>(() => SessionCatalog.Resolve(list, "missing"));
            first.Rename("external change");
            await File.WriteAllTextAsync(firstPath, first.ToJson());
            _ = await SessionCatalog.ListAsync(store);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(first, firstPath));
        }
        finally { Directory.Delete(cwd, true); }
    }

    [Fact]
    public async Task CorruptSessionFailsVisiblyWithItsPath()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-catalog-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(cwd, "sessions");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "corrupt.session.json");
            await File.WriteAllTextAsync(path, "{}");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                SessionCatalog.ListAsync(new ConversationStore(cwd, folder)));
            Assert.Contains(path, error.Message);
        }
        finally { Directory.Delete(cwd, true); }
    }
}
