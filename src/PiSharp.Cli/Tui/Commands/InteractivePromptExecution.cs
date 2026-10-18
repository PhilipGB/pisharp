using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using NativeCustomMessage = PiSharp.Extensions.Abstractions.PiSharpCustomMessage;

namespace PiSharp.Cli.Tui;

internal sealed class InteractivePromptExecution(TerminalEditor? editor, TerminalScreen? screen,
    Func<TextWriter> getError, Func<ResourceCatalog> getResources, Func<ConversationRun> getRun, Func<string> getFooter,
    Func<ModelSelection> getModelSelection, Func<UserSettings> getUserSettings,
    Func<string, bool, CancellationToken, Task> executeBash, Func<string, Task> handleAction,
    Func<PiSharp.Runtime.Extensions.ExtensionShortcutInfo, CancellationToken, Task>? handleExtensionShortcut,
    Func<string, CancellationToken, Task<bool>>? tryExecuteExtensionCommand) : IDisposable
{
    private readonly CancellationTokenSource _monitorStop = new();
    private Task _monitor = Task.CompletedTask;
    private bool _monitorStarted;

    public async Task ExecuteAsync(string input, IReadOnlyList<DataContent>? images, ModelSelection selection,
        UserSettings settings, InteractiveTranscript transcript, CancellationTokenSource cancellation,
        PiSharpInputSource source = PiSharpInputSource.Interactive)
    {
        var intercepted = await getRun().InterceptInputAsync(input, images, source, null, cancellation.Token);
        if (intercepted.Action == PiSharpInputResultAction.Handled) return;
        var prompt = intercepted.Action == PiSharpInputResultAction.Transform ? intercepted.Text! : input;
        var promptImages = intercepted.Action == PiSharpInputResultAction.Transform ? intercepted.Images : images;

        if (!selection.Authenticated)
            throw new InvalidOperationException($"Provider '{selection.Provider.Id}' is not authenticated. Use /login {selection.Provider.Id} before sending a prompt.");
        ValidateImages(promptImages, selection, settings);
        var expanded = await getResources().ResolveInputAsync(prompt, cancellation.Token,
            error => ReportResourceErrorAsync(error, render: false));
        var contents = new List<AIContent> { new TextContent(expanded) };
        if (promptImages is not null) contents.AddRange(promptImages);
        await ExecuteEventsAsync(token => getRun().RunUserMessageEventsAsync(new ChatMessage(ChatRole.User, contents),
            expanded, token), transcript, cancellation);
    }

    public Task ExecuteCustomMessageAsync(NativeCustomMessage message, string? sourcePath,
        InteractiveTranscript transcript, CancellationTokenSource cancellation) =>
        ExecuteEventsAsync(token => getRun().RunCustomMessageEventsAsync(message, sourcePath, token), transcript, cancellation);

    internal static void ValidateImages(IReadOnlyList<DataContent>? images, ModelSelection selection, UserSettings settings)
    {
        if (images is { Count: > 0 } && settings.BlockImages != true && selection.Model.Input is { } modalities &&
            !modalities.Contains("image", StringComparer.Ordinal))
            throw new InvalidOperationException($"Model '{selection.Model.Id}' is not declared image-capable; refusing to send image attachments.");
    }

    private async Task ExecuteEventsAsync(Func<CancellationToken, IAsyncEnumerable<AgentLifecycleEvent>> getEvents,
        InteractiveTranscript transcript, CancellationTokenSource cancellation)
    {
        void ProcessRunUpdate(AgentLifecycleEvent update)
        {
            if (update.Type is "prompt_accepted" or "agent_attempt_started" or "compaction_start")
                screen?.ReportTerminalProgress(null);

            if (update.Type is "prompt_accepted" or "agent_attempt_started")
            {
                screen?.SetFooter(getFooter(), render: false);
                screen?.SetWorkingStatus("Working", render: false);
            }
            if (update.Type == "prompt_accepted" && editor is not null && !_monitorStarted)
            {
                transcript.Render(update);
                _monitorStarted = true;
                _monitor = editor.MonitorRunAsync(async (text, followUp, token) =>
                {
                    try
                    {
                        if (InteractiveBashExecutor.TryParse(text, out var command, out var excludeFromContext))
                        {
                            await executeBash(command, excludeFromContext, token);
                            return true;
                        }
                        if (tryExecuteExtensionCommand is not null && await tryExecuteExtensionCommand(text, token))
                            return true;
                        var behavior = followUp
                            ? PiSharpInputStreamingBehavior.FollowUp
                            : PiSharpInputStreamingBehavior.Steer;
                        var intercepted = await getRun().InterceptInputAsync(text, null, PiSharpInputSource.Interactive,
                            behavior, token);
                        if (intercepted.Action == PiSharpInputResultAction.Handled) return true;
                        var prompt = intercepted.Action == PiSharpInputResultAction.Transform ? intercepted.Text! : text;
                        var images = intercepted.Action == PiSharpInputResultAction.Transform ? intercepted.Images : null;
                        if (images is { Count: > 0 })
                            ValidateImages(images, getModelSelection(), getUserSettings());
                        var queued = await getResources().ResolveInputAsync(prompt, token,
                            error => ReportResourceErrorAsync(error, render: true));
                        var contents = new List<AIContent> { new TextContent(queued) };
                        if (images is not null) contents.AddRange(images);
                        var message = new ChatMessage(ChatRole.User, contents);
                        var accepted = followUp ? getRun().TryFollowUp(message, queued) : getRun().TrySteer(message, queued);
                        if (accepted) getError().WriteLine(followUp ? "Queued follow-up." : "Queued steering message.");
                        return accepted;
                    }
                    catch (Exception queueError) when (queueError is ArgumentException or IOException)
                    {
                        getError().WriteLine($"Could not queue input: {queueError.Message}");
                        return false;
                    }
                }, () => getRun().ClearPendingPrompts().InDeliveryOrder, cancellation.Cancel, _monitorStop.Token,
                    handleAction, handleExtensionShortcut);
                return;
            }
            if (update.Type == "assistant_turn_started")
            {
                screen?.SetFooter(getFooter(), render: false);
            }
            transcript.Render(update);
            Environment.ExitCode = Math.Max(Environment.ExitCode, transcript.ExitCode);
        }
        await foreach (var update in getEvents(cancellation.Token))
            ProcessRunUpdate(update);
        transcript.FinishTurn();
        Environment.ExitCode = Math.Max(Environment.ExitCode, transcript.ExitCode);
    }

    public async Task StopMonitoringAsync()
    {
        _monitorStop.Cancel();
        try { await _monitor; }
        catch (OperationCanceledException) { }
    }

    private Task ReportResourceErrorAsync(ResourceExpansionError error, bool render)
    {
        if (screen is { IsActive: true })
        {
            screen.AppendExtensionError(error.ExtensionPath, error.Error, render);
            return Task.CompletedTask;
        }
        return getError().WriteLineAsync($"Extension error ({error.ExtensionPath}): {error.Error}");
    }

    public void Dispose() => _monitorStop.Dispose();
}
