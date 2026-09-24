using System.Net.Http.Headers;
using System.Globalization;
using System.Text.Json;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Runtime.Providers;

public sealed record ModelDescriptor(string Id, string? Owner, int? ContextLength, string? Status,
    bool? Reasoning = null, ModelPricing? Pricing = null, string? Provider = null,
    bool Available = true, string? UnavailableReason = null, string? Name = null,
    int? MaxOutputTokens = null, IReadOnlyList<string>? Input = null, string? Api = null);

/// <summary>Discover OpenAI-compatible model IDs without coupling model metadata to a specific SDK.</summary>
public static class ModelCatalog
{
    public static async Task<IReadOnlyList<ModelDescriptor>> ListAsync(HttpClient http, Uri? endpoint, string apiKey,
        CancellationToken cancellationToken = default)
    {
        var address = new Uri((endpoint?.ToString() ?? "https://api.openai.com/v1").TrimEnd('/') + "/models");
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        const int maxBytes = 1024 * 1024;
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("Model catalogue exceeds 1MB.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = new byte[maxBytes + 1];
        var length = 0;
        int read;
        while (length < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(length), cancellationToken)) > 0)
            length += read;
        if (length > maxBytes) throw new InvalidDataException("Model catalogue exceeds 1MB.");
        using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Model catalogue has no data array.");
        var models = new List<ModelDescriptor>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var name) ||
                name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())) continue;
            var id = name.GetString()!;
            if (models.Any(model => model.Id == id)) continue;
            static string? StringProperty(JsonElement element, string property) =>
                element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var context = item.TryGetProperty("context_length", out var size) && size.ValueKind == JsonValueKind.Number &&
                size.TryGetInt32(out var parsed) && parsed > 0 ? parsed : (int?)null;
            var status = item.TryGetProperty("status", out var state) && state.ValueKind == JsonValueKind.Object ?
                StringProperty(state, "value") : null;
            var reasoning = item.TryGetProperty("reasoning", out var thinking) &&
                thinking.ValueKind is JsonValueKind.True or JsonValueKind.False ? thinking.GetBoolean() : (bool?)null;
            var maxOutput = PositiveInt(item, "max_tokens") ?? PositiveInt(item, "max_output_tokens");
            var input = ParseInputs(item);
            var api = StringProperty(item, "api");
            models.Add(new(id, StringProperty(item, "owned_by"), context, status, reasoning, ParsePricing(item),
                Name: StringProperty(item, "name"), MaxOutputTokens: maxOutput, Input: input, Api: api));
        }
        return models;
    }

    private static int? PositiveInt(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) && number > 0 ? number : null;

    private static IReadOnlyList<string>? ParseInputs(JsonElement model)
    {
        if (!model.TryGetProperty("input", out var input))
        {
            if (!model.TryGetProperty("architecture", out var architecture) || architecture.ValueKind != JsonValueKind.Object ||
                !architecture.TryGetProperty("input_modalities", out input)) return null;
        }
        if (input.ValueKind != JsonValueKind.Array) return null;
        var modalities = input.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()).OfType<string>().Where(value => value is "text" or "image")
            .Distinct(StringComparer.Ordinal).ToArray();
        return modalities.Length == 0 ? null : modalities;
    }

    private static IReadOnlyList<ModelPricingTier>? ParseTiers(JsonElement cost)
    {
        if (!cost.TryGetProperty("tiers", out var tiers) || tiers.ValueKind != JsonValueKind.Array) return null;
        var result = new List<ModelPricingTier>();
        foreach (var tier in tiers.EnumerateArray())
        {
            if (tier.ValueKind != JsonValueKind.Object || PositiveInt(tier, "inputTokensAbove") is not int threshold ||
                !TryNonnegativeDecimal(tier, "input", out var input) || !TryNonnegativeDecimal(tier, "output", out var output)) return null;
            decimal? cached = TryNonnegativeDecimal(tier, "cacheRead", out var cachedRate) ? cachedRate : null;
            result.Add(new ModelPricingTier(threshold, input, output, cached,
                TryNonnegativeDecimal(tier, "cacheWrite", out var cachedWrite) ? cachedWrite : null));
        }
        return result.OrderBy(tier => tier.InputTokensAbove).ToArray();
    }

    private static ModelPricing? ParsePricing(JsonElement model)
    {
        // Pi-style model metadata expresses dollars per million tokens.
        if (model.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object &&
            TryNonnegativeDecimal(cost, "input", out var input) &&
            TryNonnegativeDecimal(cost, "output", out var output))
        {
            decimal? cached = TryNonnegativeDecimal(cost, "cacheRead", out var cacheRead) ? cacheRead : null;
            return new ModelPricing(input, output, cached, ParseTiers(cost),
                TryNonnegativeDecimal(cost, "cacheWrite", out var cachedWrite) ? cachedWrite : null);
        }
        // OpenRouter-compatible catalogues express dollars per token as JSON strings.
        if (model.TryGetProperty("pricing", out var pricing) && pricing.ValueKind == JsonValueKind.Object &&
            TryNonnegativeDecimal(pricing, "prompt", out input) &&
            TryNonnegativeDecimal(pricing, "completion", out output))
        {
            decimal? cached = TryNonnegativeDecimal(pricing, "input_cache_read", out var cachedPerToken) ? cachedPerToken * 1_000_000m : null;
            return new ModelPricing(input * 1_000_000m, output * 1_000_000m, cached);
        }
        return null;
    }

    private static bool TryNonnegativeDecimal(JsonElement parent, string name, out decimal value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out var element)) return false;
        var parsed = element.ValueKind == JsonValueKind.Number ? element.TryGetDecimal(out value) :
            element.ValueKind == JsonValueKind.String && decimal.TryParse(element.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
        return parsed && value >= 0;
    }
}
