using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.Tools;
namespace PiSharp.Runtime;

/// <summary>One shared streaming runtime for terminal and one-shot invocation.</summary>
public sealed class PiAgent
{
    private const string SummarizationSystemPrompt = "You are a context summarization assistant. Your task is to read a conversation between a user and an AI assistant, then produce a structured summary following the exact format specified.\n\nDo NOT continue the conversation. Do NOT respond to any questions in the conversation. ONLY output the structured summary.";
    private const string SummarizationPrompt = """
        The messages above are a conversation to summarize. Create a structured context checkpoint summary that another LLM will use to continue the work.

        Use this EXACT format:

        ## Goal
        [What is the user trying to accomplish? Can be multiple items if the session covers different tasks.]

        ## Constraints & Preferences
        - [Any constraints, preferences, or requirements mentioned by user]
        - [Or "(none)" if none were mentioned]

        ## Progress
        ### Done
        - [x] [Completed tasks/changes]

        ### In Progress
        - [ ] [Current work]

        ### Blocked
        - [Issues preventing progress, if any]

        ## Key Decisions
        - **[Decision]**: [Brief rationale]

        ## Next Steps
        1. [Ordered list of what should happen next]

        ## Critical Context
        - [Any data, examples, or references needed to continue]
        - [Or "(none)" if not applicable]

        Keep each section concise. Preserve exact file paths, function names, and error messages.
        """;
    private const string UpdateSummarizationPrompt = """
        The messages above are NEW conversation messages to incorporate into the existing summary provided in <previous-summary> tags.

        Update the existing structured summary with new information. RULES:
        - PRESERVE all existing information from the previous summary
        - ADD new progress, decisions, and context from the new messages
        - UPDATE the Progress section: move items from "In Progress" to "Done" when completed
        - UPDATE "Next Steps" based on what was accomplished
        - PRESERVE exact file paths, function names, and error messages
        - If something is no longer relevant, you may remove it

        Use this EXACT format:

        ## Goal
        [Preserve existing goals, add new ones if the task expanded]

        ## Constraints & Preferences
        - [Preserve existing, add new ones discovered]

        ## Progress
        ### Done
        - [x] [Include previously done items AND newly completed items]

        ### In Progress
        - [ ] [Current work - update based on progress]

        ### Blocked
        - [Current blockers - remove if resolved]

        ## Key Decisions
        - **[Decision]**: [Brief rationale] (preserve all previous, add new)

        ## Next Steps
        1. [Update based on current state]

        ## Critical Context
        - [Preserve important context, add new if needed]

        Keep each section concise. Preserve exact file paths, function names, and error messages.
        """;
    private const string TurnPrefixSummarizationPrompt = """
        The messages above are earlier context from an ongoing conversation. Later messages are stored separately and do not need to be reconstructed.

        Create a concise checkpoint of the user's request and the progress shown above. This checkpoint will be placed before the later messages so the conversation can continue with the necessary context.

        ## Original Request
        [What did the user ask for?]

        ## Progress So Far
        - [Key decisions and work completed in these messages]

        ## Context Needed to Continue
        - [Information from these messages needed to understand the later work]

        Only summarize information explicitly present above. Do not infer or recreate later messages.
        """;
    private readonly InMemoryChatHistoryProvider _history = new();
    private readonly CodingTools _codingTools;
    private readonly ChatClientAgent _agent;
    private readonly ChatClientAgent _summarizer;
    private readonly MutableChatClient _chatClient;
    private readonly IReadOnlyList<AIFunctionDeclaration> _toolDeclarations;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private ReasoningOptions? _reasoning;
    private DurableExecution? _active;
    private Action<AgentLifecycleEvent>? _events;
    private Func<bool, IReadOnlyList<ChatMessage>>? _takeSteering;
    private Func<IReadOnlyList<ChatMessage>, bool, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? _projectContext;
    private readonly List<(ChatMessage Message, string? AfterCallId)> _injectedSteering = [];
    private int _providerRequestIndex;
    private int _supportsImages;
    private long _systemMessageTimestamp;

    public string SystemInstructions { get; }
    public IReadOnlyList<AIFunctionDeclaration> ToolDeclarations => _toolDeclarations;
    public long? SystemMessageTimestamp => Volatile.Read(ref _systemMessageTimestamp) is var timestamp && timestamp != 0
        ? timestamp
        : null;

    public PiAgent(IChatClient client, CodingTools tools, IReadOnlyList<string>? selectedTools = null, IReadOnlyList<string>? excludedTools = null, bool noTools = false, string? contextInstructions = null, string? systemPrompt = null, string? appendSystemPrompt = null,
        IReadOnlyCollection<AIFunction>? extensionTools = null, ProviderRetryPolicy? retryPolicy = null,
        ReasoningOptions? reasoning = null, bool blockImages = false, bool noBuiltinTools = false, bool supportsImages = true)
    {
        _codingTools = tools;
        _chatClient = new MutableChatClient(client);
        _supportsImages = supportsImages ? 1 : 0;
        _reasoning = reasoning;
        _summarizer = new ChatClientAgent(_chatClient, new ChatClientAgentOptions
        {
            Name = "PiSharpCompaction",
            ChatOptions = new ChatOptions
            {
                Instructions = SummarizationSystemPrompt
            }
        });
        var added = extensionTools?.ToArray() ?? [];
        var builtin = tools.Create(selectedTools?.Where(name => added.All(tool => tool.Name != name)).ToArray(), excludedTools, noTools || noBuiltinTools);
        var external = added.Where(tool => (selectedTools?.Contains(tool.Name) ?? !noTools) &&
            excludedTools?.Contains(tool.Name) != true).Cast<AITool>().ToArray();
        if (added.Any(tool => new[] { "read", "bash", "edit", "write", "grep", "find", "ls" }.Contains(tool.Name, StringComparer.Ordinal)) ||
            builtin.Concat(external).GroupBy(tool => tool.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("Extension tool conflicts with a built-in tool name.");
        var configuredTools = builtin.Concat(external).ToArray();
        _toolDeclarations = configuredTools.OfType<AIFunctionDeclaration>().ToArray();
        SystemInstructions = (systemPrompt ?? "You are PiSharp, a coding agent. Inspect files before modifying them when tools are available. Use only the tools provided for this run.") +
            "\n\n" + (appendSystemPrompt ?? "") + "\n\n" + (contextInstructions ?? "");
        _agent = new ChatClientAgent(new ObservedChatClient(_chatClient, value => _events?.Invoke(value),
            retryPolicy ?? ProviderRetryPolicy.Default, TakeSteeringForRequest, blockImages, ProjectForRequestAsync,
            supportsImages, () => Volatile.Read(ref _reasoning), () => Volatile.Read(ref _supportsImages) != 0,
            _chatClient), new ChatClientAgentOptions
            {
                Name = "PiSharp",
                ChatHistoryProvider = _history,
                AllowConcurrentInvocation = true,
                ChatOptions = new ChatOptions
                {
                    Instructions = SystemInstructions,
                    Tools = configuredTools.Select(tool => tool is AIFunction function ? new DurableToolFunction(function, () => _active, value => _events?.Invoke(value)) : tool).Cast<AITool>().ToArray(),
                    Reasoning = reasoning
                }
            });
    }

    private IReadOnlyList<ChatMessage> TakeSteeringForRequest(IEnumerable<ChatMessage> messages)
    {
        var firstProviderRequest = _providerRequestIndex++ == 0;
        var steering = _takeSteering?.Invoke(firstProviderRequest) ?? [];
        if (steering.Count == 0) return steering;
        var afterCallId = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
            .LastOrDefault()?.CallId;
        _injectedSteering.AddRange(steering.Select(message => (message, afterCallId)));
        return steering;
    }

    private Task<IReadOnlyList<ChatMessage>> ProjectForRequestAsync(IReadOnlyList<ChatMessage> messages, bool force, CancellationToken token) =>
        // The pre-prompt policy owns the first request; only a pre-content overflow can force it.
        (force || _providerRequestIndex > 1) && _projectContext is { } project
            ? project(messages, force, token) : Task.FromResult(messages);

    public async Task<CompactionSummary> SummarizeAsync(IReadOnlyList<ChatMessage> messages, string? focus,
        CancellationToken cancellationToken = default, string? previousSummary = null, bool turnPrefix = false)
    {
        if (focus?.Length > 4096) throw new ArgumentException("Compaction instructions exceed 4096 characters.", nameof(focus));
        // Select recent messages first so a long prefix cannot hide the latest tool outcome.
        // Render selected entries in chronological order for the summarizer.
        var excerpts = new Stack<string>();
        var length = 0;
        var omittedEarlier = false;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            var line = SummaryTranscriptExcerpt.SerializeForSummary(message) + Environment.NewLine + Environment.NewLine;
            if (line.Length == 2 * Environment.NewLine.Length) continue;
            if (length + line.Length > 64 * 1024 - 128)
            {
                omittedEarlier = true;
                break;
            }
            excerpts.Push(line);
            length += line.Length;
        }
        var transcript = new System.Text.StringBuilder(length + 128);
        if (omittedEarlier)
            transcript.AppendLine("[Earlier conversation omitted from bounded summarization transcript.]");
        foreach (var line in excerpts) transcript.Append(line);
        var request = turnPrefix
            ? $"# Conversation\n{transcript}\n\n# Instructions\n{TurnPrefixSummarizationPrompt}"
            : $"<conversation>\n{transcript}\n</conversation>\n\n" +
                (previousSummary is null ? "" : $"<previous-summary>\n{previousSummary}\n</previous-summary>\n\n") +
                (previousSummary is null ? SummarizationPrompt : UpdateSummarizationPrompt) +
                (string.IsNullOrEmpty(focus) ? "" : $"\n\nAdditional focus: {focus}");
        var response = await _summarizer.RunAsync(request, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(response.Text)) throw new InvalidDataException("Summarizer returned an empty response.");
        return new CompactionSummary(response.Text, response.Usage);
    }

    public sealed record CompactionSummary(string Text, UsageDetails? Usage);

    public async Task<AgentSession> CreateSessionAsync(CancellationToken cancellationToken = default) =>
        await _agent.CreateSessionAsync(cancellationToken);

    public void SetReasoningOptions(ReasoningOptions? reasoning) => Volatile.Write(ref _reasoning, reasoning);

    public void SetModelRuntime(IChatClient client, bool supportsImages, ModelImageResizeOptions? imageResizeOptions)
    {
        _chatClient.SetClient(client);
        Volatile.Write(ref _supportsImages, supportsImages ? 1 : 0);
        _codingTools.SetImageResizeOptions(imageResizeOptions);
    }

    public Task<BashExecutionResult> ExecuteBashAsync(string command, Action<string>? onUpdate = null,
        CancellationToken cancellationToken = default) => _codingTools.ExecuteBashAsync(command, onUpdate, cancellationToken);

    public void AbortBash() => _codingTools.AbortBash();

    public IReadOnlyList<ChatMessage> GetHistory(AgentSession session) => _history.GetMessages(session).ToArray();

    public void AppendToHistory(AgentSession session, ChatMessage message)
    {
        var messages = _history.GetMessages(session).ToList();
        messages.Add(message);
        _history.SetMessages(session, messages);
    }

    public async Task<AgentSession> RestoreHistoryAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var session = await CreateSessionAsync(cancellationToken);
        _history.SetMessages(session, messages.ToList());
        return session;
    }


    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(string prompt, AgentSession session,
        CancellationToken cancellationToken = default) => RunStreamingAsync(new ChatMessage(ChatRole.User, prompt), session, cancellationToken);

    public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(ChatMessage prompt, AgentSession session,
        CancellationToken cancellationToken = default) => RunStreamingDurableAsync(prompt, session, cancellationToken, null);

    internal IAsyncEnumerable<AgentResponseUpdate> RunStreamingDurableAsync(string prompt, AgentSession session,
        CancellationToken cancellationToken, DurableExecution? durable, Action<AgentLifecycleEvent>? onEvent = null,
        Func<bool, IReadOnlyList<ChatMessage>>? takeSteering = null,
        Func<IReadOnlyList<ChatMessage>, bool, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? projectContext = null,
        IReadOnlyDictionary<string, string?>? bashSessionEnvironment = null) =>
        RunStreamingDurableAsync(new ChatMessage(ChatRole.User, prompt), session, cancellationToken, durable, onEvent,
            takeSteering, projectContext, bashSessionEnvironment);

    internal IAsyncEnumerable<AgentResponseUpdate> RunStreamingDurableAsync(ChatMessage prompt, AgentSession session,
        CancellationToken cancellationToken, DurableExecution? durable,
        Action<AgentLifecycleEvent>? onEvent = null, Func<bool, IReadOnlyList<ChatMessage>>? takeSteering = null,
        Func<IReadOnlyList<ChatMessage>, bool, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? projectContext = null,
        IReadOnlyDictionary<string, string?>? bashSessionEnvironment = null) =>
        RunStreamingDurableCoreAsync(prompt, session, cancellationToken, durable, onEvent, takeSteering,
            projectContext, bashSessionEnvironment);

    internal IAsyncEnumerable<AgentResponseUpdate> RunStreamingContinuationDurableAsync(AgentSession session,
        CancellationToken cancellationToken, DurableExecution? durable, Action<AgentLifecycleEvent>? onEvent = null,
        Func<bool, IReadOnlyList<ChatMessage>>? takeSteering = null,
        Func<IReadOnlyList<ChatMessage>, bool, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? projectContext = null,
        IReadOnlyDictionary<string, string?>? bashSessionEnvironment = null) =>
        RunStreamingDurableCoreAsync(null, session, cancellationToken, durable, onEvent, takeSteering,
            projectContext, bashSessionEnvironment);

    private async IAsyncEnumerable<AgentResponseUpdate> RunStreamingDurableCoreAsync(ChatMessage? prompt,
        AgentSession session,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, DurableExecution? durable,
        Action<AgentLifecycleEvent>? onEvent, Func<bool, IReadOnlyList<ChatMessage>>? takeSteering,
        Func<IReadOnlyList<ChatMessage>, bool, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? projectContext,
        IReadOnlyDictionary<string, string?>? bashSessionEnvironment)
    {
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.CompareExchange(ref _systemMessageTimestamp, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0);
            _active = durable;
            _events = onEvent;
            _takeSteering = takeSteering;
            _projectContext = projectContext;
            _providerRequestIndex = 0;
            _injectedSteering.Clear();
            AgentRunOptions? runOptions = null;
            if (bashSessionEnvironment is { Count: > 0 })
            {
                var properties = new AdditionalPropertiesDictionary();
                foreach (var (key, value) in bashSessionEnvironment)
                    if (value is not null) properties[key] = value;
                runOptions = new AgentRunOptions { AdditionalProperties = properties };
            }
            try
            {
                var updates = prompt is null
                    ? _agent.RunStreamingAsync(session, runOptions, cancellationToken)
                    : _agent.RunStreamingAsync(prompt, session, runOptions, cancellationToken);
                await foreach (var update in updates)
                    yield return update;
            }
            finally
            {
                PersistInjectedSteering(session);
                _history.SetMessages(session, ObservedChatClient.NormalizeReadImagesForHistory(_history.GetMessages(session)).ToList());
            }
        }
        finally { _projectContext = null; _takeSteering = null; _events = null; _active = null; _runGate.Release(); }
    }

    private void PersistInjectedSteering(AgentSession session)
    {
        if (_injectedSteering.Count == 0) return;
        var history = _history.GetMessages(session).ToList();
        foreach (var (message, afterCallId) in _injectedSteering)
        {
            if (history.Any(item => ReferenceEquals(item, message))) continue;
            var index = afterCallId is null ? history.Count - 1 : history.FindLastIndex(item =>
                item.Contents.OfType<FunctionResultContent>().Any(result => result.CallId == afterCallId));
            var insertionIndex = Math.Clamp(index + 1, 0, history.Count);
            while (insertionIndex < history.Count && _injectedSteering.Any(steering =>
                steering.AfterCallId == afterCallId && ReferenceEquals(steering.Message, history[insertionIndex])))
                insertionIndex++;
            history.Insert(insertionIndex, message);
        }
        _history.SetMessages(session, history);
        _injectedSteering.Clear();
    }
}
