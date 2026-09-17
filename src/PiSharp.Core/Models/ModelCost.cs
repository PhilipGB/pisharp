namespace PiSharp.Core.Models;

/// <summary>
/// Per-million-token price rates (pinned pi-ai: ModelCostRates). All rates are USD per
/// million tokens; cost calculations divide by 1,000,000.
/// </summary>
public record ModelCostRates
{
    /// <summary>Input (prompt) price per million tokens.</summary>
    public required double Input { get; init; }

    /// <summary>Output price per million tokens.</summary>
    public required double Output { get; init; }

    /// <summary>Cache read price per million tokens.</summary>
    public required double CacheRead { get; init; }

    /// <summary>Cache write price per million tokens.</summary>
    public required double CacheWrite { get; init; }
}

/// <summary>
/// A request-wide pricing tier (pinned pi-ai: ModelCostTier). The highest tier whose
/// input-token threshold the request exceeds applies to the full request.
/// </summary>
public sealed record ModelCostTier : ModelCostRates
{
    /// <summary>Use this tier when total input usage exceeds this many tokens.</summary>
    public required double InputTokensAbove { get; init; }
}

/// <summary>Model pricing: base rates plus optional tiers (pinned pi-ai: ModelCost).</summary>
public sealed record ModelCost : ModelCostRates
{
    /// <summary>Optional input-token pricing tiers.</summary>
    public IReadOnlyList<ModelCostTier>? Tiers { get; init; }
}
