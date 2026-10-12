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
    public ChatMessage? Response { get; set; }
    public StringBuilder PartialAssistantText { get; } = new();

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
    public void CompleteTurn()
    {
        if (Response is not null)
        {
            var response = Response;
            var toolResults = _toolResults.ToArray();
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
        foreach (var result in _history.MissingToolResults(conversation.ContextMessages()))
            foreach (var normalized in ObservedChatClient.NormalizeReadImagesForHistory([result]))
                conversation.Append(normalized, NestedCallsFor(normalized));
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
