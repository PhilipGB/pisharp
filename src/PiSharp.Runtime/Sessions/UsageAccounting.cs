using Microsoft.Extensions.AI;
using System.Text.Json.Serialization;

namespace PiSharp.Runtime.Sessions;

/// <summary>Model prices in US dollars per one million tokens. Unknown prices stay unknown.</summary>
public sealed record ModelPricing(decimal Input, decimal Output, decimal? CachedInput = null,
    IReadOnlyList<ModelPricingTier>? Tiers = null, decimal? CachedWrite = null)
{
    public ModelPricing ForInput(long inputTokens) => Tiers?.Where(tier => inputTokens > tier.InputTokensAbove)
        .OrderByDescending(tier => tier.InputTokensAbove)
        .Select(tier => new ModelPricing(tier.Input, tier.Output, tier.CachedInput, CachedWrite: tier.CachedWrite)).FirstOrDefault() ?? this;

    public static ModelPricing? FromEnvironment(Func<string, string?> get)
    {
        var input = get("PISHARP_INPUT_COST_PER_MILLION");
        var output = get("PISHARP_OUTPUT_COST_PER_MILLION");
        var cached = get("PISHARP_CACHED_INPUT_COST_PER_MILLION");
        if (input is null && output is null && cached is null) return null;
        if (!TryPrice(input, out var inputPrice) || !TryPrice(output, out var outputPrice) ||
            (cached is not null && !TryPrice(cached, out _)))
            throw new ArgumentException("PISHARP token prices must be nonnegative decimal US-dollar amounts per million tokens; input and output prices are both required.");
        return new ModelPricing(inputPrice, outputPrice, cached is null ? null : decimal.Parse(cached,
            System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool TryPrice(string? value, out decimal price) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out price) && price >= 0;
}

public sealed record ModelPricingTier(int InputTokensAbove, decimal Input, decimal Output, decimal? CachedInput = null,
    decimal? CachedWrite = null);

/// <summary>An immutable provider-usage snapshot persisted outside model context.</summary>
public sealed record UsageRecord(string Model, string Source, long? InputTokens, long? OutputTokens,
    long? CachedInputTokens, long? ReasoningTokens, long? TotalTokens, decimal? Cost, long? CachedWriteTokens = null)
{
    /// <summary>Input tokens including cached reads, when the provider reports that total.</summary>
    public long? InputIncludingCachedTokens { get; init; }

    [JsonIgnore]
    public long? InputTokenCountIncludingCached => InputIncludingCachedTokens ??
        (InputTokens is long input && CachedInputTokens is long cached ? SaturatingAdd(input, cached) : null);

    [JsonIgnore]
    public decimal? InputCost { get; init; }

    [JsonIgnore]
    public decimal? OutputCost { get; init; }

    [JsonIgnore]
    public decimal? CachedInputCost { get; init; }

    [JsonIgnore]
    public decimal? CachedWriteCost { get; init; }

    public static UsageRecord Create(string model, string source, UsageDetails details, ModelPricing? pricing)
    {
        var inputIncludingCached = details.InputTokenCount;
        var output = details.OutputTokenCount;
        var cached = details.CachedInputTokenCount ?? (inputIncludingCached is null ? null : 0);
        var cachedWriteCounts = details.AdditionalCounts?.Where(count => count.Key.Equals("cacheWriteTokens", StringComparison.OrdinalIgnoreCase) ||
                count.Key.Equals("cacheWriteTokenCount", StringComparison.OrdinalIgnoreCase) ||
                count.Key.Equals("cache_write_tokens", StringComparison.OrdinalIgnoreCase) ||
                count.Key.Equals("cache_creation_input_tokens", StringComparison.OrdinalIgnoreCase) ||
                count.Key.Equals("piMessages.cacheWrite", StringComparison.OrdinalIgnoreCase) ||
                count.Key.Equals("bedrock.cacheWriteInputTokenCount", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        long? cachedWrite = cachedWriteCounts is { Length: > 0 } ? cachedWriteCounts.Sum(count => count.Value) :
            inputIncludingCached is null ? null : 0;
        long? input = inputIncludingCached is long inputTotal ? inputTotal - (cached ?? 0) : null;
        var reasoning = details.ReasoningTokenCount ??
            (inputIncludingCached is not null || details.OutputTokenCount is not null ? 0 : null);
        var reportedTotal = details.TotalTokenCount;
        long? total = reportedTotal is > 0 ? reportedTotal :
            inputIncludingCached is long knownInput && output is long knownOutput && cachedWrite is long knownWrite
                ? SaturatingAdd(SaturatingAdd(knownInput, knownOutput), knownWrite) : null;
        if (inputIncludingCached is < 0 || input is < 0 || output is < 0 || cached is < 0 || cachedWrite is < 0 ||
            reasoning is < 0 || reportedTotal is < 0 || total is < 0 ||
            (inputIncludingCached is long suppliedInput && cached is long suppliedCached && suppliedCached > suppliedInput))
            throw new InvalidDataException("Provider usage counts cannot be negative.");
        decimal? cost = null;
        decimal? inputCost = null;
        decimal? outputCost = null;
        decimal? cachedInputCost = null;
        decimal? cachedWriteCost = null;
        if (pricing is not null && inputIncludingCached is long pricedInput && output is long pricedOutput &&
            cachedWrite is long pricedCacheWrite)
        {
            var rates = pricing.ForInput(SaturatingAdd(pricedInput, pricedCacheWrite));
            var cachedRate = rates.CachedInput ?? rates.Input;
            var cachedWriteRate = rates.CachedWrite ?? rates.Input;
            if (cached is long pricedCacheRead)
            {
                var ordinaryInput = pricedInput - pricedCacheRead;
                inputCost = ordinaryInput * rates.Input / 1_000_000m;
                cachedInputCost = pricedCacheRead * cachedRate / 1_000_000m;
            }
            else if (rates.CachedInput is null || rates.CachedInput == rates.Input)
            {
                // The total input charge is known when cached and ordinary input use the same rate,
                // although their individual cost breakdown is not known.
                inputCost = null;
                cachedInputCost = null;
            }
            outputCost = pricedOutput * rates.Output / 1_000_000m;
            cachedWriteCost = pricedCacheWrite * cachedWriteRate / 1_000_000m;
            if (cached is long)
                cost = inputCost + cachedInputCost + cachedWriteCost + outputCost;
            else if (rates.CachedInput is null || rates.CachedInput == rates.Input)
                cost = pricedInput * rates.Input / 1_000_000m + cachedWriteCost + outputCost;
        }
        return new UsageRecord(model, source, input, output, cached, reasoning, total, cost, cachedWrite)
        {
            InputIncludingCachedTokens = inputIncludingCached,
            InputCost = inputCost,
            OutputCost = outputCost,
            CachedInputCost = cachedInputCost,
            CachedWriteCost = cachedWriteCost
        };
    }

    private static long SaturatingAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
}

internal static class UsageTokenAggregation
{
    public static long? SumKnown(IEnumerable<long?> counts)
    {
        var values = counts.ToArray();
        return values.Length == 0 ? 0 : values.All(value => value.HasValue)
            ? values.Sum(value => value!.Value) : null;
    }
}
