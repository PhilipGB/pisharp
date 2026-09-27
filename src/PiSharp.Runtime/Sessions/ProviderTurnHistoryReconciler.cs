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

    internal static bool AreEquivalent(ChatMessage left, ChatMessage right)
    {
        if (left.Role == ChatRole.Assistant && right.Role == ChatRole.Assistant)
        {
            if (!string.Equals(left.Text, right.Text, StringComparison.Ordinal)) return false;
            var leftCalls = left.Contents.OfType<FunctionCallContent>().ToArray();
            var rightCalls = right.Contents.OfType<FunctionCallContent>().ToArray();
            if (leftCalls.Length > 0 || rightCalls.Length > 0)
                return leftCalls.Length == rightCalls.Length && leftCalls.Zip(rightCalls, (leftCall, rightCall) =>
                    leftCall.CallId == rightCall.CallId && leftCall.Name == rightCall.Name).All(equal => equal);
            var leftOther = left.Contents.Where(content => content is not TextContent and not UsageContent).ToArray();
            var rightOther = right.Contents.Where(content => content is not TextContent and not UsageContent).ToArray();
            return JsonElement.DeepEquals(JsonSerializer.SerializeToElement(leftOther, AIJsonUtilities.DefaultOptions),
                JsonSerializer.SerializeToElement(rightOther, AIJsonUtilities.DefaultOptions));
        }

        return MessagesEqual(left, right);
    }
}
