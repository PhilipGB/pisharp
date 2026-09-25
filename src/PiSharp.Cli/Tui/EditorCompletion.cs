using System.Globalization;

namespace PiSharp.Cli.Tui;

/// <summary>Completes implemented slash commands and local paths; UI only, no agent state.</summary>
public sealed class EditorCompletion(string workingDirectory, Func<IReadOnlyList<string>>? dynamicCommands = null)
{
    private static readonly string[] s_commands =
    [
        "/tree",
        "/branch",
        "/fork",
        "/clone",
        "/new",
        "/sessions",
        "/resume",
        "/delete-session",
        "/import",
        "/name",
        "/model",
        "/models",
        "/settings",
        "/compact",
        "/export",
        "/export-jsonl",
        "/session",
        "/trust",
        "/reload",
        "/hotkeys",
        "/quit"
    ];
    private static readonly string s_cjkPunctuation = "，．：；！？（）［］｛｝“”‘’…—、。「」『』《》〈〉【】〔〕〖〗〘〙〚〛〝〞";
    private static readonly IReadOnlyDictionary<char, char> s_pathWrappers = new Dictionary<char, char>
    {
        ['('] = ')',
        ['['] = ']',
        ['{'] = '}',
        ['<'] = '>',
        ['`'] = '`'
    };
    private readonly string _cwd = Path.GetFullPath(workingDirectory);

    public IReadOnlyList<string> Complete(EditorBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var before = buffer.Text[..buffer.Cursor];
        var start = FindTokenStart(before);
        if (start == 0 && before[start..].StartsWith('/'))
        {
            var slashFragment = before[start..];
            var matches = s_commands.Concat(dynamicCommands?.Invoke() ?? [])
                .Where(command => command.StartsWith(slashFragment, StringComparison.OrdinalIgnoreCase)).ToArray();
            return Apply(buffer, start, slashFragment, matches);
        }

        var wrapperStart = start;
        while (wrapperStart < before.Length && s_pathWrappers.TryGetValue(before[wrapperStart], out var closing) &&
            before.IndexOf(closing, wrapperStart + 1) < 0)
            wrapperStart++;
        var fragmentStart = wrapperStart;
        var fragment = before[fragmentStart..];
        var hasAtPrefix = fragment.StartsWith('@');
        var quoted = fragment.StartsWith("\"", StringComparison.Ordinal) || fragment.StartsWith("@\"", StringComparison.Ordinal);

        var path = fragment;
        if (hasAtPrefix) path = path[1..];
        if (path.StartsWith('"')) path = path[1..];
        var hasExistingClosingQuote = buffer.Cursor < buffer.Text.Length && buffer.Text[buffer.Cursor] == '"';
        var matchesForPath = GetPathCompletions(path, hasAtPrefix, quoted);
        return Apply(buffer, fragmentStart, fragment, matchesForPath, hasExistingClosingQuote);
    }

    public void ApplySelected(EditorBuffer buffer, string completion)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(completion);
        var before = buffer.Text[..buffer.Cursor];
        var start = FindTokenStart(before);
        if (start == 0 && before[start..].StartsWith('/'))
        {
            var slashFragment = before[start..];
            Apply(buffer, start, slashFragment, [completion]);
            return;
        }

        var wrapperStart = start;
        while (wrapperStart < before.Length && s_pathWrappers.TryGetValue(before[wrapperStart], out var closing) &&
            before.IndexOf(closing, wrapperStart + 1) < 0)
            wrapperStart++;
        var fragmentStart = wrapperStart;
        var fragment = before[fragmentStart..];
        var hasExistingClosingQuote = buffer.Cursor < buffer.Text.Length && buffer.Text[buffer.Cursor] == '"';
        Apply(buffer, fragmentStart, fragment, [completion], hasExistingClosingQuote);
    }

    private static int FindTokenStart(string before)
    {
        var quoteStart = FindUnclosedQuoteStart(before);
        if (quoteStart >= 0)
            return quoteStart > 0 && before[quoteStart - 1] == '@' ? quoteStart - 1 : quoteStart;

        var start = 0;
        for (var index = 0; index < before.Length; index++)
        {
            if (char.IsWhiteSpace(before[index]) || s_cjkPunctuation.Contains(before[index]) || before[index] == '"')
                start = index + 1;
        }
        return start;
    }

    private static int FindUnclosedQuoteStart(string text)
    {
        var open = -1;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '"' || index > 0 && text[index - 1] == '\\') continue;
            open = open < 0 ? index : -1;
        }
        return open;
    }

    private IReadOnlyList<string> GetPathCompletions(string rawPath, bool hasAtPrefix, bool quoted)
    {
        var separator = Math.Max(rawPath.LastIndexOf('/'), rawPath.LastIndexOf('\\'));
        var parent = separator < 0 ? "" : rawPath[..(separator + 1)];
        var namePrefix = rawPath[(separator + 1)..];
        if (rawPath == "~")
        {
            parent = "~/";
            namePrefix = "";
        }

        try
        {
            string folder;
            if (parent.StartsWith("~/", StringComparison.Ordinal))
                folder = Path.GetFullPath(parent[2..].Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            else if (parent == "~")
                folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            else if (parent.Length == 0)
                folder = _cwd;
            else
            {
                var localParent = parent.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                folder = Path.IsPathRooted(localParent) ? Path.GetFullPath(localParent) : Path.GetFullPath(localParent, _cwd);
            }
            if (!Directory.Exists(folder)) return [];
            var matches = new List<(string Value, string Name, bool IsDirectory)>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                var name = Path.GetFileName(entry);
                if (name.Any(char.IsControl)) continue;
                if (!name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (name == ".git") continue;
                var isDirectory = Directory.Exists(entry);
                var displayParent = parent.Replace('\\', '/');
                var displayPath = displayParent + name + (isDirectory ? "/" : "");
                var needsQuotes = quoted || displayPath.Any(character => char.IsWhiteSpace(character) || s_cjkPunctuation.Contains(character));
                var marker = hasAtPrefix ? "@" : "";
                var value = needsQuotes ? $"{marker}\"{displayPath}\"" : marker + displayPath;
                matches.Add((value, name, isDirectory));
            }
            return matches.OrderByDescending(match => match.IsDirectory)
                .ThenBy(match => match.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(match => match.Name, StringComparer.Ordinal)
                .Take(100).Select(match => match.Value).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> Apply(EditorBuffer buffer, int start, string fragment, IReadOnlyList<string> matches,
        bool hasExistingClosingQuote = false)
    {
        if (matches.Count == 1)
        {
            var replacement = hasExistingClosingQuote && matches[0].EndsWith('"') ? matches[0][..^1] : matches[0];
            buffer.Replace(start, fragment.Length, replacement);
            if (replacement.EndsWith('"') && buffer.Cursor > 0 && buffer.Text[buffer.Cursor - 1] == '"')
                buffer.SetText(buffer.Text, buffer.Cursor - 1);
        }
        else if (matches.Count > 1)
        {
            var common = CommonPrefix(matches);
            if (common.Length > fragment.Length) buffer.Replace(start, fragment.Length, common);
        }
        return matches;
    }

    private static string CommonPrefix(IReadOnlyList<string> matches)
    {
        var first = matches[0];
        var boundaries = StringInfo.ParseCombiningCharacters(first);
        var length = 0;
        for (var index = 0; index < boundaries.Length; index++)
        {
            var boundary = boundaries[index];
            var end = index + 1 < boundaries.Length ? boundaries[index + 1] : first.Length;
            var element = first[boundary..end];
            if (matches.Skip(1).Any(match => match.Length < end ||
                !string.Equals(element, match[boundary..end], StringComparison.OrdinalIgnoreCase))) break;
            length = end;
        }
        return first[..length];
    }
}
