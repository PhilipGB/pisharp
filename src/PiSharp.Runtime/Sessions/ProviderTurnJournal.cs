using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Sessions;

/// <summary>Retains one attempt's provider turns and reconciles tool results with canonical history.</summary>
internal sealed class ProviderTurnJournal(ConversationSession conversation,
    Func<IReadOnlyList<ChatMessage>> providerHistory, Func<bool> hasCompacted, Func<int> historyCount,
    Action<AgentLifecycleEvent>? publish)
{
    private readonly ProviderTurnHistoryReconciler _history = new();
    private readonly ConcurrentDictionary<string, PiSharpNestedToolCalls> _nestedCalls = new(StringComparer.Ordinal);
    private readonly List<ChatMessage> _toolResults = [];
    private int _completedPathLength = conversation.Tree.ActivePath().Count;
    public ChatMessage? Response { get; set; }
    public StringBuilder PartialAssistantText { get; } = new();
    public bool RequestInProgress { get; private set; }

    public void MarkRequestStarted() => RequestInProgress = true;

    public void MarkRequestCompleted() => RequestInProgress = false;

    public ProviderTurnSnapshot SnapshotTurn() => new(Response, Array.AsReadOnly(_toolResults.ToArray()));

    // Pi emits parallel tool results in the assistant message's call order, not completion order.
    public static List<ChatMessage> OrderByAssistantCalls(ChatMessage? assistant, IReadOnlyList<ChatMessage> results)
    {
        if (assistant is null || results.Count <= 1) return results.ToList();
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        var index = 0;
        foreach (var call in assistant.Contents.OfType<FunctionCallContent>())
            if (!string.IsNullOrEmpty(call.CallId))
                order.TryAdd(call.CallId, index++);
        return results
            .Select((result, position) => (Result: result, Position: position))
            .OrderBy(item => item.Result.Contents.OfType<FunctionResultContent>().FirstOrDefault()?.CallId is { } callId &&
                    order.TryGetValue(callId, out var callIndex)
                ? callIndex
                : int.MaxValue)
            .ThenBy(item => item.Position)
            .Select(item => item.Result)
            .ToList();
    }

    public PiSharpNestedToolCalls? NestedCallsFor(ChatMessage message)
    {
        var callId = message.Contents.OfType<FunctionResultContent>().FirstOrDefault()?.CallId;
        return callId is not null && _nestedCalls.TryGetValue(callId, out var nested) ? nested : null;
    }

    public void RecordToolResult(ChatMessage result, PiSharpNestedToolCalls? nested)
    {
        _toolResults.Add(result);
        if (nested is not null && result.Contents.OfType<FunctionResultContent>().FirstOrDefault() is { } resultContent)
            _nestedCalls[resultContent.CallId] = nested;
    }

    // The caller holds ConversationRun's runtime-state lock for result capture and turn closure.
    public ProviderTurnSnapshot CompleteTurn()
    {
        var completed = SnapshotTurn();
        if (Response is not null)
        {
            var response = completed.Message!;
            var toolResults = OrderByAssistantCalls(response, completed.ToolResults);
            _history.RecordCompletedTurn(response, toolResults);
            publish?.Invoke(new AgentLifecycleEvent("assistant_turn_completed")
            {
                TurnMessage = response,
                TurnToolResults = toolResults
            });
            Response = null;
            _toolResults.Clear();
        }
        var provider = ObservedChatClient.NormalizeReadImagesForHistory(providerHistory());
        var canonical = hasCompacted() ? conversation.ActiveMessages() : conversation.ContextMessages();
        AppendMissingMessages(hasCompacted() ? provider.Skip(historyCount()) : provider, canonical);
        if (completed.Message is { } assistant && !conversation.Tree.ActivePath().Skip(_completedPathLength)
                .Any(entry => entry.Type == "chat" && ProviderTurnHistoryReconciler.AreEquivalent(
                    ConversationMessageCodec.RestoreEntry(entry), assistant)))
            conversation.Append(assistant);
        foreach (var result in _history.MissingToolResults(conversation.ActiveMessages()))
            foreach (var normalized in ObservedChatClient.NormalizeReadImagesForHistory([result]))
                conversation.Append(normalized, NestedCallsFor(normalized));
        _completedPathLength = conversation.Tree.ActivePath().Count;
        return completed;
    }

    public void RestoreMissingMessages(List<ChatMessage> provider) => _history.RestoreMissingMessages(provider);

    public void AppendMissingMessages(IEnumerable<ChatMessage> provider, List<ChatMessage> canonical,
        IReadOnlyList<ChatMessage>? knownHistory = null)
    {
        var knownCallIds = (knownHistory ?? canonical).SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Select(result => result.CallId).ToHashSet(StringComparer.Ordinal);
        var canonicalIndex = 0;
        foreach (var providerMessage in provider)
        {
            var matchEnd = _history.FindEquivalentRangeEnd(canonical, canonicalIndex, providerMessage);
            if (matchEnd >= 0)
            {
                canonicalIndex = matchEnd;
                continue;
            }
            var message = ProviderTurnHistoryReconciler.RetainUncheckpointedToolResults(providerMessage, knownCallIds);
            if (message is null) continue;
            matchEnd = _history.FindEquivalentRangeEnd(canonical, canonicalIndex, message);
            if (matchEnd >= 0) canonicalIndex = matchEnd;
            else
            {
                conversation.Append(message, NestedCallsFor(message));
                canonical.Add(message);
                canonicalIndex = canonical.Count;
            }
        }
    }
}

internal sealed record ProviderTurnSnapshot(ChatMessage? Message, IReadOnlyList<ChatMessage> ToolResults);
