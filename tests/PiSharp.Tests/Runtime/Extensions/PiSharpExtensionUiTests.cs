using PiSharp.Extensions.Abstractions;

namespace PiSharp.Tests;

public sealed class PiSharpExtensionUiTests
{
    [Fact]
    public async Task ExistingCommandContextConstructorDefaultsToDeterministicHeadlessUi()
    {
        var context = new PiSharpExtensionCommandContext("/tmp/project", true, false, false,
            static (_, _) => Task.CompletedTask,
            static (_, _, _, _) => Task.CompletedTask);

        Assert.Equal("unknown", context.Mode);
        Assert.False(context.HasUserInterface);
        Assert.Null(await context.Ui.SelectAsync("Choose", ["one"]));
        Assert.False(await context.Ui.ConfirmAsync("Confirm", "Continue?"));
        Assert.Null(await context.Ui.InputAsync("Value"));

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Ui.SelectAsync("Choose", ["one"], cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Ui.ConfirmAsync("Confirm", "Continue?", cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Ui.InputAsync("Value", cancellationToken: cancellation.Token));

        var lifecycleContext = new PiSharpExtensionContext("/tmp/project");
        Assert.Equal("unknown", lifecycleContext.Mode);
        Assert.False(lifecycleContext.HasUserInterface);
        Assert.Null(await lifecycleContext.Ui.SelectAsync("Choose", ["one"]));
        Assert.False(await lifecycleContext.Ui.ConfirmAsync("Confirm", "Continue?"));
        Assert.Null(await lifecycleContext.Ui.InputAsync("Value"));
    }

    [Fact]
    public async Task ExtendedCommandContextExposesHostModeAndSuppliedUi()
    {
        var ui = new RecordingUi();
        var context = new PiSharpExtensionCommandContext("/tmp/project", true, true, false,
            static (_, _) => Task.CompletedTask,
            static (_, _, _, _) => Task.CompletedTask,
            "tui", true, ui);

        Assert.Equal("tui", context.Mode);
        Assert.True(context.HasUserInterface);
        Assert.Same(ui, context.Ui);
        Assert.Equal("Production", await context.Ui.SelectAsync("Environment", ["Development", "Production"]));
        Assert.True(await context.Ui.ConfirmAsync("Confirm", "Use production?"));
        Assert.Equal("approved", await context.Ui.InputAsync("Label"));
        context.Ui.Notify("saved", "info");
        Assert.Equal(["select:Environment", "confirm:Confirm", "input:Label", "notify:info:saved"], ui.Calls);
    }

    [Fact]
    public async Task PreCancelledTimeoutDialogsPropagateCancellationBeforeImmediateTimeout()
    {
        var recordingUi = new RecordingUi();
        IPiSharpExtensionUi ui = recordingUi;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var options = new PiSharpExtensionUiDialogOptions(TimeSpan.Zero);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ui.SelectWithOptionsAsync("Choose", ["one"], options, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ui.ConfirmWithOptionsAsync("Confirm", "Continue?", options, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ui.InputWithOptionsAsync("Value", dialogOptions: options, cancellationToken: cancellation.Token));
        Assert.Empty(recordingUi.Calls);
    }

    [Fact]
    public async Task CommandContextExposesLiveControlsAndRevokesThemAfterDispose()
    {
        var activeTools = new List<string> { "read" };
        var configuredTools = new List<PiSharpExtensionToolInfo>
        {
            new("read", "Read files", true, PiSharpExtensionToolExposure.Direct, "builtin")
        };
        var shutdownRequests = 0;
        IReadOnlyList<string>? selectedTools = null;
        PiSharpThinkingLevel selectedThinking = PiSharpThinkingLevel.Medium;
        CancellationToken modelToken = default;
        CancellationToken compactionToken = default;
        var operations = new PiSharpExtensionCommandControlOperations(
            () => [new("/sample", "Sample command", "extension", "/sample.cs")],
            () => activeTools,
            () => configuredTools,
            () => new("fixture", "model-1", "Fixture model", 32_000, true),
            names => selectedTools = names,
            (_, _, token) =>
            {
                modelToken = token;
                return Task.FromResult(new PiSharpModelSelectionResult(PiSharpModelSelectionStatus.Selected));
            },
            () => selectedThinking,
            (level, _) => Task.FromResult(selectedThinking = level),
            static () => { },
            token =>
            {
                compactionToken = token;
                return Task.FromResult(new PiSharpCompactionResult(PiSharpCompactionStatus.Compacted));
            },
            () => shutdownRequests++);
        var context = new PiSharpExtensionCommandContext("/tmp/project", true, false, false,
            static (_, _) => Task.CompletedTask,
            static (_, _, _, _) => Task.CompletedTask,
            operations);

        Assert.Equal("/sample", Assert.Single(context.GetCommands()).Name);
        var initialActiveTools = context.GetActiveTools();
        var initialConfiguredTools = context.GetConfiguredTools();
        Assert.Equal(["read"], initialActiveTools);
        Assert.Equal("read", Assert.Single(initialConfiguredTools).Name);
        Assert.Equal("model-1", context.GetModel()!.ModelId);
        activeTools.Add("write");
        configuredTools.Clear();
        Assert.Equal(["read", "write"], context.GetActiveTools());
        Assert.Empty(context.GetConfiguredTools());
        Assert.Equal(["read"], initialActiveTools);
        Assert.Single(initialConfiguredTools);

        using var cancellation = new CancellationTokenSource();
        var selected = new List<string> { "write", "unknown" };
        context.SetActiveTools(selected);
        selected.Clear();
        Assert.Equal(["write", "unknown"], selectedTools);
        Assert.Equal(PiSharpModelSelectionStatus.Selected,
            (await context.SetModelAsync("fixture", "model-1", cancellation.Token)).Status);
        Assert.Equal(cancellation.Token, modelToken);
        Assert.Equal(PiSharpThinkingLevel.High,
            await context.SetThinkingLevelAsync(PiSharpThinkingLevel.High));
        Assert.Equal(PiSharpThinkingLevel.High, context.GetThinkingLevel());
        context.AbortCurrentRun();
        Assert.Equal(PiSharpCompactionStatus.Compacted,
            (await context.CompactCurrentSessionAsync(cancellation.Token)).Status);
        Assert.Equal(cancellation.Token, compactionToken);
        context.RequestShutdown();
        context.RequestShutdown();
        Assert.Equal(1, shutdownRequests);

        context.Dispose();
        Assert.Throws<ObjectDisposedException>(() => context.GetCommands());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.SetModelAsync("fixture", "model-1"));
    }

    [Fact]
    public async Task CommandContextPreservesModelAndCompactionFailureResults()
    {
        var operations = new PiSharpExtensionCommandControlOperations(
            static () => [], static () => [], static () => [], static () => null,
            static _ => { },
            (provider, model, _) => Task.FromResult(new PiSharpModelSelectionResult(
                provider == "known" && model == "locked"
                    ? PiSharpModelSelectionStatus.AuthenticationUnavailable
                    : PiSharpModelSelectionStatus.UnknownModel)),
            static () => PiSharpThinkingLevel.Off,
            static (level, _) => Task.FromResult(level),
            static () => { },
            static _ => Task.FromResult(new PiSharpCompactionResult(PiSharpCompactionStatus.Failed, "fixture failure")),
            static () => { });
        using var context = new PiSharpExtensionCommandContext("/tmp/project", true, false, false,
            static (_, _) => Task.CompletedTask,
            static (_, _, _, _) => Task.CompletedTask,
            operations);

        Assert.Equal(PiSharpModelSelectionStatus.AuthenticationUnavailable,
            (await context.SetModelAsync("known", "locked")).Status);
        Assert.Equal(PiSharpModelSelectionStatus.UnknownModel,
            (await context.SetModelAsync("unknown", "model")).Status);
        var compaction = await context.CompactCurrentSessionAsync();
        Assert.Equal(PiSharpCompactionStatus.Failed, compaction.Status);
        Assert.Equal("fixture failure", compaction.Error);
    }

    private sealed class RecordingUi : IPiSharpExtensionUi
    {
        public List<string> Calls { get; } = [];

        public Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"select:{title}");
            return Task.FromResult<string?>("Production");
        }

        public Task<bool> ConfirmAsync(string title, string message,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"confirm:{title}");
            return Task.FromResult(true);
        }

        public Task<string?> InputAsync(string title, string? placeholder = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"input:{title}");
            return Task.FromResult<string?>("approved");
        }

        public void Notify(string message, string type = "info") => Calls.Add($"notify:{type}:{message}");
    }
}
