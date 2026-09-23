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
    public async Task SearchAndDeletionRequireUnchangedSameProjectCatalogSnapshot()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-catalog-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            var first = new ConversationSession(cwd, "fixture", null);
            first.Rename("Search Me");
            var firstPath = store.NewPath(first);
            await store.SaveAsync(first, firstPath);
            var second = new ConversationSession(cwd, "fixture", null);
            var secondPath = store.NewPath(second);
            await store.SaveAsync(second, secondPath);
            var indexed = await SessionCatalog.ListAsync(store);
            Assert.Equal(firstPath, Assert.Single(SessionCatalog.Search(indexed, "search me")).Path);
            Assert.Empty(SessionCatalog.Search(indexed, "not found"));
            var victim = SessionCatalog.Resolve(indexed, first.Id[..12]);
            await File.AppendAllTextAsync(firstPath, " ");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.DeleteAsync(victim));
            Assert.True(File.Exists(firstPath));
            // Listing must not reset the store's independent stale-writer guard.
            var refreshed = SessionCatalog.Resolve(await SessionCatalog.ListAsync(store), first.Id[..12]);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.DeleteAsync(refreshed));
            var other = new ConversationStore(cwd, Path.Combine(cwd, "other"));
            await Assert.ThrowsAsync<InvalidDataException>(() => other.DeleteAsync(refreshed));
            var freshStore = new ConversationStore(cwd, store.DirectoryPath);
            await freshStore.DeleteAsync(refreshed);
            Assert.False(File.Exists(firstPath));
            Assert.True(File.Exists(secondPath));
        }
        finally { Directory.Delete(cwd, true); }
    }

    [Fact]
    public async Task CatalogDoesNotIndexSymlinkAsProjectSession()
    {
        if (!OperatingSystem.IsLinux()) return;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-catalog-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
            Directory.CreateDirectory(store.DirectoryPath);
            var outside = Path.Combine(cwd, "outside.session.json");
            await File.WriteAllTextAsync(outside, new ConversationSession(cwd, "fixture", null).ToJson());
            var link = Path.Combine(store.DirectoryPath, "link.session.json");
            File.CreateSymbolicLink(link, outside);
            await Assert.ThrowsAsync<InvalidDataException>(() => SessionCatalog.ListAsync(store));
            Assert.True(File.Exists(outside));
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
