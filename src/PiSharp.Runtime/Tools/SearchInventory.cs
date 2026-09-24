using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PiSharp.Runtime.Tools;

/// <summary>Bounded file discovery; Git handles tracked/untracked ignore rules when available.</summary>
internal static class SearchInventory
{
    private const int MaxEntries = 20_000;

    private sealed record IgnoreRule(string BaseDirectory, Regex Pattern, bool Negated, bool DirectoryOnly);

    public static async Task<IReadOnlyList<string>> EnumerateAsync(string root, CancellationToken cancellationToken,
        bool includeDirectories = false)
    {
        if (File.Exists(root)) return [root];
        if (!Directory.Exists(root)) throw new ToolFailureException($"Path not found: {root}");
        try
        {
            using var git = new Process
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList = { "ls-files", "--cached", "--others", "--exclude-standard" }
                }
            };
            git.Start();
            try
            {
                var error = git.StandardError.ReadToEndAsync(cancellationToken);
                var results = new List<string>();
                var reachedLimit = false;
                while (await git.StandardOutput.ReadLineAsync(cancellationToken) is { } relative)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (results.Count >= MaxEntries) { reachedLimit = true; break; }
                    var file = Path.GetFullPath(relative, root);
                    if (File.Exists(file) && !File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) results.Add(file);
                }
                if (reachedLimit) git.Kill(entireProcessTree: true);
                await git.WaitForExitAsync(cancellationToken);
                await error;
                if (reachedLimit) throw new ToolFailureException("Search exceeds 20000 files; narrow the search path.");
                if (git.ExitCode == 0)
                {
                    if (includeDirectories)
                        results.AddRange(await EnumerateDirectoriesAsync(root, MaxEntries - results.Count, cancellationToken));
                    return results;
                }
            }
            finally
            {
                if (!git.HasExited)
                {
                    git.Kill(entireProcessTree: true);
                    await git.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
        catch (System.ComponentModel.Win32Exception) { /* Git is optional outside repositories. */ }
        // Outside Git, walk without traversing symlinked directories or common dependency trees.
        var files = new List<string>();
        var pending = new Stack<(string Directory, IReadOnlyList<IgnoreRule> Rules)>();
        pending.Push((root, []));
        while (pending.TryPop(out var item) && files.Count < MaxEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = item.Directory;
            var rules = new List<IgnoreRule>(item.Rules);
            rules.AddRange(ReadIgnoreRules(directory));
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (files.Count >= MaxEntries) break;
                try
                {
                    var isDirectory = Directory.Exists(entry);
                    if (IsIgnored(entry, isDirectory, rules)) continue;
                    if (isDirectory)
                    {
                        if (Path.GetFileName(entry) is ".git" or "node_modules" ||
                            File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)) continue;
                        if (includeDirectories) files.Add(entry);
                        pending.Push((entry, rules));
                    }
                    else if (File.Exists(entry)) files.Add(entry);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        if (files.Count >= MaxEntries) throw new ToolFailureException("Search exceeds 20000 files; narrow the search path.");
        return files;
    }

    private static async Task<IReadOnlyList<string>> EnumerateDirectoriesAsync(string root, int maxEntries,
        CancellationToken cancellationToken)
    {
        var directories = new List<string>();
        var pending = new Stack<(string Directory, IReadOnlyList<IgnoreRule> Rules)>();
        pending.Push((root, []));
        while (pending.TryPop(out var item) && directories.Count < maxEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rules = new List<IgnoreRule>(item.Rules);
            rules.AddRange(ReadIgnoreRules(item.Directory));
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateDirectories(item.Directory).ToArray(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (IsIgnored(entry, isDirectory: true, rules) ||
                        Path.GetFileName(entry) is ".git" or "node_modules" ||
                        File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (directories.Count >= maxEntries)
                        throw new ToolFailureException("Search exceeds 20000 files; narrow the search path.");
                    directories.Add(entry);
                    pending.Push((entry, rules));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        if (maxEntries > 0 && directories.Count >= maxEntries)
            throw new ToolFailureException("Search exceeds 20000 files; narrow the search path.");
        return directories;
    }

    private static IReadOnlyList<IgnoreRule> ReadIgnoreRules(string directory)
    {
        string[] lines;
        try { lines = File.ReadAllLines(Path.Combine(directory, ".gitignore")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }

        var rules = new List<IgnoreRule>();
        foreach (var rawLine in lines)
        {
            var line = rawLine;
            if (line.Length == 0 || line[0] == '#') continue;

            var negated = line[0] == '!';
            if (negated) line = line[1..];
            if (line.Length == 0) continue;

            var directoryOnly = line.EndsWith('/');
            if (directoryOnly) line = line[..^1];
            var anchored = line.StartsWith("/", StringComparison.Ordinal);
            if (anchored) line = line[1..];
            if (line.Length == 0) continue;

            var hasSlash = line.Contains('/');
            var prefix = anchored || hasSlash ? "^" : "(?:^|/)";
            Regex pattern;
            try
            {
                pattern = new Regex(prefix + IgnoreGlobRegex(line) + "$", RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException) { continue; }
            rules.Add(new IgnoreRule(directory, pattern, negated, directoryOnly));
        }
        return rules;
    }

    private static bool IsIgnored(string path, bool isDirectory, IReadOnlyList<IgnoreRule> rules)
    {
        var ignored = false;
        foreach (var rule in rules)
        {
            var relativeToRule = Path.GetRelativePath(rule.BaseDirectory, path).Replace('\\', '/');
            if (relativeToRule == ".." || relativeToRule.StartsWith("../", StringComparison.Ordinal)) continue;
            var parts = relativeToRule.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var candidate = "";
            for (var index = 0; index < parts.Length; index++)
            {
                candidate = candidate.Length == 0 ? parts[index] : candidate + "/" + parts[index];
                var candidateIsDirectory = index < parts.Length - 1 || isDirectory;
                if (rule.DirectoryOnly && !candidateIsDirectory) continue;
                if (rule.Pattern.IsMatch(candidate)) ignored = !rule.Negated;
            }
        }
        return ignored;
    }

    private static string IgnoreGlobRegex(string pattern)
    {
        var result = new System.Text.StringBuilder();
        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (current == '\\' && index + 1 < pattern.Length)
            {
                result.Append(Regex.Escape(pattern[++index].ToString()));
            }
            else if (current == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                    {
                        index++;
                        result.Append("(?:.*/)?");
                    }
                    else result.Append(".*");
                }
                else result.Append("[^/]*");
            }
            else if (current == '?') result.Append("[^/]");
            else if (current == '[' && pattern.IndexOf(']', index + 1) is var close && close > index + 1)
            {
                var characterClass = pattern[(index + 1)..close];
                if (characterClass[0] == '!') characterClass = "^" + characterClass[1..];
                result.Append('[').Append(characterClass).Append(']');
                index = close;
            }
            else result.Append(Regex.Escape(current.ToString()));
        }
        return result.ToString();
    }
}
