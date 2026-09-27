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

    [Fact]
    public void RestoreMissingMessagesRecognizesGroupedProviderToolResults()
    {
        var assistant = new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("call-a", "read", new Dictionary<string, object?>()),
            new FunctionCallContent("call-b", "read", new Dictionary<string, object?>())
        ]);
        var resultA = ToolResult("call-a", "first");
        var resultB = ToolResult("call-b", "second");
        var groupedResult = new ChatMessage(ChatRole.Tool, [resultA.Contents[0], resultB.Contents[0]]);
        var reconciler = new ProviderTurnHistoryReconciler();
        reconciler.RecordCompletedTurn(assistant, [resultA, resultB]);
        var history = new List<ChatMessage> { assistant, groupedResult };

        reconciler.RestoreMissingMessages(history);

        Assert.Collection(history, message => Assert.Same(assistant, message),
            message => Assert.Same(groupedResult, message));
    }

    [Fact]
    public void FindEquivalentRangeEndMatchesGroupedProviderResultsToSeparateCanonicalEntries()
    {
        var resultA = ToolResult("call-a", "first");
        var resultB = ToolResult("call-b", "second");
        var grouped = new ChatMessage(ChatRole.Tool, [resultA.Contents[0], resultB.Contents[0]]);
        var history = new ChatMessage[] { ToolResult("call-a", "first"), ToolResult("call-b", "second") };
        var reconciler = new ProviderTurnHistoryReconciler();

        var end = reconciler.FindEquivalentRangeEnd(history, 0, grouped);

        Assert.Equal(2, end);
    }

    [Fact]
    public void RetainUncheckpointedToolResultsFiltersCallIdsAcrossDifferentGrouping()
    {
        var knownCallIds = new HashSet<string>(["call-a", "call-b"], StringComparer.Ordinal);
        var repeated = ToolResult("call-b", "provider representation differs");
        var grouped = new ChatMessage(ChatRole.Tool,
        [
            new FunctionResultContent("call-a", "first"),
            new FunctionResultContent("call-c", "new result")
        ]);

        Assert.Null(ProviderTurnHistoryReconciler.RetainUncheckpointedToolResults(repeated, knownCallIds));
        var retained = Assert.IsType<ChatMessage>(
            ProviderTurnHistoryReconciler.RetainUncheckpointedToolResults(grouped, knownCallIds));
        var result = Assert.Single(retained.Contents.OfType<FunctionResultContent>());
        Assert.Equal("call-c", result.CallId);
        Assert.Contains("call-c", knownCallIds);
    }

    [Fact]
    public void MissingToolResultsFillsOnlyResultsNotAlreadyCheckpointed()
    {
        var assistant = new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("call-a", "read", new Dictionary<string, object?>()),
            new FunctionCallContent("call-b", "read", new Dictionary<string, object?>())
        ]);
        var resultA = ToolResult("call-a", "first");
        var resultB = ToolResult("call-b", "second");
        var reconciler = new ProviderTurnHistoryReconciler();
        reconciler.RecordCompletedTurn(assistant, [resultA, resultB]);

        var missing = reconciler.MissingToolResults([assistant, ToolResult("call-a", "provider copy")]);

        Assert.Collection(missing, message => Assert.Same(resultB, message));
    }

    [Fact]
    public void MissingToolResultsReturnsCompletedBatchesInAssistantCallOrder()
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

        var missing = reconciler.MissingToolResults([assistant]);

        Assert.Collection(missing, message => Assert.Same(resultA, message),
            message => Assert.Same(resultB, message));
    }

    [Fact]
    public void MissingToolResultsFiltersAlreadyCheckpointedResultsFromGroupedMessage()
    {
        var assistant = new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("call-a", "read", new Dictionary<string, object?>()),
            new FunctionCallContent("call-b", "read", new Dictionary<string, object?>())
        ]);
        var resultA = ToolResult("call-a", "first");
        var resultB = ToolResult("call-b", "second");
        var groupedResults = new ChatMessage(ChatRole.Tool, [resultA.Contents[0], resultB.Contents[0]]);
        var reconciler = new ProviderTurnHistoryReconciler();
        reconciler.RecordCompletedTurn(assistant, [groupedResults]);

        var missing = reconciler.MissingToolResults([assistant, ToolResult("call-a", "provider copy")]);

        var partial = Assert.Single(missing);
        var missingResult = Assert.Single(partial.Contents.OfType<FunctionResultContent>());
        Assert.Equal("call-b", missingResult.CallId);
    }

    private static ChatMessage ToolResult(string callId, string value) =>
        new(ChatRole.Tool, [new FunctionResultContent(callId, value)]);
}
