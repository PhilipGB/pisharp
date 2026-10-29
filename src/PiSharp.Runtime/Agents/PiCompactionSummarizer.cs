using System.Text;
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
    private readonly AgentRunRetryPolicy _retryPolicy;
    private readonly Func<AgentLifecycleEvent, Task> _publishLifecycleEvent;

    public PiCompactionSummarizer(RoutedChatClient client, Func<ReasoningOptions?> getReasoning,
        Func<int?> getDesiredMaxOutputTokens, AgentRunRetryPolicy retryPolicy,
        Func<AgentLifecycleEvent, Task> publishLifecycleEvent)
    {
        _routedChatClient = client;
        _getReasoning = getReasoning;
        _getDesiredMaxOutputTokens = getDesiredMaxOutputTokens;
        _retryPolicy = retryPolicy;
        _publishLifecycleEvent = publishLifecycleEvent;
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
        int? reserveTokens = null, string compactionReason = "manual",
        Func<AgentLifecycleEvent, Task>? publishLifecycleEvent = null)
    {
        if (focus?.Length > 4096) throw new ArgumentException("Compaction instructions exceed 4096 characters.", nameof(focus));
        var transcript = BuildTranscript(messages);
        var request = turnPrefix
            ? $"# Conversation\n{transcript}\n\n# Instructions\n{TurnPrefixSummarizationPrompt}"
            : $"<conversation>\n{transcript}\n</conversation>\n\n" +
                (previousSummary is null ? "" : $"<previous-summary>\n{previousSummary}\n</previous-summary>\n\n") +
                (previousSummary is null ? SummarizationPrompt : UpdateSummarizationPrompt) +
                (string.IsNullOrEmpty(focus) ? "" : $"\n\nAdditional focus: {focus}");
        return await RunSummaryAsync(request, messages, "compaction", cancellationToken, reserveTokens, turnPrefix,
            "compaction", compactionReason, publishLifecycleEvent)
            .ConfigureAwait(false);
    }

    public async Task<PiAgent.CompactionSummary> SummarizeBranchAsync(IReadOnlyList<ChatMessage> messages,
        string? customInstructions, bool replaceInstructions, CancellationToken cancellationToken = default,
        Func<AgentLifecycleEvent, Task>? publishLifecycleEvent = null)
    {
        if (customInstructions?.Length > 4096)
            throw new ArgumentException("Branch summary instructions exceed 4096 characters.", nameof(customInstructions));
        var instructions = replaceInstructions && !string.IsNullOrEmpty(customInstructions)
            ? customInstructions
            : string.IsNullOrEmpty(customInstructions)
                ? BranchSummaryPrompt
                : $"{BranchSummaryPrompt}\n\nAdditional focus: {customInstructions}";
        var request = $"<conversation>\n{BuildTranscript(messages)}\n</conversation>\n\n{instructions}";
        var summary = await RunSummaryAsync(request, messages, "branch_summary", cancellationToken,
            retrySource: "branchSummary", publishLifecycleEvent: publishLifecycleEvent).ConfigureAwait(false);
        return summary with { Text = BranchSummaryPreamble + summary.Text };
    }

    private async Task<PiAgent.CompactionSummary> RunSummaryAsync(string request, IReadOnlyList<ChatMessage> messages,
        string usageSource, CancellationToken cancellationToken, int? reserveTokens = null, bool turnPrefix = false,
        string? retrySource = null, string? compactionReason = null,
        Func<AgentLifecycleEvent, Task>? publishLifecycleEvent = null)
    {
        publishLifecycleEvent ??= _publishLifecycleEvent;
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
        var responseText = new StringBuilder();
        UsageDetails? responseUsage = null;
        ChatFinishReason? finishReason = null;
        var retryCount = 0;
        try
        {
            while (true)
            {
                try
                {
                    var updates = _summarizer.RunStreamingAsync(request,
                        options: requestOptions is null ? null : new ChatClientAgentRunOptions(requestOptions),
                        cancellationToken: cancellationToken);
                    await foreach (var update in updates.ConfigureAwait(false))
                    {
                        responseText.Append(update.Text);
                        if (update.FinishReason is { } updateFinishReason) finishReason = updateFinishReason;
                        if (update.Contents is null) continue;
                        foreach (var content in update.Contents)
                            if (content is UsageContent usageContent) responseUsage = usageContent.Details;
                    }

                    break;
                }
                catch (Exception error) when (responseText.Length == 0 &&
                    _retryPolicy.CanRetry(error, error.Message, retryCount, _retryPolicy.Enabled))
                {
                    retryCount++;
                    var delay = _retryPolicy.DelayForAttempt(retryCount);
                    var errorMessage = PiProviderErrorFormatter.Format(error.Message) ?? error.Message;
                    await publishLifecycleEvent(new AgentLifecycleEvent("summarization_retry_scheduled", Error: errorMessage)
                    {
                        RetryAttempt = retryCount,
                        RetryMaxAttempts = _retryPolicy.MaxRetries,
                        RetryDelayMs = (long)delay.TotalMilliseconds
                    }).ConfigureAwait(false);
                    try
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        await publishLifecycleEvent(new AgentLifecycleEvent("summarization_retry_finished"))
                            .ConfigureAwait(false);
                        throw;
                    }
                    await publishLifecycleEvent(new AgentLifecycleEvent("summarization_retry_attempt_start")
                    {
                        SummarizationRetrySource = retrySource,
                        CompactionReason = compactionReason
                    }).ConfigureAwait(false);
                    responseText.Clear();
                    responseUsage = null;
                    finishReason = null;
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (retryCount > 0)
                await publishLifecycleEvent(new AgentLifecycleEvent("summarization_retry_finished"))
                    .ConfigureAwait(false);
            var message = PiProviderErrorFormatter.Format(error.Message) ?? error.Message;
            if (!message.StartsWith("Summarization failed:", StringComparison.Ordinal))
                message = $"Summarization failed: {message}";
            throw new IOException(message, error);
        }

        if (retryCount > 0)
            await publishLifecycleEvent(new AgentLifecycleEvent("summarization_retry_finished"))
                .ConfigureAwait(false);

        if (finishReason == ChatFinishReason.Length)
            throw new InvalidDataException("Summarization failed: generation hit the token cap and the summary is incomplete");
        if (string.IsNullOrWhiteSpace(responseText.ToString()))
            throw new InvalidDataException("Summarizer returned an empty response.");
        var route = VirtualModelRequestHints.ReadHint(directOptions)?.Execution?.Route;
        return new PiAgent.CompactionSummary(responseText.ToString(), responseUsage,
            route is not null && responseUsage is { } usageDetails
                ? [UsageRecord.Create(route.Model.Id, usageSource, usageDetails, route.Pricing)] : null);
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
