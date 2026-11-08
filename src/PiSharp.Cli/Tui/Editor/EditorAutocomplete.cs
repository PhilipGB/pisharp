using System.Diagnostics;
using System.Text;

namespace PiSharp.Cli.Tui;

internal enum EditorCompletionKind { Slash, Path, Argument }

internal sealed record EditorCompletionItem(string Value, string Label, string? Description,
    bool IsDirectory, bool IsSlash, bool IsArgument, bool IsAt);

internal sealed record EditorCompletionResult(EditorCompletionKind Kind, string Prefix,
    IReadOnlyList<EditorCompletionItem> Items);

public sealed record EditorModelOption(string Provider, string Id, string? Name);

/// <summary>Pi-compatible autocomplete provider: slash commands, arguments and local paths.</summary>
internal sealed class EditorAutocomplete
{
    private static readonly string[] s_piCommandOrder =
    [
        "/settings", "/model", "/tree", "/thinking", "/scoped-models", "/export", "/import", "/share",
        "/copy", "/name", "/session", "/hotkeys", "/fork", "/clone", "/trust", "/login", "/logout",
        "/new", "/compact", "/resume", "/reload", "/quit"
    ];
    private static readonly HashSet<string> s_piCommands = new(s_piCommandOrder, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> s_pathDelimiters = new([" ", "\t", "\"", "'", "="]);
    private static readonly Dictionary<char, char> s_wrappers = new()
    {
        ['('] = ')',
        ['['] = ']',
        ['{'] = '}',
        ['<'] = '>',
        ['`'] = '`'
    };
    private static readonly char[] s_cjkPunctuation =
        "，．：；！？（）［］｛｝“”‘’…—、。「」『』《》〈〉【】〔〕〖〗〘〙〚〛〝〞".ToCharArray();

    private readonly Func<string> _workingDirectory;
    private readonly Func<IReadOnlyList<string>>? _dynamicCommands;
    private readonly Func<string, string?>? _describeCommand;
    private readonly Func<IReadOnlyList<EditorModelOption>>? _getModels;
    private string? _fdPath;
    private bool _fdResolved;

    public EditorAutocomplete(Func<string> workingDirectory, Func<IReadOnlyList<string>>? dynamicCommands = null,
        Func<string, string?>? describeCommand = null,
        Func<IReadOnlyList<EditorModelOption>>? getModels = null)
    {
        _workingDirectory = workingDirectory;
        _dynamicCommands = dynamicCommands;
        _describeCommand = describeCommand;
        _getModels = getModels;
    }

    public EditorCompletionResult? GetSuggestions(string text, int cursor, bool force)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);
        var lineStart = text.LastIndexOf('\n', Math.Max(0, cursor - 1)) + 1;
        var lineEnd = text.IndexOf('\n', cursor);
        if (lineEnd < 0) lineEnd = text.Length;
        var before = text[lineStart..cursor];

        if (ExtractAtPrefix(before) is { } atPrefix)
        {
            var parsed = ParsePathPrefix(atPrefix);
            var items = GetFuzzyFileSuggestions(parsed.RawPrefix, parsed.IsQuotedPrefix);
            return items.Count == 0 ? null : new(EditorCompletionKind.Path, atPrefix, items);
        }

        var commandText = before.TrimStart();
        if (!force && commandText.StartsWith('/'))
        {
            var spaceIndex = commandText.IndexOf(' ');
            if (spaceIndex < 0)
            {
                var items = GetSlashCommandSuggestions(commandText[1..]);
                return items.Count == 0 ? null : new(EditorCompletionKind.Slash, commandText, items);
            }
            var commandName = commandText[1..spaceIndex];
            var argument = commandText[(spaceIndex + 1)..];
            var arguments = GetArgumentSuggestions(commandName, argument);
            return arguments.Count == 0 ? null : new(EditorCompletionKind.Argument, argument, arguments);
        }

        var pathPrefix = ExtractPathPrefix(before, force);
        if (pathPrefix is null) return null;
        var pathItems = GetFileSuggestions(pathPrefix);
        return pathItems.Count == 0 ? null : new(EditorCompletionKind.Path, pathPrefix, pathItems);
    }

    public void Apply(EditorBuffer buffer, EditorCompletionResult result, EditorCompletionItem item)
    {
        var text = buffer.Text;
        var cursor = buffer.Cursor;
        var lineStart = text.LastIndexOf('\n', Math.Max(0, cursor - 1)) + 1;
        var lineEnd = text.IndexOf('\n', cursor);
        if (lineEnd < 0) lineEnd = text.Length;
        var prefixStart = cursor - result.Prefix.Length;
        if (prefixStart < lineStart) prefixStart = lineStart;
        var beforePrefix = text[lineStart..prefixStart];
        var afterCursor = text[cursor..lineEnd];
        var isQuotedPrefix = result.Prefix.StartsWith('"') ||
            result.Prefix.StartsWith("@\"", StringComparison.Ordinal);
        var hasLeadingQuoteAfterCursor = afterCursor.StartsWith('"');
        var hasTrailingQuoteInItem = item.Value.EndsWith('"');
        var adjustedAfter = isQuotedPrefix && hasTrailingQuoteInItem && hasLeadingQuoteAfterCursor
            ? afterCursor[1..] : afterCursor;
        string newLine;
        int cursorColumn;
        if (result.Kind == EditorCompletionKind.Slash)
        {
            newLine = beforePrefix + "/" + item.Value + " " + adjustedAfter;
            cursorColumn = beforePrefix.Length + item.Value.Length + 2;
        }
        else if (result.Kind == EditorCompletionKind.Argument)
        {
            newLine = beforePrefix + item.Value + adjustedAfter;
            var offset = item.IsDirectory && hasTrailingQuoteInItem ? item.Value.Length - 1 : item.Value.Length;
            cursorColumn = beforePrefix.Length + offset;
        }
        else
        {
            var suffix = item.IsDirectory ? "" : " ";
            newLine = beforePrefix + item.Value + suffix + adjustedAfter;
            var offset = item.IsDirectory && hasTrailingQuoteInItem ? item.Value.Length - 1 : item.Value.Length;
            cursorColumn = beforePrefix.Length + offset + suffix.Length;
        }
        var updated = text[..lineStart] + newLine + text[lineEnd..];
        buffer.SetText(updated, lineStart + cursorColumn);
    }

    private IReadOnlyList<EditorCompletionItem> GetSlashCommandSuggestions(string prefix)
    {
        var catalogue = BuildPiCatalogue();
        var commands = PiFuzzy.Filter<(string Name, string? Description)>(catalogue, prefix, command =>
            command.Name.StartsWith("skill:", StringComparison.Ordinal) ? command.Name[6..] : command.Name);
        var bareSet = commands.ToHashSet();
        var fullNameOnly = PiFuzzy.Filter<(string Name, string? Description)>(catalogue.Where(command =>
                command.Name.StartsWith("skill:", StringComparison.Ordinal) && !bareSet.Contains(command)).ToList(),
            prefix, command => command.Name);
        var matches = commands.Concat(fullNameOnly).ToList();
        if (matches.Count == 0)
        {
            var extras = PiFuzzy.Filter<(string Name, string? Description)>(BuildExtraCatalogue(), prefix, command =>
                command.Name.StartsWith("skill:", StringComparison.Ordinal) ? command.Name[6..] : command.Name);
            matches.AddRange(extras);
        }
        return matches.Select(command => new EditorCompletionItem(command.Name, command.Name, command.Description,
                IsDirectory: false, IsSlash: true, IsArgument: false, IsAt: false))
            .ToArray();
    }

    private List<(string Name, string? Description)> BuildPiCatalogue()
    {
        var merged = InteractiveCommandCatalog.Merge(
            (_dynamicCommands?.Invoke() ?? []).Select(command =>
                new InteractiveCommandMetadata(command, _describeCommand?.Invoke(command) ?? "")));
        var byName = merged.ToDictionary(command => command.Command, StringComparer.OrdinalIgnoreCase);
        var result = new List<(string Name, string? Description)>();
        foreach (var name in s_piCommandOrder)
        {
            if (byName.TryGetValue(name, out var metadata))
                result.Add((name[1..], metadata.Description));
        }
        var builtinNames = InteractiveCommandCatalog.BuiltIns
            .Select(command => command.Command).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var command in merged)
        {
            var name = command.Command.TrimStart('/');
            if (builtinNames.Contains(command.Command) || s_piCommands.Contains(command.Command) ||
                result.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add((name, command.Description));
        }
        return result;
    }

    private static List<(string Name, string? Description)> BuildExtraCatalogue() =>
        InteractiveCommandCatalog.BuiltIns
            .Where(command => !s_piCommands.Contains(command.Command))
            .Select(command => (Name: command.Command.TrimStart('/'), Description: (string?)command.Description))
            .ToList();

    private IReadOnlyList<EditorCompletionItem> GetArgumentSuggestions(string commandName, string prefix)
    {
        if (!string.Equals(commandName, "model", StringComparison.Ordinal) || _getModels is null) return [];
        var models = _getModels();
        var filtered = PiFuzzy.Filter(models, prefix, item =>
            $"{item.Id} {item.Provider} {item.Provider}/{item.Id} {item.Provider} {item.Id}" +
            (string.IsNullOrEmpty(item.Name) ? "" : $" {item.Name}"));
        return filtered.Select(model => new EditorCompletionItem($"{model.Provider}/{model.Id}", model.Id,
            model.Provider, IsDirectory: false, IsSlash: false, IsArgument: true, IsAt: false)).ToArray();
    }

    private string? ExtractAtPrefix(string before)
    {
        var quoted = ExtractQuotedPrefix(before);
        if (quoted?.StartsWith("@\"", StringComparison.Ordinal) == true) return quoted;
        var delimiter = FindLastDelimiter(before);
        var token = StripLeadingWrappers(delimiter < 0 ? before : before[(delimiter + 1)..]);
        return token.StartsWith('@') ? token : null;
    }

    private string? ExtractPathPrefix(string before, bool force)
    {
        var quoted = ExtractQuotedPrefix(before);
        if (quoted is not null) return quoted;
        var delimiter = FindLastDelimiter(before);
        var token = StripLeadingWrappers(delimiter < 0 ? before : before[(delimiter + 1)..]);
        if (force) return token;
        if (token.Contains('/') || token.StartsWith('.') || token.StartsWith("~/", StringComparison.Ordinal))
            return token;
        if (token.Length == 0 && before.Length > 0 && IsTokenBoundary(before)) return token;
        return null;
    }

    private static string? ExtractQuotedPrefix(string before)
    {
        var quoteStart = FindUnclosedQuoteStart(before);
        if (quoteStart is null) return null;
        if (quoteStart > 0 && before[quoteStart.Value - 1] == '@')
        {
            if (!IsTokenStart(before, quoteStart.Value - 1)) return null;
            return before[(quoteStart.Value - 1)..];
        }
        if (!IsTokenStart(before, quoteStart.Value)) return null;
        return before[quoteStart.Value..];
    }

    private static int? FindUnclosedQuoteStart(string text)
    {
        var inQuotes = false;
        int? start = null;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '"') continue;
            inQuotes = !inQuotes;
            if (inQuotes) start = index;
        }
        return inQuotes ? start : null;
    }

    private static bool IsTokenStart(string text, int index)
    {
        var start = index;
        while (start > 0 && s_wrappers.ContainsKey(text[start - 1])) start--;
        return start == 0 || s_pathDelimiters.Contains(text[start - 1].ToString()) || IsCjkSeparator(text[start - 1]);
    }

    private static bool IsTokenBoundary(string text) => text.Length > 0 &&
        (s_pathDelimiters.Contains(text[^1].ToString()) || IsCjkSeparator(text[^1]) || char.IsWhiteSpace(text[^1]));

    private static bool IsCjkSeparator(char value) => Array.IndexOf(s_cjkPunctuation, value) >= 0;

    private static int FindLastDelimiter(string text)
    {
        var last = -1;
        for (var index = 0; index < text.Length; index++)
        {
            if (s_pathDelimiters.Contains(text[index].ToString()) || char.IsWhiteSpace(text[index]) ||
                IsCjkSeparator(text[index])) last = index;
        }
        return last;
    }

    private static string StripLeadingWrappers(string token)
    {
        var result = token;
        while (result.Length > 0)
        {
            if (!s_wrappers.TryGetValue(result[0], out var closing) ||
                result.Contains(closing, StringComparison.Ordinal)) break;
            result = result[1..];
        }
        return result;
    }

    private readonly record struct PathPrefix(string RawPrefix, bool IsAtPrefix, bool IsQuotedPrefix);

    private static PathPrefix ParsePathPrefix(string prefix)
    {
        if (prefix.StartsWith("@\"", StringComparison.Ordinal)) return new(prefix[2..], true, true);
        if (prefix.StartsWith('"')) return new(prefix[1..], false, true);
        if (prefix.StartsWith('@')) return new(prefix[1..], true, false);
        return new(prefix, false, false);
    }

    private static string BuildCompletionValue(string path, bool isDirectory, bool isAtPrefix, bool isQuotedPrefix)
    {
        var needsQuotes = isQuotedPrefix || path.Any(character => char.IsWhiteSpace(character) || IsCjkSeparator(character));
        var marker = isAtPrefix ? "@" : "";
        return needsQuotes ? $"{marker}\"{path}\"" : marker + path;
    }

    private IReadOnlyList<EditorCompletionItem> GetFuzzyFileSuggestions(string rawQuery, bool isQuotedPrefix)
    {
        var fdPath = ResolveFd();
        if (fdPath is null) return [];
        var basePath = Path.GetFullPath(_workingDirectory());
        var scoped = ResolveScopedFuzzyQuery(rawQuery, basePath);
        var baseDir = scoped?.BaseDir ?? basePath;
        var fdQuery = scoped?.Query ?? rawQuery;
        var baseEntries = WalkWithFd(fdPath, baseDir, fdQuery, maxDepth: 1);
        var recursiveEntries = WalkWithFd(fdPath, baseDir, fdQuery, maxDepth: null);
        var seen = new HashSet<string>(baseEntries.Select(entry => entry.Path), StringComparer.Ordinal);
        var entries = baseEntries.Concat(recursiveEntries.Where(entry => seen.Add(entry.Path))).ToArray();
        var scored = entries.Select(entry => (entry, ScoreEntry(entry.Path, fdQuery, entry.IsDirectory)))
            .Where(item => item.Item2 > 0)
            .OrderByDescending(item => item.Item2)
            .ThenBy(item => item.entry.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length)
            .ThenBy(item => item.entry.Path.Length)
            .ThenBy(item => item.entry.Path, StringComparer.CurrentCulture)
            .Take(20);
        var items = new List<EditorCompletionItem>();
        foreach (var (entry, _) in scored)
        {
            var pathWithoutSlash = entry.IsDirectory ? entry.Path[..^1] : entry.Path;
            var displayPath = scoped is { } value ? value.DisplayBase + pathWithoutSlash : pathWithoutSlash;
            var name = Path.GetFileName(pathWithoutSlash);
            var completionPath = entry.IsDirectory ? displayPath + "/" : displayPath;
            var completionValue = BuildCompletionValue(completionPath, entry.IsDirectory, true, isQuotedPrefix);
            items.Add(new(completionValue, name + (entry.IsDirectory ? "/" : ""), displayPath,
                entry.IsDirectory, IsSlash: false, IsArgument: false, IsAt: true));
        }
        return items;
    }

    private (string BaseDir, string Query, string DisplayBase)? ResolveScopedFuzzyQuery(string raw, string basePath)
    {
        var normalized = raw.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        if (slash < 0) return null;
        var displayBase = normalized[..(slash + 1)];
        var query = normalized[(slash + 1)..];
        string baseDir;
        if (displayBase.StartsWith("~/", StringComparison.Ordinal))
            baseDir = Path.GetFullPath(displayBase[2..], Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        else if (displayBase.StartsWith('/')) baseDir = displayBase;
        else baseDir = Path.GetFullPath(displayBase, basePath);
        return Directory.Exists(baseDir) ? (baseDir, query, displayBase) : null;
    }

    private IReadOnlyList<(string Path, bool IsDirectory)> WalkWithFd(string fdPath, string baseDir,
        string query, int? maxDepth)
    {
        try
        {
            var start = new ProcessStartInfo(fdPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8
            };
            foreach (var argument in new[]
                     {
                         "--base-directory", baseDir, "--max-results", "100", "--type", "f", "--type", "d",
                         "--follow", "--hidden", "--exclude", ".git", "--exclude", ".git/*", "--exclude", ".git/**"
                     }) start.ArgumentList.Add(argument);
            if (maxDepth is { } depth) { start.ArgumentList.Add("--max-depth"); start.ArgumentList.Add(depth.ToString()); }
            if (query.Replace('\\', '/').Contains('/')) start.ArgumentList.Add("--full-path");
            if (!string.IsNullOrEmpty(query)) start.ArgumentList.Add(BuildFdPathQuery(query));
            using var process = Process.Start(start);
            if (process is null) return [];
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return [];
            }
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return [];
            var results = new List<(string, bool)>();
            foreach (var rawLine in output.Trim().Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Replace('\\', '/');
                if (line.Length == 0) continue;
                var directory = line.EndsWith('/');
                var normalized = directory ? line[..^1] : line;
                if (normalized == ".git" || normalized.StartsWith(".git/", StringComparison.Ordinal) ||
                    normalized.Contains("/.git/", StringComparison.Ordinal)) continue;
                results.Add((line, directory));
            }
            return results;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return [];
        }
    }

    private static string BuildFdPathQuery(string query)
    {
        var normalized = query.Replace('\\', '/');
        if (!normalized.Contains('/')) return normalized;
        var trailing = normalized.EndsWith('/');
        var trimmed = normalized.Trim('/');
        if (trimmed.Length == 0) return normalized;
        const string separator = "[\\\\/]";
        var pattern = string.Join(separator, trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => System.Text.RegularExpressions.Regex.Escape(segment)));
        return trailing ? pattern + separator : pattern;
    }

    private static int ScoreEntry(string filePath, string query, bool isDirectory)
    {
        if (string.IsNullOrEmpty(query)) return 1;
        var name = Path.GetFileName(filePath.TrimEnd('/')).ToLowerInvariant();
        var lower = query.ToLowerInvariant();
        var score = name == lower ? 100 : name.StartsWith(lower, StringComparison.Ordinal) ? 80 :
            name.Contains(lower, StringComparison.Ordinal) ? 50 :
            filePath.ToLowerInvariant().Contains(lower, StringComparison.Ordinal) ? 30 : 0;
        if (isDirectory && score > 0) score += 10;
        return score;
    }

    private string? ResolveFd()
    {
        if (_fdResolved) return _fdPath;
        _fdResolved = true;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "fd", "fdfind" })
            {
                var candidate = Path.Combine(directory, name);
                if (!File.Exists(candidate)) continue;
                if (!WorksAsFd(candidate)) continue;
                _fdPath = candidate;
                return _fdPath;
            }
        }
        return null;
    }

    private static bool WorksAsFd(string candidate)
    {
        try
        {
            var start = new ProcessStartInfo(candidate, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(start);
            if (process is null) return false;
            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            return process.WaitForExit(1500) && process.ExitCode == 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private IReadOnlyList<EditorCompletionItem> GetFileSuggestions(string prefix)
    {
        var parsed = ParsePathPrefix(prefix);
        var expanded = parsed.RawPrefix;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (expanded.StartsWith('~')) expanded = expanded == "~" ? home : Path.GetFullPath(expanded[2..], home);
        var basePath = Path.GetFullPath(_workingDirectory());
        var isRoot = parsed.RawPrefix is "" or "./" or "../" or "~" or "~/" or "/";
        string searchDir;
        string searchPrefix;
        if (isRoot || parsed.RawPrefix.EndsWith('/'))
        {
            searchDir = parsed.RawPrefix.StartsWith('~') || expanded.StartsWith('/')
                ? expanded : Path.GetFullPath(expanded, basePath);
            searchPrefix = "";
        }
        else
        {
            var directory = Path.GetDirectoryName(expanded) ?? "";
            searchPrefix = Path.GetFileName(expanded);
            searchDir = parsed.RawPrefix.StartsWith('~') || expanded.StartsWith('/')
                ? directory : Path.GetFullPath(directory, basePath);
        }
        if (!Directory.Exists(searchDir)) return [];
        var suggestions = new List<EditorCompletionItem>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(searchDir))
        {
            var name = Path.GetFileName(entry);
            if (!name.StartsWith(searchPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var isDirectory = Directory.Exists(entry);
            string relativePath;
            var displayPrefix = parsed.RawPrefix;
            if (displayPrefix.EndsWith('/')) relativePath = displayPrefix + name;
            else if (displayPrefix.Contains('/') || displayPrefix.Contains('\\'))
            {
                if (displayPrefix.StartsWith("~/", StringComparison.Ordinal))
                {
                    var homeRelative = displayPrefix[2..];
                    var directory = Path.GetDirectoryName(homeRelative) ?? "";
                    relativePath = $"~/{(directory.Length == 0 || directory == "." ? name : Path.Combine(directory, name))}";
                }
                else if (displayPrefix.StartsWith('/'))
                {
                    var directory = Path.GetDirectoryName(displayPrefix) ?? "/";
                    relativePath = directory == "/" ? "/" + name : Path.Combine(directory, name);
                }
                else
                {
                    relativePath = Path.Combine(Path.GetDirectoryName(displayPrefix) ?? "", name);
                    if (displayPrefix.StartsWith("./", StringComparison.Ordinal) &&
                        !relativePath.StartsWith("./", StringComparison.Ordinal)) relativePath = "./" + relativePath;
                }
            }
            else if (displayPrefix.StartsWith('~')) relativePath = "~/" + name;
            else relativePath = name;
            relativePath = relativePath.Replace('\\', '/');
            var completionPath = isDirectory ? relativePath + "/" : relativePath;
            var value = BuildCompletionValue(completionPath, isDirectory, parsed.IsAtPrefix, parsed.IsQuotedPrefix);
            suggestions.Add(new(value, name + (isDirectory ? "/" : ""), null, isDirectory,
                IsSlash: false, IsArgument: false, IsAt: parsed.IsAtPrefix));
        }
        return suggestions.OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Label, StringComparer.CurrentCulture)
            .ToArray();
    }
}
