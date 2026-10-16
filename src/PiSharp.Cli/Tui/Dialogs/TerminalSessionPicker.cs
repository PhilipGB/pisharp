using System.Globalization;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Tui;

/// <summary>Adapts the reusable list overlay to project-scoped saved sessions.</summary>
internal sealed class TerminalSessionPicker(ConversationStore store, TerminalEditor editor)
{
    public async Task<SessionListing?> ShowAsync(string? currentPath = null,
        ConversationSession? currentConversation = null, CancellationToken cancellationToken = default)
    {
        var sessions = await SessionCatalog.ListAsync(store, cancellationToken);
        var sortByName = false;
        var branchHeads = await GetBranchHeadsAsync(sessions, currentPath, currentConversation, cancellationToken);
        var options = ToOptions(sessions, currentPath, sortByName, branchHeads);
        var current = sessions.FirstOrDefault(session => PathsEqual(session.Path, currentPath));
        var sortKey = editor.GetDisplayKeys("app.session.toggleSort");
        var renameKey = editor.GetDisplayKeys("app.session.rename");
        var actionHint = string.Join(" · ", new[]
            {
                sortKey is null ? null : $"{sortKey} sort",
                renameKey is null ? null : $"{renameKey} rename"
            }.Where(value => value is not null));
        var chosen = await editor.ShowSelectionListAsync("Resume session · Recent", options,
            current is null ? null : Key(current), emptyMessage: "No saved sessions in this project",
            actionHint: actionHint.Length == 0 ? null : actionHint,
            handleInput: async (list, input) =>
            {
                if (input.Key is not { } key) return false;
                if (editor.MatchesKeybinding("app.session.toggleSort", key))
                {
                    sortByName = !sortByName;
                    list.SetTitle($"Resume session · {(sortByName ? "Name" : "Recent")}");
                    list.ReplaceOptions(ToOptions(sessions, currentPath, sortByName, branchHeads), scopedOptions: null);
                    return true;
                }
                if (!editor.MatchesKeybinding("app.session.rename", key)) return false;
                var selected = list.Selected?.Option.Value;
                if (selected is null) return true;
                var refreshed = await RenameSessionAsync(selected, currentPath, currentConversation, cancellationToken);
                if (refreshed is not null)
                {
                    sessions = refreshed;
                    branchHeads = await GetBranchHeadsAsync(sessions, currentPath, currentConversation, cancellationToken);
                    list.ReplaceOptions(ToOptions(sessions, currentPath, sortByName, branchHeads), scopedOptions: null);
                }
                return true;
            }, cancellationToken: cancellationToken);
        return chosen?.Option.Value;
    }

    private async Task<IReadOnlyList<SessionListing>?> RenameSessionAsync(SessionListing listing,
        string? currentPath, ConversationSession? currentConversation, CancellationToken cancellationToken)
    {
        var entered = await editor.PromptSequenceAsync("Rename Session",
            [("Session name", "A short descriptive name")], cancellationToken: cancellationToken,
            initialValues: [listing.Name ?? ""]);
        if (entered is not { Count: 1 } || string.IsNullOrWhiteSpace(entered[0])) return null;

        var conversation = PathsEqual(listing.Path, currentPath) && currentConversation?.Id == listing.Id
            ? currentConversation
            : await store.LoadAsync(listing.Path, cancellationToken);
        using (var nameChange = conversation.BeginSessionNameChange(entered[0]))
        {
            await store.SaveAsync(conversation, listing.Path, cancellationToken);
            nameChange.Commit();
        }

        return await SessionCatalog.ListAsync(store, cancellationToken);
    }

    private static IReadOnlyList<TerminalSelectionOption<SessionListing>> ToOptions(
        IReadOnlyList<SessionListing> sessions, string? currentPath, bool sortByName,
        IReadOnlyDictionary<string, SessionPickerBranchHeads> branchHeads)
    {
        IEnumerable<SessionListing> ordered = sortByName
            ? sessions.OrderBy(session => string.IsNullOrWhiteSpace(session.Name) ? 1 : 0)
                .ThenBy(session => session.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(session => session.Id, StringComparer.Ordinal)
            : sessions;
        return ordered.Select(session => ToOption(session, currentPath, branchHeads[session.Path])).ToArray();
    }

    internal static TerminalSelectionOption<SessionListing> ToOption(SessionListing session, string? currentPath,
        SessionPickerBranchHeads branchHeads)
    {
        var id = session.Id[..Math.Min(12, session.Id.Length)];
        var name = string.IsNullOrWhiteSpace(session.Name) ? "(unnamed)" : session.Name;
        var modified = session.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var branchDescription = branchHeads.IsAvailable
            ? $"head {(branchHeads.HeadId is null ? "(empty)" : branchHeads.HeadId[..Math.Min(12, branchHeads.HeadId.Length)])} · {branchHeads.Count} branch heads"
            : "branch details unavailable";
        var current = PathsEqual(session.Path, currentPath);
        var description = $"{(current ? "Current · " : "")}{branchDescription} · {session.Model} · {session.MessageCount} messages · {modified}";
        var searchText = $"{name} {session.Id} {session.Model} {session.Path} {branchHeads.HeadId}";
        return new(Key(session), session, $"{name} · {id}", description, searchText,
            current);
    }

    private async Task<IReadOnlyDictionary<string, SessionPickerBranchHeads>> GetBranchHeadsAsync(
        IReadOnlyList<SessionListing> sessions, string? currentPath, ConversationSession? currentConversation,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SessionPickerBranchHeads>(StringComparer.Ordinal);
        foreach (var listing in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PathsEqual(listing.Path, currentPath) && currentConversation?.Id == listing.Id)
            {
                result.Add(listing.Path, SessionPickerBranchHeads.From(currentConversation));
                continue;
            }
            try
            {
                var conversation = await store.LoadAsync(listing.Path, cancellationToken);
                result.Add(listing.Path, SessionPickerBranchHeads.From(conversation));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                // A session can change between cataloguing and opening the picker. Keep its row visible
                // so one stale file cannot prevent resuming another session.
                result.Add(listing.Path, new(null, 0, IsAvailable: false));
            }
        }
        return result;
    }

    private static string Key(SessionListing session) => session.Id;

    private static bool PathsEqual(string path, string? other) => other is not null &&
        Path.GetFullPath(path).Equals(Path.GetFullPath(other), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
}

internal sealed record SessionPickerBranchHeads(string? HeadId, int Count, bool IsAvailable = true)
{
    public static SessionPickerBranchHeads From(ConversationSession conversation)
    {
        var entries = conversation.Tree.Entries;
        var parents = entries.Where(entry => entry.ParentId is not null)
            .Select(entry => entry.ParentId!).ToHashSet(StringComparer.Ordinal);
        return new(conversation.Tree.HeadId, entries.Count(entry => !parents.Contains(entry.Id)));
    }
}
