using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Model prices in US dollars per one million tokens. Unknown prices stay unknown.</summary>
public sealed record ModelPricing(decimal Input, decimal Output, decimal? CachedInput = null)
{
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

/// <summary>An immutable provider-usage snapshot persisted outside model context.</summary>
public sealed record UsageRecord(string Model, string Source, long InputTokens, long OutputTokens,
    long CachedInputTokens, long ReasoningTokens, long TotalTokens, decimal? Cost)
{
    public static UsageRecord Create(string model, string source, UsageDetails details, ModelPricing? pricing)
    {
        var input = details.InputTokenCount ?? 0;
        var output = details.OutputTokenCount ?? 0;
        var cached = details.CachedInputTokenCount ?? 0;
        var reasoning = details.ReasoningTokenCount ?? 0;
        var reportedTotal = details.TotalTokenCount ?? 0;
        var total = reportedTotal > 0 ? reportedTotal : input + output;
        if (input < 0 || output < 0 || cached < 0 || reasoning < 0 || total < 0)
            throw new InvalidDataException("Provider usage counts cannot be negative.");
        decimal? cost = null;
        if (pricing is not null)
        {
            var ordinaryInput = Math.Max(0, input - cached);
            var cachedRate = pricing.CachedInput ?? pricing.Input;
            cost = (ordinaryInput * pricing.Input + cached * cachedRate + output * pricing.Output) / 1_000_000m;
        }
        return new UsageRecord(model, source, input, output, cached, reasoning, total, cost);
    }
}
