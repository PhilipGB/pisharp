using Microsoft.Extensions.AI;
using PiSharp.Cli.Composition;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli.Tui;

internal sealed class TerminalConversationExecution
{
    public TerminalConversationExecution(TerminalPresentation presentation, ActiveProjectRuntime projectBinding,
        ActiveModelRuntime modelBinding, ActiveConversationRuntime conversationBinding, bool print)
    {
        _presentation = presentation;
        _projectBinding = projectBinding;
        _modelBinding = modelBinding;
        _conversationBinding = conversationBinding;
        _print = print;
        if (_presentation.Editor is null) Console.CancelKeyPress += (_, e) => { e.Cancel = true; _activeRun?.Cancel(); };
    }

    private readonly TerminalPresentation _presentation;
    private readonly ActiveProjectRuntime _projectBinding;
    private readonly ActiveModelRuntime _modelBinding;
    private readonly ActiveConversationRuntime _conversationBinding;
    private readonly bool _print;

    public async Task RunBashAsync(string command, bool excludeFromContext)
    {
        using var cancellation = new CancellationTokenSource();
        _activeRun = cancellation;
        try { await ExecuteInteractiveBashAsync(command, excludeFromContext, cancellation.Token); }
        finally { _activeRun = null; }
    }

    public InteractiveTranscript? ActiveTranscript { get; private set; }
    private CancellationTokenSource? _activeRun;
    private TaskCompletionSource? _activeRunCompletion;

    public void AbortCurrentRun()
    {
        try { Volatile.Read(ref _activeRun)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async Task CancelActiveRunAsync(CancellationToken cancellationToken)
    {
        var completion = Volatile.Read(ref _activeRunCompletion);
        if (completion is null) return;
        AbortCurrentRun();
        await completion.Task.WaitAsync(cancellationToken);
    }


    public Task RunAsync(string input, IReadOnlyList<DataContent>? images = null,
        Func<string, Task>? handleApplicationAction = null, CancellationToken cancellationToken = default,
        Func<PiSharp.Runtime.Extensions.ExtensionShortcutInfo, CancellationToken, Task>? handleExtensionShortcut = null) =>
        RunCoreAsync((promptExecution, transcript, cancellation) =>
            promptExecution.ExecuteAsync(input, images, _modelBinding.Selection, _projectBinding.Settings,
                transcript, cancellation), handleApplicationAction, cancellationToken, handleExtensionShortcut);

    public Task RunCustomMessageAsync(PiSharpCustomMessage message, string? sourcePath,
        Func<string, Task>? handleApplicationAction = null, CancellationToken cancellationToken = default,
        Func<PiSharp.Runtime.Extensions.ExtensionShortcutInfo, CancellationToken, Task>? handleExtensionShortcut = null) =>
        RunCoreAsync((promptExecution, transcript, cancellation) =>
            promptExecution.ExecuteCustomMessageAsync(message, sourcePath, transcript, cancellation), handleApplicationAction,
            cancellationToken, handleExtensionShortcut);

    private async Task RunCoreAsync(
        Func<InteractivePromptExecution, InteractiveTranscript, CancellationTokenSource, Task> run,
        Func<string, Task>? handleApplicationAction, CancellationToken cancellationToken,
        Func<PiSharp.Runtime.Extensions.ExtensionShortcutInfo, CancellationToken, Task>? handleExtensionShortcut)
    {
        using var runCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var execution = new InteractivePromptExecution(_presentation.Editor, _presentation.Screen, () => Console.Error,
            () => _projectBinding.Resources, () => _conversationBinding.Run, () => _presentation.IdleFooter(), ExecuteInteractiveBashAsync,
            handleApplicationAction ?? (_ => Task.CompletedTask), handleExtensionShortcut);
        _presentation.Screen?.Activate();
        _presentation.Editor?.AttachScreen(_presentation.Screen);
        var transcript = _presentation.CreateInteractiveTranscript(interactive: !_print);
        ActiveTranscript = transcript;
        Volatile.Write(ref _activeRunCompletion, completion);
        _activeRun = runCancel;
        try
        {
            await run(execution, transcript, runCancel);
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Interrupted."); }
        catch (Exception ex) { Console.Error.WriteLine($"Agent error: {ex.Message}"); Environment.ExitCode = 1; }
        finally
        {
            _presentation.Screen?.ClearTerminalProgress();
            await execution.StopMonitoringAsync();
            // Store the selected branch and any completed/aborted messages even when a turn fails.
            if (_conversationBinding.Path is not null)
                try { await _projectBinding.Store.SaveAsync(_conversationBinding.Conversation, _conversationBinding.Path); }
                catch (Exception e) { Console.Error.WriteLine($"Could not save session: {e.Message}"); Environment.ExitCode = 1; }
            _activeRun = null;
            if (ReferenceEquals(ActiveTranscript, transcript)) ActiveTranscript = null;
            _presentation.Screen?.SetFooter(_presentation.IdleFooter(), render: false);
            _presentation.Screen?.SetWorkingStatus(null);
            completion.TrySetResult();
            Interlocked.CompareExchange(ref _activeRunCompletion, null, completion);
        }
    }

    private async Task ExecuteInteractiveBashAsync(string command, bool excludeFromContext, CancellationToken cancellationToken)
    {
        await InteractiveBashExecutor.ExecuteAsync(_conversationBinding.Run, command, excludeFromContext,
            _presentation.CreateInteractiveTranscript(), _conversationBinding.Path is null ? null : token => _projectBinding.Store.SaveAsync(_conversationBinding.Conversation, _conversationBinding.Path, token),
            cancellationToken);
    }

}
