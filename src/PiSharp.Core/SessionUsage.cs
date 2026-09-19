using System.Globalization;
using System.Text.Json;

namespace PiSharp.Core;

/// <summary>
/// Session usage aggregation, per-model cost breakdown, and cache-waste analysis
/// (pinned usage-totals.ts + cache-stats.ts + AgentSession.getSessionStats). All scans
/// iterate every session entry — including compacted-away history — so token/cost totals
/// reflect what was actually billed across the session.
/// </summary>
public static class SessionUsage
{
    /// <summary>Pinned NOISE_FLOOR_TOKENS: per-turn misses at or below this are breakpoint granularity noise.</summary>
    private const long NoiseFloorTokens = 1_024;

    /// <summary>Key for usage without model attribution (pinned getUsageCostBreakdown).</summary>
    private const string ToolsSummariesKey = "Tools/summaries";

    /// <summary>Usage totals across a session (pinned UsageTotals).</summary>
    public sealed record Totals(long Input, long Output, long CacheRead, long CacheWrite, double Cost)
    {
        /// <summary>Input + output + cacheRead + cacheWrite (pinned tokens.total).</summary>
        public long TotalTokens => Input + Output + CacheRead + CacheWrite;

        /// <summary>Full prompt volume: input + cacheRead + cacheWrite (pinned /session "Input").</summary>
        public long PromptTokens => Input + CacheRead + CacheWrite;
    }

    /// <summary>One cost-breakdown row: model key (or "Tools/summaries") with cost and tokens.</summary>
    public sealed record BreakdownEntry(string Key, double Cost, long Tokens)
    {
        /// <summary>Pinned formatTokens: thousands separators, invariant culture.</summary>
        public string FormatTokens() => Tokens.ToString("N0", CultureInfo.InvariantCulture);
    }

    /// <summary>Cumulative cache waste (pinned CacheWasteTotals).</summary>
    public sealed record CacheWaste(long MissedTokens, double MissedCost, int MissCount)
    {
        /// <summary>Pinned miss label: "1 miss" vs "N misses".</summary>
        public string MissLabel => MissCount == 1 ? "1 miss" : $"{MissCount} misses";
    }

    /// <summary>Usage read from one message/entry (pinned Usage shape, cost split kept for the waste scan).</summary>
    internal sealed record MessageUsage(
        long Input,
        long Output,
        long CacheRead,
        long CacheWrite,
        double CostInput,
        double CostCacheRead,
        double CostCacheWrite,
        double CostTotal)
    {
        public long PromptTokens => Input + CacheRead + CacheWrite;
        public long CacheActivity => CacheRead + CacheWrite;
    }

    /// <summary>Pinned ModelPriceSource: the cache-read rate ($/million tokens) for a model.</summary>
    public interface ICacheRateSource
    {
        /// <summary>Cache-read rate for the model; 0 when the model is unknown (pinned `?? 0`).</summary>
        double GetCacheReadRate(string provider, string model);
    }

    /// <summary>
    /// Pinned getSessionStats usage loop: assistant, tool-result, compaction, and
    /// branch-summary usage all count toward the session totals.
    /// </summary>
    public static Totals Aggregate(IReadOnlyList<SessionEntry> entries)
    {
        var input = 0L;
        var output = 0L;
        var cacheRead = 0L;
        var cacheWrite = 0L;
        var cost = 0.0;

        foreach (var entry in entries)
        {
            var entryUsage = entry switch
            {
                CompactionEntry compaction => ReadEntryUsage(compaction.Usage),
                BranchSummaryEntry summary => ReadEntryUsage(summary.Usage),
                _ => null,
            };
            if (entryUsage is { } usage)
            {
                input += usage.Input; output += usage.Output;
                cacheRead += usage.CacheRead; cacheWrite += usage.CacheWrite; cost += usage.CostTotal;
            }

            if (entry is not MessageEntry message || message.Message.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var messageUsage = ReadMessageUsage(message.Message);
            if (messageUsage is not null)
            {
                input += messageUsage.Input;
                output += messageUsage.Output;
                cacheRead += messageUsage.CacheRead;
                cacheWrite += messageUsage.CacheWrite;
                cost += messageUsage.CostTotal;
            }
        }

        return new Totals(input, output, cacheRead, cacheWrite, cost);
    }

    /// <summary>
    /// Pinned getUsageCostBreakdown: assistant usage is keyed by
    /// <c>provider/responseModel ?? model</c>; unattributable usage (tool results,
    /// summaries) groups under "Tools/summaries" so the breakdown reconciles with the
    /// session total. Rows with no cost and no tokens are dropped, sorted by cost desc.
    /// </summary>
    public static IReadOnlyList<BreakdownEntry> CostBreakdown(IReadOnlyList<SessionEntry> entries)
    {
        var totals = new Dictionary<string, (long Input, long Output, long CacheRead, long CacheWrite, double Cost)>(
            StringComparer.Ordinal);

        void Add(string key, MessageUsage usage)
        {
            var current = totals.GetValueOrDefault(key);
            totals[key] = (
                current.Input + usage.Input,
                current.Output + usage.Output,
                current.CacheRead + usage.CacheRead,
                current.CacheWrite + usage.CacheWrite,
                current.Cost + usage.CostTotal);
        }

        foreach (var entry in entries)
        {
            if (entry is CompactionEntry or BranchSummaryEntry)
            {
                var entryUsage = entry switch
                {
                    CompactionEntry compaction => ReadEntryUsage(compaction.Usage),
                    _ => ReadEntryUsage(((BranchSummaryEntry)entry).Usage),
                };
                if (entryUsage is not null)
                {
                    Add(ToolsSummariesKey, entryUsage);
                }

                continue;
            }

            if (entry is not MessageEntry message || message.Message.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var usage = ReadMessageUsage(message.Message);
            if (usage is null)
            {
                continue;
            }

            var role = GetString(message.Message, "role");
            if (role == "assistant")
            {
                var provider = GetString(message.Message, "provider");
                var model = GetString(message.Message, "model");
                // Pinned: the concrete response model (e.g. OpenRouter auto) wins.
                var concrete = GetString(message.Message, "responseModel");
                if (provider.Length > 0 && model.Length > 0)
                {
                    Add($"{provider}/{(concrete.Length > 0 ? concrete : model)}", usage);
                }
            }
            else if (role == "toolResult")
            {
                Add(ToolsSummariesKey, usage);
            }
        }

        return totals
            .Select(kvp => new BreakdownEntry(kvp.Key, kvp.Value.Cost,
                kvp.Value.Input + kvp.Value.Output + kvp.Value.CacheRead + kvp.Value.CacheWrite))
            .Where(row => row.Cost > 0 || row.Tokens > 0)
            .OrderByDescending(row => row.Cost)
            .ToList();
    }

    private sealed record PreviousRequest(long PromptTokens, string ModelKey, long TimestampMs, bool ReportedCache);

    private sealed record CacheMiss(long MissedTokens, double MissedCost);

    /// <summary>
    /// Pinned computeCacheWaste: prompt tokens that should have been cache reads (they were
    /// in the previous turn's prompt) but were re-billed. Compaction/branch-summary entries
    /// reset the baseline (new content, not re-billed); model switches are NOT exempt.
    /// </summary>
    public static CacheWaste ComputeCacheWaste(IReadOnlyList<SessionEntry> entries, ICacheRateSource? rates)
    {
        PreviousRequest? previous = null;
        var missedTokens = 0L;
        var missedCost = 0.0;
        var missCount = 0;

        foreach (var entry in entries)
        {
            if (entry is CompactionEntry or BranchSummaryEntry)
            {
                previous = null;
                continue;
            }

            if (entry is not MessageEntry message ||
                message.Message.ValueKind != JsonValueKind.Object ||
                GetString(message.Message, "role") != "assistant")
            {
                continue;
            }

            var miss = DetectMiss(previous, message.Message, rates);
            if (miss is not null)
            {
                missedTokens += miss.MissedTokens;
                missedCost += miss.MissedCost;
                missCount++;
            }

            previous = AsPreviousRequest(message.Message, previous?.ReportedCache ?? false) ?? previous;
        }

        return new CacheWaste(missedTokens, missedCost, missCount);
    }

    /// <summary>
    /// Pinned detectMiss: a zero-cache turn counts only when cache activity was reported
    /// before (distinguishing a total miss on a cache-read-only provider from a provider
    /// that never reports caching); misses at or below the noise floor are dropped.
    /// </summary>
    private static CacheMiss? DetectMiss(
        PreviousRequest? previous,
        JsonElement message,
        ICacheRateSource? rates)
    {
        if (previous is null || ReadMessageUsage(message) is not { } usage)
        {
            return null;
        }

        if (usage.PromptTokens <= 0 || (usage.CacheActivity == 0 && !previous.ReportedCache))
        {
            return null;
        }

        var missedTokens = Math.Min(previous.PromptTokens, usage.PromptTokens) - usage.CacheRead;
        if (missedTokens <= NoiseFloorTokens)
        {
            return null;
        }

        // Pinned: missed tokens land in the input or cacheWrite buckets, so the paid rate
        // comes from this message's own cost breakdown; the read rate falls back to the
        // catalogue when the message reported no cache reads.
        var paidTokens = usage.Input + usage.CacheWrite;
        var paidPerToken = paidTokens > 0 ? (usage.CostInput + usage.CostCacheWrite) / paidTokens : 0.0;
        var readPerToken = usage.CacheRead > 0
            ? usage.CostCacheRead / usage.CacheRead
            : (rates?.GetCacheReadRate(GetString(message, "provider"), GetString(message, "model")) ?? 0.0)
                / 1_000_000;

        return new CacheMiss(missedTokens, missedTokens * Math.Max(0.0, paidPerToken - readPerToken));
    }

    private static PreviousRequest? AsPreviousRequest(JsonElement message, bool stickyReportedCache)
    {
        if (ReadMessageUsage(message) is not { } usage || usage.PromptTokens <= 0)
        {
            return null;
        }

        return new PreviousRequest(
            usage.PromptTokens,
            $"{GetString(message, "provider")}/{GetString(message, "model")}",
            GetTimestampMs(message),
            stickyReportedCache || usage.CacheActivity > 0);
    }

    /// <summary>
    /// Reads the pinned usage shape off a raw message element
    /// (input/output/cacheRead/cacheWrite + cost). Returns null when absent.
    /// </summary>
    internal static MessageUsage? ReadMessageUsage(JsonElement message)
    {
        if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var cost = usage.TryGetProperty("cost", out var costValue) && costValue.ValueKind == JsonValueKind.Object
            ? costValue
            : default;
        return new MessageUsage(
            ReadLong(usage, "input"),
            ReadLong(usage, "output"),
            ReadLong(usage, "cacheRead"),
            ReadLong(usage, "cacheWrite"),
            ReadDouble(cost, "input"),
            ReadDouble(cost, "cacheRead"),
            ReadDouble(cost, "cacheWrite"),
            ReadDouble(cost, "total"));
    }

    /// <summary>Compaction/branch-summary entries store the same usage shape (nullable).</summary>
    private static MessageUsage? ReadEntryUsage(JsonElement? usage) =>
        usage is { ValueKind: JsonValueKind.Object } element ? ReadMessageUsageElement(element) : null;

    private static MessageUsage ReadMessageUsageElement(JsonElement usage) => new(
        ReadLong(usage, "input"),
        ReadLong(usage, "output"),
        ReadLong(usage, "cacheRead"),
        ReadLong(usage, "cacheWrite"),
        ReadCost(usage, "input"),
        ReadCost(usage, "cacheRead"),
        ReadCost(usage, "cacheWrite"),
        ReadCost(usage, "total"));

    private static string GetString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long GetTimestampMs(JsonElement message) =>
        message.TryGetProperty("timestamp", out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0L;

    private static long ReadLong(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0L;

    private static double ReadDouble(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0.0;

    private static double ReadCost(JsonElement usage, string property) =>
        usage.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object &&
        cost.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0.0;
}
