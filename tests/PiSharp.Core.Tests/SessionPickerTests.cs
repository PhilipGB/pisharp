using System.Text.Json;
using PiSharp.Cli;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

/// <summary>
/// Console session picker: metadata listing, search, sort/name filters, rename, and delete
/// with the pinned confirmation and active-session guard.
/// </summary>
public sealed class SessionPickerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PickerSelectsByNumberAndCancelsWithEsc()
    {
        var (store, temp) = await CreateStoreAsync("s1", "first", "s2", "second");
        using var _ = temp;

        // mtime descending: s2 (the newer file) is row 1.
        var console = new FakeConsoleIO();
        console.EnqueueText("1\n");
        var selected = await SessionPicker.PickAsync(store, console, null, CancellationToken.None);
        Assert.NotNull(selected);
        Assert.Equal("s2", selected!.Id);

        var cancelled = new FakeConsoleIO();
        cancelled.EnqueueKey(FakeConsoleIO.EscapeKey);
        Assert.Null(await SessionPicker.PickAsync(store, cancelled, null, CancellationToken.None));
    }

    [Fact]
    public async Task PickerSearchFiltersBeforeSelection()
    {
        var (store, temp) = await CreateStoreAsync("s1", "alpha", "s2", "beta");
        using var _ = temp;

        var console = new FakeConsoleIO();
        console.EnqueueText("alpha\n"); // search: only s1 matches
        console.EnqueueText("1\n");     // select the first visible row
        var selected = await SessionPicker.PickAsync(store, console, null, CancellationToken.None);

        Assert.Equal("s1", selected!.Id);
        // The final render (after the search) lists only the match.
        var finalRender = console.Output.Split("Sessions:")[^1];
        Assert.Contains("s1", finalRender);
        Assert.DoesNotContain("s2", finalRender);
    }

    [Fact]
    public async Task PickerRenamesAVisibleSession()
    {
        var (store, temp) = await CreateStoreAsync("s1", "alpha");
        using var _ = temp;

        var console = new FakeConsoleIO();
        console.EnqueueText("rename 1 My Project\n");
        console.EnqueueText("1\n");
        var selected = await SessionPicker.PickAsync(store, console, null, CancellationToken.None);

        Assert.Equal("My Project", selected!.Name);
        // The rename is durable: a fresh reader sees it.
        Assert.Equal("My Project", (await SessionInfoReader.ReadAsync(selected.Path))!.Name);
    }

    [Fact]
    public async Task PickerRenameWithBlankNameIsANoop()
    {
        var (store, temp) = await CreateStoreAsync("s1", "alpha");
        using var _ = temp;

        var console = new FakeConsoleIO();
        console.EnqueueText("rename 1 \n");
        console.EnqueueText("1\n");
        var selected = await SessionPicker.PickAsync(store, console, null, CancellationToken.None);

        Assert.Null(selected!.Name);
    }

    [Fact]
    public async Task PickerRefusesToDeleteTheActiveSession()
    {
        var (store, temp) = await CreateStoreAsync("active", "alpha");
        using var _ = temp;

        var console = new FakeConsoleIO();
        console.EnqueueText("delete 1\n"); // refused before any confirmation is asked
        console.EnqueueText("1\n");        // select it (the picker must still allow opening it)
        var selected = await SessionPicker.PickAsync(store, console, activePath: store.WorkspaceDirectory + Path.DirectorySeparatorChar + "active.jsonl", CancellationToken.None);

        Assert.Equal("active", selected!.Id);
        Assert.Contains("Cannot delete the currently active session", console.Output);
        Assert.True(File.Exists(selected.Path));
    }

    [Fact]
    public async Task PickerDeletesWithConfirmationAndFallsBackToUnlink()
    {
        var (store, temp) = await CreateStoreAsync("s1", "alpha");
        using var _ = temp;
        store.TrashLauncher = (_, _) => Task.FromResult<(int, string?)>((1, "trash: not installed"));
        store.DeleteFileOverride = path => { File.Delete(path); return true; };

        var console = new FakeConsoleIO();
        console.EnqueueText("delete 1\n");
        console.EnqueueText("y\n");
        console.EnqueueText("1\n"); // the list now shows no rows; select nothing → cancel with Esc
        console.EnqueueKey(FakeConsoleIO.EscapeKey);
        var selected = await SessionPicker.PickAsync(store, console, null, CancellationToken.None);

        Assert.Null(selected);
        Assert.Contains("Deleted session s1 (unlink)", console.Output);
    }

    [Fact]
    public async Task PickerDeleteDeclinesWithoutConfirmation()
    {
        var (store, temp) = await CreateStoreAsync("s1", "alpha");
        using var _ = temp;

        var console = new FakeConsoleIO();
        console.EnqueueText("delete 1\n");
        console.EnqueueText("n\n");
        console.EnqueueText("1\n");
        var selected = await SessionPicker.PickAsync(store, console, null, CancellationToken.None);

        Assert.Equal("s1", selected!.Id);
        Assert.Contains("Delete cancelled.", console.Output);
    }

    [Fact]
    public async Task PickerNamedFilterAppliesToTheQuerylessThreadedTree()
    {
        // Exactly two sessions: A gets a name, B stays unnamed. The default view is
        // threaded with an empty query — the named-only filter must still apply there.
        var (store, temp) = await CreateStoreAsync("a", "alpha", "b", "beta");
        using var _ = temp;
        await store.RenameSessionAsync(
            Path.Combine(store.WorkspaceDirectory, "a.jsonl"), "Named", CancellationToken.None);

        var console = new FakeConsoleIO();
        console.EnqueueText("named\n"); // switch to named-only (sort stays threaded)
        console.EnqueueText("1\n");     // select the only visible row
        var selected = await SessionPicker.PickAsync(store, console, null, CancellationToken.None);

        Assert.NotNull(selected);
        Assert.Equal("a", selected!.Id);
        // The final render (threaded, empty query, named-only) lists only the named
        // session (by its quoted name); the unnamed session's row must be absent.
        var finalRender = console.Output.Split("Sessions:")[^1];
        Assert.Contains("\"Named\"", finalRender);
        Assert.DoesNotContain("beta", finalRender);
    }

    [Fact]
    public async Task PickerNamedFilterHidesUnnamedSessions()
    {
        var (store, temp) = await CreateStoreAsync("s1", "alpha");
        using var _ = temp;
        await store.RenameSessionAsync(
            Path.Combine(store.WorkspaceDirectory, "s1.jsonl"), "Named", CancellationToken.None);

        var console = new FakeConsoleIO();
        console.EnqueueText("named\n");
        console.EnqueueText("1\n");
        var selected = await SessionPicker.PickAsync(store, console, null, CancellationToken.None);
        Assert.Equal("s1", selected!.Id);

        var unnamedView = new FakeConsoleIO();
        unnamedView.EnqueueText("named\n");
        unnamedView.EnqueueKey(FakeConsoleIO.EscapeKey);
        await SessionPicker.PickAsync(
            await CreateUnrenamedStoreAsync(), unnamedView, null, CancellationToken.None);
        Assert.Contains("No saved sessions", unnamedView.Output);
    }

    [Fact]
    public async Task PickerRendersAnOldSessionWithoutAUsableCwd()
    {
        var temp = TempDirectory.Create();
        using var _ = temp;
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        Directory.CreateDirectory(store.WorkspaceDirectory);

        // Old/versionless session metadata with no usable cwd: the header omits the
        // property entirely, so the reader reports an empty cwd.
        var time = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var path = Path.Combine(store.WorkspaceDirectory, "nocwd.jsonl");
        await File.WriteAllLinesAsync(path, [
            $"{{\"type\":\"session\",\"id\":\"nocwd123\",\"timestamp\":\"{time:yyyy-MM-ddTHH:mm:ss.fffZ}\"}}",
            JsonSerializer.Serialize(new MessageEntry("u", null, time,
                JsonSerializer.SerializeToElement(new { role = "user", content = "old prompt" })), Json),
            JsonSerializer.Serialize(new MessageEntry("a", "u", time.AddMilliseconds(500),
                JsonSerializer.SerializeToElement(new { role = "assistant", content = "old answer" })), Json),
        ]);
        File.SetLastWriteTimeUtc(path, time.UtcDateTime);

        var console = new FakeConsoleIO();
        console.EnqueueText("1\n"); // the session must remain selectable
        var selected = await SessionPicker.PickAsync(store, console, null, CancellationToken.None);

        Assert.NotNull(selected);
        Assert.Equal("nocwd123", selected!.Id);
        Assert.Equal(string.Empty, selected.Cwd);
        // The rendered row carries the id and no fake cwd after the title.
        var render = console.Output.Split("Sessions:")[1];
        var row = render.Split('\n').First(line => line.Contains("nocwd123"));
        Assert.EndsWith("old prompt", row.TrimEnd());
        Assert.DoesNotContain(Environment.CurrentDirectory, render);
    }

    // --- fixtures -------------------------------------------------------------

    private static async Task<(SessionStore store, TempDirectory temp)> CreateStoreAsync(params string[] idsAndTexts)
    {
        var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var store = new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
        Directory.CreateDirectory(store.WorkspaceDirectory);

        // Pinned listing order is the latest entry timestamp (not the file mtime), so the
        // fixture pins explicit, distinct entry timestamps per file.
        var baseTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < idsAndTexts.Length; i += 2)
        {
            var (id, text) = (idsAndTexts[i], idsAndTexts[i + 1]);
            var path = Path.Combine(store.WorkspaceDirectory, $"{id}.jsonl");
            var fileTime = baseTime.AddMinutes(i / 2);
            await WriteSessionFileAsync(path, id, workspace, text, fileTime);
            File.SetLastWriteTimeUtc(path, fileTime.UtcDateTime);
        }

        return (store, temp);
    }

    private static async Task<SessionStore> CreateUnrenamedStoreAsync()
    {
        var temp = TempDirectory.Create();
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        return new SessionStore(workspace, Path.Combine(temp.Path, "sessions"));
    }

    private static async Task WriteSessionFileAsync(string path, string id, string cwd, string userText, DateTimeOffset fileTime)
    {
        var header = new PiSessionHeader("session", 3, id, fileTime, Path.GetFullPath(cwd));
        var user = new MessageEntry("u", null, fileTime,
            JsonSerializer.SerializeToElement(new { role = "user", content = userText }));
        var assistant = new MessageEntry("a", "u", fileTime.AddMilliseconds(500),
            JsonSerializer.SerializeToElement(new { role = "assistant", content = "answer" }));
        await File.WriteAllLinesAsync(path,
        [
            JsonSerializer.Serialize(header, Json),
            JsonSerializer.Serialize(user, Json),
            JsonSerializer.Serialize(assistant, Json),
        ]);
    }
}
