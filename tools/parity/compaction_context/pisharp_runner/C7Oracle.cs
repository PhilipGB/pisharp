using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

internal static class C7Oracle
{
    public static async Task<object> RunAsync()
    {
        var cases = new Dictionary<string, object>
        {
            ["C7.initial-summary-instructions-match-pinned-prompt"] = await ObserveSummaryAsync(),
            ["C7.previous-summary-is-updated-with-prior-facts-preserved"] = await ObserveSummaryAsync(
                previousSummary: "Known fact: release uses branch stable."),
            ["C7.manual-focus-is-excluded-from-turn-prefix-summary"] = await ObserveSplitCompactionAsync(
                "prioritize exact file paths"),
            ["C7.split-turn-uses-history-and-turn-summary-requests"] = await ObserveSplitCompactionAsync(
                "manual focus"),
            ["C7.tool-call-result-content-is-serialized-for-summary"] = await ObserveToolSummaryAsync(),
            ["C7.file-operation-details-follow-summarized-history"] = await ObserveFileSummaryAsync(),
            ["C7.length-stop-and-empty-summary-fail-safely"] = await ObserveFailedCompactionsAsync(),
            ["C7.transient-stream-failure-obeys-retry-policy"] = await ObserveRetryAsync(),
            ["C7.summary-usage-is-reported-only-when-supplied"] = await ObserveUsageAsync(),
            ["C7.summary-model-routing-and-thinking-options"] = await ObserveRoutedSummaryAsync(),
        };
        return new { cases };
    }

    private static async Task<object> ObserveSummaryAsync(string? focus = null, string? previousSummary = null)
    {
        var client = new CaptureClient([new("summary")]);
        var agent = CreateAgent(client);
        var result = await agent.SummarizeAsync(
            [new ChatMessage(ChatRole.User, "open the release task"),
             new ChatMessage(ChatRole.Assistant, "keep the release decision")],
            focus, previousSummary: previousSummary);
        return new { request = client.Requests.Single(), text = result.Text };
    }

    private static async Task<object> ObserveSplitCompactionAsync(string focus)
    {
        var conversation = SplitConversation();
        var client = new CaptureClient([new("history summary"), new("turn prefix summary")]);
        var agent = CreateAgent(client);
        var run = await ConversationRun.OpenAsync(agent, conversation,
            autoCompaction: new AutoCompactionPolicy(5000, ReserveTokens: 100, KeepRecentTokens: 15));
        _ = await run.CompactAsync(focus);
        var compaction = conversation.Tree.ActivePath().LastOrDefault(entry => entry.Type == "compaction");
        return new
        {
            requests = client.Requests,
            summary = compaction?.Payload.GetProperty("summary").GetString(),
            historyIncludesFocus = client.Requests.FirstOrDefault()?.Prompt?.Contains(focus, StringComparison.Ordinal) ?? false,
            turnPrefixExcludesFocus = !(client.Requests.Skip(1).FirstOrDefault()?.Prompt ?? "")
                .Contains(focus, StringComparison.Ordinal),
            turnPrefixPrompt = client.Requests.Skip(1).FirstOrDefault()?.Prompt,
        };
    }

    private static async Task<object> ObserveToolSummaryAsync()
    {
        var client = new CaptureClient([new("tool summary")]);
        var agent = CreateAgent(client);
        await agent.SummarizeAsync(
        [
            new ChatMessage(ChatRole.User, "read the file"),
            new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("read-1", "read", new Dictionary<string, object?> { ["path"] = "plan.md" })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("read-1", "Decision: ship on Friday.")]),
        ], null);
        return new { request = client.Requests.Single() };
    }

    private static async Task<object> ObserveFileSummaryAsync()
    {
        var conversation = new ConversationSession(Path.GetTempPath(), "c7-files", null);
        conversation.Append(new ChatMessage(ChatRole.User, "read and update files"));
        conversation.Append(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("read-1", "read", new Dictionary<string, object?> { ["path"] = "plan.md" })]));
        conversation.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("read-1", "plan contents")]));
        conversation.Append(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("write-1", "write", new Dictionary<string, object?> { ["path"] = "release.md" })]));
        conversation.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("write-1", "saved")]));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "files updated"));
        conversation.Append(new ChatMessage(ChatRole.User, "continue"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "ready"));
        var client = new CaptureClient([new("file history summary")]);
        var run = await ConversationRun.OpenAsync(CreateAgent(client), conversation,
            autoCompaction: new AutoCompactionPolicy(5000, ReserveTokens: 100, KeepRecentTokens: 1));
        _ = await run.CompactAsync();
        var compaction = conversation.Tree.ActivePath().LastOrDefault(entry => entry.Type == "compaction");
        return new
        {
            requests = client.Requests,
            summary = compaction?.Payload.GetProperty("summary").GetString(),
        };
    }

    private static async Task<object> ObserveFailedCompactionsAsync()
    {
        async Task<object> ObserveAsync(ResponseSpec response)
        {
            var conversation = BasicConversation("c7-failed");
            var headBefore = conversation.Tree.HeadId;
            var client = new CaptureClient([response]);
            var run = await ConversationRun.OpenAsync(CreateAgent(client), conversation,
                autoCompaction: new AutoCompactionPolicy(5000, ReserveTokens: 100, KeepRecentTokens: 1));
            string? error = null;
            try { await run.CompactAsync(); }
            catch (Exception failure) { error = failure.Message; }
            return new
            {
                error,
                requestCount = client.Requests.Count,
                compactionCount = conversation.Tree.ActivePath().Count(entry => entry.Type == "compaction"),
                canonicalStateUnchanged = conversation.Tree.HeadId == headBefore,
            };
        }

        return new
        {
            length = await ObserveAsync(new("partial summary", FinishReason: ChatFinishReason.Length)),
            empty = await ObserveAsync(new("   ")),
        };
    }

    private static async Task<object> ObserveRetryAsync()
    {
        var client = new CaptureClient([new("", Failure: "529 overloaded"), new("retried summary")]);
        var agent = CreateAgent(client, new ProviderRetryPolicy(maxRetries: 1));
        var result = await agent.SummarizeAsync(
            [new ChatMessage(ChatRole.User, "open the release task"),
             new ChatMessage(ChatRole.Assistant, "keep the release decision")], null);
        return new { attempts = client.Requests.Count, requests = client.Requests, summary = result.Text };
    }

    private static async Task<object> ObserveUsageAsync()
    {
        var suppliedClient = new CaptureClient([new("usage summary", Usage: new UsageDetails
        {
            InputTokenCount = 11, OutputTokenCount = 3, TotalTokenCount = 14,
        })]);
        var supplied = await CreateAgent(suppliedClient).SummarizeAsync(
            [new ChatMessage(ChatRole.User, "context")], null);
        var omittedClient = new CaptureClient([new("no usage summary")]);
        var omitted = await CreateAgent(omittedClient).SummarizeAsync(
            [new ChatMessage(ChatRole.User, "context")], null);
        return new
        {
            supplied = new { usageSupplied = supplied.Usage is not null, usage = supplied.Usage },
            omitted = new { usageSupplied = omitted.Usage is not null, usage = omitted.Usage },
        };
    }

    private static async Task<object> ObserveRoutedSummaryAsync()
    {
        var physical = new CaptureClient([new("routed summary")]);
        var session = new ConversationSession(Path.GetTempPath(), "c7-route", null, "router");
        session.SelectModel("auto", null, "router");
        session.Append(new ChatMessage(ChatRole.User, "first history"));
        session.Append(new ChatMessage(ChatRole.Assistant, "first answer"));
        session.Append(new ChatMessage(ChatRole.User, "recent history"));
        session.Append(new ChatMessage(ChatRole.Assistant, "recent answer"));
        var agent = new PiAgent(new CaptureClient([]), new CodingTools(Path.GetTempPath()), noTools: true,
            reasoning: new ReasoningOptions { Effort = ReasoningEffort.Low },
            virtualModelRequestRouter: _ => Task.FromResult(new VirtualModelRequestRoute(
                physical,
                new ModelDescriptor("large", null, 50_000, null, Reasoning: true,
                    Provider: "fixture", Api: "fixture-api", MaxOutputTokens: 48),
                "fixture", "high", new ReasoningOptions { Effort = ReasoningEffort.High }, null,
                new AutoCompactionPolicy(50_000, ReserveTokens: 100, KeepRecentTokens: 1))));
        var run = await ConversationRun.OpenAsync(agent, session,
            autoCompaction: new AutoCompactionPolicy(50_000, ReserveTokens: 100, KeepRecentTokens: 1),
            reasoningLevel: "high");
        await run.CompactAsync();
        var firstRequest = physical.Requests.FirstOrDefault();
        return new
        {
            requests = physical.Requests,
            modelId = firstRequest?.ModelId,
            maxTokens = firstRequest?.MaxTokens,
            reasoning = firstRequest?.Reasoning,
        };
    }

    private static PiAgent CreateAgent(CaptureClient client, ProviderRetryPolicy? retryPolicy = null) =>
        new(client, new CodingTools(Path.GetTempPath()), noTools: true,
            reasoning: new ReasoningOptions { Effort = ReasoningEffort.High },
            retryPolicy: retryPolicy ?? ProviderRetryPolicy.None);

    private static ConversationSession BasicConversation(string name)
    {
        var conversation = new ConversationSession(Path.GetTempPath(), name, null);
        conversation.Append(new ChatMessage(ChatRole.User, "first history"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "first answer"));
        conversation.Append(new ChatMessage(ChatRole.User, "recent history"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "recent answer"));
        return conversation;
    }

    private static ConversationSession SplitConversation()
    {
        var conversation = new ConversationSession(Path.GetTempPath(), "c7-split", null);
        conversation.Append(new ChatMessage(ChatRole.User, "older question"));
        conversation.Append(new ChatMessage(ChatRole.Assistant, "older answer"));
        conversation.Append(new ChatMessage(ChatRole.User, new string('U', 100)));
        conversation.Append(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("read-call", "read", new Dictionary<string, object?> { ["path"] = "current.txt" })]));
        conversation.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent("read-call", new string('R', 80))]));
        conversation.Tree.Append("interrupted", JsonSerializer.SerializeToElement(new
        {
            prompt = "continue the current task",
            partialAssistantText = new string('P', 80),
            terminalType = "turn_interrupted",
            stopReason = "aborted",
            errorMessage = "Request was aborted",
        }));
        return conversation;
    }

    private sealed record ResponseSpec(string Text, UsageDetails? Usage = null,
        ChatFinishReason? FinishReason = null, string? Failure = null);
    private sealed record CapturedRequest(string? ModelId, int? MaxTokens, string? Reasoning,
        string? SystemPrompt, string? Prompt, IReadOnlyList<object> Messages);

    private sealed class CaptureClient(IReadOnlyList<ResponseSpec> responses) : IChatClient
    {
        private int _callCount;
        public List<CapturedRequest> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _callCount);
            var requestMessages = messages.ToArray();
            var reasoning = options?.Reasoning is { } reasoningOptions
                ? reasoningOptions.Effort.ToString().ToLowerInvariant()
                : null;
            var request = new CapturedRequest(options?.ModelId, options?.MaxOutputTokens,
                reasoning,
                options?.Instructions ?? requestMessages.FirstOrDefault(message => message.Role == ChatRole.System)?.Text,
                requestMessages.LastOrDefault(message => message.Role == ChatRole.User)?.Text,
                requestMessages.Select(message => (object)new
                {
                    role = message.Role.ToString().ToLowerInvariant(),
                    text = message.Text,
                }).ToArray());
            Requests.Add(request);
            var spec = call <= responses.Count ? responses[call - 1] : new ResponseSpec("summary");
            if (spec.Failure is not null) throw new IOException(spec.Failure);
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, spec.Text)])
            {
                Usage = spec.Usage,
                FinishReason = spec.FinishReason,
                ModelId = options?.ModelId,
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text)
            {
                FinishReason = response.FinishReason,
                ModelId = response.ModelId,
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
