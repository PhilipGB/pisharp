using System.Text.Json;
using PiSharp.Runtime.Classifiers;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.VirtualModels;

namespace PiSharp.Cli;

public sealed record ProviderProfile(string Id, string Name, Uri Endpoint, bool AuthRequired,
    bool OAuthSupported, string? ApiKeyEnvironment, string? ConfiguredApiKey,
    IReadOnlyList<ModelDescriptor> Models, string? Api = null, JsonElement? Compatibility = null,
    AzureOpenAiProviderOptions? AzureOpenAi = null, bool ApiKeySupported = true,
    GoogleVertexProviderOptions? GoogleVertex = null, BedrockProviderOptions? Bedrock = null, IReadOnlyList<ClassifierModel>? Classifiers = null, IReadOnlyList<ImageModelDescriptor>? Images = null);

public sealed record ModelSelection(ProviderProfile Provider, ModelDescriptor Model, string ApiKey,
    bool Authenticated, string AuthSource,
    Func<CancellationToken, Task<(string Access, string AccountId)>>? OAuthCredentialResolver = null)
{
    public string? AnthropicAuthToken { get; init; }
    public bool AnthropicIsOAuthToken { get; init; }
    public AnthropicWorkloadIdentityOptions? AnthropicWorkloadIdentity { get; init; }

    public ConnectionSettings Connection
    {
        get
        {
            var endpoint = Model.BaseUrl is { } baseUrl && !baseUrl.Contains("{location}", StringComparison.Ordinal)
                ? new Uri(baseUrl) : Provider.Endpoint;
            return new ConnectionSettings(Model.Id,
                ProviderModelRuntime.IsOfficialOpenAiEndpoint(endpoint) ? null : endpoint, ApiKey);
        }
    }
}

public sealed record AnthropicWorkloadIdentityOptions(string FederationRuleId, string OrganizationId,
    string IdentityTokenFile, string? ServiceAccountId, string? WorkspaceId);

/// <summary>Credential-aware OpenAI-compatible provider/catalog selection shared by CLI commands.</summary>
public sealed class ProviderModelRuntime
{
    private readonly Dictionary<string, ProviderProfile> _providers;
    private readonly AuthStorage _auth;
    private readonly ProviderOAuthCoordinator _oauth;
    private readonly Func<string, string?> _environment;
    private readonly HttpClient _http;
    private readonly string? _runtimeApiKey;
    private string? _runtimeApiKeyProvider;
    private readonly bool _offline;
    private readonly LlamaRouterCatalogStore _llamaRouterCache;
    private readonly SemaphoreSlim _llamaRouterRefresh = new(1, 1);
    private readonly object _availableProviderGate = new();
    private readonly Dictionary<string, bool> _availableProviderSnapshot = new(StringComparer.Ordinal);
    private ProviderProfile? _llamaRouterProfile;
    private IReadOnlyList<string> _scope;
    private VirtualModelRegistry? _virtualModels;

    private ProviderModelRuntime(Dictionary<string, ProviderProfile> providers, AuthStorage auth,
        ProviderOAuthCoordinator oauth,
        Func<string, string?> environment, HttpClient http, string? runtimeApiKey, IReadOnlyList<string>? scope,
        bool offline, string agentDirectory)
    {
        _providers = providers;
        _auth = auth;
        _oauth = oauth;
        _environment = environment;
        _http = http;
        _runtimeApiKey = runtimeApiKey;
        _offline = offline;
        _llamaRouterCache = new LlamaRouterCatalogStore(agentDirectory);
        _llamaRouterProfile = providers.GetValueOrDefault("llama.cpp");
        _scope = scope ?? [];
    }

    public IReadOnlyList<string> Scope => _scope;
    public VirtualModelRegistry? VirtualModels => _virtualModels;
    public int AvailableModelProviderCount
    {
        get
        {
            lock (_availableProviderGate) return _availableProviderSnapshot.Count(item => item.Value);
        }
    }

    public IReadOnlyList<ProviderProfile> Providers
    {
        get
        {
            var providers = new Dictionary<string, ProviderProfile>(_providers, StringComparer.Ordinal);
            if (Volatile.Read(ref _llamaRouterProfile) is { } llamaRouter)
                providers[llamaRouter.Id] = llamaRouter;
            foreach (var providerId in _virtualModels?.Models.Select(item => item.Model.Provider)
                         .Where(providerId => providerId is not null).Select(providerId => providerId!)
                         .Distinct(StringComparer.Ordinal) ?? [])
                if (!providers.ContainsKey(providerId)) providers[providerId] = VirtualProvider(providerId);
            return providers.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        }
    }

    public void SetVirtualModelRegistry(VirtualModelRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _virtualModels = registry;
        foreach (var item in registry.Models)
            if (item.Model.Provider is { } providerId && _providers.TryGetValue(providerId, out var provider) &&
                provider.Models.Any(model => string.Equals(model.Id, item.Model.Id, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Virtual model {item.Model.Provider}/{item.Model.Id} conflicts with a configured physical model.");
    }

    public static async Task<ProviderModelRuntime> CreateAsync(string agentDirectory, bool includeLocal,
        Func<string, string?> environment, HttpClient http, string? runtimeApiKey = null,
        IReadOnlyList<string>? scope = null, CancellationToken cancellationToken = default, bool offline = false)
    {
        var providers = BuiltinProviderProfiles.Create(environment);
        ClassifierCatalog.AddBuiltins(providers, environment);
        ImageCatalog.AddBuiltins(providers);
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
        var auth = new AuthStorage(authPath);
        var oauthAdapters = new List<IProviderOAuthAdapter>
        {
            new OpenAiCodexOAuthAdapter(http),
            new AnthropicOAuthAdapter(http)
        };
        if (providers.TryGetValue("radius", out var radius))
            oauthAdapters.Add(new RadiusOAuthAdapter(http, radius.Endpoint));
        var runtime = new ProviderModelRuntime(providers, auth, new ProviderOAuthCoordinator(auth, http, oauthAdapters),
            environment, http, runtimeApiKey, scope, offline, agentDirectory);
        if (runtime.ResolveLlamaRouterUrl(await auth.ReadAsync("llama.cpp", cancellationToken).ConfigureAwait(false)) is not null)
            runtime.RecordAvailableProvider("llama.cpp", true);
        return runtime;
    }

    public ProviderProfile GetProvider(string id) => id == "llama.cpp" && Volatile.Read(ref _llamaRouterProfile) is { } llamaRouter
        ? llamaRouter : _providers.TryGetValue(id, out var provider) ? provider :
        _virtualModels?.Models.Any(item => item.Model.Provider == id) == true ? VirtualProvider(id) :
        throw new ArgumentException($"Unknown provider '{id}'. Available providers: {string.Join(", ", Providers.Select(item => item.Id).Order())}.");

    public void SetScope(IEnumerable<string> patterns)
    {
        var values = patterns.Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
        _scope = values;
    }

    public async Task<(string Key, bool Authenticated, string Source)> ResolveAuthAsync(string providerId,
        bool useRuntimeOverride = false, CancellationToken cancellationToken = default,
        bool allowOAuthRefresh = true)
    {
        var provider = GetProvider(providerId);
        if (provider.Id == "llama.cpp")
        {
            var llamaStored = await _auth.ReadAsync(provider.Id, cancellationToken);
            return await ResolveAuthCoreAsync(provider, llamaStored, useRuntimeOverride, allowOAuthRefresh, cancellationToken);
        }
        if (useRuntimeOverride && !string.IsNullOrWhiteSpace(_runtimeApiKey) &&
            (_runtimeApiKeyProvider is null || _runtimeApiKeyProvider == provider.Id))
            return (_runtimeApiKey, true, "command line");
        var stored = await _auth.ReadAsync(provider.Id, cancellationToken);
        return await ResolveAuthCoreAsync(provider, stored, useRuntimeOverride, allowOAuthRefresh, cancellationToken);
    }

    /// <summary>Resolves the current provider credential for an explicitly configured MCP bearer token.</summary>
    public async Task<string?> GetApiKeyForProviderAsync(string providerId,
        CancellationToken cancellationToken = default)
    {
        ProviderProfile provider;
        try { provider = GetProvider(providerId); }
        catch (ArgumentException) { return null; }
        var auth = await ResolveAuthAsync(provider.Id, useRuntimeOverride: false, cancellationToken)
            .ConfigureAwait(false);
        if (!auth.Authenticated || string.IsNullOrWhiteSpace(auth.Key) || auth.Source == "not required" ||
            auth.Key == GoogleVertexProviderOptions.AdcCredentialMarker || auth.Key == "not-configured" ||
            auth.Key == "not-needed") return null;
        return auth.Key;
    }

    internal async Task<(string Key, bool Authenticated, string Source, IReadOnlyDictionary<string, string> Environment)> ResolveClassifierAccessAsync(
        string providerId, CancellationToken cancellationToken)
    {
        var provider = GetProvider(providerId);
        var stored = await _auth.ReadAsync(provider.Id, cancellationToken);
        var auth = await ResolveAuthCoreAsync(provider, stored, _runtimeApiKeyProvider == provider.Id, true, cancellationToken);
        var environment = new Dictionary<string, string>();
        if (provider.Id == "cloudflare-workers-ai")
        {
            var name = "CLOUDFLARE_ACCOUNT_ID";
            var account = stored?.Type == "api_key" && stored.Env?.TryGetValue(name, out var value) == true
                ? value : _environment(name);
            if (account is not null) environment[name] = account;
        }
        return (auth.Key, auth.Authenticated, auth.Source, environment);
    }

    private async Task<(string Key, bool Authenticated, string Source)> ResolveAuthCoreAsync(ProviderProfile provider,
        StoredCredential? stored, bool useRuntimeOverride, bool allowOAuthRefresh, CancellationToken cancellationToken)
    {
        if (provider.Id == "llama.cpp")
        {
            if (stored is not null && stored.Type != "api_key")
                return ("not-configured", false, "unsupported stored credential type");
            if (ResolveLlamaRouterUrl(stored) is null)
                return ("not-configured", false, "LLAMA_BASE_URL is not configured");
            if (useRuntimeOverride && !string.IsNullOrWhiteSpace(_runtimeApiKey) &&
                (_runtimeApiKeyProvider is null || _runtimeApiKeyProvider == provider.Id))
                return (_runtimeApiKey, true, "command line");
            if (stored?.Key is not null) return (stored.Key, true, "stored API key");
            if (_environment("LLAMA_API_KEY") is { } environmentKey)
                return (environmentKey, true, "LLAMA_API_KEY");
            return ("local", true, "local llama.cpp router");
        }
        if (useRuntimeOverride && !string.IsNullOrWhiteSpace(_runtimeApiKey) &&
            (_runtimeApiKeyProvider is null || _runtimeApiKeyProvider == provider.Id))
            return (_runtimeApiKey, true, "command line");
        if (stored is not null)
        {
            // A stored credential owns the provider: never fall back to ambient credentials.
            if (stored.Type == "oauth")
            {
                if (!provider.OAuthSupported)
                    return ("not-configured", false, "OAuth unsupported for provider");
                if (!_oauth.Supports(provider.Id))
                    return ("not-configured", false, "OAuth adapter unavailable");
                return await _oauth.ResolveAsync(provider.Id, stored, allowOAuthRefresh, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (!provider.ApiKeySupported)
                return ("not-configured", false, "API key authentication unsupported for provider");
            return (stored.Key!, true, "stored API key");
        }
        if (!string.IsNullOrWhiteSpace(provider.ConfiguredApiKey)) return (provider.ConfiguredApiKey, true, "models.json");
        if (provider.Id == "anthropic")
        {
            var authToken = _environment("ANTHROPIC_AUTH_TOKEN");
            if (!string.IsNullOrWhiteSpace(authToken)) return (authToken, true, "ANTHROPIC_AUTH_TOKEN");
            var oauthToken = _environment("ANTHROPIC_OAUTH_TOKEN");
            if (!string.IsNullOrWhiteSpace(oauthToken)) return (oauthToken, true, "ANTHROPIC_OAUTH_TOKEN");
        }
        if (provider.ApiKeyEnvironment is not null && !string.IsNullOrWhiteSpace(_environment(provider.ApiKeyEnvironment)))
            return (_environment(provider.ApiKeyEnvironment)!, true, provider.ApiKeyEnvironment);
        if (provider.Id == "anthropic" && ResolveAnthropicWorkloadIdentity() is not null)
            return (string.Empty, true, "workload identity federation");
        if (provider.GoogleVertex?.HasConfiguredApplicationDefaultCredentials == true)
            return (GoogleVertexProviderOptions.AdcCredentialMarker, true, GoogleVertexProviderOptions.AdcAuthSource);
        if (provider.Bedrock?.AmbientAuthSource is { } bedrockAuthSource)
            return (string.Empty, true, bedrockAuthSource);
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
            if (provider.Models.Count == 0 && provider.Classifiers?.Count > 0)
            {
                RecordAvailableProvider(provider.Id, false);
                continue;
            }
            var auth = await ResolveAuthAsync(provider.Id, useRuntimeOverride: providerId is not null || _runtimeApiKeyProvider == provider.Id, cancellationToken);
            var providerModels = provider.Models;
            if (provider.Id == "llama.cpp")
            {
                await AddLlamaRouterModelsAsync(result, auth, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (provider.Id == "radius" && !_offline)
            {
                try
                {
                    var refreshed = await RadiusModelCatalog.LoadAsync(_http, provider.Id, provider.Endpoint,
                        auth.Authenticated ? auth.Key : null, cancellationToken).ConfigureAwait(false);
                    providerModels = RadiusModelCatalog.Merge(provider.Models, refreshed);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
                {
                    // Keep the pinned public catalogue available if the gateway cannot be refreshed.
                }
            }
            if (!auth.Authenticated)
            {
                result.AddRange(providerModels.Select(model => model with
                {
                    Provider = provider.Id,
                    Available = false,
                    UnavailableReason = auth.Source,
                    Status = model.Status ?? "authentication required"
                }));
                result.AddRange(VirtualModelsFor(provider.Id).Select(model => model with
                {
                    Available = false,
                    UnavailableReason = auth.Source,
                    Status = model.Status ?? "authentication required"
                }));
                continue;
            }
            if (provider.Api == "pi-virtual")
            {
                result.AddRange(VirtualModelsFor(provider.Id));
                continue;
            }
            // These built-ins use pinned, provider-owned catalogues rather than generic /models discovery.
            if (_offline || provider.Id is "xai" or "anthropic" or "mistral" or "azure-openai-responses" or "openai-codex" or "google" or "google-vertex" or "amazon-bedrock" or "radius")
            {
                result.AddRange(providerModels.Select(model => model with { Provider = provider.Id }));
                result.AddRange(VirtualModelsFor(provider.Id));
                continue;
            }
            try
            {
                var discovered = await ModelCatalog.ListAsync(_http,
                    IsOfficialOpenAiEndpoint(provider.Endpoint) ? null : provider.Endpoint,
                    auth.Key, cancellationToken);
                var merged = discovered.Select(model => Merge(provider, model)).ToList();
                foreach (var configured in providerModels.Where(model => merged.All(item => item.Id != model.Id)))
                    merged.Add(configured with { Provider = provider.Id, Available = false, UnavailableReason = "not advertised by provider", Status = configured.Status ?? "unavailable" });
                var virtualModels = VirtualModelsFor(provider.Id);
                var virtualIds = virtualModels.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
                result.AddRange(merged.Where(model => !virtualIds.Contains(model.Id)));
                result.AddRange(virtualModels);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException)
            {
                var safe = SecretRedactor.Redact(error.Message, auth.Key, _runtimeApiKey, provider.ConfiguredApiKey);
                result.AddRange(providerModels.Select(model => model with
                {
                    Provider = provider.Id,
                    Available = false,
                    UnavailableReason = "catalog unavailable: " + safe,
                    Status = "catalog unavailable"
                }));
                result.AddRange(VirtualModelsFor(provider.Id));
            }
        }
        var visible = includeOutOfScope ? result : ApplyScope(result);
        foreach (var provider in providers)
            RecordAvailableProvider(provider.Id, visible.Any(model => model.Provider == provider.Id && model.Available));
        return visible;
    }

    public async Task<ModelDescriptor?> FindPhysicalModelAsync(string provider, string model,
        CancellationToken cancellationToken = default)
    {
        var catalog = await ListModelsAsync(provider, cancellationToken, includeOutOfScope: true);
        return catalog.FirstOrDefault(item => item.Id.Equals(model, StringComparison.Ordinal) &&
            item.Api != VirtualModelContract.Api && item.Available);
    }

    public async Task<ModelSelection> ResolveAsync(string? providerId, string? modelReference,
        CancellationToken cancellationToken = default, bool includeOutOfScope = false)
    {
        var selected = await ResolveSelectionAsync(providerId, modelReference, cancellationToken, includeOutOfScope);
        if (!string.IsNullOrWhiteSpace(_runtimeApiKey))
            Interlocked.CompareExchange(ref _runtimeApiKeyProvider, selected.Provider.Id, null);
        return selected;
    }

    private async Task<ModelSelection> ResolveSelectionAsync(string? providerId, string? modelReference,
        CancellationToken cancellationToken, bool includeOutOfScope)
    {
        ProviderProfile? explicitProvider = providerId is null ? null : GetProvider(providerId);
        var inferredReference = modelReference?.Trim();
        var slash = inferredReference?.IndexOf('/') ?? -1;
        if (explicitProvider is null && slash > 0)
        {
            var prefix = inferredReference![..slash];
            if (_providers.ContainsKey(prefix) || _virtualModels?.Models.Any(item => item.Model.Provider == prefix) == true)
            {
                explicitProvider = GetProvider(prefix);
                inferredReference = inferredReference[(slash + 1)..];
            }
        }
        explicitProvider ??= GetProvider(_providers.ContainsKey("local") ? "local" :
            _providers.ContainsKey("custom") ? "custom" : "openai");
        var reference = inferredReference ?? explicitProvider.Models.FirstOrDefault()?.Id ?? _virtualModels?.List(explicitProvider.Id).FirstOrDefault()?.Id;
        if (string.IsNullOrWhiteSpace(reference)) throw new InvalidOperationException($"Provider '{explicitProvider.Id}' has no models.");
        if (explicitProvider.Classifiers?.Any(item => item.Id.Equals(reference, StringComparison.OrdinalIgnoreCase)) == true &&
            !explicitProvider.Models.Any(item => item.Id.Equals(reference, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Model '{explicitProvider.Id}/{reference}' is a classifier and cannot be selected for chat.");
        if (_virtualModels?.Get(explicitProvider.Id, reference) is { } virtualModel)
        {
            var virtualAuth = await ResolveAuthAsync(explicitProvider.Id, useRuntimeOverride: true, cancellationToken);
            var virtualDescriptor = virtualModel.Model with
            {
                Available = virtualAuth.Authenticated,
                UnavailableReason = virtualAuth.Authenticated ? null : virtualAuth.Source
            };
            RecordAvailableProvider(explicitProvider.Id, virtualDescriptor.Available);
            return new ModelSelection(explicitProvider, virtualDescriptor, virtualAuth.Key, virtualAuth.Authenticated, virtualAuth.Source);
        }
        // An explicitly configured exact ID is usable without /models: many compatible servers
        // do not implement that endpoint, and it must not consume a prompt's first response.
        var configured = (includeOutOfScope ? explicitProvider.Models : ApplyScope(explicitProvider.Models)).Where(item =>
            item.Id.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        var models = configured.Length == 1 && explicitProvider.Id is not ("radius" or "llama.cpp") ? configured :
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
        RecordAvailableProvider(explicitProvider.Id, model.Available);
        Func<CancellationToken, Task<(string Access, string AccountId)>>? oauthCredentialResolver = null;
        if (auth.Source.StartsWith("stored OAuth", StringComparison.Ordinal) &&
            explicitProvider.Id is "openai-codex" or "radius")
            oauthCredentialResolver = cancellationToken =>
                _oauth.ResolveCredentialAsync(explicitProvider.Id, cancellationToken);
        return new ModelSelection(explicitProvider, model, auth.Key, auth.Authenticated, auth.Source,
            oauthCredentialResolver)
        {
            AnthropicAuthToken = auth.Source is "ANTHROPIC_AUTH_TOKEN" or "ANTHROPIC_OAUTH_TOKEN"
                ? auth.Key : explicitProvider.Id == "anthropic" && auth.Key.Contains("sk-ant-oat", StringComparison.Ordinal)
                    ? auth.Key : null,
            AnthropicIsOAuthToken = explicitProvider.Id == "anthropic" &&
                (auth.Source == "ANTHROPIC_OAUTH_TOKEN" || auth.Key.Contains("sk-ant-oat", StringComparison.Ordinal)),
            AnthropicWorkloadIdentity = auth.Source == "workload identity federation"
                ? ResolveAnthropicWorkloadIdentity() : null
        };
    }

    private void RecordAvailableProvider(string providerId, bool available)
    {
        lock (_availableProviderGate) _availableProviderSnapshot[providerId] = available;
    }

    private AnthropicWorkloadIdentityOptions? ResolveAnthropicWorkloadIdentity()
    {
        var ruleId = _environment("ANTHROPIC_FEDERATION_RULE_ID");
        var organizationId = _environment("ANTHROPIC_ORGANIZATION_ID");
        var tokenFile = _environment("ANTHROPIC_IDENTITY_TOKEN_FILE");
        if (string.IsNullOrWhiteSpace(ruleId) || string.IsNullOrWhiteSpace(organizationId) ||
            string.IsNullOrWhiteSpace(tokenFile)) return null;
        return new AnthropicWorkloadIdentityOptions(ruleId, organizationId, tokenFile,
            NullIfWhiteSpace(_environment("ANTHROPIC_SERVICE_ACCOUNT_ID")),
            NullIfWhiteSpace(_environment("ANTHROPIC_WORKSPACE_ID")));
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public Task LoginApiKeyAsync(string provider, string secret, CancellationToken cancellationToken = default)
    {
        var profile = GetProvider(provider);
        if (!profile.ApiKeySupported)
            throw new InvalidOperationException($"Provider '{provider}' requires its OAuth login flow.");
        return _auth.StoreApiKeyAsync(provider, secret, cancellationToken);
    }

    public async Task LoginLlamaRouterAsync(string? apiKey, string serverUrl,
        CancellationToken cancellationToken = default)
    {
        var endpoint = LlamaRouterClient.NormalizeServerUrl(serverUrl);
        _ = await new LlamaRouterClient(_http, endpoint, apiKey).ListAsync(cancellationToken).ConfigureAwait(false);
        await _auth.StoreLlamaRouterAsync(apiKey, endpoint.AbsoluteUri, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<LlamaRouterClient> CreateLlamaRouterClientAsync(CancellationToken cancellationToken)
    {
        var auth = await ResolveAuthAsync("llama.cpp", useRuntimeOverride: true, cancellationToken)
            .ConfigureAwait(false);
        if (!auth.Authenticated) throw new InvalidOperationException(auth.Source);
        var stored = await _auth.ReadAsync("llama.cpp", cancellationToken).ConfigureAwait(false);
        var serverUrl = ResolveLlamaRouterUrl(stored);
        if (serverUrl is null) throw new InvalidOperationException("LLAMA_BASE_URL is not configured");
        return new LlamaRouterClient(_http, serverUrl, auth.Key);
    }

    internal async Task<IReadOnlyList<LlamaRouterModelInfo>> RefreshLlamaRouterCatalogAsync(
        LlamaRouterClient client, CancellationToken cancellationToken)
    {
        await _llamaRouterRefresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await _llamaRouterCache.ReadAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await LlamaRouterCatalog.RefreshAsync(client, previous, cancellationToken)
                .ConfigureAwait(false);
            await _llamaRouterCache.WriteAsync(catalog.Cache, cancellationToken).ConfigureAwait(false);
            var profile = (_llamaRouterProfile ?? _providers["llama.cpp"]) with
            {
                Endpoint = new Uri(client.ServerUrl.AbsoluteUri.TrimEnd('/') + "/v1"),
                Models = catalog.Models,
                Classifiers = catalog.Classifiers
            };
            Volatile.Write(ref _llamaRouterProfile, profile);
            return catalog.Cache.Models;
        }
        finally { _llamaRouterRefresh.Release(); }
    }

    public Task LoginOAuthAsync(string provider, IProviderOAuthInteraction interaction,
        CancellationToken cancellationToken = default)
    {
        var profile = GetProvider(provider);
        if (!profile.OAuthSupported) throw new InvalidOperationException($"Provider '{provider}' has no configured OAuth adapter.");
        return _oauth.LoginAsync(profile.Id, interaction, cancellationToken);
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

    private IReadOnlyList<ModelDescriptor> VirtualModelsFor(string provider) =>
        _virtualModels?.List(provider) ?? [];

    private Uri? ResolveLlamaRouterUrl(StoredCredential? stored)
    {
        var value = stored?.Env?.TryGetValue("LLAMA_BASE_URL", out var storedUrl) == true &&
                    !string.IsNullOrWhiteSpace(storedUrl)
            ? storedUrl : _environment("LLAMA_BASE_URL");
        return string.IsNullOrWhiteSpace(value) ? null : LlamaRouterClient.NormalizeServerUrl(value);
    }

    private async Task AddLlamaRouterModelsAsync(List<ModelDescriptor> result,
        (string Key, bool Authenticated, string Source) auth, CancellationToken cancellationToken)
    {
        var stored = await _auth.ReadAsync("llama.cpp", cancellationToken).ConfigureAwait(false);
        var serverUrl = ResolveLlamaRouterUrl(stored);
        if (serverUrl is null || !auth.Authenticated) return;

        await _llamaRouterRefresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await _llamaRouterCache.ReadAsync(cancellationToken).ConfigureAwait(false);
            LlamaRouterCatalogResult catalog;
            if (_offline)
            {
                catalog = LlamaRouterCatalog.Restore(previous, serverUrl);
            }
            else
            {
                try
                {
                    var client = new LlamaRouterClient(_http, serverUrl, auth.Key);
                    catalog = await LlamaRouterCatalog.RefreshAsync(client, previous, cancellationToken).ConfigureAwait(false);
                    await _llamaRouterCache.WriteAsync(catalog.Cache, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or ArgumentException)
                {
                    catalog = LlamaRouterCatalog.Restore(previous, serverUrl);
                    if (catalog.Models.Count == 0)
                    {
                        var safe = SecretRedactor.Redact(error.Message, auth.Key, _runtimeApiKey);
                        result.AddRange(_llamaRouterProfile?.Models.Select(model => model with
                        {
                            Available = false,
                            UnavailableReason = "catalog unavailable: " + safe,
                            Status = "catalog unavailable"
                        }) ?? []);
                        return;
                    }
                }
            }

            var inferenceUrl = new Uri(serverUrl.AbsoluteUri.TrimEnd('/') + "/v1");
            var profile = (_llamaRouterProfile ?? _providers["llama.cpp"]) with
            {
                Endpoint = inferenceUrl,
                Models = catalog.Models,
                Classifiers = catalog.Classifiers
            };
            Volatile.Write(ref _llamaRouterProfile, profile);
            result.AddRange(catalog.Models);
        }
        finally { _llamaRouterRefresh.Release(); }
    }

    private static ProviderProfile VirtualProvider(string provider) => new(provider, provider,
        new Uri("https://virtual.invalid"), false, false, null, null, [], Api: "pi-virtual", ApiKeySupported: false);

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
