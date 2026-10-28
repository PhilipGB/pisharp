using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Runtime.Codemode;

internal static class CodemodeUsageAccounting
{
    public static (UsageDetails? Usage, decimal? Cost) Aggregate(IReadOnlyList<UsageRecord> records)
    {
        if (records.Count == 0) return (null, null);
        var cacheWrite = UsageTokenAggregation.SumKnown(records.Select(record => record.CachedWriteTokens));
        return (new UsageDetails
        {
            InputTokenCount = UsageTokenAggregation.SumKnown(records.Select(record => record.InputTokenCountIncludingCached)),
            OutputTokenCount = UsageTokenAggregation.SumKnown(records.Select(record => record.OutputTokens)),
            TotalTokenCount = UsageTokenAggregation.SumKnown(records.Select(record => record.TotalTokens)),
            CachedInputTokenCount = UsageTokenAggregation.SumKnown(records.Select(record => record.CachedInputTokens)),
            ReasoningTokenCount = UsageTokenAggregation.SumKnown(records.Select(record => record.ReasoningTokens)),
            AdditionalCounts = cacheWrite is long knownCacheWrite
                ? new() { ["cacheWriteTokens"] = knownCacheWrite } : null
        }, records.All(record => record.Cost.HasValue) ? records.Sum(record => record.Cost!.Value) : null);
    }
}
