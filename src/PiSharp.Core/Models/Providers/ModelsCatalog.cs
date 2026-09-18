using PiSharp.Core.Models.Auth;

namespace PiSharp.Core.Models.Providers;

/// <summary>
/// Options for a ModelsCatalog refresh (pinned pi-ai: ModelsRefreshOptions).
/// </summary>
public sealed class ModelsRefreshOptions
{
    public bool? AllowNetwork { get; init; }
    public IReadOnlyList<string>? Providers { get; init; }
    public bool? Force { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>Result of a ModelsCatalog refresh (pinned pi-ai: ModelsRefreshResult).</summary>
public sealed record ModelsRefreshResult(bool Aborted, IReadOnlyDictionary<string, Exception> Errors);

/// <summary>
/// Runtime collection of providers plus auth application and stream convenience
/// (pinned pi-ai: Models/ModelsImpl). Providers own model lists and auth; the
/// catalog resolves credentials and tracks refresh generations.
/// </summary>
public sealed class ModelsCatalog
{
    private readonly Dictionary<string, ProviderSpec> _providers = new(StringComparer.Ordinal);
    private readonly ICredentialStore _credentials;
    private readonly IModelsStore _modelsStore;
    private readonly AuthContext _authContext;
    private readonly Dictionary<string, int> _refreshGenerations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _refreshControllers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _publicationChains = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Creates a catalog over the given stores (in-memory by default).</summary>
    public ModelsCatalog(
        ICredentialStore? credentials = null,
        IModelsStore? modelsStore = null,
        AuthContext? authContext = null)
    {
        _credentials = credentials ?? new InMemoryCredentialStore();
        _modelsStore = modelsStore ?? new InMemoryModelsStore();
        _authContext = authContext ?? AuthContext.CreateDefault();
    }

    /// <summary>Replaces a provider; supersedes any in-flight refresh for it.</summary>
    public void SetProvider(ProviderSpec provider)
    {
        lock (_gate)
        {
            SupersedeProviderRefresh(provider.Id);
            _providers[provider.Id] = provider;
        }
    }

    /// <summary>Removes a provider; supersedes any in-flight refresh for it.</summary>
    public void DeleteProvider(string id)
    {
        lock (_gate)
        {
            SupersedeProviderRefresh(id);
            _providers.Remove(id);
        }
    }

    /// <summary>Removes all providers and supersedes pending refresh state.</summary>
    public void ClearProviders()
    {
        lock (_gate)
        {
            foreach (var id in _providers.Keys.Concat(_refreshControllers.Keys).ToList())
            {
                SupersedeProviderRefresh(id);
            }

            _providers.Clear();
        }
    }

    /// <summary>Returns all registered providers.</summary>
    public IReadOnlyList<ProviderSpec> GetProviders()
    {
        lock (_gate)
        {
            return _providers.Values.ToArray();
        }
    }

    /// <summary>Returns the provider with the given id, if registered.</summary>
    public ProviderSpec? GetProvider(string id)
    {
        lock (_gate)
        {
            return _providers.TryGetValue(id, out var provider) ? provider : null;
        }
    }

    /// <summary>
    /// Sync read of last-known models from one provider or all providers. Best effort:
    /// a provider whose model list throws yields no models.
    /// </summary>
    public IReadOnlyList<ModelInfo> GetModels(string? provider = null)
    {
        lock (_gate)
        {
            if (provider is not null)
            {
                return _providers.TryGetValue(provider, out var entry) ? SafeGetModels(entry) : [];
            }

            var models = new List<ModelInfo>();
            foreach (var entry in _providers.Values)
            {
                models.AddRange(SafeGetModels(entry));
            }

            return models;
        }
    }

    /// <summary>Runtime model lookup against last-known lists.</summary>
    public ModelInfo? GetModel(string provider, string id)
    {
        foreach (var model in GetModels(provider))
        {
            if (model.Id == id)
            {
                return model;
            }
        }

        return null;
    }

    private static IReadOnlyList<ModelInfo> SafeGetModels(ProviderSpec provider)
    {
        try
        {
            return provider.GetModels() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private int SupersedeProviderRefresh(string providerId)
    {
        var generation = (_refreshGenerations.TryGetValue(providerId, out var current) ? current : 0) + 1;
        _refreshGenerations[providerId] = generation;
        if (_refreshControllers.Remove(providerId, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        return generation;
    }

    private (int Generation, CancellationTokenSource Controller) BeginProviderRefresh(string providerId)
    {
        // SupersedeProviderRefresh and the controller table are shared with the
        // locked registration paths (DeleteProvider/ClearProviders); hold the same
        // gate or the dictionary state corrupts under a concurrent rebuild.
        lock (_gate)
        {
            var generation = SupersedeProviderRefresh(providerId);
            var controller = new CancellationTokenSource();
            _refreshControllers[providerId] = controller;
            return (generation, controller);
        }
    }

    private Task<bool> PublishProviderModels(
        string providerId,
        int generation,
        CancellationToken signal,
        ModelsPublication publication)
    {
        Task previous;
        lock (_gate)
        {
            previous = _publicationChains.TryGetValue(providerId, out var chain) ? chain : Task.CompletedTask;
        }

        var queued = Task.Run(async () =>
        {
            await previous;
            if (signal.IsCancellationRequested || GetGeneration(providerId) != generation)
            {
                return false;
            }

            if (publication.PersistDelete)
            {
                await _modelsStore.DeleteAsync(providerId, signal);
            }
            else if (publication.Persist is { } entry)
            {
                await _modelsStore.WriteAsync(providerId, entry, signal);
            }

            if (signal.IsCancellationRequested || GetGeneration(providerId) != generation)
            {
                return false;
            }

            publication.Update?.Invoke();
            return true;
        }, CancellationToken.None);

        Task tail;
        lock (_gate)
        {
            tail = queued.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _publicationChains[providerId] = tail;
        }

        _ = tail.ContinueWith(
            _ =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_publicationChains.GetValueOrDefault(providerId), tail))
                    {
                        _publicationChains.Remove(providerId);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);

        return WithCancellation(queued, signal);
    }

    private int GetGeneration(string providerId)
    {
        lock (_gate)
        {
            return _refreshGenerations.TryGetValue(providerId, out var generation) ? generation : 0;
        }
    }

    private static Task WithCancellation(Task task, CancellationToken signal)
    {
        return WithCancellation<bool>(
            task.ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        throw t.Exception!;
                    }

                    return true;
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default),
            signal);
    }

    private static Task<T> WithCancellation<T>(Task<T> task, CancellationToken signal)
    {
        if (signal.CanBeCanceled)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = signal.Register(
                () => tcs.TrySetCanceled(signal));
            _ = task.ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        tcs.TrySetException(t.Exception!.InnerExceptions!);
                    }
                    else
                    {
                        tcs.TrySetResult(t.Result);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            return tcs.Task;
        }

        return task;
    }

    private async Task RunProviderRefreshPhase(
        ProviderSpec provider,
        Credential? credential,
        bool allowNetwork,
        bool? force,
        int generation,
        CancellationToken signal)
    {
        if (provider.RefreshModelsAsync is null)
        {
            return;
        }

        ModelsStoreEntry? stored = null;
        try
        {
            stored = await _modelsStore.ReadAsync(provider.Id, signal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Storage failure degrades to a no-cache refresh; models remain usable.
        }

        await provider.RefreshModelsAsync(new RefreshModelsContext
        {
            Credential = credential,
            Stored = stored,
            Publish = publication => PublishProviderModels(provider.Id, generation, signal, publication),
            AllowNetwork = allowNetwork,
            Force = allowNetwork && force is true,
            CancellationToken = signal,
        });
    }

    /// <summary>
    /// Refresh selected dynamic providers concurrently (all when providers is
    /// omitted). Provider errors and cancellation are returned without throwing;
    /// static and unconfigured providers are skipped.
    /// </summary>
    public async Task<ModelsRefreshResult> RefreshAsync(ModelsRefreshOptions? options = null)
    {
        options ??= new ModelsRefreshOptions();
        var allowNetwork = options.AllowNetwork ?? true;
        var callerSignal = options.CancellationToken;
        var errors = new Dictionary<string, Exception>(StringComparer.Ordinal);
        if (callerSignal.IsCancellationRequested)
        {
            return new ModelsRefreshResult(true, errors);
        }

        var selected = options.Providers is null ? null : new HashSet<string>(options.Providers, StringComparer.Ordinal);
        ProviderSpec[] refreshable;
        lock (_gate)
        {
            refreshable = _providers.Values
                .Where(provider => provider.IsDynamic && (selected is null || selected.Contains(provider.Id)))
                .ToArray();
        }

        var refresh = Task.WhenAll(refreshable.Select(async provider =>
        {
            (var generation, var controller) = BeginProviderRefresh(provider.Id);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerSignal, controller.Token);
            var signal = linked.Token;
            try
            {
                await RunProviderRefreshPhase(provider, null, false, null, generation, signal);
                if (signal.IsCancellationRequested || !allowNetwork)
                {
                    return;
                }

                var credential = await ResolveRefreshCredentialAsync(provider, signal);
                if (credential is null || signal.IsCancellationRequested)
                {
                    return;
                }

                await RunProviderRefreshPhase(provider, credential, true, options.Force, generation, signal);
            }
            catch (Exception exception) when (!signal.IsCancellationRequested)
            {
                errors[provider.Id] = exception is Exception e ? e : new ModelsException(
                    ModelsErrorCode.ModelSource,
                    $"Model refresh failed for {provider.Id}",
                    exception);
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_refreshControllers.GetValueOrDefault(provider.Id), controller))
                    {
                        _refreshControllers.Remove(provider.Id);
                    }
                }
            }
        }));

        try
        {
            await WithCancellation(refresh, callerSignal);
        }
        catch
        {
            if (!callerSignal.IsCancellationRequested)
            {
                throw;
            }
        }

        return new ModelsRefreshResult(callerSignal.IsCancellationRequested, errors);
    }

    private async Task<Credential?> ResolveRefreshCredentialAsync(ProviderSpec provider, CancellationToken signal)
    {
        Credential? stored;
        try
        {
            stored = await ReadCredentialAsync(provider.Id, signal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }

        if (stored is OAuthCredential)
        {
            var oauth = provider.Auth.OAuth;
            if (oauth is null)
            {
                return null;
            }

            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < ((OAuthCredential)stored).Expires)
            {
                return stored;
            }

            if (signal.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                var post = await _credentials.ModifyAsync(
                    provider.Id,
                    async current =>
                    {
                        if (current is not OAuthCredential currentOAuth ||
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < currentOAuth.Expires)
                        {
                            return null;
                        }

                        return await oauth.RefreshAsync(currentOAuth, signal);
                    },
                    signal);
                return post;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        var apiKey = provider.Auth.ApiKey;
        if (apiKey is null)
        {
            return null;
        }

        var storedKey = stored is ApiKeyCredential key ? key : null;
        try
        {
            var result = await apiKey.ResolveAsync(new ApiKeyAuthInput
            {
                Context = _authContext,
                Credential = storedKey,
                CancellationToken = signal,
            });
            if (result?.Auth.ApiKey is null)
            {
                return null;
            }

            return new ApiKeyCredential(result.Auth.ApiKey);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private Task<Credential?> ReadCredentialAsync(string providerId, CancellationToken signal)
    {
        return Task.Run(async () =>
        {
            try
            {
                return await _credentials.ReadAsync(providerId, signal);
            }
            catch (Exception exception)
            {
                throw new ModelsException(ModelsErrorCode.Auth, $"Credential store read failed for {providerId}", exception);
            }
        }, signal);
    }

    private Task<AuthCheck?> CheckProviderAuthAsync(ProviderSpec provider, Credential? credential, CancellationToken signal)
    {
        return Task.Run(async () =>
        {
            if (credential is OAuthCredential)
            {
                return provider.Auth.OAuth is not null ? new AuthCheck("OAuth", "oauth") : null;
            }

            var apiKey = provider.Auth.ApiKey;
            if (apiKey is null)
            {
                return null;
            }

            if (apiKey.Check is { } check)
            {
                try
                {
                    return await check(new ApiKeyAuthInput
                    {
                        Context = _authContext,
                        Credential = credential is ApiKeyCredential key ? key : null,
                        CancellationToken = signal,
                    });
                }
                catch (Exception exception)
                {
                    throw new ModelsException(
                        ModelsErrorCode.Auth,
                        $"API key auth check failed for provider {provider.Id}",
                        exception);
                }
            }

            var resolution = await CredentialResolver.ResolveProviderAuthAsync(
                provider, _credentials, _authContext, null, signal);
            return resolution is null ? null : new AuthCheck(resolution.Source, "api_key");
        }, signal);
    }

    /// <summary>Checks whether a provider has complete auth configuration without refreshing OAuth.</summary>
    public Task<AuthCheck?> CheckAuthAsync(string providerId, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = GetProvider(providerId);
            if (provider is null)
            {
                return null;
            }

            var credential = await ReadCredentialAsync(providerId, cancellationToken);
            return await CheckProviderAuthAsync(provider, credential, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Returns models for providers with configured auth, applying each provider's
    /// credential-specific filter.
    /// </summary>
    public Task<IReadOnlyList<ModelInfo>> GetAvailableAsync(
        string? providerId = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ModelInfo>>(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderSpec[] providers;
            lock (_gate)
            {
                providers = providerId is null
                    ? _providers.Values.ToArray()
                    : _providers.TryGetValue(providerId, out var entry) ? [entry] : [];
            }

            var results = new List<(ProviderSpec Provider, Credential? Credential, AuthCheck? Auth)>();
            foreach (var provider in providers)
            {
                var credential = await ReadCredentialAsync(provider.Id, cancellationToken);
                var auth = await CheckProviderAuthAsync(provider, credential, cancellationToken);
                results.Add((provider, credential, auth));
            }

            var available = new List<ModelInfo>();
            foreach (var (provider, credential, auth) in results)
            {
                if (auth is null)
                {
                    continue;
                }

                var models = SafeGetModels(provider);
                available.AddRange(provider.FilterModels is { } filter ? filter(models, credential) : models);
            }

            return available;
        }, cancellationToken);
    }

    /// <summary>
    /// Resolves request auth for a provider or model. When a model is given, its
    /// headers merge over the resolved auth headers (pinned pi-ai: Models.getAuth).
    /// </summary>
    public async Task<AuthResult?> GetAuthAsync(
        string providerId,
        AuthResolutionOverrides? overrides,
        CancellationToken cancellationToken = default)
    {
        var provider = GetProvider(providerId);
        if (provider is null)
        {
            return null;
        }

        return await CredentialResolver.ResolveProviderAuthAsync(
            provider, _credentials, _authContext, overrides, cancellationToken);
    }

    public async Task<AuthResult?> GetAuthAsync(
        ModelInfo model,
        AuthResolutionOverrides? overrides,
        CancellationToken cancellationToken = default)
    {
        var result = await GetAuthAsync(model.Provider, overrides, cancellationToken);
        if (result is null || model.Headers is null || model.Headers.Count == 0)
        {
            return result;
        }

        var merged = new Dictionary<string, string>(
            result.Auth.Headers ?? new Dictionary<string, string>(),
            StringComparer.Ordinal);
        foreach (var (name, value) in model.Headers)
        {
            merged[name] = value;
        }

        return result with
        {
            Auth = result.Auth with { Headers = merged },
        };
    }

    /// <summary>
    /// Runs a provider login flow and persists the returned credential via the store
    /// modify path (pinned pi-ai: Models.login).
    /// </summary>
    public async Task<Credential> LoginAsync(
        string providerId,
        string authType,
        IAuthInteraction interaction)
    {
        var signal = interaction.Signal;
        signal.ThrowIfCancellationRequested();
        var provider = GetProvider(providerId);
        if (provider is null)
        {
            throw new ModelsException(ModelsErrorCode.Provider, $"Unknown provider: {providerId}");
        }

        Credential credential;
        if (authType == "oauth")
        {
            var oauth = provider.Auth.OAuth;
            if (oauth is null)
            {
                throw new ModelsException(ModelsErrorCode.Auth, $"{provider.Name} does not support oauth login");
            }

            credential = await oauth.LoginAsync(interaction);
        }
        else
        {
            var apiKey = provider.Auth.ApiKey;
            if (apiKey?.Login is null)
            {
                throw new ModelsException(ModelsErrorCode.Auth, $"{provider.Name} does not support api_key login");
            }

            credential = await apiKey.Login(interaction);
        }

        try
        {
            await _credentials.ModifyAsync(providerId, async _ =>
            {
                await Task.CompletedTask;
                return credential;
            }, signal);
        }
        catch (Exception exception)
        {
            throw new ModelsException(ModelsErrorCode.Auth, $"Credential store modify failed for {providerId}", exception);
        }

        return credential;
    }

    /// <summary>Removes the provider credential (pinned pi-ai: Models.logout).</summary>
    public async Task LogoutAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _credentials.DeleteAsync(providerId, cancellationToken);
        }
        catch (Exception exception)
        {
            throw new ModelsException(ModelsErrorCode.Auth, $"Credential store delete failed for {providerId}", exception);
        }
    }
}
