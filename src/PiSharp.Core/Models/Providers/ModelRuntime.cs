using PiSharp.Core.Models.Auth;

namespace PiSharp.Core.Models.Providers;

/// <summary>
/// A credential change committed, but the local model/auth snapshot could not be
/// synchronized (pinned Pi: CredentialSynchronizationError).
/// </summary>
public sealed class CredentialSynchronizationError : Exception
{
    public CredentialSynchronizationError(
        string providerId,
        string operation,
        Credential? credential,
        Exception? innerException)
        : base($"Credential {operation} committed for {providerId}, but local synchronization failed", innerException)
    {
        ProviderId = providerId;
        Operation = operation;
        Credential = credential;
    }

    public string ProviderId { get; }
    public string Operation { get; }
    public Credential? Credential { get; }
}

/// <summary>Options for creating a model runtime (pinned Pi: CreateModelRuntimeOptions).</summary>
public sealed class CreateModelRuntimeOptions
{
    /// <summary>Credential storage. Defaults to the file at the agent dir.</summary>
    public ICredentialStore? Credentials { get; init; }

    /// <summary>Auth.json path (used when Credentials is null).</summary>
    public string? AuthPath { get; init; }

    /// <summary>models.json path; null disables models.json.</summary>
    public string? ModelsPath { get; init; }

    /// <summary>Models store; defaults to a file store next to models.json.</summary>
    public IModelsStore? ModelsStore { get; init; }

    /// <summary>Allow CreateAsync to refresh model catalogs over the network.</summary>
    public bool AllowModelNetwork { get; init; }

    /// <summary>Timeout for the create-time network model refresh.</summary>
    public int? ModelRefreshTimeoutMs { get; init; }

    /// <summary>Remote catalog base URL override.</summary>
    public string? CatalogBaseUrl { get; init; }

    /// <summary>Caller cancellation for initial cache restoration and availability checks.</summary>
    public CancellationToken Signal { get; init; }

    /// <summary>Skip initial catalog and availability refresh.</summary>
    public bool RefreshOnCreate { get; init; } = true;

    /// <summary>
    /// Built-in providers; defaults to the reduced PiSharp built-in catalogue.
    /// Providers not wrapped by the remote catalog must already carry their behavior.
    /// </summary>
    public IReadOnlyList<ProviderSpec>? Builtins { get; init; }

    /// <summary>Whether network operations are allowed (default: PI_OFFLINE unset).</summary>
    public bool? NetworkEnabled { get; init; }
}

/// <summary>
/// Application-owned model/provider/auth runtime (pinned Pi: ModelRuntime). Owns the
/// provider registry composition (built-ins + models.json), the catalog refresh, the
/// availability snapshot, credential operations (login/logout/runtime keys), and auth
/// resolution with configured model headers. Streaming adapters live in PiSharp.Cli.
/// </summary>
public sealed class ModelRuntime
{
    private sealed class Snapshot
    {
        public required IReadOnlyList<ModelInfo> All { get; init; }
        public required IReadOnlyList<ModelInfo> Available { get; init; }
        public required ISet<string> ConfiguredProviders { get; init; }
        public required ISet<string> StoredProviders { get; init; }
        public required IReadOnlyDictionary<string, AuthCheck?> Auth { get; init; }
    }

    private readonly ModelsCatalog _models;
    private readonly RuntimeCredentialStore _credentials;
    private readonly Dictionary<string, ProviderSpec> _defaultBuiltins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProviderSpec> _builtins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProviderSpec> _registeredProviders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _compositionErrors = new(StringComparer.Ordinal);
    private readonly string? _modelsPath;
    private readonly bool _modelNetworkEnabled;
    private readonly object _gate = new();
    private ModelConfig _config;
    private Snapshot _snapshot = new()
    {
        All = Array.Empty<ModelInfo>(),
        Available = Array.Empty<ModelInfo>(),
        ConfiguredProviders = new HashSet<string>(StringComparer.Ordinal),
        StoredProviders = new HashSet<string>(StringComparer.Ordinal),
        Auth = new Dictionary<string, AuthCheck?>(StringComparer.Ordinal),
    };
    private int _availabilityRefreshSeq;
    private int _availabilityErrorSeq;

    /// <summary>
    /// Bounded retries for a provider availability pass invalidated by a concurrent full
    /// refresh; the invalidation is finite (one bump per in-flight full refresh), so the
    /// pass converges in one or two attempts in practice.
    /// </summary>
    private const int MaxProviderAvailabilityRetries = 5;
    private readonly Dictionary<string, int> _providerAvailabilitySeq = new(StringComparer.Ordinal);
    private string? _availabilityError;
    private readonly Dictionary<string, Task> _credentialOperations = new(StringComparer.Ordinal);

    /// <summary>
    /// Serializes runtime refreshes (intentional difference, see docs/PARITY.md): the
    /// background full refresh fired by Register/UnregisterProvider (pinned
    /// `void this.refresh(...)`) must not rebuild while a caller's refresh is in flight —
    /// the rebuild supersedes (cancels) the in-flight provider refresh and its live phase
    /// is silently discarded. The pinned code shares this latent race; serialization
    /// removes it.
    /// </summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>The in-flight background refresh (register/unregister); null when idle.</summary>
    private Task? _pendingRefresh;

    /// <summary>The error from the last background refresh; null when it completed cleanly.</summary>
    private volatile Exception? _lastRefreshError;

    private ModelRuntime(
        RuntimeCredentialStore credentials,
        ModelConfig config,
        string? modelsPath,
        IModelsStore modelsStore,
        IReadOnlyList<ProviderSpec> providers,
        bool modelNetworkEnabled)
    {
        _credentials = credentials;
        _config = config;
        _modelsPath = modelsPath;
        _modelNetworkEnabled = modelNetworkEnabled;
        foreach (var provider in providers)
        {
            _defaultBuiltins[provider.Id] = provider;
            _builtins[provider.Id] = provider;
        }

        _models = new ModelsCatalog(_credentials, modelsStore);
        RebuildProviders();
    }

    /// <summary>Creates the runtime, optionally refreshing catalogs (pinned ModelRuntime.create).</summary>
    public static async Task<ModelRuntime> CreateAsync(CreateModelRuntimeOptions? options = null)
    {
        options ??= new CreateModelRuntimeOptions();
        var baseStore = options.Credentials
            ?? FileCredentialStore.CreateDefault(options.AuthPath);
        var credentials = new RuntimeCredentialStore(baseStore);
        var modelsPath = options.ModelsPath;
        var config = await ModelConfig.LoadAsync(modelsPath);
        var modelsStore = options.ModelsStore
            ?? (modelsPath is not null
                ? new FileModelsStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(modelsPath))!, "models-store.json"))
                : new InMemoryModelsStore());
        var builtins = options.Builtins
            ?? BuiltinProviders.CreateBuiltins().Select(builtin =>
                // The llama.cpp provider syncs its catalog from the local router itself
                // (pinned registers it outside the pi.dev overlay), so skip the overlay.
                builtin.Id == BuiltinProviders.LlamaCppProviderId
                    ? builtin
                    : RemoteCatalogProvider.WithRemoteCatalog(builtin, options.CatalogBaseUrl))
            .ToList();
        var networkEnabled = options.NetworkEnabled
            ?? Environment.GetEnvironmentVariable("PI_OFFLINE") is not ("1" or "true" or "yes");

        var runtime = new ModelRuntime(
            credentials,
            config,
            modelsPath,
            modelsStore,
            builtins,
            networkEnabled);

        var refreshFromNetwork = runtime._modelNetworkEnabled && options.AllowModelNetwork;
        CancellationTokenSource? timeoutCts = null;
        var signal = options.Signal;
        if (refreshFromNetwork && options.ModelRefreshTimeoutMs is { } timeoutMs)
        {
            timeoutCts = new CancellationTokenSource(timeoutMs);
            signal = CancellationTokenSource.CreateLinkedTokenSource(signal, timeoutCts.Token).Token;
        }

        try
        {
            if (options.RefreshOnCreate)
            {
                await runtime.RefreshAsync(new ModelsRefreshOptions
                {
                    AllowNetwork = refreshFromNetwork,
                    CancellationToken = signal,
                });
            }
        }
        finally
        {
            timeoutCts?.Dispose();
        }

        return runtime;
    }

    // ── Provider registry ────────────────────────────────────────────────

    private IEnumerable<string> ProviderIds()
    {
        foreach (var id in _builtins.Keys)
        {
            yield return id;
        }

        foreach (var id in _registeredProviders.Keys)
        {
            yield return id;
        }

        foreach (var id in _config.GetProviderIds())
        {
            yield return id;
        }
    }

    private void RecomposeProvider(string providerId)
    {
        var base_ = _registeredProviders.TryGetValue(providerId, out var registered)
            ? registered
            : _builtins.TryGetValue(providerId, out var builtin)
                ? builtin
                : null;
        var config = _config.GetProvider(providerId);

        lock (_gate)
        {
            if (base_ is null && config is null)
            {
                _models.DeleteProvider(providerId);
                _compositionErrors.Remove(providerId);
                return;
            }

            if (base_ is not null && config is null)
            {
                // No overlays: use the registered/builtin untouched so its auth/login
                // behavior is exact.
                _models.SetProvider(base_);
                _compositionErrors.Remove(providerId);
                return;
            }

            try
            {
                _models.SetProvider(ProviderComposer.ComposeModelProvider(providerId, base_, _config));
                _compositionErrors.Remove(providerId);
            }
            catch (Exception exception)
            {
                _compositionErrors[providerId] = exception.Message;
                if (base_ is not null)
                {
                    _models.SetProvider(base_);
                }
                else
                {
                    _models.DeleteProvider(providerId);
                }
            }
        }
    }

    private void RebuildProviders()
    {
        lock (_gate)
        {
            _models.ClearProviders();
            _compositionErrors.Clear();
            foreach (var providerId in ProviderIds().Distinct(StringComparer.Ordinal))
            {
                RecomposeProvider(providerId);
            }

            UpdateModelSnapshot();
        }
    }

    private void UpdateModelSnapshot()
    {
        lock (_gate)
        {
            var all = _models.GetModels().ToArray();
            _snapshot = new Snapshot
            {
                All = all,
                Available = all.Where(model => _snapshot.ConfiguredProviders.Contains(model.Provider)).ToArray(),
                ConfiguredProviders = _snapshot.ConfiguredProviders,
                StoredProviders = _snapshot.StoredProviders,
                Auth = _snapshot.Auth,
            };
        }
    }

    // ── Availability ─────────────────────────────────────────────────────

    private async Task RunAvailabilityRefreshAsync(int seq, int errorSeq, CancellationToken signal)
    {
        var providers = _models.GetProviders();
        var availableTask = _models.GetAvailableAsync(null, signal);
        var checksTask = Task.WhenAll(providers
            .Select(async provider => (provider.Id, Check: await _models.CheckAuthAsync(provider.Id, signal))));
        var credentialsTask = _credentials.ListAsync(signal);
        var available = await availableTask;
        var checks = await checksTask;
        var credentials = await credentialsTask;

        if (seq != Interlocked.CompareExchange(ref _availabilityRefreshSeq, seq, seq))
        {
            return;
        }

        var auth = new Dictionary<string, AuthCheck?>(StringComparer.Ordinal);
        var configuredProviders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (providerId, check) in checks)
        {
            auth[providerId] = check;
            if (check is not null)
            {
                configuredProviders.Add(providerId);
            }
        }

        lock (_gate)
        {
            _snapshot = new Snapshot
            {
                All = _models.GetModels().ToArray(),
                Available = available.ToArray(),
                ConfiguredProviders = configuredProviders,
                StoredProviders = credentials.Select(entry => entry.ProviderId).ToHashSet(StringComparer.Ordinal),
                Auth = auth,
            };
        }

        if (Interlocked.CompareExchange(ref _availabilityErrorSeq, errorSeq, errorSeq) == errorSeq)
        {
            _availabilityError = null;
        }
    }

    private Task RunAvailabilityRefreshAsync(int seq, int errorSeq, CancellationToken signal, bool recordErrors)
        => recordErrors
            ? RunAvailabilityRefreshAsync(seq, errorSeq, signal)
            : RunAvailabilityRefreshAsync(seq, errorSeq, signal).ContinueWith(
                _ => { },
                CancellationToken.None,
                TaskContinuationOptions.DenyChildAttach,
                TaskScheduler.Default);

    private async Task QueueAvailabilityRefreshAsync(CancellationToken? signal = null)
    {
        var seq = Interlocked.Increment(ref _availabilityRefreshSeq);
        lock (_gate)
        {
            foreach (var providerId in _providerAvailabilitySeq.Keys.ToList())
            {
                _providerAvailabilitySeq[providerId]++;
            }
        }

        var errorSeq = Interlocked.Increment(ref _availabilityErrorSeq);
        try
        {
            await RunAvailabilityRefreshAsync(seq, errorSeq, signal ?? CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (Interlocked.CompareExchange(ref _availabilityErrorSeq, errorSeq, errorSeq) == errorSeq)
            {
                _availabilityError = exception.Message;
            }

            throw;
        }
    }

    private async Task RefreshProviderAvailabilityAsync(string providerId, CancellationToken signal)
    {
        var errorSeq = Interlocked.Increment(ref _availabilityErrorSeq);
        for (var attempt = 1; ; attempt++)
        {
            // Invalidate any full availability pass that started before this credential change.
            Interlocked.Increment(ref _availabilityRefreshSeq);
            int providerSeq;
            lock (_gate)
            {
                providerSeq = (_providerAvailabilitySeq.TryGetValue(providerId, out var current) ? current : 0) + 1;
                _providerAvailabilitySeq[providerId] = providerSeq;
            }

            try
            {
                var availableTask = _models.GetAvailableAsync(providerId, signal);
                var authTask = _models.CheckAuthAsync(providerId, signal);
                var credentialTask = _credentials.ReadAsync(providerId, signal);
                var available = await availableTask;
                var auth = await authTask;
                var credential = await credentialTask;
                signal.ThrowIfCancellationRequested();

                bool stillCurrent;
                lock (_gate)
                {
                    stillCurrent = _providerAvailabilitySeq.TryGetValue(providerId, out var seq) && seq == providerSeq;
                }

                if (!stillCurrent)
                {
                    // A concurrent fire-and-forget full refresh (e.g. from RegisterProvider)
                    // invalidated this pass. Its commit is not visible to the caller of the
                    // awaited credential operation, so re-run with a fresh generation until our
                    // commit lands; after the bound, the in-flight full refresh owns the
                    // snapshot and will publish the latest state.
                    if (attempt < MaxProviderAvailabilityRetries)
                    {
                        continue;
                    }

                    return;
                }


                var configuredProviders = new HashSet<string>(_snapshot.ConfiguredProviders, StringComparer.Ordinal);
                var storedProviders = new HashSet<string>(_snapshot.StoredProviders, StringComparer.Ordinal);
                var authByProvider = new Dictionary<string, AuthCheck?>(_snapshot.Auth, StringComparer.Ordinal);
                if (auth is not null)
                {
                    configuredProviders.Add(providerId);
                    authByProvider[providerId] = auth;
                }
                else
                {
                    configuredProviders.Remove(providerId);
                    authByProvider.Remove(providerId);
                }

                if (credential is not null)
                {
                    storedProviders.Add(providerId);
                }
                else
                {
                    storedProviders.Remove(providerId);
                }

                var all = _models.GetModels().ToArray();
                var availableById = new Dictionary<string, ModelInfo>(StringComparer.Ordinal);
                foreach (var model in _snapshot.Available.Where(m => m.Provider != providerId))
                {
                    availableById[$"{model.Provider}\0{model.Id}"] = model;
                }

                foreach (var model in available)
                {
                    availableById[$"{model.Provider}\0{model.Id}"] = model;
                }

                var newAvailable = new List<ModelInfo>();
                foreach (var model in all)
                {
                    if (availableById.TryGetValue($"{model.Provider}\0{model.Id}", out var availableEntry))
                    {
                        newAvailable.Add(availableEntry);
                    }
                }

                _snapshot = new Snapshot
                {
                    All = all,
                    Available = newAvailable,
                    ConfiguredProviders = configuredProviders,
                    StoredProviders = storedProviders,
                    Auth = authByProvider,
                };

                if (Interlocked.CompareExchange(ref _availabilityErrorSeq, errorSeq, errorSeq) == errorSeq)
                {
                    _availabilityError = null;
                }

                return;
            }
            catch (Exception exception)
            {
                bool current;
                lock (_gate)
                {
                    current = _providerAvailabilitySeq.TryGetValue(providerId, out var seq) && seq == providerSeq;
                }

                if (current &&
                    Interlocked.CompareExchange(ref _availabilityErrorSeq, errorSeq, errorSeq) == errorSeq &&
                    !signal.IsCancellationRequested)
                {
                    _availabilityError = exception.Message;
                }

                throw;
            }
        }
    }

    // ── Read API ─────────────────────────────────────────────────────────

    /// <summary>All registered providers.</summary>
    public IReadOnlyList<ProviderSpec> GetProviders() => _models.GetProviders();

    /// <summary>A registered provider by id.</summary>
    public ProviderSpec? GetProvider(string providerId) => _models.GetProvider(providerId);

    /// <summary>All models, optionally filtered by provider.</summary>
    public IReadOnlyList<ModelInfo> GetModels(string? providerId = null) => _models.GetModels(providerId);

    /// <summary>A single model by provider and id.</summary>
    public ModelInfo? GetModel(string providerId, string modelId) => _models.GetModel(providerId, modelId);

    /// <summary>Whether the provider's auth is currently configured (no network).</summary>
    public Task<AuthCheck?> CheckAuthAsync(string providerId, CancellationToken cancellationToken = default)
        => _models.CheckAuthAsync(providerId, cancellationToken);

    /// <summary>
    /// Available models: for a single provider, runs the provider's credential check;
    /// without one, runs a full availability pass and returns the snapshot.
    /// </summary>
    public async Task<IReadOnlyList<ModelInfo>> GetAvailableAsync(
        string? providerId = null, CancellationToken cancellationToken = default)
    {
        if (providerId is not null)
        {
            var errorSeq = Interlocked.Increment(ref _availabilityErrorSeq);
            try
            {
                var available = await _models.GetAvailableAsync(providerId, cancellationToken);
                if (Interlocked.CompareExchange(ref _availabilityErrorSeq, errorSeq, errorSeq) == errorSeq)
                {
                    _availabilityError = null;
                }

                return available;
            }
            catch (Exception exception)
            {
                if (Interlocked.CompareExchange(ref _availabilityErrorSeq, errorSeq, errorSeq) == errorSeq
                    && !cancellationToken.IsCancellationRequested)
                {
                    _availabilityError = exception.Message;
                }

                throw;
            }
        }

        await QueueAvailabilityRefreshAsync(cancellationToken);
        return _snapshot.Available;
    }

    /// <summary>The last computed available-model snapshot (no work).</summary>
    public IReadOnlyList<ModelInfo> GetAvailableSnapshot() => _snapshot.Available;

    /// <summary>Configuration/composition/availability errors, joined, or null.</summary>
    public string? GetError()
    {
        var errors = new List<string>();
        var configError = _config.GetError();
        if (configError is not null)
        {
            errors.Add(configError);
        }

        foreach (var (providerId, error) in _compositionErrors)
        {
            errors.Add($"Provider \"{providerId}\": {error}");
        }

        if (_availabilityError is not null)
        {
            errors.Add($"Availability refresh: {_availabilityError}");
        }

        return errors.Count > 0 ? string.Join("\n\n", errors) : null;
    }

    // ── Auth status ──────────────────────────────────────────────────────

    /// <summary>Whether the provider's current auth source is OAuth.</summary>
    public bool IsUsingOAuth(string providerId)
        => _snapshot.Auth.GetValueOrDefault(providerId)?.Type == "oauth";

    /// <summary>Whether the provider's current auth is a subscription-backed OAuth.</summary>
    public bool IsUsingSubscription(string providerId)
        => IsUsingOAuth(providerId)
            && _models.GetProvider(providerId)?.Auth.OAuth?.IsSubscription == true;

    /// <summary>Whether the provider has any configured auth.</summary>
    public bool HasConfiguredAuth(string providerId) => _snapshot.ConfiguredProviders.Contains(providerId);

    /// <summary>
    /// Resolves request auth for a provider or model, merging configured model headers
    /// over the resolved auth headers (pinned ModelRuntime.getAuth).
    /// </summary>
    public async Task<AuthResult?> GetAuthAsync(
        ModelInfo model,
        AuthResolutionOverrides? overrides = null,
        CancellationToken cancellationToken = default)
    {
        var resolution = await _models.GetAuthAsync(model, overrides, cancellationToken);
        if (resolution is null)
        {
            return null;
        }

        var configuredHeaders = ProviderComposer.ResolveConfiguredModelHeaders(
            model,
            _config.GetProvider(model.Provider),
            new Dictionary<string, string>(StringComparer.Ordinal));
        if (configuredHeaders is null)
        {
            return resolution;
        }

        var merged = new Dictionary<string, string>(resolution.Auth.Headers ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var (name, value) in configuredHeaders)
        {
            var lowerName = name.ToLowerInvariant();
            foreach (var existingName in merged.Keys.Where(key => key.ToLowerInvariant() == lowerName).ToArray())
            {
                merged.Remove(existingName);
            }

            merged[name] = value;
        }

        return resolution with { Auth = resolution.Auth with { Headers = merged } };
    }

    /// <summary>Resolves request auth for a provider (pinned ModelRuntime.getAuth).</summary>
    public Task<AuthResult?> GetAuthAsync(
        string providerId,
        AuthResolutionOverrides? overrides = null,
        CancellationToken cancellationToken = default)
        => _models.GetAuthAsync(providerId, overrides, cancellationToken);

    // ── Credential operations ────────────────────────────────────────────

    private Task EnqueueCredentialOperationAsync(
        string providerId, CancellationToken signal, Func<Task> task)
        => EnqueueCredentialOperationAsync<object>(providerId, signal, async () =>
        {
            await task();
            return new object();
        });

    private async Task<T> EnqueueCredentialOperationAsync<T>(
        string providerId, CancellationToken signal, Func<Task<T>> task)
    {
        Task previous;
        TaskCompletionSource startedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            previous = _credentialOperations.TryGetValue(providerId, out var existing) ? existing : Task.CompletedTask;
        }

        var operation = Task.Run(async () =>
        {
            try
            {
                await previous;
            }
            catch
            {
                // The previous operation's failure is already surfaced to its caller.
            }

            signal.ThrowIfCancellationRequested();
            startedTcs.TrySetResult();
            return await task();
        }, signal);

        var tail = operation.ContinueWith(
            _ => { },
            CancellationToken.None,
            TaskContinuationOptions.DenyChildAttach,
            TaskScheduler.Default);
        lock (_gate)
        {
            _credentialOperations[providerId] = tail;
        }

        _ = tail.ContinueWith(
            _ =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_credentialOperations.GetValueOrDefault(providerId), tail))
                    {
                        _credentialOperations.Remove(providerId);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.DenyChildAttach,
            TaskScheduler.Default);

        using (signal.Register(() => startedTcs.TrySetCanceled(signal)))
        {
            await startedTcs.Task;
        }

        return await operation;
    }

    private async Task SynchronizeCredentialStateAsync(
        string providerId, string operation, Credential? credential, CancellationToken signal)
    {
        try
        {
            signal.ThrowIfCancellationRequested();
            RecomposeProvider(providerId);
            lock (_gate)
            {
                if (_compositionErrors.TryGetValue(providerId, out var compositionError))
                {
                    throw new InvalidOperationException(compositionError);
                }
            }

            var result = await _models.RefreshAsync(new ModelsRefreshOptions
            {
                AllowNetwork = false,
                Providers = [providerId],
                CancellationToken = signal,
            });
            if (result.Aborted)
            {
                signal.ThrowIfCancellationRequested();
            }

            if (result.Errors.TryGetValue(providerId, out var refreshError))
            {
                throw refreshError;
            }

            UpdateModelSnapshot();
            await RefreshProviderAvailabilityAsync(providerId, signal);
        }
        catch (Exception exception)
        {
            throw new CredentialSynchronizationError(providerId, operation, credential, exception);
        }
    }

    /// <summary>Sets a non-persistent runtime API key and re-syncs provider state.</summary>
    public Task SetRuntimeApiKeyAsync(string providerId, string apiKey, CancellationToken cancellationToken = default)
        => EnqueueCredentialOperationAsync(providerId, cancellationToken, async () =>
        {
            _credentials.SetRuntimeApiKey(providerId, apiKey);
            await SynchronizeCredentialStateAsync(
                providerId, "setRuntimeApiKey", new ApiKeyCredential(apiKey), cancellationToken);
        });

    /// <summary>Removes a runtime API key and re-syncs provider state.</summary>
    public Task RemoveRuntimeApiKeyAsync(string providerId, CancellationToken cancellationToken = default)
        => EnqueueCredentialOperationAsync(providerId, cancellationToken, async () =>
        {
            _credentials.RemoveRuntimeApiKey(providerId);
            await SynchronizeCredentialStateAsync(providerId, "removeRuntimeApiKey", null, cancellationToken);
        });

    /// <summary>Lists non-secret credential info for all providers with stored credentials.</summary>
    public Task<IReadOnlyList<CredentialInfo>> ListCredentialsAsync(CancellationToken cancellationToken = default)
        => _credentials.ListAsync(cancellationToken);

    /// <summary>
    /// The provider's current auth status source (pinned getProviderAuthStatus):
    /// runtime > stored > models.json config > environment.
    /// </summary>
    public AuthStatus GetProviderAuthStatus(string providerId)
    {
        if (_credentials.HasRuntimeApiKey(providerId))
        {
            return new AuthStatus(true, "runtime");
        }

        if (_snapshot.StoredProviders.Contains(providerId))
        {
            return new AuthStatus(true, "stored");
        }

        var configured = ProviderComposer.ConfiguredRequestAuthStatus(_config.GetProvider(providerId));
        if (configured is not null)
        {
            return configured;
        }

        var check = _snapshot.Auth.GetValueOrDefault(providerId);
        return check is not null
            ? new AuthStatus(true, "environment", check.Source)
            : new AuthStatus(false);
    }

    // ── Login / logout / refresh ─────────────────────────────────────────

    /// <summary>
    /// Runs the provider's api-key or OAuth login flow, persists the credential, and
    /// re-syncs provider state (pinned ModelRuntime.login).
    /// </summary>
    public Task<Credential> LoginAsync(
        string providerId, string authType, IAuthInteraction interaction)
        => EnqueueCredentialOperationAsync(providerId, interaction.Signal, async () =>
        {
            var credential = await _models.LoginAsync(providerId, authType, interaction);
            await SynchronizeCredentialStateAsync(providerId, "login", credential, interaction.Signal);
            return credential;
        });

    /// <summary>Deletes the provider credential and re-syncs provider state.</summary>
    public Task LogoutAsync(string providerId, CancellationToken cancellationToken = default)
        => EnqueueCredentialOperationAsync(providerId, cancellationToken, async () =>
        {
            await _models.LogoutAsync(providerId, cancellationToken);
            await SynchronizeCredentialStateAsync(providerId, "logout", null, cancellationToken);
        });

    /// <summary>
    /// Reloads models.json, recomposes providers, refreshes catalogs, and updates the
    /// availability snapshot (pinned ModelRuntime.refresh).
    /// </summary>
    public async Task<ModelsRefreshResult> RefreshAsync(ModelsRefreshOptions? options = null)
    {
        options ??= new ModelsRefreshOptions();
        await _refreshGate.WaitAsync(options.CancellationToken);
        try
        {
            return await RefreshCoreAsync(options);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<ModelsRefreshResult> RefreshCoreAsync(ModelsRefreshOptions options)
    {
        _config = await ModelConfig.LoadAsync(_modelsPath);
        if (options.Providers is not null)
        {
            foreach (var providerId in options.Providers.Distinct(StringComparer.Ordinal))
            {
                RecomposeProvider(providerId);
            }

            UpdateModelSnapshot();
        }
        else
        {
            RebuildProviders();
        }

        var allowNetwork = options.AllowNetwork ?? _modelNetworkEnabled;
        var result = await _models.RefreshAsync(new ModelsRefreshOptions
        {
            AllowNetwork = allowNetwork,
            Providers = options.Providers,
            Force = options.Force,
            CancellationToken = options.CancellationToken,
        });

        var errors = new Dictionary<string, Exception>(result.Errors);
        UpdateModelSnapshot();
        if (options.Providers is not null)
        {
            foreach (var providerId in options.Providers.Distinct(StringComparer.Ordinal))
            {
                try
                {
                    await RefreshProviderAvailabilityAsync(providerId, options.CancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    if (!options.CancellationToken.IsCancellationRequested)
                    {
                        errors[providerId] = exception;
                    }
                }
            }
        }
        else
        {
            try
            {
                await QueueAvailabilityRefreshAsync(options.CancellationToken);
            }
            catch
            {
                // Availability errors are recorded by the latest pass; refreshed models
                // remain usable.
            }
        }

        return new ModelsRefreshResult(
            result.Aborted || options.CancellationToken.IsCancellationRequested,
            errors);
    }

    // ── Provider registration (PiSharp-native; pinned uses extensions) ───

    /// <summary>
    /// The background refresh most recently started by Register/UnregisterProvider (it
    /// may already have completed), or null when no registration has started one. Awaiting
    /// it lets a caller know the catalog has settled after a registration change (the
    /// pinned `void this.refresh(...)` gives no such signal).
    /// </summary>
    public Task? PendingRefresh => _pendingRefresh;

    /// <summary>
    /// The error from the last background refresh, or null when it completed cleanly.
    /// The background refresh is observed here so a failure is never left dangling while
    /// remaining queryable.
    /// </summary>
    public Exception? LastRefreshError => _lastRefreshError;

    /// <summary>
    /// Registers a native provider spec (PiSharp replacement for the pinned
    /// extension/native provider registration; see docs/PARITY.md).
    /// </summary>
    public void RegisterProvider(ProviderSpec provider)
    {
        if (string.IsNullOrWhiteSpace(provider.Id))
        {
            throw new InvalidOperationException("Provider id must not be empty.");
        }

        _registeredProviders[provider.Id] = provider;
        RecomposeProvider(provider.Id);
        UpdateModelSnapshot();
        StartBackgroundRefresh();
    }

    /// <summary>Removes a registered provider.</summary>
    public void UnregisterProvider(string providerId)
    {
        _registeredProviders.Remove(providerId);
        RecomposeProvider(providerId);
        UpdateModelSnapshot();
        StartBackgroundRefresh();
    }

    /// <summary>
    /// Starts the fire-and-forget full refresh a registration change requires (pinned
    /// `void this.refresh({ allowNetwork: false })`), tracking it so callers can await
    /// completion (<see cref="PendingRefresh"/>) and failures stay observable
    /// (<see cref="LastRefreshError"/>) instead of vanishing into `_ =`.
    /// </summary>
    private void StartBackgroundRefresh()
    {
        var refresh = RefreshAsync(new ModelsRefreshOptions { AllowNetwork = false });
        _pendingRefresh = refresh;
        _ = refresh.ContinueWith(
            completed =>
            {
                if (completed.IsCanceled || completed.Exception is null)
                {
                    _lastRefreshError = completed.IsCanceled ? _lastRefreshError : null;
                    return;
                }

                // Reading Exception marks the faulted task observed.
                _lastRefreshError = completed.Exception;
            },
            CancellationToken.None);
    }

    // ── Compatibility request config ─────────────────────────────────────

    /// <summary>
    /// Compatibility fallback for a model when provider auth is unconfigured (pinned
    /// getCompatibilityRequestConfig): resolves configured headers/env so the caller
    /// can still construct a request.
    /// </summary>
    public CompatibilityRequestConfig GetCompatibilityRequestConfig(ModelInfo model)
        => ProviderComposer.ResolveCompatibilityRequestConfig(model, _config.GetProvider(model.Provider));
}
