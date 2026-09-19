using PiSharp.Core;

namespace PiSharp.Cli;

/// <summary>
/// Console port of the pinned SessionSelectorComponent: the session picker used by
/// <c>/resume</c> and <c>--resume</c>. It lists sessions as metadata (no full documents),
/// supports the pinned query syntax (fuzzy tokens, quoted phrases, <c>re:</c> regex), the
/// three sort modes (threaded/recent/relevance), the all/named filter, rename, and delete
/// (confirmation plus the pinned "cannot delete the currently active session" guard).
///
/// Key mapping (documented adaptation of the TUI): number selects, free text searches,
/// <c>sort=&lt;mode&gt;</c> / <c>named</c> / <c>all</c> switch modes, <c>rename &lt;n&gt;
/// &lt;name&gt;</c> and <c>delete &lt;n&gt;</c> act on the visible list, <c>?</c> prints help,
/// Esc or an empty line cancels.
/// </summary>
internal static class SessionPicker
{
    private const int PreviewLength = 60;

    /// <summary>
    /// Shows the picker and returns the selected session, or null when the user cancels
    /// (Esc, empty line, or EOF on redirected input).
    /// </summary>
    public static async Task<PiSessionInfo?> PickAsync(
        SessionStore store,
        IConsoleIO console,
        string? activePath,
        CancellationToken cancellationToken)
    {
        var sessions = new List<PiSessionInfo>(await store.ListAllInfosAsync(cancellationToken));
        if (sessions.Count == 0)
        {
            console.WriteLine("No saved sessions.");
            return null;
        }

        var sortMode = SessionSortMode.Threaded;
        var nameFilter = SessionNameFilter.All;
        var query = string.Empty;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = ComputeRows(sessions, query, sortMode, nameFilter);
            Render(console, rows, query, sortMode, nameFilter, store.WorkspaceRoot, activePath);

            string input;
            try
            {
                input = ConsoleKeyInput.ReadLine(console, "Session (number to open, text to search, ? for help): ", cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            if (input.Length == 0)
            {
                return null;
            }

            if (int.TryParse(input, out var selectedIndex) &&
                selectedIndex >= 1 && selectedIndex <= rows.Count)
            {
                return rows[selectedIndex - 1].Info;
            }

            if (input == "?")
            {
                PrintHelp(console);
                continue;
            }

            if (input.StartsWith("sort=", StringComparison.OrdinalIgnoreCase))
            {
                var value = input["sort=".Length..].Trim();
                if (value.Equals("threaded", StringComparison.OrdinalIgnoreCase))
                {
                    sortMode = SessionSortMode.Threaded;
                }
                else if (value.Equals("recent", StringComparison.OrdinalIgnoreCase))
                {
                    sortMode = SessionSortMode.Recent;
                }
                else if (value.Equals("relevance", StringComparison.OrdinalIgnoreCase))
                {
                    sortMode = SessionSortMode.Relevance;
                }
                else
                {
                    console.WriteLine("Usage: sort=threaded | sort=recent | sort=relevance");
                }

                continue;
            }

            if (input is "named" or "all")
            {
                nameFilter = input == "named" ? SessionNameFilter.Named : SessionNameFilter.All;
                continue;
            }

            if (input.StartsWith("rename ", StringComparison.Ordinal) ||
                input.StartsWith("delete ", StringComparison.Ordinal))
            {
                // Commands are terminal: a failed rename/delete must never fall through
                // and turn the command text into a search query.
                var refresh = input.StartsWith("rename ", StringComparison.Ordinal)
                    ? await HandleRenameAsync(store, console, rows, input["rename ".Length..], activePath, cancellationToken)
                    : await HandleDeleteAsync(store, console, rows, input["delete ".Length..], activePath, cancellationToken);
                if (refresh)
                {
                    sessions = new List<PiSessionInfo>(await store.ListAllInfosAsync(cancellationToken));
                    if (sessions.Count == 0)
                    {
                        // Pinned closes the selector when no sessions remain.
                        console.WriteLine("No saved sessions.");
                        return null;
                    }
                }

                continue;
            }

            // Anything else is a search query (pinned query syntax).
            query = input;
        }
    }

    private static List<(PiSessionInfo Info, int Depth, bool IsLast)> ComputeRows(
        List<PiSessionInfo> sessions,
        string query,
        SessionSortMode sortMode,
        SessionNameFilter nameFilter)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0 && sortMode == SessionSortMode.Threaded)
        {
            // Pinned threaded view without a search: the parentSessionPath tree.
            return SessionSearch.BuildThreadedTree(sessions)
                .Select(node => (node.Session, node.Depth, node.IsLast))
                .ToList();
        }

        return SessionSearch.FilterAndSort(sessions, query, sortMode, nameFilter)
            .Select(info => (info, 0, true))
            .ToList();
    }

    private static void Render(
        IConsoleIO console,
        List<(PiSessionInfo Info, int Depth, bool IsLast)> rows,
        string query,
        SessionSortMode sortMode,
        SessionNameFilter nameFilter,
        string workspaceRoot,
        string? activePath)
    {
        var active = activePath is null ? null : Path.GetFullPath(activePath);
        var workspace = Path.GetFullPath(workspaceRoot);

        console.WriteLine($"Sessions: {sortMode} | {nameFilter}   query: '{query}'");
        for (var i = 0; i < rows.Count; i++)
        {
            var (info, depth, isLast) = rows[i];
            var indent = new string(' ', 2 + depth * 2) + (depth > 0 ? (isLast ? "└─ " : "├─ ") : string.Empty);
            var marker = Path.GetFullPath(info.Path) == active ? " (active)" : string.Empty;
            var title = !string.IsNullOrWhiteSpace(info.Name)
                ? $"\"{info.Name}\""
                : Truncate(info.FirstMessage, PreviewLength);
            var cwd = string.Equals(Path.GetFullPath(info.Cwd), workspace,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? string.Empty
                : $"  {info.Cwd}";
            console.WriteLine(
                $"{indent}{i + 1,3}. {info.Id[..Math.Min(8, info.Id.Length)]}  {info.Modified:yyyy-MM-dd}  {info.MessageCount,3} msgs  {title}{cwd}{marker}");
        }
    }

    private static void PrintHelp(IConsoleIO console)
    {
        console.WriteLine("""
            Picker:
              <number>            open the selected session
              <text>              search (fuzzy tokens, "quoted phrases", re:regex)
              sort=<mode>         threaded | recent | relevance
              named | all         toggle the name filter
              rename <n> <name>   rename session <n> (blank name is a no-op)
              delete <n>          delete session <n> (with confirmation)
              Esc / empty line    cancel
            """);
    }

    private static async Task<bool> HandleRenameAsync(
        SessionStore store,
        IConsoleIO console,
        List<(PiSessionInfo Info, int Depth, bool IsLast)> rows,
        string argument,
        string? activePath,
        CancellationToken cancellationToken)
    {
        var parts = argument.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts[0].Length == 0)
        {
            console.WriteLine("Usage: rename <number> <name>");
            return false;
        }

        if (!int.TryParse(parts[0], out var index) || index < 1 || index > rows.Count)
        {
            console.WriteLine("Unknown session number.");
            return false;
        }

        var name = parts.Length > 1 ? parts[1] : string.Empty;
        try
        {
            await store.RenameSessionAsync(rows[index - 1].Info.Path, name, cancellationToken);
            console.WriteLine(string.IsNullOrWhiteSpace(name)
                ? "Name unchanged (blank names are a no-op)."
                : $"Renamed to: {SessionStore.SanitizeName(name)}");
            return true;
        }
        catch (Exception ex)
        {
            console.WriteLine($"Rename failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> HandleDeleteAsync(
        SessionStore store,
        IConsoleIO console,
        List<(PiSessionInfo Info, int Depth, bool IsLast)> rows,
        string argument,
        string? activePath,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(argument.Trim(), out var index) || index < 1 || index > rows.Count)
        {
            console.WriteLine("Usage: delete <number>");
            return false;
        }

        var info = rows[index - 1].Info;
        var active = activePath is null ? null : Path.GetFullPath(activePath);
        if (active is not null && string.Equals(Path.GetFullPath(info.Path), active,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            // Pinned guard: the picker refuses to delete the session currently in use.
            console.WriteLine("Cannot delete the currently active session");
            return false;
        }

        string answer;
        try
        {
            answer = ConsoleKeyInput.ReadLine(
                console,
                $"Delete session {info.Id[..Math.Min(8, info.Id.Length)]} ({info.Path})? [y/N] ",
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            console.WriteLine("Delete cancelled.");
            return false;
        }

        if (!answer.Equals("y", StringComparison.OrdinalIgnoreCase) &&
            !answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            console.WriteLine("Delete cancelled.");
            return false;
        }

        var result = await store.DeleteSessionAsync(info.Path, cancellationToken);
        if (!result.Ok)
        {
            console.WriteLine($"Delete failed: {result.Error}");
            return false;
        }

        console.WriteLine($"Deleted session {info.Id[..Math.Min(8, info.Id.Length)]} ({result.Method}).");
        return true;
    }

    private static string Truncate(string text, int length)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= length ? flat : flat[..length] + "…";
    }
}
