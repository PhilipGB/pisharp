using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Core;

namespace PiSharp.Cli;

internal sealed record PiSummaryResult(
    string Summary,
    string FirstKeptEntryId,
    int TokensBefore,
    JsonElement? Details,
    JsonElement? Usage);

internal sealed class PiSummarizer
{
    private const double SummaryOutputFraction = 0.8;
    private const int MaximumBranchSummaryTokens = 4_096;
    private const int SplitTurnOutputDivisor = 2;
    private const string SummarySystemPrompt = "You are a context summarization assistant. Read the supplied conversation and output only the requested structured summary. Do not continue the conversation or call tools.";
    private const string CompactionPrompt = """
        The messages above are a conversation to summarize. Create a structured context checkpoint summary that another LLM will use to continue the work.

        Use this EXACT format:

        ## Goal
        [What is the user trying to accomplish?]

        ## Constraints & Preferences
        - [Any constraints, preferences, or requirements mentioned]
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
    private const string UpdatePrompt = """
        Update the existing structured summary with new information. Preserve all existing information, add progress from the new messages, move completed work to Done, and preserve exact file paths, function names, and error messages. Use the exact structured format requested for a context checkpoint.
        """;
    private const string TurnPrefixPrompt = """
        This is the PREFIX of a turn that was too large to keep. The SUFFIX (recent work) is retained.

        Summarize the prefix to provide context for the retained suffix:

        ## Original Request
        [What did the user ask for in this turn?]

        ## Early Progress
        - [Key decisions and work done in the prefix]

        ## Context for Suffix
        - [Information needed to understand the kept suffix]

        Be concise. Focus on what's needed to understand the kept suffix.
        """;
    private const string BranchPreamble = "The user explored a different conversation branch before returning here.\nSummary of that exploration:\n\n";
    private const string BranchPrompt = """
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

    private readonly IChatClient _client;
    private readonly RetryPolicyOptions _retryPolicy;
    private readonly int _maxOutputTokens;

    public PiSummarizer(IChatClient client, RetryPolicyOptions retryPolicy, int maxOutputTokens)
    {
        _client = client;
        _retryPolicy = retryPolicy;
        _maxOutputTokens = maxOutputTokens;
    }

    public async Task<PiSummaryResult> GenerateCompactionAsync(
        CompactionPlan plan,
        string? customInstructions,
        CancellationToken cancellationToken)
    {
        var summary = plan.IsSplitTurn
            ? await GenerateSplitSummaryAsync(plan, customInstructions, cancellationToken)
            : await GenerateSummaryAsync(
                plan.MessagesToSummarize,
                plan.PreviousSummary,
                customInstructions,
                plan.Settings.ReserveTokens,
                CompactionPrompt,
                cancellationToken);
        var completeSummary = summary.Text + PiCompactionPlanner.FormatFileOperations(plan.FileOperations);
        return new PiSummaryResult(
            completeSummary,
            plan.FirstKeptEntryId,
            plan.TokensBefore,
            CreateFileDetails(plan.FileOperations),
            summary.Usage);
    }

    public async Task<(string Summary, JsonElement? Usage, JsonElement Details)> GenerateBranchSummaryAsync(
        BranchSummaryPlan plan,
        string? customInstructions,
        CancellationToken cancellationToken)
    {
        if (plan.Entries.Count == 0)
        {
            return ("No content to summarize", null, CreateFileDetails(plan.FileOperations));
        }

        var prompt = BuildConversationPrompt(
            plan.Entries,
            customInstructions,
            BranchPrompt,
            replaceInstructions: false);
        var response = await CompleteAsync(prompt, MaximumBranchSummaryTokens, cancellationToken);
        var summary = BranchPreamble + response.Text + PiCompactionPlanner.FormatFileOperations(plan.FileOperations);
        return (summary, CreateUsage(response.Usage), CreateFileDetails(plan.FileOperations));
    }

    private async Task<(string Text, JsonElement? Usage)> GenerateSplitSummaryAsync(
        CompactionPlan plan,
        string? customInstructions,
        CancellationToken cancellationToken)
    {
        var historyResult = plan.MessagesToSummarize.Count == 0
            ? (Text: "No prior history.", Usage: (JsonElement?)null)
            : await GenerateSummaryAsync(
                plan.MessagesToSummarize,
                plan.PreviousSummary,
                customInstructions,
                plan.Settings.ReserveTokens,
                CompactionPrompt,
                cancellationToken);
        var prefix = await GenerateSummaryAsync(
            plan.TurnPrefixMessages,
            previousSummary: null,
            null,
            plan.Settings.ReserveTokens / SplitTurnOutputDivisor,
            TurnPrefixPrompt,
            cancellationToken);
        return ($"{historyResult.Text}\n\n---\n\n**Turn Context (split turn):**\n\n{prefix.Text}", CombineUsage(historyResult, prefix));
    }

    private async Task<(string Text, JsonElement? Usage)> GenerateSummaryAsync(
        IReadOnlyList<SessionEntry> entries,
        string? previousSummary,
        string? customInstructions,
        int reserveTokens,
        string defaultPrompt,
        CancellationToken cancellationToken)
    {
        // Pi switches to "update" instructions when a previous checkpoint exists so the
        // model preserves existing goals/decisions instead of starting a brand-new summary.
        var effectivePrompt = string.IsNullOrWhiteSpace(previousSummary) ? defaultPrompt : UpdatePrompt;
        var prompt = BuildConversationPrompt(entries, customInstructions, effectivePrompt, replaceInstructions: false, previousSummary);
        var maxTokens = Math.Min(
            Math.Max(1, (int)(reserveTokens * SummaryOutputFraction)),
            _maxOutputTokens);
        var response = await CompleteAsync(prompt, maxTokens, cancellationToken);
        return (response.Text, CreateUsage(response.Usage));
    }

    private string BuildConversationPrompt(
        IReadOnlyList<SessionEntry> entries,
        string? customInstructions,
        string defaultPrompt,
        bool replaceInstructions,
        string? previousSummary = null)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("<conversation>\n");
        builder.Append(PiCompactionPlanner.SerializeForSummary(entries));
        builder.Append("\n</conversation>\n\n");
        if (!string.IsNullOrWhiteSpace(previousSummary))
        {
            builder.Append("<previous-summary>\n");
            builder.Append(previousSummary);
            builder.Append("\n</previous-summary>\n\n");
        }
        builder.Append(replaceInstructions && !string.IsNullOrWhiteSpace(customInstructions)
            ? customInstructions
            : defaultPrompt);
        if (!replaceInstructions && !string.IsNullOrWhiteSpace(customInstructions))
        {
            builder.Append("\n\nAdditional focus: ");
            builder.Append(customInstructions);
        }
        return builder.ToString();
    }

    private async Task<ChatResponse> CompleteAsync(
        string prompt,
        int maxOutputTokens,
        CancellationToken cancellationToken)
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, SummarySystemPrompt),
            new ChatMessage(ChatRole.User, prompt),
        };
        var options = new ChatOptions
        {
            MaxOutputTokens = maxOutputTokens,
            Tools = [],
        };
        var response = await RetryPolicy.ExecuteAsync(
            token => _client.GetResponseAsync(messages, options, token),
            _retryPolicy,
            cancellationToken);
        if (response.FinishReason?.Value == ChatFinishReason.Length.Value)
        {
            throw new InvalidOperationException("Summarization failed: generation hit the token cap and the summary is incomplete.");
        }
        if (response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Any())
        {
            throw new InvalidOperationException("Summarization attempted to call a tool.");
        }
        if (string.IsNullOrWhiteSpace(response.Text))
        {
            throw new InvalidOperationException("Summarization returned no content.");
        }
        return response;
    }

    private static JsonElement CreateFileDetails(CompactionFileOperations operations) =>
        JsonSerializer.SerializeToElement(new
        {
            readFiles = operations.ReadFiles,
            modifiedFiles = operations.ModifiedFiles,
        });

    private static JsonElement? CreateUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return null;
        }
        return JsonSerializer.SerializeToElement(new
        {
            input = usage.InputTokenCount ?? 0,
            output = usage.OutputTokenCount ?? 0,
            cacheRead = usage.CachedInputTokenCount ?? 0,
            reasoning = usage.ReasoningTokenCount ?? 0,
            totalTokens = usage.TotalTokenCount ?? 0,
        });
    }

    private static JsonElement? CombineUsage(
        (string Text, JsonElement? Usage) first,
        (string Text, JsonElement? Usage) second)
    {
        if (first.Usage is null && second.Usage is null)
        {
            return null;
        }
        var values = new[] { first.Usage, second.Usage }
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
        return JsonSerializer.SerializeToElement(new
        {
            input = values.Sum(value => ReadInt64(value, "input")),
            output = values.Sum(value => ReadInt64(value, "output")),
            cacheRead = values.Sum(value => ReadInt64(value, "cacheRead")),
            reasoning = values.Sum(value => ReadInt64(value, "reasoning")),
            totalTokens = values.Sum(value => ReadInt64(value, "totalTokens")),
        });
    }

    private static long ReadInt64(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : 0;
}
