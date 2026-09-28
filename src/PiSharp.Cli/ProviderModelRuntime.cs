using System.Text.Json;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

public sealed record ProviderProfile(string Id, string Name, Uri Endpoint, bool AuthRequired,
    bool OAuthSupported, string? ApiKeyEnvironment, string? ConfiguredApiKey,
    IReadOnlyList<ModelDescriptor> Models, string? Api = null, JsonElement? Compatibility = null);

public sealed record ModelSelection(ProviderProfile Provider, ModelDescriptor Model, string ApiKey,
    bool Authenticated, string AuthSource)
{
    public ConnectionSettings Connection
    {
        get
        {
            var endpoint = Model.BaseUrl is { } baseUrl ? new Uri(baseUrl) : Provider.Endpoint;
            return new ConnectionSettings(Model.Id,
                ProviderModelRuntime.IsOfficialOpenAiEndpoint(endpoint) ? null : endpoint, ApiKey);
        }
    }
}

/// <summary>Credential-aware OpenAI-compatible provider/catalog selection shared by CLI commands.</summary>
public sealed class ProviderModelRuntime
{
    private readonly Dictionary<string, ProviderProfile> _providers;
    private readonly AuthStorage _auth;
    private readonly Func<string, string?> _environment;
    private readonly HttpClient _http;
    private readonly string? _runtimeApiKey;
    private readonly bool _offline;
    private IReadOnlyList<string> _scope;

    private ProviderModelRuntime(Dictionary<string, ProviderProfile> providers, AuthStorage auth,
        Func<string, string?> environment, HttpClient http, string? runtimeApiKey, IReadOnlyList<string>? scope, bool offline)
    {
        _providers = providers;
        _auth = auth;
        _environment = environment;
        _http = http;
        _runtimeApiKey = runtimeApiKey;
        _offline = offline;
        _scope = scope ?? [];
    }

    public IReadOnlyList<string> Scope => _scope;
    public IReadOnlyList<ProviderProfile> Providers => _providers.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();

    public static async Task<ProviderModelRuntime> CreateAsync(string agentDirectory, bool includeLocal,
        Func<string, string?> environment, HttpClient http, string? runtimeApiKey = null,
        IReadOnlyList<string>? scope = null, CancellationToken cancellationToken = default, bool offline = false)
    {
        var providers = BuiltinProviderProfiles.Create(environment);
        var configuredEndpoint = environment("PISHARP_BASE_URL");
        if (includeLocal || configuredEndpoint is not null)
        {
            var endpoint = ProviderProfileLoader.ParseEndpoint(configuredEndpoint ?? ConnectionSettings.LocalEndpoint, "PISHARP_BASE_URL");
            var id = includeLocal ? "local" : "custom";
            var model = environment("PISHARP_MODEL") ?? (includeLocal ? ConnectionSettings.LocalModel : "default");
            providers[id] = new(id, includeLocal ? "Local" : "Custom", endpoint, false, false,
                "PISHARP_API_KEY", null, [new(model, id, null, "configured", Provider: id)]);
        }

        var modelsPath = environment("PISHARP_MODELS_PATH") ?? Path.Combine(agentDirectory, "models.json");
        if (File.Exists(modelsPath))
        {
            try { await ProviderProfileLoader.LoadConfiguredProvidersAsync(modelsPath, providers, cancellationToken); }
            catch (JsonException error) { throw new InvalidDataException("Invalid models.json JSON.", error); }
        }
        var authPath = environment("PISHARP_AUTH_PATH") ?? Path.Combine(agentDirectory, "auth.json");
        return new ProviderModelRuntime(providers, new AuthStorage(authPath), environment, http, runtimeApiKey, scope, offline);
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
        CancellationToken cancellationToken = default, bool includeOutOfScope = false)
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
            // These built-ins use pinned, provider-owned catalogues rather than generic /models discovery.
            if (_offline || provider.Id is "xai" or "anthropic" or "mistral")
            {
                result.AddRange(provider.Models.Select(model => model with { Provider = provider.Id }));
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
        return includeOutOfScope ? result : ApplyScope(result);
    }

    public async Task<ModelSelection> ResolveAsync(string? providerId, string? modelReference,
        CancellationToken cancellationToken = default, bool includeOutOfScope = false)
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
        var configured = (includeOutOfScope ? explicitProvider.Models : ApplyScope(explicitProvider.Models)).Where(item =>
            item.Id.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        var models = configured.Length == 1 ? configured :
            await ListModelsAsync(explicitProvider.Id, cancellationToken, includeOutOfScope);
        var matches = models.Where(item => item.Id.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
            matches = models.Where(item => item.Id.Contains(reference, StringComparison.OrdinalIgnoreCase) ||
                (item.Owner?.Contains(reference, StringComparison.OrdinalIgnoreCase) ?? false)).ToArray();
        if (matches.Length > 1)
            throw new ArgumentException($"Model '{reference}' is ambiguous: {string.Join(", ", matches.Select(item => explicitProvider.Id + "/" + item.Id))}.");
        if (matches.Length == 0 && _scope.Count > 0 && !includeOutOfScope)
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
        var scoped = new List<ModelDescriptor>();
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in _scope)
        {
            foreach (var model in all)
            {
                if (!ModelScopeGlob.Matches(pattern, $"{model.Provider}/{model.Id}") &&
                    !ModelScopeGlob.Matches(pattern, model.Id)) continue;
                if (included.Add($"{model.Provider}\0{model.Id}")) scoped.Add(model);
            }
        }
        return scoped;
    }

    private static ModelDescriptor Merge(ProviderProfile provider, ModelDescriptor discovered)
    {
        var configured = provider.Models.FirstOrDefault(model => model.Id == discovered.Id);
        var baseUrl = configured?.BaseUrl ?? discovered.BaseUrl;
        if (baseUrl is not null)
        {
            var modelEndpoint = ProviderProfileLoader.ParseEndpoint(baseUrl, $"Model '{provider.Id}/{discovered.Id}' baseUrl");
            if (!IsSameAuthority(provider.Endpoint, modelEndpoint))
                throw new InvalidDataException($"Model '{provider.Id}/{discovered.Id}' baseUrl must use the provider's scheme, host, and port.");
            baseUrl = modelEndpoint.ToString().TrimEnd('/');
        }
        return discovered with
        {
            Provider = provider.Id,
            Owner = discovered.Owner ?? configured?.Owner ?? provider.Id,
            ContextLength = configured?.ContextLength ?? discovered.ContextLength ?? 128000,
            Reasoning = configured?.Reasoning ?? discovered.Reasoning ?? false,
            Pricing = configured?.Pricing ?? discovered.Pricing ?? new ModelPricing(0, 0, 0, CachedWrite: 0),
            Name = configured?.Name ?? discovered.Name ?? discovered.Id,
            MaxOutputTokens = configured?.MaxOutputTokens ?? discovered.MaxOutputTokens ?? 16384,
            Input = configured?.Input ?? discovered.Input ?? ["text"],
            Api = configured?.Api ?? discovered.Api,
            InputLimits = ModelInputLimits.Merge(configured?.InputLimits, discovered.InputLimits),
            BaseUrl = baseUrl,
            ThinkingLevelMap = ModelMetadataJson.MergeObjects(configured?.ThinkingLevelMap, discovered.ThinkingLevelMap),
            PromptCache = ModelMetadataJson.MergeObjects(configured?.PromptCache, discovered.PromptCache),
            SamplingParameters = ModelMetadataJson.MergeObjects(configured?.SamplingParameters, discovered.SamplingParameters),
            Compatibility = ModelMetadataJson.MergeCompatibility(configured?.Compatibility,
                ModelMetadataJson.MergeCompatibility(discovered.Compatibility, provider.Compatibility)),
            Available = true,
            UnavailableReason = null
        };
    }

    internal static bool IsSameAuthority(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    internal static bool IsOfficialOpenAiEndpoint(Uri endpoint) => endpoint.Scheme == Uri.UriSchemeHttps &&
        endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) && endpoint.Port == 443 &&
        endpoint.AbsolutePath.TrimEnd('/').Equals("/v1", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment);
}
