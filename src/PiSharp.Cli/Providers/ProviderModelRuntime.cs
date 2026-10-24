using System.Text.Json;
using PiSharp.Runtime.Classifiers;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.VirtualModels;
using PiSharp.Runtime.Extensions;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli;

public sealed record ProviderProfile(string Id, string Name, Uri Endpoint, bool AuthRequired,
    bool OAuthSupported, string? ApiKeyEnvironment, string? ConfiguredApiKey,
    IReadOnlyList<ModelDescriptor> Models, string? Api = null, JsonElement? Compatibility = null,
    AzureOpenAiProviderOptions? AzureOpenAi = null, bool ApiKeySupported = true,
    GoogleVertexProviderOptions? GoogleVertex = null, BedrockProviderOptions? Bedrock = null, IReadOnlyList<ClassifierModel>? Classifiers = null, IReadOnlyList<ImageModelDescriptor>? Images = null)
{
    internal ExtensionProviderHandle? ExtensionProviderHandle { get; init; }
    internal string? ExtensionPath { get; init; }
}

public sealed record ModelSelection(ProviderProfile Provider, ModelDescriptor Model, string ApiKey,
    bool Authenticated, string AuthSource,
    Func<CancellationToken, Task<(string Access, string AccountId)>>? OAuthCredentialResolver = null)
{
    public string? RequestedThinkingLevel { get; init; }
    public string? ResolutionWarning { get; init; }
    public string? AnthropicAuthToken { get; init; }
    public bool AnthropicIsOAuthToken { get; init; }
    public AnthropicWorkloadIdentityOptions? AnthropicWorkloadIdentity { get; init; }

    public ConnectionSettings Connection
    {
        get
        {
            var endpoint = Model.BaseUrl is { } baseUrl && !baseUrl.Contains("{location}", StringComparison.Ordinal)
                ? new Uri(baseUrl)
                : Provider.ExtensionProviderHandle is { } extensionHandle
                    ? extensionHandle.Definition?.Endpoint : Provider.Endpoint;
            return new ConnectionSettings(Model.Id,
                endpoint is null || ProviderModelRuntime.IsOfficialOpenAiEndpoint(endpoint) ? null : endpoint, ApiKey);
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
    private readonly HttpClient _http;
    private readonly string? _runtimeApiKey;
    private readonly ProviderCredentialResolver _credentials;
    private readonly bool _offline;
    private readonly LlamaRouterDiscovery _llamaDiscovery;
    private readonly AvailableModelSnapshot _available;
    private IReadOnlyList<string> _scope;
    private bool _scopeConfigured;
    private VirtualModelRegistry? _virtualModels;
    private readonly object _extensionProviderGate = new();
    private IReadOnlyDictionary<string, ProviderProfile> _extensionProviders =
        new Dictionary<string, ProviderProfile>(StringComparer.Ordinal);

    private ProviderModelRuntime(Dictionary<string, ProviderProfile> providers, AuthStorage auth,
        ProviderOAuthCoordinator oauth,
        Func<string, string?> environment, HttpClient http, string? runtimeApiKey, IReadOnlyList<string>? scope,
        bool offline, string agentDirectory)
    {
        _providers = providers;
        _auth = auth;
        _oauth = oauth;
        _credentials = new ProviderCredentialResolver(auth, oauth, environment, runtimeApiKey);
        _http = http;
        _runtimeApiKey = runtimeApiKey;
        _offline = offline;
        _scope = scope ?? [];
        _scopeConfigured = scope is not null;
        _available = new AvailableModelSnapshot(providers.Values.Where(provider => provider.ApiKeyEnvironment is { } keyName &&
            !string.IsNullOrWhiteSpace(environment(keyName)) && provider.Models.Any(model => model.Available)));
        _llamaDiscovery = new LlamaRouterDiscovery(agentDirectory, http, providers["llama.cpp"], _available);
    }

    public IReadOnlyList<string> Scope => _scope;
    public bool HasScope => _scopeConfigured;
    public VirtualModelRegistry? VirtualModels => _virtualModels;
    public int AvailableModelProviderCount => _available.ProviderCount;
    internal IReadOnlyList<ModelDescriptor> GetAvailableModelSnapshot() => _available.Models;

    public IReadOnlyList<ProviderProfile> Providers
    {
        get
        {
            var providers = new Dictionary<string, ProviderProfile>(_providers, StringComparer.Ordinal);
            foreach (var (id, provider) in Volatile.Read(ref _extensionProviders))
                providers[id] = provider;
            if (_llamaDiscovery.Profile is { } llamaRouter)
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
        ValidateVirtualModelRegistry(registry);
        _virtualModels = registry;
    }

    internal void ValidateVirtualModelRegistry(VirtualModelRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ValidateVirtualModelRegistry(registry, Volatile.Read(ref _extensionProviders));
    }

    internal void ValidateExtensionModelRegistrations(IEnumerable<RegisteredExtensionProvider> providers,
        VirtualModelRegistry virtualModels)
    {
        ArgumentNullException.ThrowIfNull(virtualModels);
        var snapshot = CreateExtensionProviderSnapshot(providers);
        ValidateVirtualModelRegistry(virtualModels, snapshot);
    }

    private void ValidateVirtualModelRegistry(VirtualModelRegistry registry,
        IReadOnlyDictionary<string, ProviderProfile> extensionProviders)
    {
        foreach (var item in registry.Models)
            if (item.Model.Provider is { } providerId &&
                (_providers.GetValueOrDefault(providerId) ?? extensionProviders.GetValueOrDefault(providerId)) is { } provider &&
                provider.Models.Any(model => string.Equals(model.Id, item.Model.Id, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Virtual model {item.Model.Provider}/{item.Model.Id} registered by " +
                    $"{item.ExtensionPath} conflicts with a physical model owned by {provider.ExtensionPath ?? provider.Id}.");
    }

    internal void RestoreVirtualModelRegistry(VirtualModelRegistry? registry) => _virtualModels = registry;

    internal void ValidateExtensionProviders(IEnumerable<RegisteredExtensionProvider> registrations) =>
        _ = CreateExtensionProviderSnapshot(registrations);

    internal void SetExtensionProviders(IEnumerable<RegisteredExtensionProvider> registrations,
        bool deactivateReplaced = true)
    {
        IReadOnlyDictionary<string, ProviderProfile> previous;
        IReadOnlyDictionary<string, ProviderProfile> snapshot;
        lock (_extensionProviderGate)
        {
            previous = Volatile.Read(ref _extensionProviders);
            snapshot = CreateExtensionProviderSnapshot(registrations, previous);
            Volatile.Write(ref _extensionProviders, snapshot);
        }
        if (deactivateReplaced) DeactivateProvidersNotIn(previous, snapshot);
    }

    internal IReadOnlyDictionary<string, ProviderProfile> CaptureExtensionProviderSnapshot() =>
        Volatile.Read(ref _extensionProviders);

    internal void RestoreExtensionProviderSnapshot(IReadOnlyDictionary<string, ProviderProfile> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        IReadOnlyDictionary<string, ProviderProfile> current;
        lock (_extensionProviderGate)
        {
            current = Volatile.Read(ref _extensionProviders);
            Volatile.Write(ref _extensionProviders, snapshot);
        }
        DeactivateProvidersNotIn(current, snapshot);
    }

    internal void CompleteExtensionProviderReplacement(IReadOnlyDictionary<string, ProviderProfile> previous) =>
        DeactivateProvidersNotIn(previous, Volatile.Read(ref _extensionProviders));

    private static void DeactivateProvidersNotIn(IReadOnlyDictionary<string, ProviderProfile> previous,
        IReadOnlyDictionary<string, ProviderProfile> current)
    {
        var currentHandles = current.Values.Select(provider => provider.ExtensionProviderHandle)
            .Where(handle => handle is not null).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var provider in previous.Values)
            if (provider.ExtensionProviderHandle is { } handle && !currentHandles.Contains(handle))
                handle.Deactivate();
    }

    private IReadOnlyDictionary<string, ProviderProfile> CreateExtensionProviderSnapshot(
        IEnumerable<RegisteredExtensionProvider> registrations,
        IReadOnlyDictionary<string, ProviderProfile>? reuseFrom = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var snapshot = new Dictionary<string, ProviderProfile>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            var definition = registration.Definition;
            if (_providers.TryGetValue(definition.Id, out var configured))
                throw new InvalidOperationException($"Provider '{definition.Id}' registered by extension " +
                    $"'{registration.ExtensionPath}' conflicts with configured provider '{configured.Id}'.");
            var models = definition.Models.Select(model => new ModelDescriptor(model.Id, definition.Name,
                model.ContextLength, "extension provider", model.Reasoning,
                Provider: definition.Id, Name: model.Name ?? model.Id,
                MaxOutputTokens: model.MaxOutputTokens, Input: model.Input?.ToArray() ?? ["text"],
                Api: NativeExtensions.PiSharpExtensionProviderContract.ChatApi,
                ThinkingLevelMap: model.ThinkingLevelMap?.Clone())).ToArray();
            var handle = reuseFrom?.GetValueOrDefault(definition.Id)?.ExtensionProviderHandle;
            if (handle?.Definition is not { } existingDefinition ||
                !ReferenceEquals(existingDefinition, definition) || handle.ExtensionPath != registration.ExtensionPath)
                handle = new ExtensionProviderHandle(definition, registration.ExtensionPath);
            var profile = new ProviderProfile(definition.Id, definition.Name,
                definition.Endpoint ?? new Uri("https://extension.invalid"), definition.AuthRequired, false,
                definition.ApiKeyEnvironment, null, models,
                NativeExtensions.PiSharpExtensionProviderContract.ChatApi)
            {
                ExtensionProviderHandle = handle,
                ExtensionPath = registration.ExtensionPath
            };
            if (!snapshot.TryAdd(definition.Id, profile))
                throw new InvalidOperationException($"Provider '{definition.Id}' is registered more than once, including " +
                    $"extension '{registration.ExtensionPath}'.");
        }
        return snapshot;
    }

    private ProviderProfile? GetConfiguredProvider(string id) =>
        _providers.GetValueOrDefault(id) ?? Volatile.Read(ref _extensionProviders).GetValueOrDefault(id);

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
        return runtime;
    }

    public ProviderProfile GetProvider(string id) => id == "llama.cpp" && _llamaDiscovery.Profile is { } llamaRouter
        ? llamaRouter : GetConfiguredProvider(id) is { } provider ? provider :
        _virtualModels?.Models.Any(item => item.Model.Provider == id) == true ? VirtualProvider(id) :
        throw new ArgumentException($"Unknown provider '{id}'. Available providers: {string.Join(", ", Providers.Select(item => item.Id).Order())}.");

    public void SetScope(IEnumerable<string>? patterns)
    {
        if (patterns is null)
        {
            ClearScope();
            return;
        }
        var values = patterns.Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
        _scope = values;
        _scopeConfigured = true;
    }

    public void ClearScope()
    {
        _scope = [];
        _scopeConfigured = false;
    }

    internal async Task<ModelScopeResolution> ResolveScopeAsync(CancellationToken cancellationToken = default)
    {
        if (!_scopeConfigured) return new([], []);
        var catalog = await ListModelsAsync(cancellationToken: cancellationToken, includeOutOfScope: true)
            .ConfigureAwait(false);
        return ModelScopeResolver.Resolve(_scope, catalog.Where(model => model.Available).ToArray());
    }

    internal string? GetScopeThinkingLevel(string providerId, string modelId)
    {
        if (!_scopeConfigured) return null;
        var models = Providers.SelectMany(provider => provider.Models.Select(model => model with { Provider = provider.Id }))
            .Concat(_available.Models)
            .Concat(_virtualModels?.Models.Select(item => item.Model) ?? [])
            .Where(model => model.Provider is not null)
            .GroupBy(model => (Provider: model.Provider!, model.Id), StringTupleComparer.Instance)
            .Select(group => group.First()).ToArray();
        var resolved = ModelScopeResolver.Resolve(_scope, models);
        return resolved.Models.FirstOrDefault(item => item.Model.Provider?.Equals(providerId,
            StringComparison.OrdinalIgnoreCase) == true && item.Model.Id.Equals(modelId,
            StringComparison.OrdinalIgnoreCase))?.ThinkingLevel;
    }

    public Task<(string Key, bool Authenticated, string Source)> ResolveAuthAsync(string providerId,
        bool useRuntimeOverride = false, CancellationToken cancellationToken = default,
        bool allowOAuthRefresh = true) =>
        _credentials.ResolveAsync(GetProvider(providerId), useRuntimeOverride, cancellationToken, allowOAuthRefresh);

    internal Task<(string Key, bool Authenticated, string Source, IReadOnlyDictionary<string, string> Environment)> ResolveClassifierAccessAsync(
        string providerId, CancellationToken cancellationToken) =>
        _credentials.ResolveClassifierAsync(GetProvider(providerId), cancellationToken);

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

    public async Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(string? providerId = null,
        CancellationToken cancellationToken = default, bool includeOutOfScope = false)
    {
        var providers = providerId is null ? Providers : [GetProvider(providerId)];
        var result = new List<ModelDescriptor>();
        foreach (var provider in providers)
        {
            if (provider.Models.Count == 0 && provider.Classifiers?.Count > 0)
            {
                continue;
            }
            var auth = await ResolveAuthAsync(provider.Id, useRuntimeOverride: providerId is not null || _credentials.BoundProvider == provider.Id, cancellationToken);
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
                    providerModels = refreshed;
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
            if (provider.ExtensionProviderHandle is not null)
            {
                result.AddRange(providerModels.Select(model => model with { Provider = provider.Id }));
                result.AddRange(VirtualModelsFor(provider.Id));
                continue;
            }
            // These built-ins use pinned, provider-owned catalogues rather than generic /models discovery.
            if (_offline || provider.Id is "xai" or "anthropic" or "mistral" or "huggingface" or "azure" or "azure-openai-responses" or "openai-codex" or "google" or "google-vertex" or "amazon-bedrock" or "radius")
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
        // A provider-specific lookup (for example /llama's manager panel) does not refresh the
        // interactive model snapshot that drives Pi's footer provider count. Count llama.cpp
        // only after the shared catalog is refreshed or a model is selected for the session.
        if (providerId is null)
        {
            _available.RecordModels(result);
        }
        else
        {
            _available.RecordModels(providerId, result);
        }
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
        CancellationToken cancellationToken = default, bool includeOutOfScope = false,
        bool allowThinkingSuffix = true, string? customThinkingLevel = null)
    {
        var selected = await ResolveSelectionAsync(providerId, modelReference, cancellationToken, includeOutOfScope,
            allowThinkingSuffix, customThinkingLevel);
        _credentials.BindRuntimeCredential(selected.Provider.Id);
        return selected;
    }

    private async Task<ModelSelection> ResolveSelectionAsync(string? providerId, string? modelReference,
        CancellationToken cancellationToken, bool includeOutOfScope, bool allowThinkingSuffix = true,
        string? customThinkingLevel = null)
    {
        if (providerId is null && !string.IsNullOrWhiteSpace(modelReference))
            return await ResolveAcrossProvidersAsync(modelReference, cancellationToken, includeOutOfScope,
                    allowThinkingSuffix, customThinkingLevel)
                .ConfigureAwait(false);

        ProviderProfile? explicitProvider = providerId is null ? null : GetProvider(providerId);
        var inferredReference = modelReference?.Trim();
        var slash = inferredReference?.IndexOf('/') ?? -1;
        if (explicitProvider is null && slash > 0)
        {
            var prefix = inferredReference![..slash];
            if (GetConfiguredProvider(prefix) is not null ||
                _virtualModels?.Models.Any(item => item.Model.Provider == prefix) == true)
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
            _available.RecordProvider(explicitProvider.Id, virtualDescriptor.Available);
            return new ModelSelection(explicitProvider, virtualDescriptor, virtualAuth.Key, virtualAuth.Authenticated, virtualAuth.Source);
        }
        // An explicitly configured exact ID is usable without /models: many compatible servers
        // do not implement that endpoint, and it must not consume a prompt's first response.
        var configuredModels = explicitProvider.Models.Concat(_available.Models.Where(model =>
            model.Provider?.Equals(explicitProvider.Id, StringComparison.OrdinalIgnoreCase) == true))
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First());
        var configured = (includeOutOfScope ? configuredModels : ApplyScope(configuredModels)).Where(item =>
            item.Id.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        var models = configured.Length == 1 && explicitProvider.Id is not ("radius" or "llama.cpp") ? configured :
            await ListModelsAsync(explicitProvider.Id, cancellationToken, includeOutOfScope);
        var matches = models.Where(item => item.Id.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        string? requestedThinkingLevel = null;
        if (matches.Length == 0)
            matches = BestFuzzyMatches(models.Where(item => item.Id.Contains(reference, StringComparison.OrdinalIgnoreCase) ||
                (item.Name?.Contains(reference, StringComparison.OrdinalIgnoreCase) ?? false))).Take(1).ToArray();
        if (allowThinkingSuffix && matches.Length == 0 && TrySplitThinkingSuffix(reference, out var baseReference, out var suffixLevel))
        {
            var suffixMatches = models.Where(item => item.Id.Equals(baseReference, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (suffixMatches.Length > 0)
            {
                reference = baseReference;
                requestedThinkingLevel = suffixLevel;
                matches = suffixMatches;
            }
            else
            {
                // Pi treats an otherwise unknown valid suffix as a model-reference suffix for
                // custom-model fallback. Preserve the original spelling when there is no suffix.
                reference = baseReference;
                requestedThinkingLevel = suffixLevel;
            }
        }
        if (matches.Length > 1)
            throw new ArgumentException($"Model '{reference}' is ambiguous: {string.Join(", ", matches.Select(item => explicitProvider.Id + "/" + item.Id))}.");
        if (matches.Length == 0 && _scopeConfigured && !includeOutOfScope)
            throw new ArgumentException($"Model '{explicitProvider.Id}/{reference}' is outside the --models scope.");
        string? resolutionWarning = null;
        var model = matches.Length == 1 ? matches[0] : CreateCustomModel(explicitProvider, reference,
            requestedThinkingLevel ?? customThinkingLevel);
        if (matches.Length == 0)
            resolutionWarning = $"Model \"{reference}\" not found for provider \"{explicitProvider.Id}\". Using custom model id.";
        var auth = await ResolveAuthAsync(explicitProvider.Id, useRuntimeOverride: true, cancellationToken);
        model = model with
        {
            Available = auth.Authenticated && model.Available,
            UnavailableReason = auth.Authenticated ? model.UnavailableReason : auth.Source
        };
        _available.RecordProvider(explicitProvider.Id, model.Available);
        if (model.Available) _available.RecordModel(model);
        Func<CancellationToken, Task<(string Access, string AccountId)>>? oauthCredentialResolver = null;
        if (auth.Source.StartsWith("stored OAuth", StringComparison.Ordinal) &&
            explicitProvider.Id is "openai-codex" or "radius")
            oauthCredentialResolver = cancellationToken =>
                _oauth.ResolveCredentialAsync(explicitProvider.Id, cancellationToken);
        return new ModelSelection(explicitProvider, model, auth.Key, auth.Authenticated, auth.Source,
            oauthCredentialResolver)
        {
            RequestedThinkingLevel = requestedThinkingLevel,
            ResolutionWarning = resolutionWarning,
            AnthropicAuthToken = auth.Source is "ANTHROPIC_AUTH_TOKEN" or "ANTHROPIC_OAUTH_TOKEN"
                ? auth.Key : explicitProvider.Id == "anthropic" && auth.Key.Contains("sk-ant-oat", StringComparison.Ordinal)
                    ? auth.Key : null,
            AnthropicIsOAuthToken = explicitProvider.Id == "anthropic" &&
                (auth.Source == "ANTHROPIC_OAUTH_TOKEN" || auth.Key.Contains("sk-ant-oat", StringComparison.Ordinal)),
            AnthropicWorkloadIdentity = auth.Source == "workload identity federation"
                ? _credentials.ResolveAnthropicWorkloadIdentity() : null
        };
    }

    private async Task<ModelSelection> ResolveAcrossProvidersAsync(string modelReference,
        CancellationToken cancellationToken, bool includeOutOfScope, bool allowThinkingSuffix,
        string? customThinkingLevel)
    {
        var providers = Providers;
        var allModels = providers.SelectMany(provider => provider.Models.Select(model =>
                model with { Provider = provider.Id }))
            .Concat(_available.Models)
            .Concat(_virtualModels?.Models.Select(item => item.Model) ?? [])
            .Where(model => model.Provider is not null)
            .GroupBy(model => (Provider: model.Provider!, model.Id), StringTupleComparer.Instance)
            .Select(group => group.First())
            .ToArray();
        var candidates = includeOutOfScope ? allModels : ApplyScope(allModels);
        var text = modelReference.Trim();
        var matches = FindExactReferenceCandidates(text, candidates, out var namedCandidate);
        string? requestedThinkingLevel = null;

        if (matches.Length == 0)
        {
            var prefixSlash = text.IndexOf('/');
            var prefixProvider = prefixSlash > 0
                ? providers.FirstOrDefault(provider => provider.Id.Equals(text[..prefixSlash], StringComparison.OrdinalIgnoreCase))
                : null;
            var fuzzyText = text;
            if (prefixProvider is not null) fuzzyText = text[(prefixSlash + 1)..];
            var fuzzyCandidates = candidates.Where(model =>
                (prefixProvider is null || model.Provider!.Equals(prefixProvider.Id, StringComparison.OrdinalIgnoreCase)) &&
                (model.Id.Contains(fuzzyText, StringComparison.OrdinalIgnoreCase) ||
                 (model.Name?.Contains(fuzzyText, StringComparison.OrdinalIgnoreCase) ?? false))).ToArray();
            if (fuzzyCandidates.Length > 0)
            {
                if (prefixProvider is null && fuzzyCandidates.Select(model => model.Provider)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                {
                    var usable = _runtimeApiKey is null
                        ? await GetAuthenticatedCandidatesAsync(fuzzyCandidates, cancellationToken).ConfigureAwait(false)
                        : [];
                    if (usable.Length > 0) fuzzyCandidates = usable;
                }
                matches = BestFuzzyMatches(fuzzyCandidates);
            }
        }

        // Pi resolves exact and fuzzy matches against the entire reference before interpreting a
        // valid thinking suffix. Literal IDs and names containing `:high` therefore take priority.
        if (allowThinkingSuffix && matches.Length == 0 && TrySplitThinkingSuffix(text, out var baseReference, out var suffixLevel))
        {
            text = baseReference;
            matches = FindExactReferenceCandidates(text, candidates, out namedCandidate);
            requestedThinkingLevel = suffixLevel;
            if (matches.Length == 0)
            {
                var suffixSlash = text.IndexOf('/');
                var suffixProvider = suffixSlash > 0
                    ? providers.FirstOrDefault(provider => provider.Id.Equals(text[..suffixSlash], StringComparison.OrdinalIgnoreCase))
                    : null;
                var suffixFuzzyText = suffixProvider is null ? text : text[(suffixSlash + 1)..];
                var suffixFuzzyCandidates = candidates.Where(model =>
                    (suffixProvider is null || model.Provider!.Equals(suffixProvider.Id, StringComparison.OrdinalIgnoreCase)) &&
                    (model.Id.Contains(suffixFuzzyText, StringComparison.OrdinalIgnoreCase) ||
                     (model.Name?.Contains(suffixFuzzyText, StringComparison.OrdinalIgnoreCase) ?? false))).ToArray();
                if (suffixFuzzyCandidates.Length > 0)
                {
                    if (suffixProvider is null && suffixFuzzyCandidates.Select(model => model.Provider)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    {
                        var usable = _runtimeApiKey is null
                            ? await GetAuthenticatedCandidatesAsync(suffixFuzzyCandidates, cancellationToken).ConfigureAwait(false)
                            : [];
                        if (usable.Length > 0) suffixFuzzyCandidates = usable;
                    }
                    matches = BestFuzzyMatches(suffixFuzzyCandidates);
                }
            }
        }

        if (matches.Length == 0)
        {
            if (!includeOutOfScope && _scopeConfigured && allModels.Any(model =>
                    model.Id.Equals(text, StringComparison.OrdinalIgnoreCase) ||
                    model.Id.Equals(modelReference.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Model '{modelReference}' is outside the --models scope.");

            var slash = modelReference.IndexOf('/');
            var namedProvider = slash > 0
                ? providers.FirstOrDefault(provider => provider.Id.Equals(modelReference[..slash], StringComparison.OrdinalIgnoreCase))
                : null;
            var fallbackProvider = namedProvider ?? GetProvider(_providers.ContainsKey("local") ? "local" :
                _providers.ContainsKey("custom") ? "custom" : "openai");
            var fallbackReference = namedProvider is null ? text : modelReference[(slash + 1)..];
            var fallback = await ResolveSelectionAsync(fallbackProvider.Id, fallbackReference,
                cancellationToken, includeOutOfScope, allowThinkingSuffix, customThinkingLevel).ConfigureAwait(false);
            return fallback with { RequestedThinkingLevel = requestedThinkingLevel ?? fallback.RequestedThinkingLevel };
        }

        var byProvider = matches.GroupBy(model => model.Provider!, StringComparer.OrdinalIgnoreCase).ToArray();
        ModelDescriptor selected;
        if (byProvider.Length == 1)
        {
            selected = matches[0];
        }
        else
        {
            var named = namedCandidate;
            if (named is null)
            {
                var slash = text.IndexOf('/');
                var namedProvider = slash > 0
                    ? providers.FirstOrDefault(provider => provider.Id.Equals(text[..slash], StringComparison.OrdinalIgnoreCase))
                    : null;
                var namedModelId = namedProvider is null ? null : text[(slash + 1)..];
                named = namedProvider is null ? null : matches.FirstOrDefault(model =>
                    model.Provider!.Equals(namedProvider.Id, StringComparison.OrdinalIgnoreCase) &&
                    model.Id.Equals(namedModelId, StringComparison.OrdinalIgnoreCase));
            }
            var others = matches.Where(model => model != named).ToArray();
            if (named is not null)
            {
                var namedAuth = await ResolveAuthAsync(named.Provider!, useRuntimeOverride: false,
                    cancellationToken, allowOAuthRefresh: false).ConfigureAwait(false);
                if (namedAuth.Authenticated)
                {
                    selected = named;
                }
                else
                {
                    var usableOthers = _runtimeApiKey is null
                        ? await GetAuthenticatedCandidatesAsync(others, cancellationToken).ConfigureAwait(false)
                        : [];
                    if (usableOthers.Length == 1) selected = usableOthers[0];
                    else if (usableOthers.Length == 0) selected = named;
                    else throw AmbiguousModelReference(modelReference, matches, usableOthers.Length,
                        inlineApiKey: _runtimeApiKey is not null);
                }
            }
            else
            {
                var usable = _runtimeApiKey is null
                    ? await GetAuthenticatedCandidatesAsync(matches, cancellationToken).ConfigureAwait(false)
                    : [];
                if (usable.Length == 1) selected = usable[0];
                else if (usable.Length == 0) throw AmbiguousModelReference(modelReference, matches, 0,
                    inlineApiKey: _runtimeApiKey is not null);
                else throw AmbiguousModelReference(modelReference, matches, usable.Length,
                    inlineApiKey: _runtimeApiKey is not null);
            }
        }

        var resolved = await ResolveSelectionAsync(selected.Provider, selected.Id,
            cancellationToken, includeOutOfScope).ConfigureAwait(false);
        return resolved with { RequestedThinkingLevel = requestedThinkingLevel };
    }

    private async Task<ModelDescriptor[]> GetAuthenticatedCandidatesAsync(IEnumerable<ModelDescriptor> candidates,
        CancellationToken cancellationToken)
    {
        var result = new List<ModelDescriptor>();
        foreach (var candidate in candidates.GroupBy(model => model.Provider!, StringComparer.OrdinalIgnoreCase))
        {
            var auth = await ResolveAuthAsync(candidate.Key, useRuntimeOverride: false,
                cancellationToken, allowOAuthRefresh: false).ConfigureAwait(false);
            if (auth.Authenticated) result.AddRange(candidate);
        }
        return result.ToArray();
    }

    private static ModelDescriptor[] BestFuzzyMatches(IEnumerable<ModelDescriptor> candidates)
    {
        var matches = candidates.ToArray();
        if (matches.Length <= 1) return matches;
        var aliases = matches.Where(model => !System.Text.RegularExpressions.Regex.IsMatch(model.Id,
            "-\\d{8}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            model.Id.EndsWith("-latest", StringComparison.OrdinalIgnoreCase)).ToArray();
        var ranked = (aliases.Length > 0 ? aliases : matches)
            .OrderByDescending(model => model.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var bestId = ranked[0].Id;
        return ranked.Where(model => model.Id.Equals(bestId, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private ModelDescriptor[] FindExactReferenceCandidates(string reference,
        IReadOnlyList<ModelDescriptor> candidates, out ModelDescriptor? named)
    {
        var matches = candidates.Where(model => model.Id.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToList();
        ModelDescriptor? namedCandidate = null;
        var slash = reference.IndexOf('/');
        if (slash > 0)
        {
            var provider = Providers.FirstOrDefault(item =>
                item.Id.Equals(reference[..slash], StringComparison.OrdinalIgnoreCase));
            if (provider is not null)
            {
                namedCandidate = candidates.FirstOrDefault(model => model.Provider!.Equals(provider.Id, StringComparison.OrdinalIgnoreCase) &&
                    model.Id.Equals(reference[(slash + 1)..], StringComparison.OrdinalIgnoreCase)) ??
                    matches.FirstOrDefault(model => model.Provider!.Equals(provider.Id, StringComparison.OrdinalIgnoreCase));
                if (namedCandidate is not null && matches.All(model => model != namedCandidate)) matches.Insert(0, namedCandidate);
            }
        }
        named = namedCandidate;
        return matches.ToArray();
    }

    private static ArgumentException AmbiguousModelReference(string reference,
        IEnumerable<ModelDescriptor> candidates, int usableCount, bool inlineApiKey = false)
    {
        var authHint = inlineApiKey ? "--api-key does not choose a provider." : usableCount == 0
            ? "No matching provider is authenticated."
            : "More than one matching provider is authenticated.";
        return new($"Model \"{reference}\" is ambiguous across providers: {string.Join(", ", candidates
            .Select(model => $"{model.Provider}/{model.Id}").Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase))}. {authHint} Use --provider or provider/model.");
    }

    private ModelDescriptor CreateCustomModel(ProviderProfile provider, string modelId,
        string? thinkingLevel)
    {
        var models = provider.Models.Concat(_available.Models.Where(model =>
                model.Provider?.Equals(provider.Id, StringComparison.OrdinalIgnoreCase) == true))
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        var defaultId = PinnedProviderModelDefaults.GetModel(provider.Id);
        var baseModel = (defaultId is null ? null : models.FirstOrDefault(model =>
                model.Id.Equals(defaultId, StringComparison.Ordinal))) ?? models.FirstOrDefault();
        var custom = baseModel is null
            ? new ModelDescriptor(modelId, provider.Id, null, "custom model ID", Provider: provider.Id)
            : baseModel with { Id = modelId, Name = modelId, Status = "custom model ID", Provider = provider.Id };
        if (thinkingLevel is not null && thinkingLevel != "off")
            custom = custom with { Reasoning = true };
        return custom;
    }

    private static bool TrySplitThinkingSuffix(string reference, out string modelId, out string level)
    {
        var colon = reference.LastIndexOf(':');
        modelId = colon < 0 ? reference : reference[..colon];
        level = colon < 0 ? string.Empty : reference[(colon + 1)..];
        return colon > 0 && ThinkingLevels.IsValid(level);
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Provider, string Id)>
    {
        public static StringTupleComparer Instance { get; } = new();
        public bool Equals((string Provider, string Id) x, (string Provider, string Id) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Provider, y.Provider) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Id, y.Id);
        public int GetHashCode((string Provider, string Id) value) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Provider),
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Id));
    }

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
        var serverUrl = _credentials.ResolveRouterUrl(stored);
        if (serverUrl is null) throw new InvalidOperationException("LLAMA_BASE_URL is not configured");
        return new LlamaRouterClient(_http, serverUrl, auth.Key);
    }

    internal Task<IReadOnlyList<LlamaRouterModelInfo>> RefreshLlamaRouterCatalogAsync(
        LlamaRouterClient client, CancellationToken cancellationToken) =>
        _llamaDiscovery.RefreshAsync(client, cancellationToken);

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
        if (!_scopeConfigured) return all;
        return ModelScopeResolver.Resolve(_scope, all).Models.Select(item => item.Model).ToArray();
    }

    private IReadOnlyList<ModelDescriptor> VirtualModelsFor(string provider) =>
        _virtualModels?.List(provider) ?? [];

    private async Task AddLlamaRouterModelsAsync(List<ModelDescriptor> result,
        (string Key, bool Authenticated, string Source) auth, CancellationToken cancellationToken)
    {
        var stored = await _auth.ReadAsync("llama.cpp", cancellationToken).ConfigureAwait(false);
        var serverUrl = _credentials.ResolveRouterUrl(stored);
        if (serverUrl is null || !auth.Authenticated) return;

        await _llamaDiscovery.AddModelsAsync(result, serverUrl, auth, _offline, _runtimeApiKey, cancellationToken);
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
            SamplingParametersByThinkingLevel = ModelMetadataJson.MergeSamplingParametersByThinkingLevel(
                configured?.SamplingParametersByThinkingLevel, discovered.SamplingParametersByThinkingLevel),
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
