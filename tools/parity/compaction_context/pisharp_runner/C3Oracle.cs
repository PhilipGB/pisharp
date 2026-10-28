using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

internal static class C3Oracle
{
    private const string SummaryPrefix = "The conversation history before this point was compacted into the following summary:\n\n<summary>\n";
    private const string SummarySuffix = "\n</summary>";

    public static object Run(string workingDirectory)
    {
        var cases = new Dictionary<string, object>
        {
            ["C3.user-boundary-selection"] = UserBoundary(workingDirectory),
            ["C3.assistant-boundary-split-turn"] = AssistantBoundary(workingDirectory),
            ["C3.tool-call-result-kept-as-one-group"] = ToolCallResultGroup(workingDirectory),
            ["C3.sequential-tool-cycles"] = SequentialToolCycles(workingDirectory),
            ["C3.parallel-tool-batch-boundary"] = ParallelToolBatch(workingDirectory),
            ["C3.oversized-trailing-result-falls-back-before-call"] = OversizedTrailingResult(workingDirectory),
            ["C3.never-cut-at-tool-result"] = NeverCutAtToolResult(workingDirectory),
            ["C3.incomplete-assistant-and-tool-call-retained"] = IncompleteToolCall(workingDirectory),
            ["C3.missing-tool-result-provider-projection"] = MissingToolResultProjection(workingDirectory),
            ["C3.unmatched-or-duplicate-tool-result-handling"] = DuplicateToolResults(workingDirectory),
            ["C3.repeated-compaction-updates-prior-summary"] = RepeatedCompaction(workingDirectory),
            ["C3.latest-compaction-controls-context"] = LatestCompaction(workingDirectory),
            ["C3.recovery-omission-suffix-boundary"] = RecoveryOmissionSuffix(workingDirectory),
            ["C3.empty-or-insufficient-history-is-no-op"] = EmptyOrInsufficientHistory(workingDirectory),
        };
        return new { cases };
    }

    private static object UserBoundary(string cwd)
    {
        var fixture = new Fixture(cwd);
        fixture.User("older question");
        fixture.Assistant("older answer");
        fixture.User(new string('U', 80));
        fixture.Assistant(new string('A', 80));
        return fixture.Observe(30);
    }

    private static object AssistantBoundary(string cwd)
    {
        var fixture = new Fixture(cwd);
        fixture.User("older question");
        fixture.Assistant("older answer");
        fixture.User("current request");
        fixture.Assistant("current response");
        return fixture.Observe(1);
    }

    private static object ToolCallResultGroup(string cwd)
    {
        var fixture = new Fixture(cwd);
        fixture.User("older question");
        fixture.Assistant("older answer");
        fixture.User("current request");
        fixture.ToolCall("call-1", "read", new Dictionary<string, object?> { ["path"] = "small.txt" });
        fixture.ToolResult("call-1", "short result");
        return fixture.Observe(1);
    }

    private static object SequentialToolCycles(string cwd)
    {
        var fixture = new Fixture(cwd);
        fixture.User("older question");
        fixture.Assistant("older answer");
        fixture.User("current request");
        fixture.ToolCall("cycle-1", "read", new Dictionary<string, object?>());
        fixture.ToolResult("cycle-1", "first result");
        fixture.Assistant("continue after first tool cycle");
        fixture.ToolCall("cycle-2", "read", new Dictionary<string, object?>());
        fixture.ToolResult("cycle-2", "second result");
        return fixture.Observe(1);
    }

    private static object ParallelToolBatch(string cwd)
    {
        var fixture = new Fixture(cwd);
        fixture.User("older question");
        fixture.Assistant("older answer");
        fixture.User("current request");
        fixture.ToolCalls(
            ("parallel-1", "read", new Dictionary<string, object?>()),
            ("parallel-2", "read", new Dictionary<string, object?>()));
        fixture.ToolResult("parallel-1", "first result");
        fixture.ToolResult("parallel-2", "second result");
        return fixture.Observe(1);
    }

    private static object OversizedTrailingResult(string cwd)
    {
        var fixture = new Fixture(cwd);
        fixture.User("old history");
        fixture.Assistant("old answer");
        fixture.User("read the large file");
        fixture.ToolCall("call-1", "read", new Dictionary<string, object?> { ["path"] = "big.txt" });
        fixture.ToolResult("call-1", new string('x', 8000));
        return fixture.Observe(1000);
    }

    private static object NeverCutAtToolResult(string cwd)
    {
        var fixture = new Fixture(cwd);
        fixture.User("old history");
        fixture.Assistant("old answer");
        fixture.User("current request");
        fixture.Assistant(new string('A', 80));
        fixture.ToolResult("unmatched-result", new string('R', 2000));
        return fixture.Observe(100);
    }

    private static object IncompleteToolCall(string cwd)
    {
        var fixture = new Fixture(cwd);
        fixture.User("old history");
        fixture.Assistant("old answer");
        fixture.User("current request");
        fixture.ToolCall("incomplete-call", "read", new Dictionary<string, object?> { ["path"] = "pending.txt" });
        return fixture.Observe(1);
    }

    private static object MissingToolResultProjection(string cwd)
    {
        var fixture = new Fixture(cwd);
        fixture.User("current request");
        fixture.ToolCall("missing-result", "read", new Dictionary<string, object?> { ["path"] = "pending.txt" });
        return fixture.Observe(1);
    }

    private static object DuplicateToolResults(string cwd)
    {
        var fixture = new Fixture(cwd);
        fixture.User("older question");
        fixture.Assistant("older answer");
        fixture.User("current request");
        fixture.ToolCall("duplicate-result", "read", new Dictionary<string, object?>());
        fixture.ToolResult("duplicate-result", "first result");
        fixture.ToolResult("duplicate-result", "duplicate result");
        fixture.User("new request");
        return fixture.Observe(1);
    }

    private static object RepeatedCompaction(string cwd)
    {
        var fixture = SeedCompaction(cwd);
        var firstPlan = fixture.Session.PrepareCompaction(30) ?? throw new InvalidOperationException("first compaction plan missing");
        fixture.Session.AppendCompaction(firstPlan, "First summary", 30);
        fixture.User(new string('U', 120));
        fixture.Assistant(new string('A', 120));
        return fixture.Observe(40);
    }

    private static object LatestCompaction(string cwd)
    {
        var fixture = SeedCompaction(cwd);
        var firstPlan = fixture.Session.PrepareCompaction(30) ?? throw new InvalidOperationException("first compaction plan missing");
        fixture.Session.AppendCompaction(firstPlan, "First summary", 30);
        fixture.User(new string('U', 120));
        fixture.Assistant(new string('A', 120));
        var secondPlan = fixture.Session.PrepareCompaction(40) ?? throw new InvalidOperationException("second compaction plan missing");
        fixture.Session.AppendCompaction(secondPlan, "Second summary", 40);
        var context = fixture.Session.ContextMessages().Select(SnapshotProjected).ToArray();
        return new
        {
            compactionCount = fixture.Session.Tree.ActivePath().Count(entry => entry.Type == "compaction"),
            contextMessages = context,
            latestSummaryIsSecond = context.Any(message => message.role == "compactionSummary" && message.text == "Second summary"),
            previousSummaryOmitted = context.All(message => message.text != "First summary"),
            retainedCurrentAnswer = context.Any(message => message.text == new string('A', 120)),
        };
    }

    private static object RecoveryOmissionSuffix(string cwd)
    {
        var fixture = new Fixture(cwd);
        fixture.User("older question");
        fixture.Assistant("older answer");
        fixture.User(new string('U', 100));
        var failedAssistant = fixture.ToolCall("failed-call", "read", new Dictionary<string, object?> { ["path"] = "current.txt" });
        var failedResult = fixture.ToolResult("failed-call", new string('R', 100));
        fixture.Omit(failedAssistant);
        fixture.Omit(failedResult);
        return fixture.Observe(15);
    }

    private static object EmptyOrInsufficientHistory(string cwd)
    {
        var empty = new Fixture(cwd).Observe(1);
        var insufficient = new Fixture(cwd);
        insufficient.User("only one user message");
        var oneUser = insufficient.Observe(1);
        return new { emptyPlanExists = empty.planExists, oneUserPlanExists = oneUser.planExists };
    }

    private static Fixture SeedCompaction(string cwd)
    {
        var fixture = new Fixture(cwd);
        fixture.User(new string('U', 80));
        fixture.Assistant(new string('A', 80));
        fixture.User(new string('V', 80));
        fixture.Assistant(new string('B', 80));
        return fixture;
    }

    private sealed class Fixture
    {
        private readonly Dictionary<string, EntryInfo> _entries = new(StringComparer.Ordinal);

        public ConversationSession Session { get; }

        public Fixture(string cwd)
        {
            Session = new ConversationSession(cwd, "fixture", null);
        }

        public string User(string text) => Add(new ChatMessage(ChatRole.User, text), "user", text);

        public string Assistant(string text) => Add(new ChatMessage(ChatRole.Assistant, text), "assistant", text);

        public string ToolCall(string id, string name, Dictionary<string, object?> arguments) =>
            Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(id, name, arguments)]),
                "assistant", "", [id]);

        public void ToolCalls(params (string Id, string Name, Dictionary<string, object?> Arguments)[] calls) =>
            Add(new ChatMessage(ChatRole.Assistant, calls.Select(call =>
                (AIContent)new FunctionCallContent(call.Id, call.Name, call.Arguments)).ToArray()),
                "assistant", "", calls.Select(call => call.Id).ToArray());

        public string ToolResult(string id, string text) =>
            Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(id, text)]),
                "toolResult", text, resultIds: [id]);

        public void Omit(string id) => Session.Tree.Append("context_edit",
            System.Text.Json.JsonSerializer.SerializeToElement(new { targetId = id, replacement = (string?)null }));

        public PlanObservation Observe(int keepRecentTokens)
        {
            var plan = Session.PrepareCompaction(keepRecentTokens);
            var boundary = plan is null ? null : _entries.GetValueOrDefault(plan.FirstKeptEntryId);
            return new PlanObservation(
                plan is not null,
                boundary?.Role,
                boundary is null ? null : CompactText(boundary.Text),
                boundary?.Index,
                boundary?.CallIds.ToArray() ?? [],
                boundary?.ResultCallIds.ToArray() ?? [],
                plan?.IsSplitTurn,
                plan?.PreviousSummary,
                plan?.MessagesToSummarize.Select(Snapshot).ToArray() ?? [],
                plan?.TurnPrefixMessages?.Select(Snapshot).ToArray() ?? [],
                Session.ContextMessages().Select(SnapshotProjected).ToArray());
        }

        private string Add(ChatMessage message, string role, string text,
            IReadOnlyList<string>? callIds = null, IReadOnlyList<string>? resultIds = null)
        {
            Session.Append(message);
            var node = Session.Tree.ActivePath().Last();
            _entries[node.Id] = new EntryInfo(role, text, callIds ?? [], resultIds ?? [], Session.Tree.ActivePath().Count - 1);
            return node.Id;
        }
    }

    private sealed record EntryInfo(string Role, string Text, IReadOnlyList<string> CallIds,
        IReadOnlyList<string> ResultCallIds, int Index);

    private sealed record MessageObservation(string role, string text, string[] callIds, string[] resultCallIds);

    private sealed record PlanObservation(bool planExists, string? boundaryRole, string? boundaryText,
        int? boundaryIndex, string[] boundaryCallIds, string[] boundaryResultCallIds, bool? isSplitTurn,
        string? previousSummary, MessageObservation[] messagesToSummarize, MessageObservation[] turnPrefixMessages,
        MessageObservation[] contextMessages);

    private static MessageObservation Snapshot(ChatMessage message)
    {
        var role = message.Role == ChatRole.Tool ? "toolResult" : message.Role.Value;
        var resultContents = message.Contents.OfType<FunctionResultContent>().ToArray();
        var text = role == "toolResult" && resultContents.Length > 0
            ? string.Concat(resultContents.Select(content => content.Result?.ToString() ?? ""))
            : message.Text;
        return new MessageObservation(role, CompactText(text),
            message.Contents.OfType<FunctionCallContent>().Select(content => content.CallId).ToArray(),
            resultContents.Select(content => content.CallId).ToArray());
    }

    private static MessageObservation SnapshotProjected(ChatMessage message)
    {
        var role = message.Role.Value;
        var text = message.Text;
        if (role == "user" && text.StartsWith(SummaryPrefix, StringComparison.Ordinal) && text.EndsWith(SummarySuffix, StringComparison.Ordinal))
        {
            role = "compactionSummary";
            text = text[SummaryPrefix.Length..^SummarySuffix.Length];
        }
        var resultContents = message.Contents.OfType<FunctionResultContent>().ToArray();
        return new MessageObservation(role == "tool" ? "toolResult" : role,
            CompactText(role == "tool" && resultContents.Length > 0
                ? string.Concat(resultContents.Select(content => content.Result?.ToString() ?? ""))
                : role == "compactionSummary" ? text : message.Text),
            message.Contents.OfType<FunctionCallContent>().Select(content => content.CallId).ToArray(),
            resultContents.Select(content => content.CallId).ToArray());
    }

    private static string CompactText(string text) => text.Length <= 256
        ? text
        : text[..24] + $"<{text.Length} chars>" + text[^24..];
}
