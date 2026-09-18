namespace PiSharp.Core.Models;

/// <summary>
/// Token usage for one provider response (pinned pi-ai: Usage). Costs are USD;
/// <see cref="Cost"/> is calculated from model catalogue prices, never fabricated.
/// </summary>
public sealed record ModelUsage
{
    /// <summary>Non-cache input tokens.</summary>
    public long Input { get; init; }

    /// <summary>Output tokens (includes reasoning tokens when the provider splits them).</summary>
    public long Output { get; init; }

    /// <summary>Prompt-cache read tokens.</summary>
    public long CacheRead { get; init; }

    /// <summary>Prompt-cache write tokens.</summary>
    public long CacheWrite { get; init; }

    /// <summary>Subset of <see cref="CacheWrite"/> written with 1h retention (Anthropic only).</summary>
    public long? CacheWrite1h { get; init; }

    /// <summary>Reasoning/thinking tokens when the provider reports them (subset of output).</summary>
    public long? Reasoning { get; init; }

    /// <summary>Total tokens for the response (input + output + cache).</summary>
    public long TotalTokens { get; init; }

    /// <summary>Cost breakdown in USD.</summary>
    public ModelUsageCost Cost { get; init; } = new();

    /// <summary>Zeroed usage (used when a provider reports nothing).</summary>
    public static ModelUsage Empty { get; } = new();

    /// <summary>
    /// Maps raw openai-completions counts to pinned Usage semantics (pinned parseChunkUsage):
    /// cached and cache-write counts are carved out of the prompt total (clamped at zero),
    /// and totalTokens is recomputed as input + output + cacheRead + cacheWrite rather than
    /// trusting the provider-reported total.
    /// </summary>
    public static ModelUsage FromOpenAiCounts(
        long promptTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        long outputTokens,
        long? reasoningTokens)
    {
        var input = Math.Max(0, promptTokens - cacheReadTokens - cacheWriteTokens);
        return new ModelUsage
        {
            Input = input,
            Output = outputTokens,
            CacheRead = cacheReadTokens,
            CacheWrite = cacheWriteTokens,
            Reasoning = reasoningTokens,
            TotalTokens = input + outputTokens + cacheReadTokens + cacheWriteTokens,
        };
    }
}

/// <summary>USD cost breakdown for a provider response.</summary>
public sealed record ModelUsageCost
{
    public double Input { get; init; }
    public double Output { get; init; }
    public double CacheRead { get; init; }
    public double CacheWrite { get; init; }
    public double Total { get; init; }
}

/// <summary>
/// Cost calculation ported from pinned pi-ai models.ts calculateCost. The highest
/// matching input tier rates the full request; Anthropic 1h cache writes bill at 2x base
/// input price.
/// </summary>
public static class ModelCostCalculator
{
    private const double TokensPerMillion = 1_000_000;

    /// <summary>Returns the usage with its cost fields filled from the model's catalogue prices.</summary>
    public static ModelUsage WithCost(ModelUsage usage, ModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(model);

        var inputTokens = usage.Input + usage.CacheRead + usage.CacheWrite;
        ModelCostRates rates = model.Cost;
        var matchedThreshold = -1.0;
        foreach (var tier in model.Cost.Tiers ?? Array.Empty<ModelCostTier>())
        {
            if (inputTokens > tier.InputTokensAbove && tier.InputTokensAbove > matchedThreshold)
            {
                rates = tier;
                matchedThreshold = tier.InputTokensAbove;
            }
        }

        var longWrite = usage.CacheWrite1h ?? 0;
        var shortWrite = usage.CacheWrite - longWrite;
        var cost = new ModelUsageCost
        {
            Input = rates.Input / TokensPerMillion * usage.Input,
            Output = rates.Output / TokensPerMillion * usage.Output,
            CacheRead = rates.CacheRead / TokensPerMillion * usage.CacheRead,
            CacheWrite = (rates.CacheWrite * shortWrite + rates.Input * 2 * longWrite) / TokensPerMillion,
        };
        cost = cost with { Total = cost.Input + cost.Output + cost.CacheRead + cost.CacheWrite };
        return usage with { Cost = cost };
    }
}
