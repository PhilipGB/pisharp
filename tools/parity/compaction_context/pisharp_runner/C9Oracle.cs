using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Core;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

internal static class C9Oracle
{
    private const string SummaryText = "generated branch summary";
    private const string ToolResultSentinel = "TOOL RESULT MUST NOT BE DUPLICATED";
    private const string SummaryPreamble = "The user explored a different conversation branch before returning here.\nSummary of that exploration:\n\n";

    public static async Task<object> RunAsync(string workingDirectory)
    {
        var primary = CreateBranchedConversation(workingDirectory, includeToolCall: true);
        var primaryClient = new CaptureClient();
        var primaryRun = await OpenRunAsync(primary.Conversation, workingDirectory, primaryClient);
        await primaryRun.NavigateTreeAsync(primary.TargetId,
            new ConversationTreeSelectionOptions(Summarize: true, Label: "returned-to-target"));
        var primarySummary = primary.Conversation.Tree.ActivePath().SingleOrDefault(entry => entry.Type == "branch_summary") ??
            throw new InvalidOperationException($"Primary branch summary missing; calls={primaryClient.Requests.Count}; " +
                $"head={primary.Conversation.Tree.HeadId}; " + string.Join(",", primary.Conversation.Tree.ActivePath().Select(entry => entry.Type)));
        var primaryPrompt = primaryClient.RequestText;
        var primaryOldEntries = OldPath(primary.Conversation, primary.OldLeafId, primary.CommonAncestorId);

        var budget = CreateBudgetConversation(workingDirectory);
        var budgetClient = new CaptureClient();
        var budgetRun = await OpenRunAsync(budget.Conversation, workingDirectory, budgetClient);
        await budgetRun.NavigateTreeAsync(budget.TargetId,
            new ConversationTreeSelectionOptions(Summarize: true, ReserveTokens: 127_988));
        var budgetPrompt = budgetClient.RequestText;

        var nested = CreateNestedFileOperationConversation(workingDirectory);
        var nestedClient = new CaptureClient();
        var nestedRun = await OpenRunAsync(nested.Conversation, workingDirectory, nestedClient);
        await nestedRun.NavigateTreeAsync(nested.TargetId, new ConversationTreeSelectionOptions(Summarize: true));
        var nestedSummary = nested.Conversation.Tree.ActivePath().Single(entry => entry.Type == "branch_summary");

        var appendInstructions = await RunCustomInstructionsAsync(workingDirectory, replace: false);
        var replaceInstructions = await RunCustomInstructionsAsync(workingDirectory, replace: true);

        var failed = await RunFailedNavigationAsync(workingDirectory, cancellation: false);
        var cancelled = await RunFailedNavigationAsync(workingDirectory, cancellation: true);

        var defaultSettings = new BranchSummarySettings();
        var skipSettings = new BranchSummarySettings(SkipPrompt: true);
        return new
        {
            cases = new Dictionary<string, object>
            {
                ["C9.branch-diff-uses-common-ancestor-and-old-path"] = new
                {
                    commonAncestorContainsSharedMarker = MessageText(primary.Conversation.Tree.Entries.Single(entry => entry.Id == primary.CommonAncestorId))!
                        .Contains("SHARED ANCESTOR ANSWER", StringComparison.Ordinal),
                    oldPathEntryCount = primaryOldEntries.Count,
                    oldPathEntries = primaryOldEntries.Select(EntrySnapshot).ToArray(),
                    targetBranchEntriesExcluded = primaryOldEntries.All(entry => entry.Id != primary.TargetId),
                    summaryFromIdIsOldHead = primarySummary.Payload.GetProperty("fromId").GetString() == primary.OldLeafId,
                },
                ["C9.branch-entries-are-chronological-and-no-duplicate-tool-results"] = new
                {
                    markerOrder = OrderedMarkers(primaryPrompt, ["OLD BRANCH FIRST", "branch.md", "OLD BRANCH LAST", "OLD BRANCH FINAL"]),
                    toolResultDuplicated = primaryPrompt.Contains(ToolResultSentinel, StringComparison.Ordinal),
                    targetBranchIncluded = primaryPrompt.Contains("TARGET BRANCH", StringComparison.Ordinal),
                },
                ["C9.token-budget-retains-recent-summary-context"] = new
                {
                    retainsRecentMessage = budgetPrompt.Contains("RECENT-BUDGET-MARKER", StringComparison.Ordinal),
                    retainsImportantSummary = budgetPrompt.Contains("IMPORTANT-SUMMARY-MARKER", StringComparison.Ordinal),
                    dropsOlderMessage = !budgetPrompt.Contains("VERY-OLD-BUDGET-MARKER", StringComparison.Ordinal),
                },
                ["C9.nested-branch-file-operation-details-accumulate"] = new
                {
                    readFiles = StringArray(nestedSummary.Payload.GetProperty("details").GetProperty("readFiles")),
                    modifiedFiles = StringArray(nestedSummary.Payload.GetProperty("details").GetProperty("modifiedFiles")),
                    promptIncludesNestedSummary = nestedClient.RequestText.Contains("NESTED PREVIOUS SUMMARY", StringComparison.Ordinal),
                },
                ["C9.custom-instructions-append-or-replace-default"] = new
                {
                    appendHasDefaultInstructions = appendInstructions.Contains("Create a structured summary of this conversation branch", StringComparison.Ordinal),
                    appendHasCustomInstructions = appendInstructions.Contains("Additional focus: CUSTOM BRANCH FOCUS", StringComparison.Ordinal),
                    replaceHasCustomInstructions = replaceInstructions.Contains("CUSTOM BRANCH FOCUS", StringComparison.Ordinal),
                    replaceHasDefaultInstructions = replaceInstructions.Contains("Create a structured summary of this conversation branch", StringComparison.Ordinal),
                },
                ["C9.summary-output-has-preamble-and-file-details"] = new
                {
                    summaryText = primarySummary.Payload.GetProperty("summary").GetString(),
                    summaryStartsWithPreamble = primarySummary.Payload.GetProperty("summary").GetString()!.StartsWith(SummaryPreamble, StringComparison.Ordinal),
                    summaryIncludesGeneratedText = primarySummary.Payload.GetProperty("summary").GetString()!.Contains(SummaryText, StringComparison.Ordinal),
                    summaryIncludesReadFileDetails = primarySummary.Payload.GetProperty("summary").GetString()!.Contains("<read-files>\nbranch.md\n</read-files>", StringComparison.Ordinal),
                    detailsReadFiles = StringArray(primarySummary.Payload.GetProperty("details").GetProperty("readFiles")),
                },
                ["C9.summary-applies-at-navigation-target-with-label"] = new
                {
                    parentIsSelectedTarget = primarySummary.ParentId == primary.TargetId,
                    fromIdIsAbandonedHead = primarySummary.Payload.GetProperty("fromId").GetString() == primary.OldLeafId,
                    label = primary.Conversation.GetEntryLabel(primarySummary.Id),
                },
                ["C9.cancel-or-failure-keeps-selected-head-and-history"] = new
                {
                    failedUnchanged = failed.Unchanged,
                    failedError = failed.Error,
                    cancelledUnchanged = cancelled.Unchanged,
                    cancelled = cancelled.Cancelled,
                },
                ["C9.public-navigation-prompts-and-skips-as-pinned"] = new
                {
                    defaultReserveTokens = defaultSettings.EffectiveReserveTokens,
                    defaultPrompts = defaultSettings.SkipPrompt != true,
                    skipPromptOverrideSuppressesPrompt = skipSettings.SkipPrompt == true,
                    promptTitle = "Summarize branch?",
                    choices = new[] { "No summary", "Summarize", "Summarize with custom prompt" },
                    defaultChoice = "No summary",
                },
            }
        };
    }

    private static async Task<string> RunCustomInstructionsAsync(string workingDirectory, bool replace)
    {
        var fixture = CreateBranchedConversation(workingDirectory, includeToolCall: false);
        var client = new CaptureClient();
        var run = await OpenRunAsync(fixture.Conversation, workingDirectory, client);
        await run.NavigateTreeAsync(fixture.TargetId, new ConversationTreeSelectionOptions(
            Summarize: true, CustomInstructions: "CUSTOM BRANCH FOCUS", ReplaceInstructions: replace));
        return client.RequestText;
    }

    private static async Task<FailureObservation> RunFailedNavigationAsync(string workingDirectory, bool cancellation)
    {
        var fixture = CreateBranchedConversation(workingDirectory, includeToolCall: false);
        var client = new CaptureClient(fail: !cancellation, waitForCancellation: cancellation);
        var run = await OpenRunAsync(fixture.Conversation, workingDirectory, client);
        var entriesBefore = fixture.Conversation.Tree.Entries.Count;
        string? error = null;
        var cancelled = false;
        try
        {
            var navigation = run.NavigateTreeAsync(fixture.TargetId, new ConversationTreeSelectionOptions(Summarize: true));
            if (cancellation)
            {
                await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                client.Cancel();
            }
            await navigation;
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception exception) { error = exception.Message; }
        return new FailureObservation(
            fixture.Conversation.Tree.HeadId == fixture.OldLeafId &&
            fixture.Conversation.Tree.Entries.Count == entriesBefore &&
            !fixture.Conversation.Tree.Entries.Any(entry => entry.Type == "branch_summary"),
            cancelled, error);
    }

    private static async Task<ConversationRun> OpenRunAsync(ConversationSession conversation, string workingDirectory,
        CaptureClient client) => await ConversationRun.OpenAsync(
            new PiAgent(client, new CodingTools(workingDirectory), noTools: true,
                retryPolicy: ProviderRetryPolicy.None), conversation);

    private static BranchedFixture CreateBranchedConversation(string workingDirectory, bool includeToolCall)
    {
        var conversation = new ConversationSession(workingDirectory, "fixture-model", null, "fixture");
        conversation.Append(new ChatMessage(ChatRole.User, "SHARED ROOT QUESTION"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "SHARED ANCESTOR ANSWER"));
        var commonAncestorId = conversation.Tree.HeadId!;
        conversation.Append(new ChatMessage(ChatRole.User, "OLD BRANCH FIRST"));
        if (includeToolCall)
        {
            conversation.Append(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("branch-read", "read", new Dictionary<string, object?> { ["path"] = "branch.md" })]));
            conversation.Append(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent("branch-read", ToolResultSentinel)]));
        }
        conversation.Append(new ChatMessage(ChatRole.User, "OLD BRANCH LAST"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "OLD BRANCH FINAL"));
        var oldLeafId = conversation.Tree.HeadId!;
        conversation.Tree.Select(commonAncestorId);
        conversation.Append(new ChatMessage(ChatRole.User, "TARGET BRANCH QUESTION"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "TARGET BRANCH ANSWER"));
        var targetId = conversation.Tree.HeadId!;
        conversation.Tree.Select(oldLeafId);
        return new BranchedFixture(conversation, commonAncestorId, oldLeafId, targetId);
    }

    private static BranchedFixture CreateBudgetConversation(string workingDirectory)
    {
        var conversation = new ConversationSession(workingDirectory, "fixture-model", null, "fixture");
        conversation.Append(new ChatMessage(ChatRole.User, "shared"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "shared answer"));
        var common = conversation.Tree.HeadId!;
        conversation.Append(new ChatMessage(ChatRole.User, "VERY-OLD-BUDGET-MARKER " + new string('x', 1_000)));
        conversation.AppendBranchSummary("nested-source", "IMPORTANT-SUMMARY-MARKER");
        conversation.Append(new ChatMessage(ChatRole.User, "RECENT-BUDGET-MARKER"));
        var oldLeaf = conversation.Tree.HeadId!;
        conversation.Tree.Select(common);
        conversation.Append(new ChatMessage(ChatRole.User, "target"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "target answer"));
        var target = conversation.Tree.HeadId!;
        conversation.Tree.Select(oldLeaf);
        return new BranchedFixture(conversation, common, oldLeaf, target);
    }

    private static BranchedFixture CreateNestedFileOperationConversation(string workingDirectory)
    {
        var conversation = new ConversationSession(workingDirectory, "fixture-model", null, "fixture");
        conversation.Append(new ChatMessage(ChatRole.User, "shared"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "shared answer"));
        var common = conversation.Tree.HeadId!;
        conversation.Append(new ChatMessage(ChatRole.User, "nested branch work"));
        using var details = JsonDocument.Parse("{\"readFiles\":[\"nested-read.md\",\"overwritten.md\"],\"modifiedFiles\":[\"nested-write.md\"]}");
        conversation.AppendBranchSummary("older-source", "NESTED PREVIOUS SUMMARY", details: details.RootElement);
        conversation.Append(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent("read-fresh", "read", new Dictionary<string, object?> { ["path"] = "fresh-read.md" }),
            new FunctionCallContent("write-fresh", "write", new Dictionary<string, object?> { ["path"] = "fresh-write.md" }),
            new FunctionCallContent("edit-old", "edit", new Dictionary<string, object?> { ["path"] = "overwritten.md" }),
        ]));
        var oldLeaf = conversation.Tree.HeadId!;
        conversation.Tree.Select(common);
        conversation.Append(new ChatMessage(ChatRole.User, "target"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "target answer"));
        var target = conversation.Tree.HeadId!;
        conversation.Tree.Select(oldLeaf);
        return new BranchedFixture(conversation, common, oldLeaf, target);
    }

    private static IReadOnlyList<ConversationNode> OldPath(ConversationSession conversation, string oldLeafId,
        string commonAncestorId)
    {
        var path = conversation.Tree.ClonePath(oldLeafId).ActivePath();
        var commonIndex = path.ToList().FindIndex(entry => entry.Id == commonAncestorId);
        return path.Skip(commonIndex + 1).ToArray();
    }

    private static object EntrySnapshot(ConversationNode entry) => new { type = entry.Type };

    private static string? MessageText(ConversationNode entry) => entry.Type == "chat"
        ? entry.Payload.GetRawText()
        : entry.Type == "branch_summary" ? entry.Payload.GetProperty("summary").GetString()
        : null;

    private static string[] OrderedMarkers(string text, IReadOnlyList<string> markers)
    {
        var position = -1;
        var found = new List<string>();
        foreach (var marker in markers)
        {
            var next = text.IndexOf(marker, position + 1, StringComparison.Ordinal);
            if (next < 0) continue;
            found.Add(marker);
            position = next;
        }
        return found.ToArray();
    }

    private static string[] StringArray(JsonElement value) => value.EnumerateArray()
        .Select(item => item.GetString() ?? "").ToArray();

    private sealed record BranchedFixture(ConversationSession Conversation, string CommonAncestorId,
        string OldLeafId, string TargetId);
    private sealed record FailureObservation(bool Unchanged, bool Cancelled, string? Error);

    private sealed class CaptureClient(bool fail = false, bool waitForCancellation = false) : IChatClient
    {
        private readonly CancellationTokenSource _cancellation = new();
        public List<ChatMessage[]> Requests { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string RequestText => string.Join("\n", Requests.SelectMany(messages => messages)
            .SelectMany(message => message.Contents.OfType<TextContent>()).Select(content => content.Text));

        public void Cancel() => _cancellation.Cancel();

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(messages.Select(message => message.Clone()).ToArray());
            Started.TrySetResult();
            if (fail) throw new InvalidOperationException("fixture branch summary failure");
            if (waitForCancellation)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);
                await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token).ConfigureAwait(false);
            }
            return new ChatResponse([new ChatMessage(ChatRole.Assistant, SummaryText)])
            {
                ModelId = options?.ModelId,
            };
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text) { ModelId = response.ModelId };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => _cancellation.Dispose();
    }
}
