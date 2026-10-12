using System.Text.Json;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

/// <summary>Owns serialized Llama router discovery/cache updates and the current provider profile.</summary>
internal sealed class LlamaRouterDiscovery
{
    private readonly HttpClient _http;
    private readonly LlamaRouterCatalogStore _cache;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly ProviderProfile _configuredProfile;
    private readonly AvailableModelSnapshot _available;
    private ProviderProfile? _profile;

    public LlamaRouterDiscovery(string agentDirectory, HttpClient http, ProviderProfile configuredProfile,
        AvailableModelSnapshot available)
    {
        _http = http;
        _cache = new LlamaRouterCatalogStore(agentDirectory);
        _configuredProfile = configuredProfile;
        _profile = configuredProfile;
        _available = available;
    }

    public ProviderProfile? Profile => Volatile.Read(ref _profile);

    public async Task<IReadOnlyList<LlamaRouterModelInfo>> RefreshAsync(
        LlamaRouterClient client, CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await _cache.ReadAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await LlamaRouterCatalog.RefreshAsync(client, previous, cancellationToken)
                .ConfigureAwait(false);
            await _cache.WriteAsync(catalog.Cache, cancellationToken).ConfigureAwait(false);
            var profile = (_profile ?? _configuredProfile) with
            {
                Endpoint = new Uri(client.ServerUrl.AbsoluteUri.TrimEnd('/') + "/v1"),
                Models = catalog.Models,
                Classifiers = catalog.Classifiers
            };
            Volatile.Write(ref _profile, profile);
            _available.RecordModels("llama.cpp", catalog.Models);
            return catalog.Cache.Models;
        }
        finally { _refresh.Release(); }
    }

    public async Task AddModelsAsync(List<ModelDescriptor> result, Uri serverUrl,
        (string Key, bool Authenticated, string Source) auth, bool offline, string? runtimeApiKey, CancellationToken cancellationToken)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await _cache.ReadAsync(cancellationToken).ConfigureAwait(false);
            LlamaRouterCatalogResult catalog;
            if (offline)
            {
                catalog = LlamaRouterCatalog.Restore(previous, serverUrl);
            }
            else
            {
                try
                {
                    var client = new LlamaRouterClient(_http, serverUrl, auth.Key);
                    catalog = await LlamaRouterCatalog.RefreshAsync(client, previous, cancellationToken).ConfigureAwait(false);
                    await _cache.WriteAsync(catalog.Cache, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException or ArgumentException)
                {
                    catalog = LlamaRouterCatalog.Restore(previous, serverUrl);
                    if (catalog.Models.Count == 0)
                    {
                        var safe = SecretRedactor.Redact(error.Message, auth.Key, runtimeApiKey);
                        result.AddRange(_profile?.Models.Select(model => model with
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
            var profile = (_profile ?? _configuredProfile) with
            {
                Endpoint = inferenceUrl,
                Models = catalog.Models,
                Classifiers = catalog.Classifiers
            };
            Volatile.Write(ref _profile, profile);
            result.AddRange(catalog.Models);
        }
        finally { _refresh.Release(); }
    }

}
