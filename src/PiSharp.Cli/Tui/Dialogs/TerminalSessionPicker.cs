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
        var options = sessions.Select(session => ToOption(session, currentPath)).ToArray();
        var current = sessions.FirstOrDefault(session => PathsEqual(session.Path, currentPath));
        var chosen = editor.ShowSelectionList("Resume session", options, current is null ? null : Key(current),
            emptyMessage: "No saved sessions in this project");
        return chosen?.Option.Value;
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
