using System.Reflection;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Sessions;

internal static class C6Oracle
{
    private static readonly Type s_budgetType = typeof(ConversationSession).Assembly.GetType(
        "PiSharp.Runtime.Sessions.InFlightContextBudget", throwOnError: true)!;
    private static readonly MethodInfo s_project = s_budgetType.GetMethod("ProjectAsync", BindingFlags.Public | BindingFlags.Instance)!;
    private static readonly Type s_estimatorType = typeof(ConversationSession).Assembly.GetType(
        "PiSharp.Runtime.Sessions.PiContextUsageEstimator", throwOnError: true)!;
    private static readonly MethodInfo s_piMessageEstimate = s_estimatorType.GetMethod(
        "EstimateAfterUsage", BindingFlags.Public | BindingFlags.Static)!;

    public static async Task<object> RunAsync()
    {
        var cases = new Dictionary<string, object>
        {
            ["C6.transient-projection-does-not-change-canonical-history"] = await ObserveAsync(
                "canonical", BasicLargeResult()),
            ["C6.single-large-current-tool-result-is-bounded"] = await ObserveAsync(
                "large-result", BasicLargeResult()),
            ["C6.sequential-tool-cycles-preserve-order"] = await ObserveAsync(
                "sequential", SequentialCycles()),
            ["C6.parallel-calls-and-results-remain-matched"] = await ObserveAsync(
                "parallel", ParallelCalls()),
            ["C6.large-tool-output-summary-retains-latest-outcome"] = await ObserveAsync(
                "latest-outcome", LatestOutcome()),
            ["C6.nested-tool-call-sequence-is-closed-before-cut"] = await ObserveNestedAsync(),
            ["C6.incomplete-call-graph-fails-before-provider-send"] = await ObserveAsync(
                "incomplete", IncompleteCall()),
            ["C6.duplicate-or-unmatched-results-never-replay-tools"] = await ObserveAsync(
                "duplicate", DuplicateResult()),
            ["C6.steering-after-tool-result-keeps-active-turn"] = await ObserveAsync(
                "steering", SteeringAfterResult()),
            ["C6.post-compaction-request-contains-valid-provider-history"] = await ObserveAsync(
                "post-compaction", SequentialCycles()),
        };
        return new { cases };
    }

    private static async Task<object> ObserveAsync(string scenario, ChatMessage[] messages)
    {
        var canonicalBefore = messages.Select(Snapshot).ToArray();
        var transientBudgetEstimate = AutoCompactionPolicy.Estimate(messages, "");
        var piCompatibleMessageEstimate = (int)s_piMessageEstimate.Invoke(null, [0L, messages])!;
        var summaryInputs = Array.Empty<ChatMessage>();
        var summaryText = "";
        var getPolicy = new Func<AutoCompactionPolicy>(() =>
            new AutoCompactionPolicy(2_700, ReserveTokens: 300, KeepRecentTokens: 200));
        var summarize = new Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<PiAgent.CompactionSummary>>(
            (prefix, _) =>
            {
                summaryInputs = prefix.ToArray();
                var latest = prefix.SelectMany(message => message.Contents.OfType<FunctionResultContent>())
                    .Select(result => ContentText(result.Result)).LastOrDefault() ?? "no tool outcome";
                summaryText = latest.Length <= 180 ? latest : latest[..180];
                return Task.FromResult(new PiAgent.CompactionSummary($"latest outcome: {summaryText}", null));
            });
        var budget = Activator.CreateInstance(s_budgetType,
            getPolicy,
            summarize,
            new Func<PiAgent.CompactionSummary, CancellationToken, Task>((_, _) => Task.CompletedTask))!;

        IReadOnlyList<ChatMessage>? projected = null;
        string? error = null;
        var providerSendCount = 0;
        try
        {
            var pending = (Task<IReadOnlyList<ChatMessage>>)s_project.Invoke(budget,
                [messages, true, CancellationToken.None])!;
            projected = await pending;
            providerSendCount++;
        }
        catch (TargetInvocationException exception)
        {
            error = exception.InnerException?.Message ?? exception.Message;
        }
        catch (InvalidOperationException exception)
        {
            error = exception.Message;
        }

        var canonicalAfter = messages.Select(Snapshot).ToArray();
        var projectedSnapshots = projected?.Select(Snapshot).ToArray() ?? [];
        var summaryInputSnapshots = summaryInputs.Select(Snapshot).ToArray();
        var projectedCalls = projectedSnapshots.SelectMany(message => message.callIds).ToArray();
        var projectedResults = projectedSnapshots.SelectMany(message => message.resultCallIds).ToArray();
        var summaryCalls = summaryInputSnapshots.SelectMany(message => message.callIds).ToArray();
        var summaryResults = summaryInputSnapshots.SelectMany(message => message.resultCallIds).ToArray();
        return new
        {
            scenario,
            transientBudgetEstimate,
            piCompatibleMessageEstimate,
            transientEstimateIsMoreConservative = transientBudgetEstimate > piCompatibleMessageEstimate,
            canonicalHistoryUnchanged = System.Text.Json.JsonSerializer.Serialize(canonicalBefore) ==
                System.Text.Json.JsonSerializer.Serialize(canonicalAfter),
            canonicalMessages = canonicalBefore,
            providerSendCount,
            providerRequestMessages = projectedSnapshots,
            providerHistoryValid = providerSendCount > 0 && IsBalanced(projectedSnapshots),
            providerCallIds = projectedCalls,
            providerResultCallIds = projectedResults,
            summarizedCallIds = summaryCalls,
            summarizedResultCallIds = summaryResults,
            summarizedHistoryValid = IsBalanced(summaryInputSnapshots),
            summaryText = projectedSnapshots.Select(message => message.text)
                .FirstOrDefault(text => text.Contains("latest outcome:", StringComparison.Ordinal)),
            latestOutcomeRetained = summaryText.Length > 0 &&
                projectedSnapshots.Any(message => message.text.Contains(summaryText, StringComparison.Ordinal)),
            projectedToolResultCharacters = projectedSnapshots.Sum(message => message.resultCharacters),
            steeringPromptPreserved = projectedSnapshots.Any(message => message.text == "change direction"),
            nestedExecutionComplete = (bool?)null,
            nestedCallIds = Array.Empty<string>(),
            error,
        };
    }

    private static async Task<object> ObserveNestedAsync()
    {
        var conversation = new ConversationSession(Path.GetTempPath(), "c6-nested", null);
        conversation.Append(new ChatMessage(ChatRole.User, "run the outer workflow"));
        conversation.Append(Calls(("outer-call", "outer")));
        using var arguments = System.Text.Json.JsonDocument.Parse("""{"key":"child"}""");
        var nested = new PiSharpNestedToolCalls(
        [
            new PiSharpNestedToolCall("outer-call/1", "child", "outer-call", "ok", arguments.RootElement.Clone()),
        ], Complete: true);
        var appendWithNested = typeof(ConversationSession).GetMethod("Append", BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, [typeof(ChatMessage), typeof(PiSharpNestedToolCalls)], modifiers: null)!;
        appendWithNested.Invoke(conversation, [Result("outer-call", "outer result after child"), nested]);
        var storedNested = conversation.ActiveNestedToolCallsByResultCallId()["outer-call"];
        var result = await ObserveAsync("nested", conversation.ContextMessages().ToArray());
        return new
        {
            scenario = "nested",
            canonicalHistoryUnchanged = ReadProperty<bool>(result, "canonicalHistoryUnchanged"),
            providerSendCount = ReadProperty<int>(result, "providerSendCount"),
            providerRequestMessages = ReadProperty<object[]>(result, "providerRequestMessages"),
            providerHistoryValid = ReadProperty<bool>(result, "providerHistoryValid"),
            providerCallIds = ReadProperty<string[]>(result, "providerCallIds"),
            providerResultCallIds = ReadProperty<string[]>(result, "providerResultCallIds"),
            summarizedCallIds = ReadProperty<string[]>(result, "summarizedCallIds"),
            summarizedResultCallIds = ReadProperty<string[]>(result, "summarizedResultCallIds"),
            summarizedHistoryValid = ReadProperty<bool>(result, "summarizedHistoryValid"),
            summaryText = ReadProperty<string?>(result, "summaryText"),
            latestOutcomeRetained = ReadProperty<bool>(result, "latestOutcomeRetained"),
            projectedToolResultCharacters = ReadProperty<int>(result, "projectedToolResultCharacters"),
            steeringPromptPreserved = false,
            nestedExecutionComplete = storedNested.Complete,
            nestedCallIds = storedNested.Calls.Select(call => call.Id).ToArray(),
            error = ReadProperty<string?>(result, "error"),
        };
    }

    private static ChatMessage[] BasicLargeResult() =>
    [
        new(ChatRole.User, "read the large file"),
        Calls(("large-1", "read")),
        Result("large-1", new string('x', 8_000)),
    ];

    private static ChatMessage[] SequentialCycles() =>
    [
        new(ChatRole.User, "run both steps"),
        Calls(("sequence-1", "first")),
        Result("sequence-1", "first outcome"),
        new(ChatRole.Assistant, "continue"),
        Calls(("sequence-2", "second")),
        Result("sequence-2", "second outcome"),
    ];

    private static ChatMessage[] ParallelCalls() =>
    [
        new(ChatRole.User, "run both tools"),
        Calls(("parallel-1", "first"), ("parallel-2", "second")),
        new(ChatRole.Tool,
        [
            new FunctionResultContent("parallel-2", "second outcome"),
            new FunctionResultContent("parallel-1", "first outcome"),
        ]),
    ];

    private static ChatMessage[] LatestOutcome() =>
    [
        new(ChatRole.User, "inspect the files"),
        Calls(("old-output", "read")),
        Result("old-output", new string('o', 8_000)),
        new(ChatRole.Assistant, "one more check"),
        Calls(("latest-output", "read")),
        Result("latest-output", "LATEST TOOL OUTCOME"),
    ];

    private static ChatMessage[] IncompleteCall() =>
    [
        new(ChatRole.User, "read pending output"),
        Calls(("pending-call", "read")),
    ];

    private static ChatMessage[] DuplicateResult() =>
    [
        new(ChatRole.User, "continue safely"),
        Calls(("once-only", "run")),
        Result("once-only", "first result"),
        Result("once-only", "duplicate result"),
    ];

    private static ChatMessage[] SteeringAfterResult() =>
    [
        new(ChatRole.User, "earlier instruction"),
        new(ChatRole.Assistant, "earlier answer"),
        new(ChatRole.User, "read the large file"),
        Calls(("steering-tool", "read")),
        Result("steering-tool", new string('s', 8_000)),
        new(ChatRole.User, "change direction"),
    ];

    private static ChatMessage Calls(params (string Id, string Name)[] calls) => new(ChatRole.Assistant,
        calls.Select(call => (AIContent)new FunctionCallContent(call.Id, call.Name,
            new Dictionary<string, object?> { ["case"] = call.Name })).ToArray());

    private static ChatMessage Result(string callId, string text) => new(ChatRole.Tool,
        [new FunctionResultContent(callId, text)]);

    private static bool IsBalanced(IReadOnlyList<SnapshotValue> messages)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var id in message.callIds)
                if (string.IsNullOrEmpty(id) || !used.Add(id) || !pending.Add(id)) return false;
            foreach (var id in message.resultCallIds)
                if (!pending.Remove(id)) return false;
        }
        return pending.Count == 0;
    }

    private static SnapshotValue Snapshot(ChatMessage message)
    {
        var resultIds = message.Contents.OfType<FunctionResultContent>().Select(content => content.CallId).ToArray();
        var callIds = message.Contents.OfType<FunctionCallContent>().Select(content => content.CallId).ToArray();
        var text = string.Join("", message.Contents.OfType<TextContent>().Select(content => content.Text));
        if (message.Role == ChatRole.User && text.Length == 0) text = message.Text;
        var resultCharacters = message.Contents.OfType<FunctionResultContent>()
            .Sum(content => ContentText(content.Result).Length);
        return new SnapshotValue(message.Role == ChatRole.Tool ? "toolResult" : message.Role.Value,
            text, callIds, resultIds, resultCharacters);
    }

    private static string ContentText(object? value) => value switch
    {
        string text => text,
        System.Text.Json.JsonElement element when element.ValueKind == System.Text.Json.JsonValueKind.String =>
            element.GetString() ?? "",
        null => "",
        _ => value.ToString() ?? "",
    };

    private static T ReadProperty<T>(object target, string name) =>
        (T)(target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.GetValue(target) ?? default(T)!);

    private sealed record SnapshotValue(string role, string text, string[] callIds,
        string[] resultCallIds, int resultCharacters);
}
