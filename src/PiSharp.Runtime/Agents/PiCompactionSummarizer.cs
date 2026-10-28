using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.VirtualModels;

namespace PiSharp.Runtime;

/// <summary>Owns bounded Pi compaction prompts and direct MAF summarizer requests.</summary>
internal sealed class PiCompactionSummarizer
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
    private const string BranchSummaryPrompt = """
        Create a structured summary of this conversation branch for context when returning later.

        Use this EXACT format:

        ## Goal
        [What was the user trying to accomplish in this branch?]

        ## Constraints & Preferences
        - [Any constraints, preferences, or requirements mentioned]
        - [Or "(none)" if none were mentioned]

        ## Progress
        ### Done
        - [x] [Completed tasks/changes]

        ### In Progress
        - [ ] [Work that was started but not finished]

        ### Blocked
        - [Issues preventing progress, if any]

        ## Key Decisions
        - **[Decision]**: [Brief rationale]

        ## Next Steps
        1. [What should happen next to continue this work]

        Keep each section concise. Preserve exact file paths, function names, and error messages.
        """;
    private const string BranchSummaryPreamble = """
        The user explored a different conversation branch before returning here.
        Summary of that exploration:


        """;
    private readonly ChatClientAgent _summarizer;
    private readonly RoutedChatClient _routedChatClient;
    private readonly Func<ReasoningOptions?> _getReasoning;
    private readonly Func<int?> _getDesiredMaxOutputTokens;
    private readonly ProviderRetryPolicy _retryPolicy;

    public PiCompactionSummarizer(RoutedChatClient client, Func<ReasoningOptions?> getReasoning,
        Func<int?> getDesiredMaxOutputTokens, ProviderRetryPolicy retryPolicy)
    {
        _routedChatClient = client;
        _getReasoning = getReasoning;
        _getDesiredMaxOutputTokens = getDesiredMaxOutputTokens;
        _retryPolicy = retryPolicy;
        _summarizer = new ChatClientAgent(_routedChatClient, new ChatClientAgentOptions
        {
            Name = "PiSharpCompaction",
            ChatOptions = new ChatOptions
            {
                Instructions = SummarizationSystemPrompt
            }
        });
    }

    public async Task<PiAgent.CompactionSummary> SummarizeAsync(IReadOnlyList<ChatMessage> messages, string? focus,
        CancellationToken cancellationToken = default, string? previousSummary = null, bool turnPrefix = false,
        int? reserveTokens = null)
    {
        if (focus?.Length > 4096) throw new ArgumentException("Compaction instructions exceed 4096 characters.", nameof(focus));
        var transcript = BuildTranscript(messages);
        var request = turnPrefix
            ? $"# Conversation\n{transcript}\n\n# Instructions\n{TurnPrefixSummarizationPrompt}"
            : $"<conversation>\n{transcript}\n</conversation>\n\n" +
                (previousSummary is null ? "" : $"<previous-summary>\n{previousSummary}\n</previous-summary>\n\n") +
                (previousSummary is null ? SummarizationPrompt : UpdateSummarizationPrompt) +
                (string.IsNullOrEmpty(focus) ? "" : $"\n\nAdditional focus: {focus}");
        return await RunSummaryAsync(request, messages, "compaction", cancellationToken, reserveTokens, turnPrefix)
            .ConfigureAwait(false);
    }

    public async Task<PiAgent.CompactionSummary> SummarizeBranchAsync(IReadOnlyList<ChatMessage> messages,
        string? customInstructions, bool replaceInstructions, CancellationToken cancellationToken = default)
    {
        if (customInstructions?.Length > 4096)
            throw new ArgumentException("Branch summary instructions exceed 4096 characters.", nameof(customInstructions));
        var instructions = replaceInstructions && !string.IsNullOrEmpty(customInstructions)
            ? customInstructions
            : string.IsNullOrEmpty(customInstructions)
                ? BranchSummaryPrompt
                : $"{BranchSummaryPrompt}\n\nAdditional focus: {customInstructions}";
        var request = $"<conversation>\n{BuildTranscript(messages)}\n</conversation>\n\n{instructions}";
        var summary = await RunSummaryAsync(request, messages, "branch_summary", cancellationToken).ConfigureAwait(false);
        return summary with { Text = BranchSummaryPreamble + summary.Text };
    }

    private async Task<PiAgent.CompactionSummary> RunSummaryAsync(string request, IReadOnlyList<ChatMessage> messages,
        string usageSource, CancellationToken cancellationToken, int? reserveTokens = null, bool turnPrefix = false)
    {
        var directOptions = _routedChatClient.HasRouter ? _routedChatClient.CreateDirectRequestOptions(messages) : null;
        var reasoning = directOptions is null ? _getReasoning() : null;
        var requestOptions = directOptions ?? (reasoning is null ? null : new ChatOptions { Reasoning = reasoning });
        var desiredOutputTokens = _getDesiredMaxOutputTokens();
        if (desiredOutputTokens is <= 0) desiredOutputTokens = null;
        var outputLimit = reserveTokens is { } reserve
            ? (int)Math.Floor((turnPrefix ? 0.5 : 0.8) * reserve)
            : desiredOutputTokens;
        if (reserveTokens is not null && desiredOutputTokens is > 0)
            outputLimit = Math.Min(outputLimit ?? desiredOutputTokens.Value, desiredOutputTokens.Value);
        if (outputLimit is { } maxOutputTokens)
        {
            requestOptions = requestOptions?.Clone() ?? new ChatOptions();
            requestOptions.MaxOutputTokens = maxOutputTokens;
        }
        var response = await RunWithRetryAsync(() => _summarizer.RunAsync(request,
            options: requestOptions is null ? null : new ChatClientAgentRunOptions(requestOptions),
            cancellationToken: cancellationToken), retryCount: 0).ConfigureAwait(false);

        async Task<TResponse> RunWithRetryAsync<TResponse>(Func<Task<TResponse>> operation, int retryCount)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception error) when (_retryPolicy.CanRetry(error, retryCount, producedOutput: false))
            {
                await Task.Delay(_retryPolicy.Delay, cancellationToken).ConfigureAwait(false);
                return await RunWithRetryAsync(operation, retryCount + 1).ConfigureAwait(false);
            }
        }

        if (response.FinishReason == ChatFinishReason.Length)
            throw new InvalidDataException("Summarization failed: generation hit the token cap and the summary is incomplete");
        if (string.IsNullOrWhiteSpace(response.Text)) throw new InvalidDataException("Summarizer returned an empty response.");
        var route = VirtualModelRequestHints.ReadHint(directOptions)?.Execution?.Route;
        return new PiAgent.CompactionSummary(response.Text, response.Usage,
            route is not null && response.Usage is { } usage
                ? [UsageRecord.Create(route.Model.Id, usageSource, usage, route.Pricing)] : null);
    }

    private static string BuildTranscript(IReadOnlyList<ChatMessage> messages)
    {
        // Select recent messages first so a long prefix cannot hide the latest tool outcome.
        // Render selected entries in chronological order for the summarizer.
        var excerpts = new Stack<string>();
        var length = 0;
        var omittedEarlier = false;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var line = SummaryTranscriptExcerpt.SerializeForSummary(messages[i]);
            if (line.Length == 0) continue;
            var separatorLength = excerpts.Count == 0 ? 0 : 2 * Environment.NewLine.Length;
            if (length + separatorLength + line.Length > 64 * 1024 - 128)
            {
                omittedEarlier = true;
                break;
            }
            excerpts.Push(line);
            length += separatorLength + line.Length;
        }
        var transcript = new System.Text.StringBuilder(length + 128);
        if (omittedEarlier)
            transcript.Append("[Earlier conversation omitted from bounded summarization transcript.]");
        foreach (var line in excerpts)
        {
            if (transcript.Length > 0) transcript.AppendLine().AppendLine();
            transcript.Append(line);
        }
        return transcript.ToString();
    }

}
