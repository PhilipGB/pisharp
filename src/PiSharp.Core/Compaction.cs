using System.Text;
using System.Text.Json;

namespace PiSharp.Core;

/// <summary>Identifies why a compaction operation was requested.</summary>
public enum CompactionReason
{
    /// <summary>The user explicitly requested compaction.</summary>
    Manual,
    /// <summary>The estimated context crossed the configured threshold.</summary>
    Threshold,
    /// <summary>The provider reported that the context was too large.</summary>
    Overflow,
}

/// <summary>Conservative token settings used by Pi-native compaction.</summary>
public sealed record CompactionSettings
{
    /// <summary>Default amount of output/context headroom reserved for summarisation.</summary>
    public const int DefaultReserveTokens = 16_384;

    /// <summary>Default amount of recent history retained after compaction.</summary>
    public const int DefaultKeepRecentTokens = 20_000;

    /// <summary>Creates the default enabled settings.</summary>
    public CompactionSettings(
        bool enabled = true,
        int reserveTokens = DefaultReserveTokens,
        int keepRecentTokens = DefaultKeepRecentTokens)
    {
        if (reserveTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reserveTokens));
        }
        if (keepRecentTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keepRecentTokens));
        }

        Enabled = enabled;
        ReserveTokens = reserveTokens;
        KeepRecentTokens = keepRecentTokens;
    }

    /// <summary>Gets a value indicating whether automatic compaction is enabled.</summary>
    public bool Enabled { get; }

    /// <summary>Gets the tokens reserved for the summarisation request and response.</summary>
    public int ReserveTokens { get; }

    /// <summary>Gets the approximate number of recent tokens to retain.</summary>
    public int KeepRecentTokens { get; }
}

/// <summary>Token estimate for an active Pi transcript.</summary>
public sealed record ContextUsageEstimate(
    int Tokens,
    int UsageTokens,
    int TrailingTokens,
    int? LastUsageEntryIndex);

/// <summary>Result of selecting a safe point at which old context can be removed.</summary>
public sealed record CutPointResult(
    int FirstKeptEntryIndex,
    int TurnStartIndex,
    bool IsSplitTurn);

/// <summary>File-operation details carried into a persisted compaction entry.</summary>
public sealed record CompactionFileOperations(
    IReadOnlyList<string> ReadFiles,
    IReadOnlyList<string> ModifiedFiles);

/// <summary>Pure, deterministic input selected for one compaction request.</summary>
public sealed record CompactionPlan(
    string FirstKeptEntryId,
    IReadOnlyList<SessionEntry> MessagesToSummarize,
    IReadOnlyList<SessionEntry> TurnPrefixMessages,
    bool IsSplitTurn,
    int TokensBefore,
    string? PreviousSummary,
    CompactionFileOperations FileOperations,
    CompactionSettings Settings);

/// <summary>Entries and ancestry selected for a branch-summary request.</summary>
public sealed record BranchSummaryPlan(
    IReadOnlyList<SessionEntry> Entries,
    string? CommonAncestorId,
    int TotalTokens,
    CompactionFileOperations FileOperations);

/// <summary>Pure planning and estimation functions for Pi-native compaction.</summary>
public static class PiCompactionPlanner
{
    private const int EstimatedImageCharacters = 4_800;
    private const int CharactersPerEstimatedToken = 4;

    /// <summary>Returns whether the estimated context has crossed the compaction threshold.</summary>
    public static bool ShouldCompact(int contextTokens, int contextWindow, CompactionSettings settings) =>
        settings.Enabled && contextTokens > contextWindow - settings.ReserveTokens;

    /// <summary>Estimates context size using persisted message content and provider usage when available.</summary>
    public static ContextUsageEstimate EstimateContextTokens(IReadOnlyList<SessionEntry> entries)
    {
        var usageIndex = FindLastUsageIndex(entries);
        if (usageIndex < 0)
        {
            var estimated = entries.Sum(EstimateEntryTokens);
            return new ContextUsageEstimate(estimated, 0, estimated, null);
        }

        var usageTokens = GetUsageTokens(entries[usageIndex]);
        var trailing = entries
            .Skip(usageIndex + 1)
            .Sum(EstimateEntryTokens);
        return new ContextUsageEstimate(usageTokens + trailing, usageTokens, trailing, usageIndex);
    }

    /// <summary>Returns the context-visible entries after the latest compaction boundary.</summary>
    public static IReadOnlyList<SessionEntry> BuildContextEntries(IReadOnlyList<SessionEntry> path)
    {
        var compactionIndex = LastIndexOf<CompactionEntry>(path);
        if (compactionIndex < 0)
        {
            return path.ToArray();
        }

        var compaction = (CompactionEntry)path[compactionIndex];
        var firstKeptIndex = IndexOf(path, compaction.FirstKeptEntryId, 0, compactionIndex);
        var result = new List<SessionEntry> { compaction };
        if (firstKeptIndex >= 0)
        {
            result.AddRange(path.Skip(firstKeptIndex).Take(compactionIndex - firstKeptIndex));
        }
        result.AddRange(path.Skip(compactionIndex + 1));
        return result;
    }

    /// <summary>Builds a deterministic compaction plan for an active root-to-leaf path.</summary>
    public static CompactionPlan? PrepareCompaction(
        IReadOnlyList<SessionEntry> path,
        CompactionSettings settings)
    {
        if (path.Count == 0 || path[^1] is CompactionEntry)
        {
            return null;
        }

        var previousIndex = LastIndexOf<CompactionEntry>(path);
        var boundaryStart = 0;
        string? previousSummary = null;
        if (previousIndex >= 0)
        {
            var previous = (CompactionEntry)path[previousIndex];
            previousSummary = previous.Summary;
            boundaryStart = IndexOf(path, previous.FirstKeptEntryId, 0, previousIndex);
            if (boundaryStart < 0)
            {
                boundaryStart = previousIndex + 1;
            }
        }

        var contextEntries = BuildContextEntries(path);
        var tokensBefore = EstimateContextTokens(contextEntries).Tokens;
        var cutPoint = FindCutPoint(path, boundaryStart, path.Count, settings.KeepRecentTokens);
        var firstKept = path[cutPoint.FirstKeptEntryIndex];
        if (firstKept is null)
        {
            return null;
        }

        var historyEnd = cutPoint.IsSplitTurn
            ? cutPoint.TurnStartIndex
            : cutPoint.FirstKeptEntryIndex;
        // A previous CompactionEntry is summary metadata, not conversation content. It must not be
        // serialized into the new conversation text; the previous summary travels only through
        // the <previous-summary> section and the update-prompt instructions.
        var messages = path
            .Skip(boundaryStart)
            .Take(Math.Max(0, historyEnd - boundaryStart))
            .Where(entry => entry is not CompactionEntry && IsContextProducing(entry))
            .ToArray();
        var turnPrefix = cutPoint.IsSplitTurn
            ? path
                .Skip(cutPoint.TurnStartIndex)
                .Take(cutPoint.FirstKeptEntryIndex - cutPoint.TurnStartIndex)
                .Where(entry => entry is not CompactionEntry && IsContextProducing(entry))
                .ToArray()
            : [];

        if (messages.Length == 0 && turnPrefix.Length == 0)
        {
            return null;
        }

        var operations = CollectFileOperations(messages.Concat(turnPrefix), path, previousIndex);
        return new CompactionPlan(
            firstKept.Id,
            messages,
            turnPrefix,
            cutPoint.IsSplitTurn,
            tokensBefore,
            previousSummary,
            operations,
            settings);
    }

    /// <summary>Finds a safe context cut point without splitting tool-call/result pairs.</summary>
    public static CutPointResult FindCutPoint(
        IReadOnlyList<SessionEntry> entries,
        int startIndex,
        int endIndex,
        int keepRecentTokens)
    {
        var cutPoints = Enumerable.Range(startIndex, Math.Max(0, endIndex - startIndex))
            .Where(index => entries[index] is not CompactionEntry && IsCutPoint(entries[index]))
            .ToArray();
        if (cutPoints.Length == 0)
        {
            return new CutPointResult(startIndex, -1, false);
        }

        var accumulated = 0;
        var cutIndex = cutPoints[0];
        for (var index = endIndex - 1; index >= startIndex; index--)
        {
            accumulated += EstimateEntryTokens(entries[index]);
            if (accumulated < keepRecentTokens)
            {
                continue;
            }

            cutIndex = cutPoints.FirstOrDefault(candidate => candidate >= index, cutPoints[^1]);
            break;
        }

        while (cutIndex > startIndex && !IsContextProducing(entries[cutIndex - 1]) &&
               entries[cutIndex - 1] is not CompactionEntry)
        {
            cutIndex--;
        }

        var startsTurn = IsTurnStart(entries[cutIndex]);
        var turnStart = startsTurn ? -1 : FindTurnStartIndex(entries, cutIndex, startIndex);
        return new CutPointResult(cutIndex, turnStart, !startsTurn && turnStart >= 0);
    }

    /// <summary>Finds the user-like entry that starts the turn containing an entry.</summary>
    public static int FindTurnStartIndex(IReadOnlyList<SessionEntry> entries, int entryIndex, int startIndex)
    {
        for (var index = entryIndex; index >= startIndex; index--)
        {
            if (IsTurnStart(entries[index]))
            {
                return index;
            }
        }
        return -1;
    }

    /// <summary>
    /// Collects the abandoned path between two tree positions and prepares it for branch
    /// summarization. Entries are gathered from the old leaf back to the common ancestor
    /// (exclusive), without stopping at compaction boundaries.
    /// </summary>
    public static BranchSummaryPlan CollectBranchSummary(
        SessionDocument document,
        string? oldLeafId,
        string targetId,
        int tokenBudget = 0)
    {
        if (oldLeafId is null)
        {
            return new BranchSummaryPlan([], null, 0, new CompactionFileOperations([], []));
        }

        var oldPath = document.GetActiveEntryPath(oldLeafId);
        var targetPath = document.GetActiveEntryPath(targetId);
        var oldIds = oldPath.Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commonAncestor = targetPath.LastOrDefault(entry => oldIds.Contains(entry.Id));
        var commonId = commonAncestor?.Id;
        var collected = new List<SessionEntry>();
        var current = oldLeafId;
        while (current is not null && !string.Equals(current, commonId, StringComparison.OrdinalIgnoreCase))
        {
            var entry = document.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, current, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                break;
            }
            collected.Add(entry);
            current = entry.ParentId;
        }
        collected.Reverse();

        return PrepareBranchEntries(collected, commonId, tokenBudget);
    }

    /// <summary>
    /// Selects the most recent useful branch history within a token budget, matching Pi's
    /// prepareBranchEntries: ordinary tool results are excluded, file operations are carried from
    /// every pi-generated nested branch summary, and compaction/branch-summary entries near the
    /// budget boundary are kept when the accumulated total is below 90% of the budget.
    /// </summary>
    public static BranchSummaryPlan PrepareBranchEntries(
        IReadOnlyList<SessionEntry> entries,
        string? commonAncestorId = null,
        int tokenBudget = 0)
    {
        var reads = new HashSet<string>(StringComparer.Ordinal);
        var modified = new HashSet<string>(StringComparer.Ordinal);

        // Pi trusts only pi-generated branch summary details (fromHook is false) for cumulative file tracking.
        foreach (var entry in entries.OfType<BranchSummaryEntry>().Where(entry => !entry.FromHook))
        {
            AddDetails(entry.Details, reads, modified);
        }

        var selected = new List<SessionEntry>();
        var total = 0;
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            var entry = entries[index];
            if (!IsBranchSummarySource(entry))
            {
                continue;
            }

            // Extract tool-call file operations before the budget check so the entry that breaks the
            // walk still contributes, matching Pi's walk order.
            ExtractFileOperations(entry, reads, modified);
            var tokens = EstimateEntryTokens(entry);
            if (tokenBudget > 0 && total + tokens > tokenBudget)
            {
                // Summary entries are important context: fit them when there is still headroom.
                if (entry is CompactionEntry or BranchSummaryEntry && total < tokenBudget * 0.9)
                {
                    selected.Insert(0, entry);
                    total += tokens;
                }

                break;
            }

            selected.Insert(0, entry);
            total += tokens;
        }

        return new BranchSummaryPlan(selected, commonAncestorId, total, ComputeFileOperations(reads, modified));
    }

    /// <summary>Estimates the durable content size of an entry.</summary>
    public static int EstimateEntryTokens(SessionEntry entry) =>
        EstimateMessageTokens(entry);

    /// <summary>Formats an operation list in the XML form used by Pi summaries.</summary>
    public static string FormatFileOperations(CompactionFileOperations operations)
    {
        var sections = new List<string>();
        if (operations.ReadFiles.Count > 0)
        {
            sections.Add($"<read-files>\n{string.Join('\n', operations.ReadFiles)}\n</read-files>");
        }
        if (operations.ModifiedFiles.Count > 0)
        {
            sections.Add($"<modified-files>\n{string.Join('\n', operations.ModifiedFiles)}\n</modified-files>");
        }
        return sections.Count == 0 ? string.Empty : $"\n\n{string.Join("\n\n", sections)}";
    }

    /// <summary>Returns a stable text representation suitable for a summary prompt.</summary>
    public static string SerializeForSummary(IEnumerable<SessionEntry> entries)
    {
        var parts = entries
            .Select(SerializeEntry)
            .Where(text => !string.IsNullOrWhiteSpace(text));
        return string.Join("\n\n", parts);
    }

    private static CompactionFileOperations CollectFileOperations(
        IEnumerable<SessionEntry> selected,
        IReadOnlyList<SessionEntry> path,
        int previousCompactionIndex)
    {
        var reads = new HashSet<string>(StringComparer.Ordinal);
        var modified = new HashSet<string>(StringComparer.Ordinal);

        // Carry file operations from the previous pi-generated compaction so incremental
        // checkpoints keep cumulative read/modified tracking.
        if (previousCompactionIndex >= 0 && previousCompactionIndex < path.Count &&
            path[previousCompactionIndex] is CompactionEntry previous && !previous.FromHook)
        {
            AddDetails(previous.Details, reads, modified);
        }

        foreach (var entry in selected)
        {
            ExtractFileOperations(entry, reads, modified);
        }

        return ComputeFileOperations(reads, modified);
    }

    private static CompactionFileOperations ComputeFileOperations(ISet<string> reads, ISet<string> modified)
    {
        var readOnly = reads.Where(path => !modified.Contains(path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var changed = modified.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        return new CompactionFileOperations(readOnly, changed);
    }

    private static void AddDetails(JsonElement? details, ISet<string> reads, ISet<string> modified)
    {
        if (details is not { ValueKind: JsonValueKind.Object })
        {
            return;
        }
        AddStringArray(details.Value, "readFiles", reads);
        AddStringArray(details.Value, "modifiedFiles", modified);
    }

    private static void AddStringArray(JsonElement details, string property, ISet<string> destination)
    {
        if (!details.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                destination.Add(value.GetString()!);
            }
        }
    }

    private static void ExtractFileOperations(SessionEntry entry, ISet<string> reads, ISet<string> modified)
    {
        if (entry is not MessageEntry { Message: var message } || !IsRole(message, "assistant") ||
            !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (!block.TryGetProperty("type", out var type) || type.GetString() != "toolCall" ||
                !block.TryGetProperty("name", out var name) || !block.TryGetProperty("arguments", out var arguments) ||
                arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("path", out var path) ||
                path.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var filePath = path.GetString();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }
            switch (name.GetString())
            {
                case "read":
                    reads.Add(filePath);
                    break;
                case "write":
                case "edit":
                    modified.Add(filePath);
                    break;
            }
        }
    }

    private static int FindLastUsageIndex(IReadOnlyList<SessionEntry> entries)
    {
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            if (entries[index] is not MessageEntry { Message: var message } || !IsRole(message, "assistant"))
            {
                continue;
            }

            // Aborted and error responses carry no usable context accounting; all-zero usage is
            // likewise ignored so the estimate anchors to the most recent valid response.
            var stopReason = message.TryGetProperty("stopReason", out var stop) ? stop.GetString() : null;
            if (stopReason is "aborted" or "error")
            {
                continue;
            }

            if (GetUsageTokens(entries[index]) > 0)
            {
                return index;
            }
        }
        return -1;
    }

    private static int GetUsageTokens(SessionEntry entry)
    {
        if (entry is not MessageEntry { Message: var message } ||
            !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var total = ReadInt(usage, "totalTokens");
        return total > 0
            ? total
            : ReadInt(usage, "input") + ReadInt(usage, "output") +
              ReadInt(usage, "cacheRead") + ReadInt(usage, "cacheWrite");
    }

    private static int EstimateMessageTokens(SessionEntry entry)
    {
        return entry switch
        {
            MessageEntry message => EstimateJsonMessage(message.Message),
            CustomMessageEntry custom => EstimateCharactersFromLength(ContentChars(custom.Content)),
            CompactionEntry compaction => EstimateCharactersFromLength(compaction.Summary.Length),
            BranchSummaryEntry branch => EstimateCharactersFromLength(branch.Summary.Length),
            // Canonical Pi bashExecution accounting: command + output.
            BashExecutionEntry bash => EstimateCharactersFromLength(bash.Command.Length + bash.Output.Length),
            _ => 0,
        };
    }

    private static int EstimateJsonMessage(JsonElement message)
    {
        if (!message.TryGetProperty("role", out var roleValue))
        {
            return 0;
        }

        var chars = roleValue.GetString() switch
        {
            "user" or "custom" or "toolResult" => TextAndImageContentChars(message),
            "assistant" => AssistantMessageChars(message),
            "bashExecution" => BashExecutionMessageChars(message),
            _ => 0,
        };
        return EstimateCharactersFromLength(chars);
    }

    private static int TextAndImageContentChars(JsonElement message) =>
        message.TryGetProperty("content", out var content)
            ? ContentChars(content)
            : ContentChars(message);

    private static int ContentChars(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()?.Length ?? 0;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var chars = 0;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object ||
                !block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var blockType = type.GetString();
            if (blockType == "text" && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                chars += text.GetString()?.Length ?? 0;
            }
            else if (blockType == "image")
            {
                // Pi approximates every image as a fixed number of characters before the chars/4 heuristic.
                chars += EstimatedImageCharacters;
            }
        }
        return chars;
    }

    private static int AssistantMessageChars(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var chars = 0;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object ||
                !block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var blockType = type.GetString();
            if (blockType == "text")
            {
                chars += ReadString(block, "text").Length;
            }
            else if (blockType == "thinking")
            {
                chars += ReadString(block, "thinking").Length;
            }
            else if (blockType == "toolCall")
            {
                // Tool calls count as name + serialized arguments.
                chars += ReadString(block, "name").Length;
                if (block.TryGetProperty("arguments", out var args) &&
                    args.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    chars += args.GetRawText().Length;
                }
            }
        }
        return chars;
    }

    private static int BashExecutionMessageChars(JsonElement message)
    {
        var chars = 0;
        if (message.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String)
        {
            chars += command.GetString()?.Length ?? 0;
        }
        if (message.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.String)
        {
            chars += output.GetString()?.Length ?? 0;
        }

        // Persisted PiSharp bash messages may carry only the rendered content text.
        return chars > 0 ? chars : TextAndImageContentChars(message);
    }

    private static int EstimateCharactersFromLength(int length) =>
        (int)Math.Ceiling(length / (double)CharactersPerEstimatedToken);

    private static bool IsContextProducing(SessionEntry entry) =>
        entry switch
        {
            MessageEntry message => IsRole(message.Message, "user") || IsRole(message.Message, "assistant") ||
                                     IsRole(message.Message, "toolResult") || IsRole(message.Message, "bashExecution") ||
                                     IsRole(message.Message, "custom"),
            CustomMessageEntry => true,
            CompactionEntry or BranchSummaryEntry => true,
            _ => false,
        };

    private static bool IsCutPoint(SessionEntry entry) =>
        entry switch
        {
            MessageEntry message => IsRole(message.Message, "user") || IsRole(message.Message, "assistant") ||
                                     IsRole(message.Message, "bashExecution") || IsRole(message.Message, "custom"),
            CustomMessageEntry or BranchSummaryEntry => true,
            _ => false,
        };

    private static bool IsBranchSummarySource(SessionEntry entry) =>
        entry switch
        {
            // Ordinary tool results are never fed into branch summarization; their context lives
            // in the assistant tool call.
            MessageEntry message => IsRole(message.Message, "user") || IsRole(message.Message, "assistant") ||
                                     IsRole(message.Message, "bashExecution") || IsRole(message.Message, "custom"),
            CustomMessageEntry or CompactionEntry or BranchSummaryEntry => true,
            _ => false,
        };

    private static bool IsTurnStart(SessionEntry entry) =>
        entry switch
        {
            MessageEntry message => IsRole(message.Message, "user") || IsRole(message.Message, "bashExecution") ||
                                     IsRole(message.Message, "custom"),
            CustomMessageEntry or BranchSummaryEntry => true,
            _ => false,
        };

    private static bool IsRole(JsonElement message, string role) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("role", out var value) &&
        string.Equals(value.GetString(), role, StringComparison.Ordinal);

    private static string SerializeEntry(SessionEntry entry) =>
        entry switch
        {
            MessageEntry message => SerializeMessage(message.Message),
            CustomMessageEntry custom => $"[User]: {ReadContentText(custom.Content)}",
            // Exact Pi summarization prefixes so incremental checkpoints see the same shape.
            CompactionEntry compaction => $"[User]: The conversation history before this point was compacted into the following summary:\n\n<summary>\n{compaction.Summary}\n</summary>",
            BranchSummaryEntry branch => $"[User]: The following is a summary of a branch that this conversation came back from:\n\n<summary>\n{branch.Summary}\n</summary>",
            BashExecutionEntry bash => $"[User]: Ran `{bash.Command}`\n{(string.IsNullOrEmpty(bash.Output) ? "(no output)" : bash.Output)}",
            _ => string.Empty,
        };

    private static string SerializeMessage(JsonElement message)
    {
        var role = message.TryGetProperty("role", out var roleValue) ? roleValue.GetString() : null;
        var text = ReadContentText(message.TryGetProperty("content", out var content) ? content : default);
        return role switch
        {
            "user" => $"[User]: {text}",
            "toolResult" => $"[Tool result]: {Truncate(text, 2_000)}",
            "assistant" => SerializeAssistant(message),
            "bashExecution" => $"[User]: {text}",
            "custom" => $"[User]: {text}",
            _ => string.Empty,
        };
    }

    private static string SerializeAssistant(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        // Pi serializes one section per block kind: thinking, text, and a single
        // "[Assistant tool calls]" line with every call separated by "; ".
        var thinkingParts = new List<string>();
        var textParts = new List<string>();
        var toolCalls = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            var type = block.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            if (type == "thinking")
            {
                thinkingParts.Add(ReadString(block, "thinking"));
            }
            else if (type == "text")
            {
                textParts.Add(ReadString(block, "text"));
            }
            else if (type == "toolCall")
            {
                toolCalls.Add($"{ReadString(block, "name")}({FormatToolCallArguments(block)})");
            }
        }

        var sections = new List<string>();
        if (thinkingParts.Count > 0)
        {
            sections.Add($"[Assistant thinking]: {string.Join("\n", thinkingParts)}");
        }
        if (textParts.Count > 0)
        {
            sections.Add($"[Assistant]: {string.Join(string.Empty, textParts)}");
        }
        if (toolCalls.Count > 0)
        {
            sections.Add($"[Assistant tool calls]: {string.Join("; ", toolCalls)}");
        }
        return string.Join("\n", sections);
    }

    private static string FormatToolCallArguments(JsonElement block)
    {
        if (!block.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return string.Join(
            ", ",
            args.EnumerateObject().Select(property => $"{property.Name}={property.Value.GetRawText()}"));
    }

    private static string ReadContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }
        if (content.ValueKind != JsonValueKind.Array)
        {
            return content.ValueKind == JsonValueKind.Undefined ? string.Empty : content.ToString();
        }
        var builder = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                builder.Append(text.GetString());
            }
            else if (block.TryGetProperty("thinking", out var thinking) && thinking.ValueKind == JsonValueKind.String)
            {
                builder.Append(thinking.GetString());
            }
        }
        return builder.ToString();
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static string Truncate(string value, int maxCharacters) =>
        value.Length <= maxCharacters
            ? value
            : $"{value[..maxCharacters]}\n\n[... {value.Length - maxCharacters} more characters truncated]";

    private static int LastIndexOf<T>(IReadOnlyList<SessionEntry> entries) where T : SessionEntry
    {
        for (var index = entries.Count - 1; index >= 0; index--)
        {
            if (entries[index] is T)
            {
                return index;
            }
        }
        return -1;
    }

    private static int IndexOf(IReadOnlyList<SessionEntry> entries, string id, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (string.Equals(entries[index].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }
}
