using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class SessionCatalogTests
{
    [Fact]
    public void ResolveUsesCaseSensitiveIdPrefixesAndCaseInsensitiveNames()
    {
        var listing = new SessionListing("/tmp/session.jsonl", "AbC123def", "Project Notes",
            DateTimeOffset.UnixEpoch, 0, "fixture", "fingerprint");

        Assert.Equal(listing, SessionCatalog.Resolve([listing], "AbC123"));
        Assert.Throws<ArgumentException>(() => SessionCatalog.Resolve([listing], "abc123"));
        Assert.Equal(listing, SessionCatalog.Resolve([listing], "project notes"));
    }

    [Fact]
    public async Task DefaultDirectoryIncludesPiJsonlWithoutPersistedWorkingDirectory()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-default-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        var isolatedDefaultDirectory = Path.Combine(cwd, "default-sessions");
        var store = new ConversationStore(cwd, isolatedDefaultDirectory, isolatedDefaultDirectory);
        try
        {
            Assert.False(store.FiltersForeignWorkingDirectories);
            Directory.CreateDirectory(store.DirectoryPath);
            var path = Path.Combine(store.DirectoryPath, "missing-cwd.jsonl");
            await File.WriteAllTextAsync(path,
                "{\"type\":\"session\",\"version\":3,\"id\":\"default-no-cwd\"}\n" +
                "{\"type\":\"message\",\"id\":\"default-no-cwd-message\",\"parentId\":null," +
                "\"message\":{\"role\":\"user\",\"content\":\"default directory transcript\"}}\n");

            var listing = Assert.Single(await SessionCatalog.ListAsync(store));
            Assert.Equal("default-no-cwd", listing.Id);
            Assert.Equal(path, store.MostRecentPath());
            var loaded = await store.LoadAsync(listing);
            Assert.Equal(cwd, loaded.WorkingDirectory);
            Assert.Equal("default directory transcript", Assert.Single(loaded.ActiveMessages()).Text);
        }
        finally
        {
            if (Directory.Exists(store.DirectoryPath)) Directory.Delete(store.DirectoryPath, recursive: true);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task MostRecentProjectSessionSkipsCandidateWithMissingHead()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-session-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        var store = new ConversationStore(cwd, Path.Combine(cwd, "sessions"));
        try
        {
            var valid = new ConversationSession(cwd, "fixture", null);
            valid.Append(new ChatMessage(ChatRole.User, "recover me"));
            var validPath = store.NewPath(valid);
            await store.SaveAsync(valid, validPath);
            var invalid = System.Text.Json.Nodes.JsonNode.Parse(valid.ToJson())!;
            invalid["HeadId"] = "missing-entry";
            var invalidPath = Path.Combine(store.DirectoryPath, "invalid-head.session.json");
            await File.WriteAllTextAsync(invalidPath, invalid.ToJsonString());
            File.SetLastWriteTimeUtc(invalidPath, DateTime.UtcNow.AddMinutes(1));

            Assert.Equal(validPath, store.MostRecentPath());
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

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
    public async Task ListsManyProjectSessionsWithoutLosingEntries()
    {
        const int sessionCount = 128;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-many-session-catalog-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(cwd, "sessions");
        Directory.CreateDirectory(folder);
        try
        {
            for (var index = 0; index < sessionCount; index++)
            {
                var id = $"catalog-{index:D4}";
                var jsonl = string.Join('\n',
                    System.Text.Json.JsonSerializer.Serialize(new { type = "session", version = 3, id, cwd }),
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        type = "message",
                        id = $"{id}-message",
                        parentId = (string?)null,
                        message = new { role = "user", content = $"history-{index:D4}" }
                    }),
                    "");
                await File.WriteAllTextAsync(Path.Combine(folder, $"{id}.jsonl"), jsonl);
            }

            var listings = await SessionCatalog.ListAsync(new ConversationStore(cwd, folder));

            Assert.Equal(sessionCount, listings.Count);
            Assert.Equal(Enumerable.Range(0, sessionCount).Select(index => $"catalog-{index:D4}").Order(StringComparer.Ordinal),
                listings.Select(listing => listing.Id).Order(StringComparer.Ordinal));
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }

    [Fact]
    public async Task ProjectDiscoveryAcceptsAHeaderLargerThanPiRecentScanLimitWithinImportBounds()
    {
        const int piRecentScanLimitBytes = 1024 * 1024;
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-large-session-header-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(cwd, "sessions");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "large-header.jsonl");
            var header = System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "session",
                version = 3,
                id = "large-header",
                cwd,
                resourceProbe = new string('x', piRecentScanLimitBytes)
            });
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(header) > piRecentScanLimitBytes);
            await File.WriteAllTextAsync(path, header + "\n");

            var store = new ConversationStore(cwd, folder);
            var listing = Assert.Single(await SessionCatalog.ListAsync(store));

            Assert.Equal("large-header", listing.Id);
            Assert.Equal(path, store.MostRecentPath());
        }
        finally { Directory.Delete(cwd, recursive: true); }
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

    [Fact]
    public async Task SharedSessionDirectoryFiltersSessionsFromOtherWorkingDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-shared-sessions-" + Guid.NewGuid().ToString("N"));
        var localCwd = Path.Combine(root, "local");
        var foreignCwd = Path.Combine(root, "foreign");
        var sharedDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(localCwd);
        Directory.CreateDirectory(foreignCwd);
        try
        {
            var localStore = new ConversationStore(localCwd, sharedDirectory);
            var foreignStore = new ConversationStore(foreignCwd, sharedDirectory);
            var local = new ConversationSession(localCwd, "fixture", null);
            var foreign = new ConversationSession(foreignCwd, "fixture", null);
            var localPath = localStore.NewPath(local);
            var foreignPath = foreignStore.NewPath(foreign);
            await localStore.SaveAsync(local, localPath);
            await foreignStore.SaveAsync(foreign, foreignPath);
            File.SetLastWriteTimeUtc(localPath, DateTime.UtcNow.AddDays(-1));
            File.SetLastWriteTimeUtc(foreignPath, DateTime.UtcNow);

            Assert.Equal(localPath, localStore.MostRecentPath());
            Assert.Equal(localPath, Assert.Single(await SessionCatalog.ListAsync(localStore)).Path);
            Assert.Equal(foreignPath, Assert.Single(await SessionCatalog.ListAsync(foreignStore)).Path);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ConfiguredCatalogListsLoadsAndSafelySavesProjectScopedPiJsonlSessions()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-jsonl-catalog-" + Guid.NewGuid().ToString("N"));
        var localCwd = Path.Combine(root, "local");
        var foreignCwd = Path.Combine(root, "foreign");
        var sharedDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(localCwd);
        Directory.CreateDirectory(foreignCwd);
        Directory.CreateDirectory(sharedDirectory);
        try
        {
            var local = new ConversationSession(localCwd, "fixture-model", null, "fixture");
            local.Append(new ChatMessage(ChatRole.User, "local transcript"));
            local.Rename("local notes");
            var localPath = Path.Combine(sharedDirectory, "local.jsonl");
            await File.WriteAllTextAsync(localPath, PiJsonlSessionInterchange.Export(local));

            var foreign = new ConversationSession(foreignCwd, "fixture-model", null, "fixture");
            foreign.Append(new ChatMessage(ChatRole.User, "foreign transcript"));
            var foreignPath = Path.Combine(sharedDirectory, "foreign.jsonl");
            await File.WriteAllTextAsync(foreignPath, PiJsonlSessionInterchange.Export(foreign));

            var noCwdPath = Path.Combine(sharedDirectory, "no-cwd.jsonl");
            await File.WriteAllTextAsync(noCwdPath,
                "{\"type\":\"session\",\"version\":3,\"id\":\"no-cwd\"}\n" +
                "{\"type\":\"message\",\"id\":\"message\",\"parentId\":null,\"message\":{\"role\":\"user\",\"content\":\"unscoped\"}}\n");

            var store = new ConversationStore(localCwd, sharedDirectory);
            var listing = Assert.Single(await SessionCatalog.ListAsync(store));
            Assert.Equal(local.Id, listing.Id);
            Assert.Equal("local notes", listing.Name);
            Assert.Equal(1, listing.MessageCount);
            Assert.Equal(localPath, listing.Path);
            Assert.Equal(local.Id, SessionCatalog.Resolve([listing], local.Id[..12]).Id);
            Assert.Equal(local.Id, SessionCatalog.Resolve([listing], "local notes").Id);

            var loaded = await store.LoadAsync(listing);
            Assert.Equal(local.Id, loaded.Id);
            Assert.Equal("local transcript", Assert.Single(loaded.ActiveMessages()).Text);
            loaded.Rename("renamed from catalog");
            await store.SaveAsync(loaded, localPath);

            var savedText = await File.ReadAllTextAsync(localPath);
            Assert.Contains("\"type\":\"session\"", savedText);
            Assert.Equal("renamed from catalog", PiJsonlSessionInterchange.Import(savedText).Name);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ModifiedPiJsonlSessionIsRejectedAfterCatalogListing()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-jsonl-stale-catalog-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(cwd, "sessions");
        Directory.CreateDirectory(folder);
        try
        {
            var session = new ConversationSession(cwd, "fixture-model", null);
            session.Append(new ChatMessage(ChatRole.User, "before listing"));
            var path = Path.Combine(folder, "session.jsonl");
            await File.WriteAllTextAsync(path, PiJsonlSessionInterchange.Export(session));
            var store = new ConversationStore(cwd, folder);
            var listing = Assert.Single(await SessionCatalog.ListAsync(store));
            await File.AppendAllTextAsync(path, "\n");

            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(listing));
        }
        finally { Directory.Delete(cwd, true); }
    }

    [Fact]
    public async Task MostRecentConfiguredPiJsonlSessionFiltersSharedDirectoryByProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-jsonl-recent-" + Guid.NewGuid().ToString("N"));
        var localCwd = Path.Combine(root, "local");
        var foreignCwd = Path.Combine(root, "foreign");
        var sharedDirectory = Path.Combine(root, "sessions");
        Directory.CreateDirectory(localCwd);
        Directory.CreateDirectory(foreignCwd);
        Directory.CreateDirectory(sharedDirectory);
        try
        {
            var local = new ConversationSession(localCwd, "fixture-model", null);
            var foreign = new ConversationSession(foreignCwd, "fixture-model", null);
            var localPath = Path.Combine(sharedDirectory, "local.jsonl");
            var foreignPath = Path.Combine(sharedDirectory, "foreign.jsonl");
            await File.WriteAllTextAsync(localPath, PiJsonlSessionInterchange.Export(local));
            await File.WriteAllTextAsync(foreignPath, PiJsonlSessionInterchange.Export(foreign));
            File.SetLastWriteTimeUtc(localPath, DateTime.UtcNow.AddMinutes(-2));
            File.SetLastWriteTimeUtc(foreignPath, DateTime.UtcNow.AddMinutes(-1));

            Assert.Equal(localPath, new ConversationStore(localCwd, sharedDirectory).MostRecentPath());
            Assert.Equal(foreignPath, new ConversationStore(foreignCwd, sharedDirectory).MostRecentPath());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InvalidPiJsonlCandidateIsReportedWithItsPath()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-jsonl-invalid-catalog-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(cwd, "sessions");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "corrupt.jsonl");
            await File.WriteAllTextAsync(path, "not json\n");

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                SessionCatalog.ListAsync(new ConversationStore(cwd, folder)));

            Assert.Contains(path, error.Message);
        }
        finally { Directory.Delete(cwd, true); }
    }

    [Fact]
    public async Task OversizedPiJsonlIsRejectedBeforeReadingItsContents()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-jsonl-oversized-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(cwd, "sessions");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "oversized.jsonl");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.SetLength(128L * 1024 * 1024 + 1);

            var store = new ConversationStore(cwd, folder);
            Assert.Null(store.MostRecentPath());

            var catalogError = await Assert.ThrowsAsync<InvalidDataException>(() => SessionCatalog.ListAsync(store));
            Assert.Contains("128 MiB", catalogError.Message);
            var loadError = await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(path));
            Assert.Contains("128 MiB", loadError.Message);
        }
        finally { Directory.Delete(cwd, true); }
    }

    [Theory]
    [InlineData("{\"type\":\"session\",\"version\":4,\"id\":\"future\",\"cwd\":\"CWD\"}")]
    [InlineData("{\"type\":\"session\",\"version\":\"3\",\"id\":\"string-version\",\"cwd\":\"CWD\"}")]
    [InlineData("{\"type\":\"session\",\"version\":3,\"id\":42,\"cwd\":\"CWD\"}")]
    public async Task FileBackedOperationsRejectInvalidPiHeadersWithoutChangingTheSource(string headerTemplate)
    {
        var cwd = Path.Combine(Path.GetTempPath(), "pisharp-invalid-jsonl-header-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(cwd, "sessions");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "invalid.jsonl");
            var content = headerTemplate.Replace("CWD", cwd, StringComparison.Ordinal) + "\n";
            await File.WriteAllTextAsync(path, content);
            var store = new ConversationStore(cwd, folder);

            Assert.Null(store.MostRecentPath());
            await Assert.ThrowsAsync<InvalidDataException>(() => SessionCatalog.ListAsync(store));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(path));
            Assert.Equal(content, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(cwd, true); }
    }

}
