using System.Text.Json;

namespace PiSharp.Extensions.Abstractions;

/// <summary>A durable JSON entry owned by an extension and stored on the active session branch.</summary>
public sealed class PiSharpExtensionSessionEntry
{
    private readonly JsonElement _data;

    /// <summary>Creates an immutable projection of a persisted extension-owned session entry.</summary>
    /// <param name="id">The conversation-tree entry identifier.</param>
    /// <param name="customType">The extension-defined entry type.</param>
    /// <param name="data">The JSON payload.</param>
    /// <param name="timestamp">The timestamp assigned when the entry was appended.</param>
    /// <param name="source">The extension package source that appended the entry, when available.</param>
    public PiSharpExtensionSessionEntry(string id, string customType, JsonElement data,
        DateTimeOffset timestamp, string? source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(customType);
        if (data.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Session entry data must be a JSON value.", nameof(data));
        Id = id;
        CustomType = customType;
        _data = data.Clone();
        Timestamp = timestamp;
        Source = source;
    }

    /// <summary>The unique conversation-tree entry ID.</summary>
    public string Id { get; }

    /// <summary>The extension-defined entry type.</summary>
    public string CustomType { get; }

    /// <summary>The cloned JSON payload.</summary>
    public JsonElement Data => _data.Clone();

    /// <summary>The timestamp assigned when the entry was appended.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>The extension package source that appended the entry, when available.</summary>
    public string? Source { get; }
}

/// <summary>A read-only snapshot of the active session identity and its extension state entries.</summary>
public sealed class PiSharpExtensionSessionSnapshot
{
    /// <summary>Creates a read-only projection of the active session branch.</summary>
    /// <param name="sessionId">The stable session identifier.</param>
    /// <param name="sessionName">The optional session name.</param>
    /// <param name="headEntryId">The current branch head entry identifier.</param>
    /// <param name="entries">Extension-owned entries on the active branch, in append order.</param>
    public PiSharpExtensionSessionSnapshot(string sessionId, string? sessionName, string? headEntryId,
        IReadOnlyList<PiSharpExtensionSessionEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(entries);
        SessionId = sessionId;
        SessionName = sessionName;
        HeadEntryId = headEntryId;
        Entries = Array.AsReadOnly(entries.ToArray());
    }

    /// <summary>The stable session identifier.</summary>
    public string SessionId { get; }
    /// <summary>The optional session name.</summary>
    public string? SessionName { get; }
    /// <summary>The current active branch head entry identifier.</summary>
    public string? HeadEntryId { get; }
    /// <summary>Extension-owned entries on the active branch, in append order.</summary>
    public IReadOnlyList<PiSharpExtensionSessionEntry> Entries { get; }
}

/// <summary>Read-only session projection plus the ability to append durable extension-owned JSON entries.</summary>
public sealed class PiSharpExtensionSessionContext : IDisposable
{
    private const int MaximumEntryBytes = 1024 * 1024;
    private readonly Func<PiSharpExtensionSessionSnapshot> _getSnapshot;
    private readonly Func<string, JsonElement, CancellationToken, Task<PiSharpExtensionSessionEntry>> _appendEntryAsync;
    private readonly Func<string, CancellationToken, Task>? _setSessionNameAsync;
    private readonly Func<string, string?, CancellationToken, Task>? _setLabelAsync;
    private readonly CancellationTokenSource _lifetime = new();
    private int _isActive = 1;

    /// <summary>Creates the bridge used by the host to adapt its authoritative conversation session.</summary>
    public PiSharpExtensionSessionContext(
        Func<PiSharpExtensionSessionSnapshot> getSnapshot,
        Func<string, JsonElement, CancellationToken, Task<PiSharpExtensionSessionEntry>> appendEntryAsync)
    {
        ArgumentNullException.ThrowIfNull(getSnapshot);
        ArgumentNullException.ThrowIfNull(appendEntryAsync);
        _getSnapshot = getSnapshot;
        _appendEntryAsync = appendEntryAsync;
    }

    /// <summary>Creates the bridge used by the host to adapt its authoritative session and metadata mutations.</summary>
    public PiSharpExtensionSessionContext(
        Func<PiSharpExtensionSessionSnapshot> getSnapshot,
        Func<string, JsonElement, CancellationToken, Task<PiSharpExtensionSessionEntry>> appendEntryAsync,
        Func<string, CancellationToken, Task> setSessionNameAsync,
        Func<string, string?, CancellationToken, Task> setLabelAsync)
        : this(getSnapshot, appendEntryAsync)
    {
        ArgumentNullException.ThrowIfNull(setSessionNameAsync);
        ArgumentNullException.ThrowIfNull(setLabelAsync);
        _setSessionNameAsync = setSessionNameAsync;
        _setLabelAsync = setLabelAsync;
    }

    /// <summary>Gets a fresh read-only projection of the active branch.</summary>
    public PiSharpExtensionSessionSnapshot Snapshot => _getSnapshot();

    /// <summary>The stable session identifier.</summary>
    public string SessionId => Snapshot.SessionId;
    /// <summary>The optional session name.</summary>
    public string? SessionName => Snapshot.SessionName;
    /// <summary>The current active branch head entry identifier.</summary>
    public string? HeadEntryId => Snapshot.HeadEntryId;
    /// <summary>Extension-owned entries on the active branch, in append order.</summary>
    public IReadOnlyList<PiSharpExtensionSessionEntry> Entries => Snapshot.Entries;

    /// <summary>Sets the active session's display name and saves it through the host session store.</summary>
    public async Task SetSessionNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isActive) == 0, this);
        ArgumentNullException.ThrowIfNull(name);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            _lifetime.Token);
        await (_setSessionNameAsync ?? throw new InvalidOperationException(
            "The host did not provide session metadata mutation for this callback."))(
                name, linkedCancellation.Token).ConfigureAwait(false);
    }

    /// <summary>Sets or clears a label on an existing session entry, then saves through the host session store.</summary>
    /// <param name="entryId">The ID of an existing session entry.</param>
    /// <param name="label">The label to set, or null or empty to clear it.</param>
    public async Task SetLabelAsync(string entryId, string? label, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isActive) == 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            _lifetime.Token);
        await (_setLabelAsync ?? throw new InvalidOperationException(
            "The host did not provide session metadata mutation for this callback."))(
                entryId, string.IsNullOrEmpty(label) ? null : label, linkedCancellation.Token).ConfigureAwait(false);
    }

    /// <summary>Appends an entry with JSON null data to the session tree and saves it before returning.</summary>
    /// <param name="customType">The extension-defined entry type.</param>
    /// <returns>The persisted extension-owned entry.</returns>
    public Task<PiSharpExtensionSessionEntry> AppendEntryAsync(string customType) =>
        AppendEntryAsync(customType, JsonSerializer.SerializeToElement<object?>(null));

    /// <summary>Appends one JSON value to the session tree and saves it before returning.</summary>
    public async Task<PiSharpExtensionSessionEntry> AppendEntryAsync(string customType, JsonElement data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isActive) == 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(customType);
        if (customType.Length > 256)
            throw new ArgumentException("Session entry type cannot exceed 256 characters.", nameof(customType));
        if (data.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Session entry data must be a JSON value.", nameof(data));
        if (System.Text.Encoding.UTF8.GetByteCount(data.GetRawText()) > MaximumEntryBytes)
            throw new ArgumentException("Session entry data cannot exceed 1 MiB.", nameof(data));

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            _lifetime.Token);
        return await _appendEntryAsync(customType, data.Clone(), linkedCancellation.Token).ConfigureAwait(false);
    }

    /// <summary>Ends this callback-scoped bridge so retained contexts cannot mutate a later session.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isActive, 0) != 0) _lifetime.Cancel();
    }
}

/// <summary>Why the host opened the active conversation for extension lifecycle callbacks.</summary>
public enum PiSharpExtensionSessionStartReason
{
    /// <summary>The host started normally.</summary>
    Startup,
    /// <summary>The host reloaded its extension configuration.</summary>
    Reload,
    /// <summary>A new conversation was created.</summary>
    New,
    /// <summary>An existing conversation was resumed.</summary>
    Resume,
    /// <summary>A new conversation was forked from an existing conversation.</summary>
    Fork
}

/// <summary>Why the host is closing or replacing the active conversation.</summary>
/// <summary>Reason that the host is closing or replacing the active conversation.</summary>
public enum PiSharpExtensionSessionShutdownReason
{
    /// <summary>The host is quitting.</summary>
    Quit,
    /// <summary>The host is reloading its extension configuration.</summary>
    Reload,
    /// <summary>The active conversation is being replaced by a new one.</summary>
    New,
    /// <summary>The active conversation is being replaced by a resumed one.</summary>
    Resume,
    /// <summary>The active conversation is being replaced by a fork.</summary>
    Fork
}

/// <summary>Event data for a session-start callback.</summary>
/// <param name="Reason">Why the host opened this session.</param>
public sealed record PiSharpExtensionSessionStartEvent(PiSharpExtensionSessionStartReason Reason);
/// <summary>Event data for a session-shutdown callback.</summary>
/// <param name="Reason">Why the host is closing or replacing this session.</param>
public sealed record PiSharpExtensionSessionShutdownEvent(PiSharpExtensionSessionShutdownReason Reason);

/// <summary>Handles a session-start lifecycle event.</summary>
/// <param name="eventData">The reason the host opened this session.</param>
/// <param name="context">Read-only host context with access to durable extension-owned session state.</param>
/// <param name="cancellationToken">Token canceled when the host stops dispatching lifecycle callbacks.</param>
/// <returns>A task that completes when the callback finishes.</returns>
public delegate ValueTask PiSharpExtensionSessionStartHandler(PiSharpExtensionSessionStartEvent eventData,
    PiSharpExtensionContext context, CancellationToken cancellationToken);

/// <summary>Handles a session-shutdown lifecycle event.</summary>
/// <param name="eventData">The reason the host is closing or replacing this session.</param>
/// <param name="context">Read-only host context with access to durable extension-owned session state.</param>
/// <param name="cancellationToken">Token canceled when the host stops dispatching lifecycle callbacks.</param>
/// <returns>A task that completes when the callback finishes.</returns>
public delegate ValueTask PiSharpExtensionSessionShutdownHandler(PiSharpExtensionSessionShutdownEvent eventData,
    PiSharpExtensionContext context, CancellationToken cancellationToken);
