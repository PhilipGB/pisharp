namespace PiSharp.Runtime.Sessions;

/// <summary>Preserves physical billing per summary request while presenting one compaction total.</summary>
internal static class CompactionUsageAccounting
{
    public static IReadOnlyList<UsageRecord> Records(PiAgent.CompactionSummary summary, string fallbackModel,
        ModelPricing? fallbackPricing) => summary.PhysicalUsage is { Count: > 0 } physical ? physical :
        summary.Usage is { } usage ? [UsageRecord.Create(fallbackModel, "compaction", usage, fallbackPricing)] : [];

    public static UsageRecord? Aggregate(IReadOnlyList<UsageRecord> records)
    {
        if (records.Count == 0) return null;
        if (records.Count == 1) return records[0];
        return new(records.Select(record => record.Model).Distinct(StringComparer.Ordinal).Count() == 1
                ? records[0].Model : "multiple", "compaction",
            records.Sum(record => record.InputTokens), records.Sum(record => record.OutputTokens),
            records.Sum(record => record.CachedInputTokens), records.Sum(record => record.ReasoningTokens),
            records.Sum(record => record.TotalTokens), records.All(record => record.Cost is not null)
                ? records.Sum(record => record.Cost) : null, records.Sum(record => record.CachedWriteTokens))
        {
            InputCost = records.All(record => record.InputCost is not null) ? records.Sum(record => record.InputCost) : null,
            OutputCost = records.All(record => record.OutputCost is not null) ? records.Sum(record => record.OutputCost) : null,
            CachedInputCost = records.All(record => record.CachedInputCost is not null) ? records.Sum(record => record.CachedInputCost) : null,
            CachedWriteCost = records.All(record => record.CachedWriteCost is not null) ? records.Sum(record => record.CachedWriteCost) : null
        };
    }
}
