namespace PiSharp.Core.Models.Auth;

/// <summary>
/// Credential store overlay for non-persistent runtime API keys (pinned Pi:
/// RuntimeCredentials). Runtime keys shadow stored credentials on reads but never
/// persist: ModifyAsync and DeleteAsync pass through to the base store, and delete
/// also clears the overlay entry.
/// </summary>
public sealed class RuntimeCredentialStore : ICredentialStore
{
    private readonly ICredentialStore _store;
    private readonly Dictionary<string, string> _overrides = new();
    private readonly object _gate = new();

    /// <summary>Wraps a base store with a runtime API key overlay.</summary>
    public RuntimeCredentialStore(ICredentialStore store)
    {
        _store = store;
    }

    /// <summary>Sets a non-persistent API key for the provider.</summary>
    public void SetRuntimeApiKey(string providerId, string apiKey)
    {
        lock (_gate)
        {
            _overrides[providerId] = apiKey;
        }
    }

    /// <summary>Removes the runtime API key for the provider.</summary>
    public void RemoveRuntimeApiKey(string providerId)
    {
        lock (_gate)
        {
            _overrides.Remove(providerId);
        }
    }

    /// <summary>Returns true when a runtime API key is set for the provider.</summary>
    public bool HasRuntimeApiKey(string providerId)
    {
        lock (_gate)
        {
            return _overrides.ContainsKey(providerId);
        }
    }

    public async Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? overrideKey;
        lock (_gate)
        {
            _overrides.TryGetValue(providerId, out overrideKey);
        }

        if (overrideKey is not null)
        {
            return new ApiKeyCredential(overrideKey);
        }

        return await _store.ReadAsync(providerId, cancellationToken);
    }

    public async Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entries = new Dictionary<string, CredentialInfo>(StringComparer.Ordinal);
        foreach (var entry in await _store.ListAsync(cancellationToken))
        {
            entries[entry.ProviderId] = entry;
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var providerId in _overrides.Keys)
            {
                entries[providerId] = new CredentialInfo(providerId, "api_key");
            }
        }

        return entries.Values.ToArray();
    }

    public Task<Credential?> ModifyAsync(
        string providerId,
        Func<Credential?, Task<Credential?>> modify,
        CancellationToken cancellationToken = default) =>
        _store.ModifyAsync(providerId, modify, cancellationToken);

    public async Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _store.DeleteAsync(providerId, cancellationToken);
        lock (_gate)
        {
            _overrides.Remove(providerId);
        }
    }
}
