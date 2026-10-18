using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class NativeExtensionSessionMutationDispatcherTests
{
    [Fact]
    public async Task BeforeHookUsesSourceOrderAndCancellationStopsLaterHandlers()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var invoked = new List<string>();
        var first = Source("/extensions/first.dll");
        var second = Source("/extensions/second.dll");
        var third = Source("/extensions/third.dll");

        registration.SetCurrentSourceInfo(third);
        adapter.AddSessionBeforeSwitchHandler((_, _, _) =>
        {
            invoked.Add("third");
            return ValueTask.FromResult<PiSharpExtensionSessionBeforeSwitchResult?>(null);
        });
        registration.SetCurrentSourceInfo(first);
        adapter.AddSessionBeforeSwitchHandler((_, _, _) =>
        {
            invoked.Add("first");
            return ValueTask.FromResult<PiSharpExtensionSessionBeforeSwitchResult?>(new(Cancel: false));
        });
        registration.SetCurrentSourceInfo(second);
        adapter.AddSessionBeforeSwitchHandler((_, _, _) =>
        {
            invoked.Add("second");
            return ValueTask.FromResult<PiSharpExtensionSessionBeforeSwitchResult?>(new(Cancel: true));
        });
        adapter.Close();
        registration.OrderExtensions([first, second, third]);

        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), diagnostics.Add);
        var result = await dispatcher.DispatchSessionBeforeSwitchAsync(
            new("session-1", null, PiSharpExtensionSessionSwitchReason.New, null), CancellationToken.None);

        Assert.True(result?.Cancel);
        Assert.Equal(["first", "second"], invoked);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task HandlerExceptionIsAttributedAndNextHandlerCanProceed()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var source = Source("/extensions/broken.dll");
        var next = Source("/extensions/next.dll");
        var invoked = new List<string>();
        registration.SetCurrentSourceInfo(source);
        adapter.AddSessionBeforeForkHandler((_, _, _) =>
        {
            invoked.Add("broken");
            throw new InvalidOperationException("fixture failure");
        });
        registration.SetCurrentSourceInfo(next);
        adapter.AddSessionBeforeForkHandler((_, _, _) =>
        {
            invoked.Add("next");
            return ValueTask.FromResult<PiSharpExtensionSessionBeforeForkResult?>(new(Cancel: false));
        });
        adapter.Close();

        var diagnostics = new List<AgentLifecycleEvent>();
        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), diagnostics.Add);
        var result = await dispatcher.DispatchSessionBeforeForkAsync(
            new("session-1", null, "entry-1", PiSharpExtensionForkPosition.At), CancellationToken.None);

        Assert.False(result?.Cancel);
        Assert.Equal(["broken", "next"], invoked);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("extension_error", diagnostic.Type);
        Assert.Equal("fixture failure", diagnostic.Error);
        Assert.Equal(source.Path, diagnostic.Details!.GetType().GetProperty("extensionPath")!.GetValue(diagnostic.Details));
    }

    [Fact]
    public async Task LastNonNullNonCancelledMutationResultWins()
    {
        var registration = new ExtensionRegistration();
        var adapter = new NativeExtensionRegistrationAdapter(registration);
        var invoked = new List<string>();
        var first = Source("/extensions/first.dll");
        var second = Source("/extensions/second.dll");
        var third = Source("/extensions/third.dll");

        registration.SetCurrentSourceInfo(first);
        adapter.AddSessionBeforeCompactHandler((_, _, _) =>
        {
            invoked.Add("first");
            return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(new(
                Compaction: new("first summary", "entry-1", 1)));
        });
        registration.SetCurrentSourceInfo(second);
        adapter.AddSessionBeforeCompactHandler((_, _, _) =>
        {
            invoked.Add("second");
            return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(null);
        });
        registration.SetCurrentSourceInfo(third);
        adapter.AddSessionBeforeCompactHandler((_, _, _) =>
        {
            invoked.Add("third");
            return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(new(
                Compaction: new("last summary", "entry-2", 2)));
        });
        adapter.Close();
        registration.OrderExtensions([first, second, third]);

        var dispatcher = new NativeExtensionEventDispatcher(registration, CreateContext(), static _ => { });
        var (result, source) = await dispatcher.DispatchSessionBeforeCompactAsync(
            new(PiSharpExtensionCompactionReason.Manual, false,
                new("entry-2", [], [], false, 2, null, new([], [])),
                [new("entry-1", null, "message", DateTimeOffset.UnixEpoch)], null), CancellationToken.None);

        Assert.Equal(["first", "second", "third"], invoked);
        Assert.Equal("last summary", result?.Compaction?.Summary);
        Assert.Equal(third.Path, source?.Path);
    }

    [Fact]
    public void CompactionAndTreeEventsExposeDetachedReadOnlySnapshots()
    {
        var original = new ChatMessage(ChatRole.User, [new TextContent("captured")]);
        var sourceMessages = new[] { original };
        var preparation = new PiSharpExtensionCompactionPreparation("entry-1", sourceMessages, [], false,
            42, "previous summary");
        original.Contents.OfType<TextContent>().Single().Text = "source mutation";
        var returnedMessage = preparation.MessagesToSummarize.Single();
        Assert.Equal("captured", returnedMessage.Text);
        returnedMessage.Contents.OfType<TextContent>().Single().Text = "returned mutation";
        Assert.Equal("captured", preparation.MessagesToSummarize.Single().Text);

        var mutableEntries = new List<PiSharpExtensionSessionBranchEntry>
        {
            new("entry-1", null, "message", DateTimeOffset.UnixEpoch)
        };
        var treeEvent = new PiSharpExtensionSessionBeforeTreeEvent("entry-2", "entry-1", "root",
            mutableEntries, userWantsSummary: false);
        mutableEntries.Add(new("entry-2", "entry-1", "message", DateTimeOffset.UnixEpoch));
        Assert.Single(treeEvent.EntriesToSummarize);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<PiSharpExtensionSessionBranchEntry>)treeEvent.EntriesToSummarize).Add(
                new("entry-3", "entry-2", "message", DateTimeOffset.UnixEpoch)));

        using var detailsDocument = System.Text.Json.JsonDocument.Parse("{\"source\":\"owned\"}");
        var custom = new PiSharpExtensionCustomCompaction("summary", "entry-1", 42,
            detailsDocument.RootElement);
        detailsDocument.Dispose();
        Assert.Equal("owned", custom.Details!.Value.GetProperty("source").GetString());
    }

    private static PiSharp.Runtime.Extensions.PiSharpExtensionContext CreateContext() => new(
        new PiSharpExtensionContextConfiguration("/tmp", "test", HasUserInterface: false,
            IsProjectTrusted: true, new TestUi()), static () => null, static () => [], static () => true);

    private static ResourceSourceInfo Source(string path) => new(path, "native-package", "user", "package", null);

    private sealed class TestUi : PiSharp.Runtime.Extensions.IPiSharpExtensionUi
    {
        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> InputAsync(string title, string? placeholder = null,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public void Notify(string message, string type = "info") { }
    }
}
