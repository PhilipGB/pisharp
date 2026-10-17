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
