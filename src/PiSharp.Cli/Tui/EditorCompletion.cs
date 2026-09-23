namespace PiSharp.Cli.Tui;

/// <summary>Completes implemented slash commands and local paths; UI only, no agent state.</summary>
public sealed class EditorCompletion(string workingDirectory, Func<IReadOnlyList<string>>? dynamicCommands = null)
{
    private static readonly string[] s_commands =
    ["/tree", "/branch", "/fork", "/clone", "/new", "/sessions", "/resume", "/name", "/model", "/models", "/compact", "/session", "/trust", "/reload", "/quit"];
    private readonly string _cwd = Path.GetFullPath(workingDirectory);

    public IReadOnlyList<string> Complete(EditorBuffer buffer)
    {
        var before = buffer.Text[..buffer.Cursor];
        var start = before.LastIndexOfAny([' ', '\n', '\t']) + 1;
        var fragment = before[start..];
        if (start == 0 && fragment.StartsWith('/'))
        {
            var matches = s_commands.Concat(dynamicCommands?.Invoke() ?? [])
                .Where(command => command.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)).ToArray();
            return Apply(buffer, start, fragment, matches);
        }
        if (!fragment.StartsWith('@')) return [];
        var path = fragment[1..];
        var parent = Path.GetDirectoryName(path);
        var prefix = Path.GetFileName(path);
        var folder = parent is null or "" ? _cwd : Path.GetFullPath(parent, _cwd);
        if (!Directory.Exists(folder)) return [];
        var entries = Directory.EnumerateFileSystemEntries(folder)
            .Where(file => Path.GetFileName(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(file => prefix.StartsWith('.') || !Path.GetFileName(file).StartsWith('.'))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase).Take(100)
            .Select(file => "@" + (parent is null or "" ? "" : parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar) +
                Path.GetFileName(file) + (Directory.Exists(file) ? Path.DirectorySeparatorChar.ToString() : ""))
            .ToArray();
        return Apply(buffer, start, fragment, entries);
    }

    private static IReadOnlyList<string> Apply(EditorBuffer buffer, int start, string fragment, IReadOnlyList<string> matches)
    {
        if (matches.Count == 1)
            buffer.Replace(start, fragment.Length, matches[0]);
        else if (matches.Count > 1)
        {
            var common = matches[0];
            foreach (var match in matches.Skip(1))
                common = common[..common.TakeWhile((c, i) => i < match.Length && char.ToUpperInvariant(c) == char.ToUpperInvariant(match[i])).Count()];
            if (common.Length > fragment.Length) buffer.Replace(start, fragment.Length, common);
        }
        return matches;
    }
}
