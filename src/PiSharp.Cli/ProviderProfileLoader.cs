using System.Globalization;
using System.Text.Json;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

/// <summary>Bounded, credential-isolated provider and model configuration loader.</summary>
internal static class ProviderProfileLoader
{
    internal static Uri ParseEndpoint(string text, string source)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException($"{source} must be an absolute HTTP(S) URL.");
        return endpoint;
    }

    internal static async Task LoadConfiguredProvidersAsync(string path, Dictionary<string, ProviderProfile> providers,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        const int maxBytes = 1024 * 1024;
        if (stream.Length > maxBytes) throw new InvalidDataException("models.json exceeds 1MB.");
        var buffer = new byte[maxBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken)) > 0)
            length += read;
        if (length > maxBytes) throw new InvalidDataException("models.json exceeds 1MB.");
        using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("providers", out var configured) || configured.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("models.json must contain a providers object.");
        var seenProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in configured.EnumerateObject())
        {
            if (!seenProviders.Add(item.Name)) throw new InvalidDataException("models.json contains duplicate provider IDs.");
            if (item.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Provider '{item.Name}' must be an object.");
            var value = item.Value;
            // Preserve built-in identity across case-insensitive models.json overrides. Protocol
            // dispatch and credential storage use the canonical provider ID, not JSON spelling.
            var canonicalId = providers.GetValueOrDefault(item.Name)?.Id ?? item.Name;
            var baseUrl = String(value, "baseUrl") ?? providers.GetValueOrDefault(item.Name)?.Endpoint.ToString();
            if (baseUrl is null) throw new InvalidDataException($"Provider '{item.Name}' requires baseUrl.");
            var models = new List<ModelDescriptor>();
            var seenModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (value.TryGetProperty("models", out var array) && array.ValueKind == JsonValueKind.Array)
                foreach (var model in array.EnumerateArray())
                {
                    if (model.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(String(model, "id")))
                        throw new InvalidDataException($"Provider '{item.Name}' has an invalid model.");
                    if (!seenModels.Add(String(model, "id")!))
                        throw new InvalidDataException($"Provider '{item.Name}' has duplicate model IDs.");
                    models.Add(new(String(model, "id")!, canonicalId, PositiveInt(model, "contextWindow") ?? PositiveInt(model, "context_length"),
                        "configured", Boolean(model, "reasoning"), ParsePricing(model), canonicalId,
                        Name: String(model, "name"), MaxOutputTokens: PositiveInt(model, "maxTokens") ?? PositiveInt(model, "max_tokens"),
                        Input: ParseInputs(model), Api: String(model, "api"),
                        InputLimits: ModelInputLimitsParser.Parse(model,
                            $"models.json provider '{item.Name}' model '{String(model, "id")}'", strict: true)));
                }
            var existing = providers.GetValueOrDefault(item.Name);
            var endpoint = ParseEndpoint(baseUrl, $"models.json provider '{item.Name}' baseUrl");
            // The built-in OpenAI identity owns credentials from auth.json and OPENAI_API_KEY.
            // Overriding its endpoint, or naming that env var for another endpoint, leaks them.
            if (item.Name.Equals("openai", StringComparison.OrdinalIgnoreCase) && !ProviderModelRuntime.IsOfficialOpenAiEndpoint(endpoint))
                throw new InvalidDataException("The built-in openai provider cannot use a custom endpoint; choose a new provider ID and its own credentials.");
            var apiKeyEnvironment = String(value, "apiKeyEnv") ?? existing?.ApiKeyEnvironment;
            if (!ProviderModelRuntime.IsOfficialOpenAiEndpoint(endpoint) && apiKeyEnvironment?.Equals("OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidDataException("A custom endpoint cannot use OPENAI_API_KEY; configure a provider-specific credential.");
            // A built-in provider ID owns its credentials; overrides must not redirect them.
            // Custom providers may use another ID with explicit credentials instead.
            if (existing is not null && item.Name is not ("local" or "custom" or "openai") &&
                !SameEndpoint(existing.Endpoint, endpoint))
                throw new InvalidDataException($"Built-in provider '{item.Name}' cannot use a custom endpoint; choose a new provider ID.");
            foreach (var reserved in providers.Values.Where(profile => profile.Id is "openai" or "openrouter" or "mistral" or "xai" or "anthropic"))
                if (apiKeyEnvironment?.Equals(reserved.ApiKeyEnvironment, StringComparison.OrdinalIgnoreCase) == true &&
                    (!item.Name.Equals(reserved.Id, StringComparison.OrdinalIgnoreCase) || !SameEndpoint(endpoint, reserved.Endpoint)))
                    throw new InvalidDataException($"Provider '{item.Name}' cannot borrow {reserved.ApiKeyEnvironment}; use its own credential environment variable.");
            var authHeader = Boolean(value, "authHeader") ?? true;
            if (value.TryGetProperty("oauth", out var oauthValue) && oauthValue.ValueKind is not JsonValueKind.Null and not JsonValueKind.False)
                throw new InvalidDataException("models.json cannot enable OAuth without a provider-specific refresh adapter.");
            providers[canonicalId] = new(canonicalId, String(value, "name") ?? existing?.Name ?? item.Name,
                endpoint, authHeader, false, apiKeyEnvironment,
                String(value, "apiKey"), models.Count == 0 ? existing?.Models ?? [] : models);
        }
    }

    private static bool SameEndpoint(Uri left, Uri right) =>
        string.Equals(left.GetLeftPart(UriPartial.Path).TrimEnd('/'), right.GetLeftPart(UriPartial.Path).TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
    private static string? String(JsonElement value, string property) => value.TryGetProperty(property, out var child) &&
        child.ValueKind == JsonValueKind.String ? child.GetString() : null;
    private static bool? Boolean(JsonElement value, string property) => value.TryGetProperty(property, out var child) &&
        child.ValueKind is JsonValueKind.True or JsonValueKind.False ? child.GetBoolean() : null;
    private static IReadOnlyList<string>? ParseInputs(JsonElement model)
    {
        if (!model.TryGetProperty("input", out var input)) return null;
        if (input.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Model input must be an array of text/image modalities.");
        var modalities = input.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .ToArray();
        if (modalities.Any(value => value is not ("text" or "image")))
            throw new InvalidDataException("Model input modalities must be text or image.");
        return modalities.Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
    }

    private static int? PositiveInt(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var child)) return null;
        if (child.ValueKind != JsonValueKind.Number || !child.TryGetInt32(out var number) || number <= 0)
            throw new InvalidDataException($"models.json {property} must be a positive integer.");
        return number;
    }

    private static ModelPricing? ParsePricing(JsonElement model)
    {
        if (!model.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object ||
            !Decimal(cost, "input", out var input) || !Decimal(cost, "output", out var output)) return null;
        IReadOnlyList<ModelPricingTier>? tiers = null;
        if (cost.TryGetProperty("tiers", out var tierArray) && tierArray.ValueKind == JsonValueKind.Array)
        {
            var parsedTiers = new List<ModelPricingTier>();
            foreach (var tier in tierArray.EnumerateArray())
            {
                if (tier.ValueKind != JsonValueKind.Object || PositiveInt(tier, "inputTokensAbove") is not int threshold ||
                    !Decimal(tier, "input", out var tierInput) || !Decimal(tier, "output", out var tierOutput))
                    throw new InvalidDataException("Configured model pricing tiers require positive inputTokensAbove and nonnegative input/output rates.");
                parsedTiers.Add(new(threshold, tierInput, tierOutput, Decimal(tier, "cacheRead", out var tierCached) ? tierCached : null,
                    Decimal(tier, "cacheWrite", out var tierCacheWrite) ? tierCacheWrite : null));
            }
            if (parsedTiers.Select(tier => tier.InputTokensAbove).Distinct().Count() != parsedTiers.Count)
                throw new InvalidDataException("Configured model pricing tiers must have unique input thresholds.");
            tiers = parsedTiers.OrderBy(tier => tier.InputTokensAbove).ToArray();
        }
        return new(input, output, Decimal(cost, "cacheRead", out var cached) ? cached : null, tiers,
            Decimal(cost, "cacheWrite", out var cachedWrite) ? cachedWrite : null);
    }

    private static bool Decimal(JsonElement parent, string property, out decimal result)
    {
        result = 0;
        if (!parent.TryGetProperty(property, out var value)) return false;
        var parsed = value.ValueKind == JsonValueKind.Number ? value.TryGetDecimal(out result) :
            value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out result);
        return parsed && result >= 0;
    }
}
