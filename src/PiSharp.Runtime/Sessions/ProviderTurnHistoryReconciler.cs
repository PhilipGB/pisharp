using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

internal sealed class ProviderTurnHistoryReconciler
{
    private readonly List<ChatMessage> _completedMessages = [];

    public void RecordCompletedTurn(ChatMessage assistantMessage, IReadOnlyList<ChatMessage> toolResults)
    {
        _completedMessages.Add(assistantMessage);
        _completedMessages.AddRange(OrderToolResults(assistantMessage, toolResults));
    }

    public void RestoreMissingMessages(List<ChatMessage> providerHistory)
    {
        var reconcileIndex = 0;
        foreach (var message in _completedMessages)
        {
            var matchIndex = providerHistory.FindIndex(reconcileIndex, candidate => MessagesEqual(candidate, message));
            if (matchIndex >= 0)
            {
                reconcileIndex = matchIndex + 1;
                continue;
            }

            providerHistory.Insert(reconcileIndex, message);
            reconcileIndex++;
        }
    }

    private static IReadOnlyList<ChatMessage> OrderToolResults(ChatMessage assistantMessage,
        IReadOnlyList<ChatMessage> results)
    {
        var byCallId = results.SelectMany(result => result.Contents.OfType<FunctionResultContent>()
                .Select(content => (content.CallId, Result: result)))
            .GroupBy(item => item.CallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Result, StringComparer.Ordinal);
        var ordered = assistantMessage.Contents.OfType<FunctionCallContent>()
            .Where(call => byCallId.ContainsKey(call.CallId))
            .Select(call => byCallId[call.CallId]).ToList();
        ordered.AddRange(results.Where(result => !ordered.Contains(result)));
        return ordered;
    }

    private static bool MessagesEqual(ChatMessage left, ChatMessage right)
    {
        if (left.Role == ChatRole.Tool && right.Role == ChatRole.Tool)
            return JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left.Contents, AIJsonUtilities.DefaultOptions),
                JsonSerializer.SerializeToElement(right.Contents, AIJsonUtilities.DefaultOptions));

        return JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left, AIJsonUtilities.DefaultOptions),
            JsonSerializer.SerializeToElement(right, AIJsonUtilities.DefaultOptions));
    }
}
