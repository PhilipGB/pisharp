namespace PiSharp.Core.Models.Auth;

/// <summary>
/// App-owned credential storage keyed by provider id, one credential per provider
/// (pinned pi-ai: CredentialStore). `ModifyAsync` is the only write path, so every
/// mutation is a serialized read-modify-write; OAuth refresh runs inside it so
/// concurrent requests cannot double-refresh a rotated token.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Reads the stored credential, possibly expired. Missing entries resolve to
    /// null. Rejects only on storage failure.
    /// </summary>
    Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>Lists stored credential metadata without exposing secrets.</summary>
    Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Serialized write — the only write path. The callback sees the current
    /// credential and returns the new credential, or null to leave the entry
    /// unchanged. Resolves with the post-write credential.
    /// </summary>
    Task<Credential?> ModifyAsync(
        string providerId,
        Func<Credential?, Task<Credential?>> modify,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a credential (logout). Serialized against ModifyAsync.</summary>
    Task DeleteAsync(string providerId, CancellationToken cancellationToken = default);
}
