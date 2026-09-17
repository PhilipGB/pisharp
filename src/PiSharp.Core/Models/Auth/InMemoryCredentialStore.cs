namespace PiSharp.Core.Models.Auth;

/// <summary>
/// In-memory credential store (pinned Pi: InMemoryAuthStorageBackend + AuthStorage.inMemory).
/// Operations chain sequentially so modify is a serialized read-modify-write; also the
/// store used by tests and for isolated CLI runs.
/// </summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, Credential> _credentials;
    private Task _chain = Task.CompletedTask;

    /// <summary>Creates an empty in-memory store.</summary>
    public InMemoryCredentialStore()
    {
        _credentials = new Dictionary<string, Credential>();
    }

    /// <summary>Creates an in-memory store seeded with the given credentials.</summary>
    public InMemoryCredentialStore(IReadOnlyDictionary<string, Credential> seed)
        : this()
    {
        foreach (var (providerId, credential) in seed)
        {
            _credentials[providerId] = credential;
        }
    }

    public Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_credentials.TryGetValue(providerId, out var credential) ? credential : null);
    }

    public Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<CredentialInfo>>(
            _credentials.Select(entry => new CredentialInfo(entry.Key, entry.Value.Type)).ToArray());
    }

    public Task<Credential?> ModifyAsync(
        string providerId,
        Func<Credential?, Task<Credential?>> modify,
        CancellationToken cancellationToken = default)
    {
        var previous = _chain;
        var operation = Task.Run(async () =>
        {
            await previous;
            cancellationToken.ThrowIfCancellationRequested();
            var next = await modify(_credentials.TryGetValue(providerId, out var current) ? current : null);
            cancellationToken.ThrowIfCancellationRequested();
            if (next is null)
            {
                return _credentials.TryGetValue(providerId, out var unchanged) ? unchanged : null;
            }

            _credentials[providerId] = next;
            return next;
        }, CancellationToken.None);
        _chain = operation.ContinueWith(
            _ => { },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        return operation;
    }

    public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var previous = _chain;
        var operation = Task.Run(() =>
        {
            previous.Wait();
            cancellationToken.ThrowIfCancellationRequested();
            _credentials.Remove(providerId);
        }, CancellationToken.None);
        _chain = operation.ContinueWith(
            _ => { },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        return operation;
    }
}
