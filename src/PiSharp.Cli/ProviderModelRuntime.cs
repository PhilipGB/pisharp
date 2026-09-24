using System.Globalization;
using System.Text.Json;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

public sealed record ProviderProfile(string Id, string Name, Uri Endpoint, bool AuthRequired,
    bool OAuthSupported, string? ApiKeyEnvironment, string? ConfiguredApiKey,
    IReadOnlyList<ModelDescriptor> Models);

public sealed record ModelSelection(ProviderProfile Provider, ModelDescriptor Model, string ApiKey,
    bool Authenticated, string AuthSource)
{
    public ConnectionSettings Connection => new(Model.Id,
        ProviderModelRuntime.IsOfficialOpenAiEndpoint(Provider.Endpoint) ? null : Provider.Endpoint,
        ApiKey);
}

/// <summary>Credential-aware OpenAI-compatible provider/catalog selection shared by CLI commands.</summary>
public sealed class ProviderModelRuntime
{
    private readonly Dictionary<string, ProviderProfile> _providers;
    private readonly AuthStorage _auth;
    private readonly Func<string, string?> _environment;
    private readonly HttpClient _http;
    private readonly string? _runtimeApiKey;
    private IReadOnlyList<string> _scope;

    private ProviderModelRuntime(Dictionary<string, ProviderProfile> providers, AuthStorage auth,
        Func<string, string?> environment, HttpClient http, string? runtimeApiKey, IReadOnlyList<string>? scope)
    {
        _providers = providers;
        _auth = auth;
        _environment = environment;
        _http = http;
        _runtimeApiKey = runtimeApiKey;
        _scope = scope ?? [];
    }

    public IReadOnlyList<string> Scope => _scope;
    public IReadOnlyList<ProviderProfile> Providers => _providers.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();

    public static async Task<ProviderModelRuntime> CreateAsync(string agentDirectory, bool includeLocal,
        Func<string, string?> environment, HttpClient http, string? runtimeApiKey = null,
        IReadOnlyList<string>? scope = null, CancellationToken cancellationToken = default)
    {
        var providers = new Dictionary<string, ProviderProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = new("openai", "OpenAI", new Uri("https://api.openai.com/v1"), true, false,
                "OPENAI_API_KEY", null, [new(environment("PISHARP_MODEL") ?? "gpt-4o-mini", "openai", null, "catalog default", false, Provider: "openai")]),
            ["openrouter"] = new("openrouter", "OpenRouter", new Uri("https://openrouter.ai/api/v1"), true, false,
                "OPENROUTER_API_KEY", null, [new(environment("PISHARP_OPENROUTER_MODEL") ?? "openai/gpt-4o-mini", "openrouter", null, "catalog default", false, Provider: "openrouter")]),
            ["mistral"] = new("mistral", "Mistral", new Uri("https://api.mistral.ai/v1"), true, false,
                "MISTRAL_API_KEY", null, [new(environment("PISHARP_MISTRAL_MODEL") ?? "mistral-small-latest", "mistral", null, "catalog default", false, Provider: "mistral")])
        };
        var configuredEndpoint = environment("PISHARP_BASE_URL");
        if (includeLocal || configuredEndpoint is not null)
        {
            var endpoint = ParseEndpoint(configuredEndpoint ?? ConnectionSettings.LocalEndpoint, "PISHARP_BASE_URL");
            var id = includeLocal ? "local" : "custom";
            var model = environment("PISHARP_MODEL") ?? (includeLocal ? ConnectionSettings.LocalModel : "default");
            providers[id] = new(id, includeLocal ? "Local" : "Custom", endpoint, false, false,
                "PISHARP_API_KEY", null, [new(model, id, null, "configured", Provider: id)]);
        }

        var modelsPath = environment("PISHARP_MODELS_PATH") ?? Path.Combine(agentDirectory, "models.json");
        if (File.Exists(modelsPath))
        {
            try { await LoadConfiguredProvidersAsync(modelsPath, providers, cancellationToken); }
            catch (JsonException error) { throw new InvalidDataException("Invalid models.json JSON.", error); }
        }
        var authPath = environment("PISHARP_AUTH_PATH") ?? Path.Combine(agentDirectory, "auth.json");
        return new ProviderModelRuntime(providers, new AuthStorage(authPath), environment, http, runtimeApiKey, scope);
    }

    public ProviderProfile GetProvider(string id) => _providers.TryGetValue(id, out var provider) ? provider :
        throw new ArgumentException($"Unknown provider '{id}'. Available providers: {string.Join(", ", _providers.Keys.Order())}.");

    public void SetScope(IEnumerable<string> patterns)
    {
        var values = patterns.Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
        _scope = values;
    }

    public async Task<(string Key, bool Authenticated, string Source)> ResolveAuthAsync(string providerId,
        bool useRuntimeOverride = false, CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(providerId);
        if (useRuntimeOverride && !string.IsNullOrWhiteSpace(_runtimeApiKey))
            return (_runtimeApiKey, true, "command line");
        var stored = await _auth.ReadAsync(provider.Id, cancellationToken);
        if (stored is not null)
        {
            // A stored credential owns the provider: never fall back to ambient credentials or
            // send a bearer token through an API-key-only adapter (Pi resolves OAuth via its handler).
            if (stored.Type == "oauth")
                return ("not-configured", false, provider.OAuthSupported ? "OAuth adapter unavailable" : "OAuth unsupported for provider");
            return (stored.Key!, true, "stored API key");
        }
        if (!string.IsNullOrWhiteSpace(provider.ConfiguredApiKey)) return (provider.ConfiguredApiKey, true, "models.json");
        if (provider.ApiKeyEnvironment is not null && !string.IsNullOrWhiteSpace(_environment(provider.ApiKeyEnvironment)))
            return (_environment(provider.ApiKeyEnvironment)!, true, provider.ApiKeyEnvironment);
        return provider.AuthRequired ? ("not-configured", false, "authentication required") :
            ("not-needed", true, "not required");
    }

    public async Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(string? providerId = null,
        CancellationToken cancellationToken = default)
    {
        var providers = providerId is null ? Providers : [GetProvider(providerId)];
        var result = new List<ModelDescriptor>();
        foreach (var provider in providers)
        {
            var auth = await ResolveAuthAsync(provider.Id, useRuntimeOverride: providerId is not null, cancellationToken);
            if (!auth.Authenticated)
            {
                result.AddRange(provider.Models.Select(model => model with
                {
                    Provider = provider.Id,
                    Available = false,
                    UnavailableReason = auth.Source,
                    Status = model.Status ?? "authentication required"
                }));
                continue;
            }
            try
            {
                var discovered = await ModelCatalog.ListAsync(_http,
                    IsOfficialOpenAiEndpoint(provider.Endpoint) ? null : provider.Endpoint,
                    auth.Key, cancellationToken);
                var merged = discovered.Select(model => Merge(provider, model)).ToList();
                foreach (var configured in provider.Models.Where(model => merged.All(item => item.Id != model.Id)))
                    merged.Add(configured with { Provider = provider.Id, Available = false, UnavailableReason = "not advertised by provider", Status = configured.Status ?? "unavailable" });
                result.AddRange(merged);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
            {
                var safe = SecretRedactor.Redact(error.Message, auth.Key, _runtimeApiKey, provider.ConfiguredApiKey);
                result.AddRange(provider.Models.Select(model => model with
                {
                    Provider = provider.Id,
                    Available = false,
                    UnavailableReason = "catalog unavailable: " + safe,
                    Status = "catalog unavailable"
                }));
            }
        }
        return ApplyScope(result);
    }

    public async Task<ModelSelection> ResolveAsync(string? providerId, string? modelReference,
        CancellationToken cancellationToken = default)
    {
        ProviderProfile? explicitProvider = providerId is null ? null : GetProvider(providerId);
        var inferredReference = modelReference?.Trim();
        var slash = inferredReference?.IndexOf('/') ?? -1;
        if (explicitProvider is null && slash > 0)
        {
            var prefix = inferredReference![..slash];
            if (_providers.TryGetValue(prefix, out var inferred))
            {
                explicitProvider = inferred;
                inferredReference = inferredReference[(slash + 1)..];
            }
        }
        explicitProvider ??= GetProvider(_providers.ContainsKey("local") ? "local" :
            _providers.ContainsKey("custom") ? "custom" : "openai");
        var reference = inferredReference ?? explicitProvider.Models.FirstOrDefault()?.Id;
        if (string.IsNullOrWhiteSpace(reference)) throw new InvalidOperationException($"Provider '{explicitProvider.Id}' has no models.");
        // An explicitly configured exact ID is usable without /models: many compatible servers
        // do not implement that endpoint, and it must not consume a prompt's first response.
        var configured = ApplyScope(explicitProvider.Models).Where(item =>
            item.Id.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        var models = configured.Length == 1 ? configured : await ListModelsAsync(explicitProvider.Id, cancellationToken);
        var matches = models.Where(item => item.Id.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
            matches = models.Where(item => item.Id.Contains(reference, StringComparison.OrdinalIgnoreCase) ||
                (item.Owner?.Contains(reference, StringComparison.OrdinalIgnoreCase) ?? false)).ToArray();
        if (matches.Length > 1)
            throw new ArgumentException($"Model '{reference}' is ambiguous: {string.Join(", ", matches.Select(item => explicitProvider.Id + "/" + item.Id))}.");
        if (matches.Length == 0 && _scope.Count > 0)
            throw new ArgumentException($"Model '{explicitProvider.Id}/{reference}' is outside the --models scope.");
        var model = matches.Length == 1 ? matches[0] : new ModelDescriptor(reference, explicitProvider.Id, null,
            "custom model ID", Provider: explicitProvider.Id);
        var auth = await ResolveAuthAsync(explicitProvider.Id, useRuntimeOverride: true, cancellationToken);
        model = model with
        {
            Available = auth.Authenticated && model.Available,
            UnavailableReason = auth.Authenticated ? model.UnavailableReason : auth.Source
        };
        return new ModelSelection(explicitProvider, model, auth.Key, auth.Authenticated, auth.Source);
    }

    public Task LoginApiKeyAsync(string provider, string secret, CancellationToken cancellationToken = default)
    {
        _ = GetProvider(provider);
        return _auth.StoreApiKeyAsync(provider, secret, cancellationToken);
    }

    public Task LoginOAuthAsync(string provider, string accessToken, CancellationToken cancellationToken = default)
    {
        var profile = GetProvider(provider);
        if (!profile.OAuthSupported) throw new InvalidOperationException($"Provider '{provider}' has no configured OAuth adapter.");
        return _auth.StoreOAuthAsync(provider, accessToken, cancellationToken: cancellationToken);
    }

    public async Task<bool> LogoutAsync(string provider, CancellationToken cancellationToken = default)
    {
        _ = GetProvider(provider);
        var existed = await _auth.ReadAsync(provider, cancellationToken) is not null;
        await _auth.DeleteAsync(provider, cancellationToken);
        return existed;
    }

    public Task<IReadOnlyDictionary<string, string>> ListCredentialsAsync(CancellationToken cancellationToken = default) =>
        _auth.ListAsync(cancellationToken);

    private IReadOnlyList<ModelDescriptor> ApplyScope(IEnumerable<ModelDescriptor> models)
    {
        var all = models.ToArray();
        if (_scope.Count == 0) return all;
        return all.Where(model => _scope.Any(pattern => GlobMatches(pattern,
            $"{model.Provider}/{model.Id}") || GlobMatches(pattern, model.Id))).ToArray();
    }

    private static bool GlobMatches(string pattern, string value)
    {
        // Greedy wildcard matching avoids catastrophic regex backtracking for multi-star patterns.
        var index = 0;
        var cursor = 0;
        var star = -1;
        var retry = 0;
        while (cursor < value.Length)
        {
            if (index < pattern.Length && (pattern[index] == '?' ||
                char.ToUpperInvariant(pattern[index]) == char.ToUpperInvariant(value[cursor])))
            {
                index++;
                cursor++;
            }
            else if (index < pattern.Length && pattern[index] == '*')
            {
                star = index++;
                retry = cursor;
            }
            else if (star >= 0)
            {
                index = star + 1;
                cursor = ++retry;
            }
            else return false;
        }
        while (index < pattern.Length && pattern[index] == '*') index++;
        return index == pattern.Length;
    }

    private static ModelDescriptor Merge(ProviderProfile provider, ModelDescriptor discovered)
    {
        var configured = provider.Models.FirstOrDefault(model => model.Id == discovered.Id);
        return discovered with
        {
            Provider = provider.Id,
            Owner = discovered.Owner ?? configured?.Owner,
            ContextLength = discovered.ContextLength ?? configured?.ContextLength,
            Reasoning = discovered.Reasoning ?? configured?.Reasoning,
            Pricing = discovered.Pricing ?? configured?.Pricing,
            Name = discovered.Name ?? configured?.Name,
            MaxOutputTokens = discovered.MaxOutputTokens ?? configured?.MaxOutputTokens,
            Input = discovered.Input ?? configured?.Input,
            Api = discovered.Api ?? configured?.Api,
            Available = true,
            UnavailableReason = null
        };
    }

    internal static bool IsOfficialOpenAiEndpoint(Uri endpoint) => endpoint.Scheme == Uri.UriSchemeHttps &&
        endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) && endpoint.Port == 443 &&
        endpoint.AbsolutePath.TrimEnd('/').Equals("/v1", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment);
    private static Uri ParseEndpoint(string text, string source)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException($"{source} must be an absolute HTTP(S) URL.");
        return endpoint;
    }

    private static async Task LoadConfiguredProvidersAsync(string path, Dictionary<string, ProviderProfile> providers,
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
                    models.Add(new(String(model, "id")!, item.Name, PositiveInt(model, "contextWindow") ?? PositiveInt(model, "context_length"),
                        "configured", Boolean(model, "reasoning"), ParsePricing(model), item.Name,
                        Name: String(model, "name"), MaxOutputTokens: PositiveInt(model, "maxTokens") ?? PositiveInt(model, "max_tokens"),
                        Input: ParseInputs(model), Api: String(model, "api")));
                }
            var existing = providers.GetValueOrDefault(item.Name);
            var endpoint = ParseEndpoint(baseUrl, $"models.json provider '{item.Name}' baseUrl");
            // The built-in OpenAI identity owns credentials from auth.json and OPENAI_API_KEY.
            // Overriding its endpoint, or naming that env var for another endpoint, leaks them.
            if (item.Name.Equals("openai", StringComparison.OrdinalIgnoreCase) && !IsOfficialOpenAiEndpoint(endpoint))
                throw new InvalidDataException("The built-in openai provider cannot use a custom endpoint; choose a new provider ID and its own credentials.");
            var apiKeyEnvironment = String(value, "apiKeyEnv") ?? existing?.ApiKeyEnvironment;
            if (!IsOfficialOpenAiEndpoint(endpoint) && apiKeyEnvironment?.Equals("OPENAI_API_KEY", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidDataException("A custom endpoint cannot use OPENAI_API_KEY; configure a provider-specific credential.");
            var authHeader = Boolean(value, "authHeader") ?? true;
            if (value.TryGetProperty("oauth", out var oauthValue) && oauthValue.ValueKind is not JsonValueKind.Null and not JsonValueKind.False)
                throw new InvalidDataException("models.json cannot enable OAuth without a provider-specific refresh adapter.");
            providers[item.Name] = new(item.Name, String(value, "name") ?? existing?.Name ?? item.Name,
                endpoint, authHeader, false, apiKeyEnvironment,
                String(value, "apiKey"), models.Count == 0 ? existing?.Models ?? [] : models);
        }
    }

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
