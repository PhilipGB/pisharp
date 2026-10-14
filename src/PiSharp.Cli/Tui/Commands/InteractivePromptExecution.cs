using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Tui;

internal sealed class InteractivePromptExecution(TerminalEditor? editor, TerminalScreen? screen,
    Func<TextWriter> getError, Func<ResourceCatalog> getResources, Func<ConversationRun> getRun, Func<string> getFooter,
    Func<string, bool, CancellationToken, Task> executeBash, Func<string, Task> handleAction) : IDisposable
{
    private readonly CancellationTokenSource _monitorStop = new();
    private Task _monitor = Task.CompletedTask;
    private bool _monitorStarted;

    public async Task ExecuteAsync(string input, IReadOnlyList<DataContent>? images, ModelSelection selection,
        UserSettings settings, InteractiveTranscript transcript, CancellationTokenSource cancellation)
    {
        if (!selection.Authenticated)
            throw new InvalidOperationException($"Provider '{selection.Provider.Id}' is not authenticated. Use /login {selection.Provider.Id} before sending a prompt.");
        if (images is { Count: > 0 } && settings.BlockImages != true && selection.Model.Input is { } modalities &&
            !modalities.Contains("image", StringComparer.Ordinal))
            throw new InvalidOperationException($"Model '{selection.Model.Id}' is not declared image-capable; refusing to send image attachments.");
        var expanded = await getResources().ResolveInputAsync(input, cancellation.Token);
        var contents = new List<AIContent> { new TextContent(expanded) };
        if (images is not null) contents.AddRange(images);
        void ProcessRunUpdate(AgentLifecycleEvent update)
        {
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
                        var queued = await getResources().ResolveInputAsync(text, token);
                        var accepted = followUp ? getRun().TryFollowUp(queued) : getRun().TrySteer(queued);
                        if (accepted) getError().WriteLine(followUp ? "Queued follow-up." : "Queued steering message.");
                        return accepted;
                    }
                    catch (Exception queueError) when (queueError is ArgumentException or IOException)
                    {
                        getError().WriteLine($"Could not queue input: {queueError.Message}");
                        return false;
                    }
                }, () => getRun().ClearPendingPrompts().InDeliveryOrder, cancellation.Cancel, _monitorStop.Token,
                    handleAction);
                return;
            }
            if (update.Type == "assistant_turn_started")
            {
                screen?.SetFooter(getFooter(), render: false);
            }
            transcript.Render(update);
            Environment.ExitCode = Math.Max(Environment.ExitCode, transcript.ExitCode);
        }
        await foreach (var update in getRun().RunEventsAsync(
            new ChatMessage(ChatRole.User, contents), expanded, cancellation.Token))
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

    public void Dispose() => _monitorStop.Dispose();
}
