using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Sessions;

public sealed partial class ConversationRun
{
    private long _boundaryContextVersion;
    private bool _boundaryContinuationRequested;
    private NativeExtensions.PiSharpExtensionTurnEndEvent? _terminalBoundaryTurn;

    private ExtensionBoundaryCoordinator CreateBoundaryCoordinator() => new(Conversation, _save,
        (messages, token) => RestoreExecutionAsync(messages, token),
        (execution, count) =>
        {
            _execution = execution;
            _historyCount = count;
            _boundaryContextVersion++;
        }, () =>
        {
            lock (_runtimeStateGate)
                return (_promptDelivery.SnapshotMessages(),
                    _pendingContextCustomMessages.Select(PiSharpCustomMessageProjection.ToChatMessage).ToArray());
        });

    private async Task<bool> DispatchMutableTurnBoundaryAsync(NativeExtensions.PiSharpExtensionTurnEndEvent turn,
        NativeExtensions.PiSharpExtensionAgentOutcome outcome, CancellationToken cancellationToken,
        Action<AgentLifecycleEvent> publish)
    {
        if (!_agent.ExtensionEvents.HasTurnEndBoundaryHandlers) return false;
        var ownsGate = ReferenceEquals(s_currentGateOwner.Value, this);
        if (!ownsGate) await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previousOwner = s_currentGateOwner.Value;
        s_currentGateOwner.Value = this;
        var coordinator = CreateBoundaryCoordinator();
        try
        {
            var decision = await _agent.ExtensionEvents.DispatchTurnEndBoundaryAsync(turn, outcome,
                entries => coordinator.Preview(entries, false,
                    cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken), cancellationToken, publish)
                .ConfigureAwait(false);
            await CommitBoundaryDecisionAsync(coordinator, decision.Entries, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return false;
            if (!decision.Continue) return false;
            if (coordinator.Preview([], false, cancellationToken).CanContinue) return true;
            ReportBoundaryError("turn_end", "Requested continuation without runnable model context.", publish);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            ReportBoundaryError("turn_end", error.Message, publish);
            return false;
        }
        finally
        {
            s_currentGateOwner.Value = previousOwner;
            if (!ownsGate) _gate.Release();
        }
    }

    private async Task<bool> DispatchBeforeSettleBoundaryAsync(NativeExtensions.PiSharpExtensionAgentOutcome outcome,
        CancellationToken cancellationToken, Action<AgentLifecycleEvent> publish)
    {
        if (!_agent.ExtensionEvents.HasAgentBeforeSettleHandlers) return false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previousOwner = s_currentGateOwner.Value;
        s_currentGateOwner.Value = this;
        try
        {
            var coordinator = CreateBoundaryCoordinator();
            var decision = await _agent.ExtensionEvents.DispatchAgentBeforeSettleAsync(outcome,
                entries => coordinator.Preview(entries, true,
                    cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken), cancellationToken, publish)
                .ConfigureAwait(false);
            await CommitBoundaryDecisionAsync(coordinator, decision.Entries, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return false;
            if (!decision.Continue) return false;
            if (coordinator.Preview([], true, cancellationToken).CanContinue) return true;
            ReportBoundaryError("agent_before_settle", "Requested continuation without runnable model context.", publish);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            ReportBoundaryError("agent_before_settle", error.Message, publish);
            return false;
        }
        finally
        {
            s_currentGateOwner.Value = previousOwner;
            _gate.Release();
        }
    }

    private static void ReportBoundaryError(string eventType, string message, Action<AgentLifecycleEvent> publish) =>
        publish(new("extension_error", Error: message,
            Details: new { extensionPath = "<boundary>", eventType }));

    private static async Task CommitBoundaryDecisionAsync(ExtensionBoundaryCoordinator coordinator,
        IReadOnlyList<NativeExtensions.PiSharpSessionBoundaryDraft> entries, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            await coordinator.CommitAsync(entries, cancellationToken).ConfigureAwait(false);
            return;
        }
        using var terminalCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await coordinator.CommitAsync(entries, terminalCancellation.Token).ConfigureAwait(false);
    }

    private async Task CompactAfterBoundaryAsync(CancellationToken cancellationToken,
        Action<AgentLifecycleEvent> publish)
    {
        if (!AutoCompactionEnabled || _autoCompaction is not { TriggerTokens: > 0 } policy ||
            EstimateNextContext("") <= policy.TriggerTokens) return;
        var ownsGate = ReferenceEquals(s_currentGateOwner.Value, this);
        if (!ownsGate) await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var previousOwner = s_currentGateOwner.Value;
        s_currentGateOwner.Value = this;
        try
        {
            if (ConversationCompactionCoordinator.PreparePlan(Conversation,
                    _autoCompaction?.KeepRecentTokens ?? _keepRecentTokens) is null) return;
            publish(new("compaction_start") { CompactionReason = "threshold" });
            try
            {
                var result = await _compactionCoordinator.CompactAsync(null, cancellationToken,
                    reason: NativeExtensions.PiSharpExtensionCompactionReason.Threshold).ConfigureAwait(false);
                if (result is null) return;
                _boundaryContextVersion++;
                await NativeExtensionCompactionEventPublisher.PublishCompletedAsync(_agent, Conversation, result,
                    NativeExtensions.PiSharpExtensionCompactionReason.Threshold, false, cancellationToken)
                    .ConfigureAwait(false);
                publish(new("compaction_end")
                {
                    CompactionReason = "threshold",
                    CompactionResult = result,
                    CompactionWillRetry = false
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await NativeExtensionCompactionEventPublisher.PublishAbortedAsync(_agent,
                    NativeExtensions.PiSharpExtensionCompactionReason.Threshold).ConfigureAwait(false);
                publish(new("compaction_end") { CompactionReason = "threshold", CompactionAborted = true });
                throw;
            }
            catch (Exception error)
            {
                if (_compactionCoordinator.LastCompactionCancelledByExtension)
                    await NativeExtensionCompactionEventPublisher.PublishAbortedAsync(_agent,
                        NativeExtensions.PiSharpExtensionCompactionReason.Threshold).ConfigureAwait(false);
                else
                    await NativeExtensionCompactionEventPublisher.PublishFailedAsync(_agent,
                        NativeExtensions.PiSharpExtensionCompactionReason.Threshold, false, false, error.Message,
                        cancellationToken).ConfigureAwait(false);
                publish(new("compaction_end", Error: error.Message) { CompactionReason = "threshold" });
            }
        }
        finally
        {
            s_currentGateOwner.Value = previousOwner;
            if (!ownsGate) _gate.Release();
        }
    }
}
