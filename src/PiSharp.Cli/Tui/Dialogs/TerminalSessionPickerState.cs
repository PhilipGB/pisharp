using System.Globalization;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Tui;

internal enum SessionPickerScope { Current, All }
internal enum SessionPickerSortMode { Threaded, Recent, Relevance }
internal enum SessionPickerNameFilter { All, Named }

internal sealed record SessionPickerEntry(SessionCatalogEntry Catalog, SessionPickerBranchHeads BranchHeads,
    bool IsCurrent)
{
    public SessionListing Listing => Catalog.Listing;
    public ConversationStore Store => Catalog.Store;
    public string Key => Listing.Id;
}

/// <summary>
/// Session-picker state, sorting, filtering and filesystem mutations. Rendering and key
/// dispatch stay in <see cref="TerminalSessionPicker"/>; this type is deterministic and
/// directly testable.
/// </summary>
internal sealed class TerminalSessionPickerState
{
    internal const string CurrentSessionDeleteError = "Cannot delete the currently active session";
    internal const string CurrentSessionMarker = "Current ·";
    private readonly ConversationStore _currentStore;
    private readonly string? _currentPath;
    private readonly ConversationSession? _currentConversation;
    private readonly Func<ConversationStore, SessionListing, CancellationToken, Task<SessionDeleteOutcome>> _delete;
    private readonly Dictionary<string, SessionPickerBranchHeads> _branchHeads = new(StringComparer.Ordinal);
    private List<SessionCatalogEntry> _current;
    private List<SessionCatalogEntry>? _all;

    private TerminalSessionPickerState(ConversationStore currentStore, string? currentPath,
        ConversationSession? currentConversation, List<SessionCatalogEntry> current,
        Func<ConversationStore, SessionListing, CancellationToken, Task<SessionDeleteOutcome>> delete)
    {
        _currentStore = currentStore;
        _currentPath = currentPath;
        _currentConversation = currentConversation;
        _current = current;
        _delete = delete;
    }

    public SessionPickerScope Scope { get; private set; } = SessionPickerScope.Current;
    public SessionPickerSortMode Sort { get; private set; } = SessionPickerSortMode.Threaded;
    public SessionPickerNameFilter NameFilter { get; private set; } = SessionPickerNameFilter.All;
    public bool ShowPath { get; private set; }
    public string? ConfirmingDeleteId { get; private set; }
    public string? Status { get; private set; }
    public bool StatusIsError { get; private set; }
    public bool HasLoadedAll => _all is not null;
    public IReadOnlyList<SessionCatalogEntry> CurrentEntries => _current;
    public IReadOnlyList<SessionCatalogEntry> AllEntries => _all ?? [];

    public static Task<TerminalSessionPickerState> LoadAsync(ConversationStore store, string? currentPath,
        ConversationSession? currentConversation, CancellationToken cancellationToken = default) =>
        LoadAsync(store, currentPath, currentConversation,
            (catalog, listing, token) => SessionFileDeletion.DeleteAsync(catalog, listing, token),
            cancellationToken);

    internal static async Task<TerminalSessionPickerState> LoadAsync(ConversationStore store, string? currentPath,
        ConversationSession? currentConversation,
        Func<ConversationStore, SessionListing, CancellationToken, Task<SessionDeleteOutcome>> delete,
        CancellationToken cancellationToken = default)
    {
        var listings = await SessionCatalog.ListAsync(store, cancellationToken);
        var current = listings.Select(listing => new SessionCatalogEntry(listing, store)).ToList();
        var state = new TerminalSessionPickerState(store, currentPath, currentConversation, current, delete);
        await state.LoadBranchHeadsAsync(current, cancellationToken);
        return state;
    }

    public async Task EnsureAllLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_all is not null) return;
        _all = (await SessionCatalog.ListAllAsync(_currentStore, cancellationToken)).ToList();
        await LoadBranchHeadsAsync(_all, cancellationToken);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        _current = (await SessionCatalog.ListAsync(_currentStore, cancellationToken))
            .Select(listing => new SessionCatalogEntry(listing, _currentStore)).ToList();
        await LoadBranchHeadsAsync(_current, cancellationToken);
        if (_all is not null)
        {
            _all = (await SessionCatalog.ListAllAsync(_currentStore, cancellationToken)).ToList();
            await LoadBranchHeadsAsync(_all, cancellationToken);
        }
    }

    public void ToggleScope()
    {
        Scope = Scope == SessionPickerScope.Current ? SessionPickerScope.All : SessionPickerScope.Current;
        Status = null;
        StatusIsError = false;
        ConfirmingDeleteId = null;
    }

    public void ToggleSort()
    {
        Sort = Sort switch
        {
            SessionPickerSortMode.Threaded => SessionPickerSortMode.Recent,
            SessionPickerSortMode.Recent => SessionPickerSortMode.Relevance,
            _ => SessionPickerSortMode.Threaded
        };
        Status = null;
        StatusIsError = false;
    }

    public void ToggleNameFilter()
    {
        NameFilter = NameFilter == SessionPickerNameFilter.All
            ? SessionPickerNameFilter.Named : SessionPickerNameFilter.All;
        Status = null;
        StatusIsError = false;
    }

    public void TogglePath()
    {
        ShowPath = !ShowPath;
        Status = null;
        StatusIsError = false;
    }

    public bool BeginDelete(string? selectedId)
    {
        if (string.IsNullOrWhiteSpace(selectedId)) return false;
        var entry = FindEntry(selectedId);
        if (entry is null) return false;
        if (IsCurrent(entry.Listing))
        {
            SetStatus(CurrentSessionDeleteError, error: true);
            return false;
        }
        ConfirmingDeleteId = entry.Key;
        Status = null;
        StatusIsError = false;
        return true;
    }

    public void CancelDelete()
    {
        ConfirmingDeleteId = null;
        Status = null;
        StatusIsError = false;
    }

    public async Task<bool> ConfirmDeleteAsync(CancellationToken cancellationToken = default)
    {
        if (ConfirmingDeleteId is not { } id) return false;
        ConfirmingDeleteId = null;
        var entry = FindEntry(id);
        if (entry is null) return false;
        var outcome = await _delete(entry.Store, entry.Listing, cancellationToken);
        if (!outcome.Succeeded)
        {
            SetStatus($"Failed to delete: {outcome.Error}", error: true);
            return false;
        }
        Remove(id);
        SetStatus(outcome.Method == "trash" ? "Session moved to trash" : "Session deleted", error: false);
        return true;
    }

    public async Task<bool> RenameAsync(string id, string name, CancellationToken cancellationToken = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            SetStatus("Session name cannot be empty", error: true);
            return false;
        }
        var entry = FindEntry(id);
        if (entry is null) return false;
        try
        {
            var conversation = IsCurrent(entry.Listing) && _currentConversation?.Id == entry.Listing.Id
                ? _currentConversation
                : await entry.Store.LoadAsync(entry.Listing, cancellationToken);
            using (var nameChange = conversation.BeginSessionNameChange(trimmed))
            {
                await entry.Store.SaveAsync(conversation, entry.Listing.Path, cancellationToken);
                nameChange.Commit();
            }
            await ReloadAsync(cancellationToken);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            SetStatus($"Failed to rename: {error.Message}", error: true);
            return false;
        }
    }

    public IReadOnlyList<SessionPickerEntry> BuildEntries() => BuildEntriesForScope(Scope);

    public IReadOnlyList<SessionPickerEntry> BuildEntriesForScope(SessionPickerScope scope)
    {
        var source = scope == SessionPickerScope.All ? AllEntries : _current;
        IEnumerable<SessionCatalogEntry> filtered = NameFilter == SessionPickerNameFilter.Named
            ? source.Where(entry => !string.IsNullOrWhiteSpace(entry.Listing.Name))
            : source;
        // Threaded and Recent keep catalog order (newest activity first); Relevance delegates
        // ordering to the list's fuzzy score while preserving this order as the stable tie-break.
        return filtered.Select(entry => new SessionPickerEntry(entry,
            _branchHeads.GetValueOrDefault(entry.Listing.Path) ?? new(null, 0, IsAvailable: false),
            IsCurrent(entry.Listing))).ToArray();
    }

    public TerminalSelectionOption<SessionPickerEntry> BuildOption(SessionPickerEntry entry)
    {
        var session = entry.Listing;
        var id = session.Id[..Math.Min(12, session.Id.Length)];
        var name = string.IsNullOrWhiteSpace(session.Name) ? "(unnamed)" : session.Name;
        var modified = session.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var branchDescription = entry.BranchHeads.IsAvailable
            ? $"head {(entry.BranchHeads.HeadId is null ? "(empty)" : entry.BranchHeads.HeadId[..Math.Min(12, entry.BranchHeads.HeadId.Length)])} · {entry.BranchHeads.Count} branch heads"
            : "branch details unavailable";
        var path = ShowPath ? $"{ShortenPath(session.Path)} · " : "";
        var description = $"{path}{(entry.IsCurrent ? CurrentSessionMarker + " " : "")}{branchDescription} · {session.Model} · {session.MessageCount} messages · {modified}";
        var searchText = $"{name} {session.Id} {session.Model} {session.Path} {entry.BranchHeads.HeadId}";
        return new(entry.Key, entry, $"{name} · {id}", description, searchText, entry.IsCurrent);
    }

    public string BaseTitle => Scope == SessionPickerScope.All
        ? "Resume Session (All)" : "Resume Session (Current Folder)";

    public string SortLabel => Sort switch
    {
        SessionPickerSortMode.Threaded => "Threaded",
        SessionPickerSortMode.Recent => "Recent",
        _ => "Fuzzy"
    };

    public string NameFilterLabel => NameFilter == SessionPickerNameFilter.All ? "All" : "Named";

    public string ScopeLine => Scope == SessionPickerScope.All ? "All" : "Current Folder";

    public void SetStatus(string? status, bool error)
    {
        Status = status;
        StatusIsError = error;
    }

    public SessionPickerEntry? FindEntry(string id) =>
        BuildEntries().FirstOrDefault(entry => entry.Key == id);

    private void Remove(string id)
    {
        _current.RemoveAll(entry => entry.Listing.Id == id);
        _all?.RemoveAll(entry => entry.Listing.Id == id);
    }

    private bool IsCurrent(SessionListing listing) => _currentPath is not null && PathsEqual(listing.Path, _currentPath);

    private async Task LoadBranchHeadsAsync(IReadOnlyList<SessionCatalogEntry> entries, CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listing = entry.Listing;
            if (IsCurrent(listing) && _currentConversation?.Id == listing.Id)
            {
                _branchHeads[listing.Path] = SessionPickerBranchHeads.From(_currentConversation);
                continue;
            }
            try
            {
                var conversation = await entry.Store.LoadAsync(listing, cancellationToken);
                _branchHeads[listing.Path] = SessionPickerBranchHeads.From(conversation);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                InvalidDataException or System.Text.Json.JsonException)
            {
                _branchHeads[listing.Path] = new(null, 0, IsAvailable: false);
            }
        }
    }

    private static string ShortenPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.Ordinal)
            ? "~" + path[home.Length..] : path;
    }

    private static bool PathsEqual(string path, string other) => Path.GetFullPath(path).Equals(
        Path.GetFullPath(other), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
