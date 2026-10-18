using PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Runtime.Extensions;

internal sealed partial class NativeExtensionEventDispatcher
{
    internal bool HasTurnEndBoundaryHandlers => registration.TurnEndBoundaryHandlers.Count > 0;
    internal bool HasAgentBeforeSettleHandlers => registration.AgentBeforeSettleHandlers.Count > 0;
    internal bool HasBoundaryHandlers => HasTurnEndBoundaryHandlers || HasAgentBeforeSettleHandlers ||
        registration.AgentSettledHandlers.Count > 0;

    internal Task<ExtensionBoundaryDecision> DispatchTurnEndBoundaryAsync(PiSharpExtensionTurnEndEvent turn,
        PiSharpExtensionAgentOutcome outcome, Func<IReadOnlyList<PiSharpSessionBoundaryDraft>, PiSharpBoundaryContextPreview> preview,
        CancellationToken cancellationToken, Action<AgentLifecycleEvent> publish) =>
        DispatchBoundaryAsync(registration.TurnEndBoundaryHandlers,
            (entries, shouldContinue, contextPreview) => new PiSharpExtensionTurnEndBoundaryEvent(
                turn, outcome, entries, shouldContinue, contextPreview), preview, "turn_end", cancellationToken, publish);

    internal Task<ExtensionBoundaryDecision> DispatchAgentBeforeSettleAsync(PiSharpExtensionAgentOutcome outcome,
        Func<IReadOnlyList<PiSharpSessionBoundaryDraft>, PiSharpBoundaryContextPreview> preview,
        CancellationToken cancellationToken, Action<AgentLifecycleEvent> publish) =>
        DispatchBoundaryAsync(registration.AgentBeforeSettleHandlers,
            (entries, shouldContinue, contextPreview) => new PiSharpExtensionAgentBeforeSettleEvent(
                outcome, entries, shouldContinue, contextPreview), preview, "agent_before_settle", cancellationToken, publish);

    internal Task DispatchAgentSettledAsync(PiSharpExtensionAgentSettledEvent eventData,
        CancellationToken cancellationToken, Action<AgentLifecycleEvent> publish) =>
        DispatchAsync(registration.AgentSettledHandlers, eventData, "agent_settled", cancellationToken, publish);

    private async Task<ExtensionBoundaryDecision> DispatchBoundaryAsync<TEvent>(
        IReadOnlyList<ExtensionHandlerRegistration<ExtensionDecisionEventHandler<TEvent, PiSharpBoundaryResult>>> handlers,
        Func<IReadOnlyList<PiSharpSessionBoundaryDraft>, bool, PiSharpBoundaryContextPreview, TEvent> createEvent,
        Func<IReadOnlyList<PiSharpSessionBoundaryDraft>, PiSharpBoundaryContextPreview> preview,
        string eventType, CancellationToken cancellationToken, Action<AgentLifecycleEvent> publish)
    {
        IReadOnlyList<PiSharpSessionBoundaryDraft> entries = Array.Empty<PiSharpSessionBoundaryDraft>();
        var shouldContinue = false;
        var contextPreview = preview(entries);
        var valid = true;
        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var pending = handler.Handler(createEvent(entries, shouldContinue, contextPreview), context,
                    cancellationToken).AsTask();
                PiSharpBoundaryResult? result;
                try { result = await pending.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // This is the host adapter task, which detaches unfinished extension code
                    // after revoking its context. Finish that cleanup before releasing the run.
                    try { await pending.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                    catch (Exception error) { Report(error, handler.SourceInfo, eventType, publish); }
                    throw;
                }
                if (result?.Entries is { } replacement) entries = replacement;
                if (result?.Continue is { } continuation) shouldContinue = continuation;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) { Report(error, handler.SourceInfo, eventType, publish); }

            try
            {
                contextPreview = preview(entries);
                valid = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                valid = false;
                Report(new InvalidDataException("Invalid boundary entries: " + error.Message, error),
                    handler.SourceInfo, eventType, publish);
            }
            if (cancellationToken.IsCancellationRequested)
                return valid ? new(entries, false, true) : new([], false, false);
        }
        return valid ? new(entries, shouldContinue, true) : new([], false, false);
    }
}

internal sealed record ExtensionBoundaryDecision(IReadOnlyList<PiSharpSessionBoundaryDraft> Entries,
    bool Continue, bool Valid);
