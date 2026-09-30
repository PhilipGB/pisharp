using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Runtime.Codemode;

internal static class CodemodeUsageAccounting
{
    public static (UsageDetails? Usage, decimal? Cost) Aggregate(IReadOnlyList<UsageRecord> records)
    {
        if (records.Count == 0) return (null, null);
        return (new UsageDetails
        {
            InputTokenCount = records.Sum(record => record.InputTokens),
            OutputTokenCount = records.Sum(record => record.OutputTokens),
            TotalTokenCount = records.Sum(record => record.TotalTokens),
            CachedInputTokenCount = records.Sum(record => record.CachedInputTokens),
            ReasoningTokenCount = records.Sum(record => record.ReasoningTokens),
            AdditionalCounts = new() { ["cacheWriteTokens"] = records.Sum(record => record.CachedWriteTokens) }
        }, records.Any(record => record.Cost.HasValue) ? records.Sum(record => record.Cost ?? 0) : null);
    }
}
