using System.Globalization;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Tui;

/// <summary>Adapts the reusable list overlay to project-scoped saved sessions.</summary>
internal sealed class TerminalSessionPicker(ConversationStore store, TerminalEditor editor)
{
    public async Task<SessionListing?> ShowAsync(string? currentPath = null,
        CancellationToken cancellationToken = default)
    {
        var sessions = await SessionCatalog.ListAsync(store, cancellationToken);
        var sortByName = false;
        var options = ToOptions(sessions, currentPath, sortByName);
        var current = sessions.FirstOrDefault(session => PathsEqual(session.Path, currentPath));
        var sortKey = editor.GetDisplayKeys("app.session.toggleSort");
        var chosen = editor.ShowSelectionList("Resume session · Recent", options,
            current is null ? null : Key(current), emptyMessage: "No saved sessions in this project",
            actionHint: sortKey is null ? null : $"{sortKey} sort",
            handleInput: (list, input) =>
            {
                if (input.Key is not { } key || !editor.MatchesKeybinding("app.session.toggleSort", key)) return false;
                sortByName = !sortByName;
                list.SetTitle($"Resume session · {(sortByName ? "Name" : "Recent")}");
                list.ReplaceOptions(ToOptions(sessions, currentPath, sortByName), scopedOptions: null);
                return true;
            });
        return chosen?.Option.Value;
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
