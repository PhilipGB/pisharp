using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Cli;
using PiSharp.Core;

namespace PiSharp.Core.Tests;

/// <summary>
/// End-to-end compaction tests: a real MAF harness agent (compaction disabled) wrapped in the
/// PiSharp compaction seam, driving the real session store, planner, summarizer, and turn
/// runner. The model is a scripted leaf client, so every provider request is deterministic.
/// </summary>
public sealed class CompactionRuntimeTests
{
    private const string OldMarkerA = "OLD-MARKER-A";
    private const string KeptMarker = "KEPT-MARKER";
    private const string SummaryText = "## Goal\ntest summary checkpoint";
    // 40k chars ≈ 10k tokens per user message, so the keep-recent walk (20k) retains turns
    // 3–4 and summarizes turns 1–2 — exactly one compaction, then the context converges.
    private const int PairUserCharacters = 40_000;

    [Fact]
    public async Task ThresholdCompactionRunsBeforePromptAndRebuildsRequestHistory()
    {
        using var temp = TempDirectory.Create();
        var harness = await BuildHarnessAsync(temp, contextTokens: 40_000, leafScript: [
            ToolCallStep("notes.txt"),
            FinalStep("done"),
        ]);

        // Four user/assistant pairs of ~7k tokens each (~28k total) exceed the 23.6k trigger
        // (40000 - 16384) before the prompt is even submitted.
        for (var i = 1; i <= 4; i++)
        {
            var marker = i switch
            {
                1 => OldMarkerA,
                4 => KeptMarker,
                _ => string.Empty,
            };
            var filler = new string((char)('a' + i), PairUserCharacters - marker.Length);
            await harness.Sessions.PersistUserMessageAsync($"turn {i} {marker} {filler}", null, CancellationToken.None);
            await harness.Sessions.PersistAssistantMessagesAsync([AssistantMessage($"answer {i}")], CancellationToken.None);
        }

        var prompt = "new prompt " + NewPromptMarker();
        var result = await RunPromptAsync(harness, prompt);

        Assert.Equal("done", result.Result.AssistantText);
        Assert.False(result.Result.Cancelled);

        // Exactly one compaction: the pre-prompt threshold check. The summarizer saw only the
        // seeded history — never the new prompt.
        Assert.Equal(1, harness.Summarizer.CallCount);
        Assert.Contains(OldMarkerA, harness.Summarizer.LastUserPrompt ?? string.Empty);
        Assert.DoesNotContain(prompt, harness.Summarizer.LastUserPrompt ?? string.Empty);

        // Durable ordering: the compaction boundary is persisted BEFORE the new prompt.
        var path = harness.Sessions.Document!.GetActiveEntryPath(harness.Sessions.ActiveEntryId).ToArray();
        var compactIndex = Array.FindIndex(path, entry => entry is CompactionEntry);
        var promptIndex = Array.FindIndex(path, entry => entry is MessageEntry { Message: var m } &&
            m.TryGetProperty("role", out var role) && role.GetString() == "user" &&
            ReadContentText(m.TryGetProperty("content", out var c) ? c : default).Contains(prompt));
        Assert.True(compactIndex >= 0, "no compaction entry was persisted");
        Assert.True(promptIndex > compactIndex, "the new prompt was not persisted after the compaction boundary");

        // The first model request sent to the leaf carries the compacted context: the summary
        // and the retained recent turns, but none of the summarized old content.
        var firstRequest = harness.Leaf.Requests[0].Select(ReadMessageText).ToArray();
        Assert.Contains(firstRequest, text => text.Contains(SummaryText));
        Assert.Contains(firstRequest, text => text.Contains(KeptMarker));
        Assert.DoesNotContain(firstRequest, text => text.Contains(OldMarkerA));
        Assert.DoesNotContain(firstRequest, text => text.Contains("turn 2 "));
        Assert.Contains(firstRequest, text => text.Contains(prompt));

        // The tool executed exactly once for the single tool-call step.
        Assert.Equal(1, harness.ToolExecutions);

        // Compaction lifecycle events are emitted reliably around the durable write.
        var events = harness.Output.Events;
        Assert.Contains("compaction_start:threshold", events);
        Assert.Contains("compaction_end:threshold:False:", events);
        Assert.True(Array.IndexOf(events.ToArray(), "compaction_start:threshold") <
                    Array.IndexOf(events.ToArray(), "compaction_end:threshold:False:"));
    }

    [Fact]
    public async Task OverflowRecoveryRetriesOnlyTheFailedProviderRequest()
    {
        using var temp = TempDirectory.Create();
        var harness = await BuildHarnessAsync(temp, contextTokens: 128_000, leafScript: [
            OverflowStep(),
            ToolCallStep("notes.txt"),
            FinalStep("recovered"),
        ]);

        for (var i = 1; i <= 4; i++)
        {
            var marker = i == 1 ? OldMarkerA : string.Empty;
            var filler = new string((char)('a' + i), PairUserCharacters - marker.Length);
            await harness.Sessions.PersistUserMessageAsync($"turn {i} {marker} {filler}", null, CancellationToken.None);
            await harness.Sessions.PersistAssistantMessagesAsync([AssistantMessage($"answer {i}")], CancellationToken.None);
        }

        var prompt = "new prompt " + NewPromptMarker();
        var result = await RunPromptAsync(harness, prompt);

        Assert.Equal("recovered", result.Result.AssistantText);
        Assert.False(result.Result.Cancelled);

        // The provider overflow forced exactly one compaction (no threshold trigger at 128k).
        Assert.Equal(1, harness.Summarizer.CallCount);

        // Three provider requests: the failed one, the retried request (now compacted), and
        // the tool-result continuation.
        Assert.Equal(3, harness.Leaf.CallCount);
        var retried = harness.Leaf.Requests[1].Select(ReadMessageText).ToArray();
        Assert.Contains(retried, text => text.Contains(SummaryText));
        Assert.DoesNotContain(retried, text => text.Contains(OldMarkerA));
        Assert.Equal(1, retried.Count(text => text.Contains(prompt)));

        // No replay: the prompt was persisted exactly once and the tool ran exactly once.
        var persistedPath = harness.Sessions.Document!.GetActiveEntryPath(harness.Sessions.ActiveEntryId).ToArray();
        var promptCopies = persistedPath
            .OfType<MessageEntry>()
            .Count(entry => entry.Message.TryGetProperty("role", out var role) && role.GetString() == "user" &&
                            ReadContentText(entry.Message.TryGetProperty("content", out var c) ? c : default).Contains(prompt));
        Assert.Equal(1, promptCopies);
        Assert.Equal(1, harness.ToolExecutions);

        // The compaction boundary persisted at the failure point (after the prompt, before the
        // assistant response that followed the retry).
        var compactIndex = Array.FindIndex(persistedPath, entry => entry is CompactionEntry);
        var assistantIndex = Array.FindLastIndex(persistedPath, entry =>
            entry is MessageEntry { Message: var m } && m.TryGetProperty("role", out var r) && r.GetString() == "assistant");
        Assert.True(compactIndex >= 0, "no compaction entry was persisted for overflow recovery");
        Assert.True(assistantIndex > compactIndex, "the post-retry assistant response did not land after the boundary");

        // The forced compaction is reported with the overflow reason.
        Assert.Contains("compaction_start:overflow", harness.Output.Events);
        Assert.Contains("compaction_end:overflow:False:", harness.Output.Events);
    }

    [Fact]
    public async Task UnderThresholdNoCompactionHappens()
    {
        using var temp = TempDirectory.Create();
        var harness = await BuildHarnessAsync(temp, contextTokens: 128_000, leafScript: [
            FinalStep("ok"),
        ]);

        await harness.Sessions.PersistUserMessageAsync("small history", null, CancellationToken.None);
        await harness.Sessions.PersistAssistantMessagesAsync([AssistantMessage("small answer")], CancellationToken.None);

        var prompt = "small prompt " + NewPromptMarker();
        var result = await RunPromptAsync(harness, prompt);

        Assert.Equal("ok", result.Result.AssistantText);
        Assert.Equal(0, harness.Summarizer.CallCount);
        Assert.Equal(1, harness.Leaf.CallCount);

        // The request passed through untouched: seeded history plus the prompt, no boundary.
        var text = string.Join("\n", harness.Leaf.Requests[0].Select(ReadMessageText));
        Assert.Contains("small history", text);
        Assert.Contains(prompt, text);
        Assert.DoesNotContain("compacted into the following summary", text);
    }

    [Fact]
    public async Task NonOverflowProviderErrorPropagatesWithoutCompaction()
    {
        using var temp = TempDirectory.Create();
        var harness = await BuildHarnessAsync(temp, contextTokens: 128_000, leafScript: [
            new LeafBehavior { Throw = new InvalidOperationException("401 Unauthorized: invalid api key") },
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunPromptAsync(harness, "prompt " + NewPromptMarker()));

        Assert.Equal(1, harness.Leaf.CallCount);
        Assert.Equal(0, harness.Summarizer.CallCount);
        var path = harness.Sessions.Document!.GetActiveEntryPath(harness.Sessions.ActiveEntryId);
        Assert.DoesNotContain(path, entry => entry is CompactionEntry);
    }

    private static async Task<PromptOutcome> RunPromptAsync(TestHarness harness, string prompt)
    {
        var liveTurns = new LiveTurnCoordinator(harness.Bootstrap.TurnQueue);
        harness.Sessions.AbortActiveTurn = liveTurns.Abort;
        harness.Sessions.EventOutput = harness.Output;
        var result = await AgentTurnRunner.RunAsync(
            harness.Bootstrap,
            harness.Sessions,
            liveTurns,
            prompt,
            harness.Workspace,
            harness.Output,
            CancellationToken.None);
        return new PromptOutcome(result, harness.Sessions);
    }

    private static async Task<TestHarness> BuildHarnessAsync(
        TempDirectory temp,
        int contextTokens,
        IReadOnlyList<LeafBehavior> leafScript)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
        var turnQueue = new TurnMessageQueue();
        var sessionHistory = new PiSessionChatHistoryProvider();
        var compactionTarget = new CompactionTarget();
        var leaf = new ScriptedLeafClient(leafScript);
        var summarizer = new ScriptedLeafClient([
            new LeafBehavior { BuildUpdates = _ => FinalUpdates(SummaryText) },
        ]);
        var toolCounter = new CountingToolExecutor();

        var compactionClient = new CompactionChatClient(
            new SteeringChatClient(leaf, turnQueue, message => message),
            () => compactionTarget.Current);

#pragma warning disable MAAI001 // Harness token-limit options are evaluation-only; omitted here.
        var agent = compactionClient.AsHarnessAgent(new HarnessAgentOptions
        {
            ChatHistoryProvider = sessionHistory,
            Name = "pisharp-test",
            HarnessInstructions = "Test harness.",
            ChatOptions = new ChatOptions
            {
                Instructions = "You are a test agent.",
                Tools = [CreateCountingReadTool(toolCounter)],
            },
            // PiSharp owns all compaction decisions; the Harness must not reduce context.
            DisableCompaction = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true,
            DisableOpenTelemetry = true,
        });
#pragma warning restore MAAI001

        var bootstrap = new AgentBootstrap(
            Agent: agent,
            SummaryClient: summarizer,
            ContextFiles: [],
            Skills: [],
            PromptTemplates: [],
            ExtensionHost: new PiSharpExtensionHost(),
            RetryPolicy: RetryPolicyOptions.Disabled,
            TurnQueue: turnQueue,
            SessionHistory: sessionHistory,
            Compaction: compactionTarget);

        var options = new CliOptions(
            WorkingDirectory: workspace,
            Model: "test-model",
            Endpoint: null,
            ApiKey: "test",
            ContextTokens: contextTokens,
            MaxOutputTokens: 1024,
            Prompt: null,
            FilePaths: [],
            ShowHelp: false,
            ContinueSession: false,
            ResumeSession: false,
            SessionSelector: null,
            SessionName: null,
            SessionDirectory: Path.Combine(temp.Path, "sessions"),
            NoSession: false,
            ContextRoot: null,
            ExtensionPaths: [],
            SkillPaths: [],
            PromptTemplatePaths: [],
            NoExtensions: true,
            NoSkills: true,
            NoPromptTemplates: true,
            ProjectTrustOverride: true,
            OutputMode: OutputMode.Text,
            PrintMode: false,
            ReadOnly: false,
            NoTools: false,
            AutoRetry: false);

        var sessions = await SessionController.CreateAsync(bootstrap, options, CancellationToken.None);
        return new TestHarness(temp, workspace, bootstrap, sessions, leaf, summarizer, toolCounter, new CapturingChatOutput());
    }

    private static AITool CreateCountingReadTool(CountingToolExecutor counter)
    {
        Func<string, CancellationToken, Task<string>> read = (path, token) =>
        {
            counter.Count();
            return Task.FromResult($"content of {path}");
        };
        return AIFunctionFactory.Create(read, "read", "Read a file.", new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        });
    }

    private static LeafBehavior ToolCallStep(string path) => new()
    {
        BuildUpdates = _ =>
        [
            new ChatResponseUpdate(ChatRole.Assistant, new AIContent[]
            {
                new FunctionCallContent("call-1", "read", new Dictionary<string, object?> { ["path"] = path }),
            }),
        ],
    };

    private static LeafBehavior FinalStep(string text) => new() { BuildUpdates = _ => FinalUpdates(text) };

    private static LeafBehavior OverflowStep() => new()
    {
        Throw = new InvalidOperationException("The prompt is too long: 200000 tokens > 128000 maximum context length."),
    };

    private static List<ChatResponseUpdate> FinalUpdates(string text) =>
    [
        new(ChatRole.Assistant, new AIContent[] { new TextContent(text) }) { FinishReason = ChatFinishReason.Stop },
    ];

    private static JsonElement AssistantMessage(string text) =>
        JsonSerializer.SerializeToElement(new
        {
            role = "assistant",
            content = new object[] { new { type = "text", text } },
        });

    private static string NewPromptMarker() => "PROMPT-" + Guid.NewGuid().ToString("N")[..8];

    private static string ReadMessageText(ChatMessage message) =>
        string.Concat(message.Contents.Select(content => content switch
        {
            TextContent text => text.Text,
            FunctionCallContent call => $"{call.Name} {call.Arguments}",
            FunctionResultContent result => result.Result?.ToString() ?? string.Empty,
            _ => string.Empty,
        }));

    private static string ReadContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }
        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = string.Empty;
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
            {
                text += value.GetString();
            }
        }
        return text;
    }

    /// <summary>One scripted provider-call behavior: throw, or stream the given updates.</summary>
    internal sealed class LeafBehavior
    {
        public Exception? Throw { get; init; }

        public Func<IReadOnlyList<ChatMessage>, List<ChatResponseUpdate>>? BuildUpdates { get; init; }
    }

    /// <summary>Scripts model responses per call and records every incoming request.</summary>
    internal sealed class ScriptedLeafClient : IChatClient
    {
        private readonly object _sync = new();
        private readonly List<IReadOnlyList<ChatMessage>> _requests = [];
        private readonly Queue<LeafBehavior> _script;
        private int _calls;

        public ScriptedLeafClient(IReadOnlyList<LeafBehavior> script)
        {
            _script = new Queue<LeafBehavior>(script);
        }

        public int CallCount
        {
            get
            {
                lock (_sync)
                {
                    return _calls;
                }
            }
        }

        public string? LastUserPrompt { get; private set; }

        public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests
        {
            get
            {
                lock (_sync)
                {
                    return _requests.ToArray();
                }
            }
        }

        private LeafBehavior Next()
        {
            lock (_sync)
            {
                _calls++;
                // The last behavior repeats: scripts describe the interesting calls, and any
                // further call (e.g. a continuation) still returns a sane response.
                return _script.Count > 1 ? _script.Dequeue() : _script.Peek();
            }
        }

        private void Record(IEnumerable<ChatMessage> messages)
        {
            var list = messages.ToList();
            var user = list.LastOrDefault(message => message.Role == ChatRole.User);
            lock (_sync)
            {
                _requests.Add(list);
                LastUserPrompt = user?.Text;
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Record(messages);
            var behavior = Next();
            if (behavior.Throw is not null)
            {
                return Task.FromException<ChatResponse>(behavior.Throw);
            }

            var list = (messages as IReadOnlyList<ChatMessage>) ?? messages.ToArray();
            var updates = behavior.BuildUpdates!(list);
            var contents = updates.SelectMany(update => update.Contents).ToArray();
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, contents.Length > 0 ? contents : new AIContent[] { new TextContent(string.Empty) })]));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            StreamAsync(messages, cancellationToken);

        private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            IEnumerable<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Record(messages);
            var behavior = Next();
            if (behavior.Throw is not null)
            {
                throw behavior.Throw;
            }

            var list = (messages as IReadOnlyList<ChatMessage>) ?? messages.ToArray();
            foreach (var update in behavior.BuildUpdates!(list))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        public IChatClient WithInstructions(string? instructions) => this;

        public IChatClient WithTools(IEnumerable<AITool> tools) => this;

        public IChatClient WithTools(params AIFunction[] functions) => this;

        public IChatClient WithFunctions(params AIFunction[] functions) => this;

        public IChatClient WithFunctions(IEnumerable<AIFunction> functions) => this;

        public object? GetService(Type serviceType, object? serviceKey) => null;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class CountingToolExecutor
    {
        private int _count;

        public void Count()
        {
            lock (this)
            {
                _count++;
            }
        }

        public int Read()
        {
            lock (this)
            {
                return _count;
            }
        }
    }

    internal sealed class CapturingChatOutput : IChatOutput
    {
        private readonly List<string> _events = [];

        public IReadOnlyList<string> Events => _events;

        public void AgentStarted() => _events.Add("agent_start");
        public void AgentFinished(string assistantText, bool cancelled) => _events.Add($"agent_end:{cancelled}");
        public void AssistantMessageStarted() { }
        public void AssistantMessageFinished(string assistantText) { }
        public void WriteText(string text) { }
        public void ToolStarted(string callId, string name, string arguments) { }
        public void ToolUpdated(string callId, string name, string arguments) { }
        public void ToolFinished(string callId, string name, string? error, string result) { }
        public void WriteLine() { }

        public void CompactionStarted(string reason) => _events.Add($"compaction_start:{reason}");

        public void CompactionFinished(string reason, int tokensBefore, int tokensAfter, bool aborted, string? error) =>
            _events.Add($"compaction_end:{reason}:{aborted}:{error}");

        public void CompactionFailed(string reason, string error, bool aborted) =>
            _events.Add($"compaction_error:{reason}:{error}");
    }

    internal sealed record PromptOutcome(LiveTurnResult Result, SessionController Sessions);

    internal sealed record TestHarness(
        TempDirectory Temp,
        string Workspace,
        AgentBootstrap Bootstrap,
        SessionController Sessions,
        ScriptedLeafClient Leaf,
        ScriptedLeafClient Summarizer,
        CountingToolExecutor Tool,
        CapturingChatOutput Output)
    {
        public int ToolExecutions => Tool.Read();
    }
}
