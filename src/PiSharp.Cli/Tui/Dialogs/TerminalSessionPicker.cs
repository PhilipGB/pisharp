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
        var options = ToOptions(sessions, currentPath, sortByName);
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
                    list.ReplaceOptions(ToOptions(sessions, currentPath, sortByName), scopedOptions: null);
                    return true;
                }
                if (!editor.MatchesKeybinding("app.session.rename", key)) return false;
                var selected = list.Selected?.Option.Value;
                if (selected is null) return true;
                var refreshed = await RenameSessionAsync(selected, currentPath, currentConversation, cancellationToken);
                if (refreshed is not null)
                {
                    sessions = refreshed;
                    list.ReplaceOptions(ToOptions(sessions, currentPath, sortByName), scopedOptions: null);
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
        IReadOnlyList<SessionListing> sessions, string? currentPath, bool sortByName)
    {
        IEnumerable<SessionListing> ordered = sortByName
            ? sessions.OrderBy(session => string.IsNullOrWhiteSpace(session.Name) ? 1 : 0)
                .ThenBy(session => session.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(session => session.Id, StringComparer.Ordinal)
            : sessions;
        return ordered.Select(session => ToOption(session, currentPath)).ToArray();
    }

    private static TerminalSelectionOption<SessionListing> ToOption(SessionListing session, string? currentPath)
    {
        var id = session.Id[..Math.Min(12, session.Id.Length)];
        var name = string.IsNullOrWhiteSpace(session.Name) ? "(unnamed)" : session.Name;
        var modified = session.ModifiedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var description = $"{id} · {session.Model} · {session.MessageCount} messages · {modified}";
        var searchText = $"{name} {session.Id} {session.Model} {session.Path}";
        return new(Key(session), session, $"{name} · {id}", description, searchText,
            PathsEqual(session.Path, currentPath));
    }

    private static string Key(SessionListing session) => session.Id;

    private static bool PathsEqual(string path, string? other) => other is not null &&
        Path.GetFullPath(path).Equals(Path.GetFullPath(other), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
}
