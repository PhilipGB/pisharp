using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Tests;

public sealed class ProviderTurnHistoryReconcilerTests
{
    [Fact]
    public void RestoreMissingMessagesRetainsToolResultsInAssistantCallOrder()
    {
        var assistant = new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("call-a", "read", new Dictionary<string, object?>()),
            new FunctionCallContent("call-b", "read", new Dictionary<string, object?>())
        ]);
        var resultA = ToolResult("call-a", "first");
        var resultB = ToolResult("call-b", "second");
        var reconciler = new ProviderTurnHistoryReconciler();
        reconciler.RecordCompletedTurn(assistant, [resultB, resultA]);

        var history = new List<ChatMessage>();
        reconciler.RestoreMissingMessages(history);

        Assert.Collection(history,
            message => Assert.Same(assistant, message),
            message => Assert.Same(resultA, message),
            message => Assert.Same(resultB, message));
    }

    [Fact]
    public void RestoreMissingMessagesDoesNotDuplicateToolResultWithDifferentTimestamp()
    {
        var assistant = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-a", "read", new Dictionary<string, object?>())]);
        var emittedResult = ToolResult("call-a", "contents");
        emittedResult.CreatedAt = DateTimeOffset.UnixEpoch;
        var providerResult = ToolResult("call-a", "contents");
        providerResult.CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
        var reconciler = new ProviderTurnHistoryReconciler();
        reconciler.RecordCompletedTurn(assistant, [emittedResult]);

        var history = new List<ChatMessage> { assistant, providerResult };
        reconciler.RestoreMissingMessages(history);

        Assert.Collection(history, message => Assert.Same(assistant, message),
            message => Assert.Same(providerResult, message));
    }

    private static ChatMessage ToolResult(string callId, string value) =>
        new(ChatRole.Tool, [new FunctionResultContent(callId, value)]);
}
