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

    public IReadOnlyList<ChatMessage> MissingToolResults(IReadOnlyList<ChatMessage> canonicalHistory)
    {
        var knownCallIds = canonicalHistory.SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Select(result => result.CallId).ToHashSet(StringComparer.Ordinal);
        var missing = new List<ChatMessage>();
        foreach (var message in _completedMessages.Where(message => message.Role == ChatRole.Tool))
        {
            var results = message.Contents.OfType<FunctionResultContent>().ToArray();
            var missingResults = results.Where(result => knownCallIds.Add(result.CallId)).ToArray();
            if (missingResults.Length == 0) continue;
            missing.Add(missingResults.Length == results.Length ? message : new ChatMessage(ChatRole.Tool, missingResults));
        }
        return missing;
    }

    public static ChatMessage? RetainUncheckpointedToolResults(ChatMessage message, HashSet<string> knownCallIds)
    {
        if (message.Role != ChatRole.Tool) return message;
        var contents = message.Contents.Where(content => content is not FunctionResultContent result ||
            knownCallIds.Add(result.CallId)).ToArray();
        if (contents.Length == 0) return null;
        return contents.Length == message.Contents.Count ? message : new ChatMessage(ChatRole.Tool, contents);
    }

    public int FindEquivalentRangeEnd(IReadOnlyList<ChatMessage> history, int startIndex, ChatMessage message)
    {
        for (var index = startIndex; index < history.Count; index++)
        {
            if (AreEquivalent(history[index], message)) return index + 1;
            if (MatchesToolResultGroup(history, index, message, out var end)) return end;
        }
        return -1;
    }

    public void RestoreMissingMessages(List<ChatMessage> providerHistory)
    {
        var reconcileIndex = 0;
        foreach (var message in _completedMessages)
        {
            var matchEnd = FindEquivalentRangeEnd(providerHistory, reconcileIndex, message);
            if (matchEnd >= 0)
            {
                reconcileIndex = matchEnd;
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

    private static bool MatchesToolResultGroup(IReadOnlyList<ChatMessage> history, int startIndex,
        ChatMessage expected, out int endIndex)
    {
        endIndex = startIndex;
        if (expected.Role != ChatRole.Tool) return false;
        var expectedResults = expected.Contents.OfType<FunctionResultContent>().ToArray();
        if (expectedResults.Length == 0) return false;
        var actualResults = new List<FunctionResultContent>();
        var lastIndex = startIndex;
        for (var index = startIndex; index < history.Count && history[index].Role == ChatRole.Tool; index++)
        {
            var results = history[index].Contents.OfType<FunctionResultContent>().ToArray();
            if (results.Length == 0) return false;
            actualResults.AddRange(results);
            lastIndex = index + 1;
        }
        foreach (var expectedResult in expectedResults)
        {
            var matchIndex = actualResults.FindIndex(result => result.CallId == expectedResult.CallId);
            if (matchIndex < 0) return false;
            actualResults.RemoveAt(matchIndex);
        }
        if (actualResults.Count == 0) endIndex = lastIndex;
        return true;
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
