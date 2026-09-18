namespace PiSharp.Core.Models.Auth;

/// <summary>
/// Read-only view over a credential store (pinned pi-ai: ReadOnlyAuthStorage). Used by
/// <c>auth check --no-refresh</c> so the command can never persist rotated OAuth credentials
/// or delete entries.
/// </summary>
public sealed class ReadOnlyCredentialStore(ICredentialStore store) : ICredentialStore
{
    /// <inheritdoc />
    public Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
        => store.ReadAsync(providerId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
        => store.ListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<Credential?> ModifyAsync(
        string providerId,
        Func<Credential?, Task<Credential?>> modify,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Credential store is read-only.");

    /// <inheritdoc />
    public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Credential store is read-only.");
}
