using PiSharp.Core.Models.Auth;

namespace PiSharp.Core.Models.Providers;

/// <summary>
/// A persisted dynamic-catalog entry (pinned pi-ai: ModelsStoreEntry). CheckedAt and
/// LastModified are epoch milliseconds; LastModified 0 means "never newer than local".
/// </summary>
public sealed record ModelsStoreEntry
{
    public required IReadOnlyList<ModelInfo> Models { get; init; }
    public required long CheckedAt { get; init; }
    public required long LastModified { get; init; }
    public string? Etag { get; init; }
}

/// <summary>
/// Storage for dynamically refreshed provider catalogs (pinned pi-ai: ModelsStore).
/// </summary>
public interface IModelsStore
{
    Task<ModelsStoreEntry?> ReadAsync(string providerId, CancellationToken cancellationToken = default);
    Task WriteAsync(string providerId, ModelsStoreEntry entry, CancellationToken cancellationToken = default);
    Task DeleteAsync(string providerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One generation-checked publication from a provider refresh (pinned pi-ai:
/// ModelsPublication). Persist is a tri-state: null = leave storage untouched, an
/// entry = write it, PersistDelete = delete the entry.
/// </summary>
public sealed class ModelsPublication
{
    /// <summary>Synchronous state update, run after the persistence mutation.</summary>
    public Action? Update { get; init; }

    /// <summary>Entry to persist, when PersistDelete is false and set.</summary>
    public ModelsStoreEntry? Persist { get; init; }

    /// <summary>True when the persistence mutation is a delete.</summary>
    public bool PersistDelete { get; init; }
}

/// <summary>
/// Context handed to provider refresh implementations (pinned pi-ai: RefreshModelsContext).
/// </summary>
public sealed class RefreshModelsContext
{
    /// <summary>Effective configured credential; OAuth credentials are refreshed before network access.</summary>
    public Credential? Credential { get; init; }

    /// <summary>Immutable provider-scoped catalog snapshot captured before this refresh phase.</summary>
    public ModelsStoreEntry? Stored { get; init; }

    /// <summary>
    /// Generation-checked publication. The update runs synchronously only after the
    /// selected persistence mutation. Returns false when superseded or aborted.
    /// </summary>
    public required Func<ModelsPublication, Task<bool>> Publish { get; init; }

    /// <summary>False during offline/cache-only initialization.</summary>
    public required bool AllowNetwork { get; init; }

    /// <summary>Bypass provider freshness checks and fetch immediately.</summary>
    public bool Force { get; init; }

    public required CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// A provider registration (pinned pi-ai: Provider), framework-neutral: stream
/// implementations are adapter-level in PiSharp (see docs/PARITY.md), while auth,
/// model lists, and refresh behavior live here.
/// </summary>
public sealed class ProviderSpec : IAuthProvidingProvider
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? BaseUrl { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Declared auth methods (at least one).</summary>
    public required ProviderAuth Auth { get; init; }

    /// <summary>
    /// Current known models, sync. Static providers return their catalog; dynamic
    /// providers return the list as of the last refresh. Must not throw; callers
    /// treat a throwing implementation as having no models.
    /// </summary>
    public required Func<IReadOnlyList<ModelInfo>> GetModels { get; init; }

    /// <summary>Dynamic providers only: refresh the model list.</summary>
    public Func<RefreshModelsContext, Task>? RefreshModelsAsync { get; init; }

    /// <summary>
    /// Optional provider policy for credential-specific model availability. The
    /// complete synchronous catalog stays in GetModels; availability filters after
    /// confirming provider auth is configured.
    /// </summary>
    public Func<IReadOnlyList<ModelInfo>, Credential?, IReadOnlyList<ModelInfo>>? FilterModels { get; init; }

    /// <summary>
    /// The API/transport this provider serves by default, used to route models.json
    /// definitions that omit an api.
    /// </summary>
    public string? DefaultApi { get; init; }

    /// <summary>True when this provider can refresh its model list dynamically.</summary>
    public bool IsDynamic => RefreshModelsAsync is not null;
}
