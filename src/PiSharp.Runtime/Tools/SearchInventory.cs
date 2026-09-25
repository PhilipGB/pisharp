using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PiSharp.Runtime.Tools;

/// <summary>Bounded file discovery with managed ignore-rule handling.</summary>
internal static class SearchInventory
{
    private const int MaxEntries = 20_000;

    private sealed record IgnoreRule(string BaseDirectory, Regex Pattern, bool Negated, bool DirectoryOnly);

    public static async Task<IReadOnlyList<string>> EnumerateAsync(string root, CancellationToken cancellationToken,
        bool includeDirectories = false, bool includeFdIgnore = false, bool includeRgIgnore = false,
        string? ignoreBaseDirectory = null, string? fdGlobalIgnorePath = null)
    {
        root = Path.GetFullPath(root);
        if (File.Exists(root)) return [root];
        if (!Directory.Exists(root)) throw new ToolFailureException($"Path not found: {root}");

        var repositoryRoot = FindGitRoot(root);
        var hasRepository = HasGitMarker(repositoryRoot);
        var baseRules = await ReadBaseIgnoreRulesAsync(root, repositoryRoot, hasRepository, includeFdIgnore,
            Path.GetFullPath(ignoreBaseDirectory ?? root), fdGlobalIgnorePath, cancellationToken);
        var rulesByDirectory = new Dictionary<string, IReadOnlyList<IgnoreRule>>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        IReadOnlyList<IgnoreRule> RulesForDirectory(string directory)
        {
            directory = Path.GetFullPath(directory);
            if (rulesByDirectory.TryGetValue(directory, out var cached)) return cached;
            var rules = new List<IgnoreRule>();
            if (!PathsEqual(directory, repositoryRoot) && Path.GetDirectoryName(directory) is { } parent &&
                IsPathWithin(repositoryRoot, parent))
                rules.AddRange(RulesForDirectory(parent));
            else
                rules.AddRange(baseRules);
            rules.AddRange(ReadIgnoreRules(directory, includeFdIgnore, includeRgIgnore));
            rulesByDirectory[directory] = rules;
            return rules;
        }

        try
        {
            using var git = new Process
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList = { "ls-files", "--cached", "--others" }
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
                    var file = Path.GetFullPath(relative, root);
                    if (File.Exists(file) && !File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint) &&
                        !IsIgnored(file, isDirectory: false, RulesForDirectory(Path.GetDirectoryName(file)!)))
                    {
                        if (results.Count >= MaxEntries) { reachedLimit = true; break; }
                        results.Add(file);
                    }
                }
                if (reachedLimit) git.Kill(entireProcessTree: true);
                await git.WaitForExitAsync(cancellationToken);
                await error;
                if (reachedLimit) throw new ToolFailureException("Search exceeds 20000 files; narrow the search path.");
                if (git.ExitCode == 0)
                {
                    if (includeDirectories)
                    {
                        var parent = Path.GetDirectoryName(root);
                        IReadOnlyList<IgnoreRule> inheritedRules = parent is not null && IsPathWithin(repositoryRoot, parent)
                            ? RulesForDirectory(parent)
                            : baseRules;
                        results.AddRange(await EnumerateDirectoriesAsync(root, MaxEntries - results.Count,
                            includeFdIgnore, includeRgIgnore, inheritedRules, cancellationToken));
                    }
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
        pending.Push((root, baseRules));
        while (pending.TryPop(out var item) && files.Count < MaxEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = item.Directory;
            var rules = new List<IgnoreRule>(item.Rules);
            rules.AddRange(ReadIgnoreRules(directory, includeFdIgnore, includeRgIgnore));
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
        bool includeFdIgnore, bool includeRgIgnore, IReadOnlyList<IgnoreRule> inheritedRules,
        CancellationToken cancellationToken)
    {
        var directories = new List<string>();
        var pending = new Stack<(string Directory, IReadOnlyList<IgnoreRule> Rules)>();
        pending.Push((root, inheritedRules));
        while (pending.TryPop(out var item) && directories.Count < maxEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rules = new List<IgnoreRule>(item.Rules);
            rules.AddRange(ReadIgnoreRules(item.Directory, includeFdIgnore, includeRgIgnore));
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

    private static async Task<IReadOnlyList<IgnoreRule>> ReadBaseIgnoreRulesAsync(string workingDirectory,
        string repositoryRoot, bool hasRepository, bool includeFdIgnore, string ignoreBaseDirectory,
        string? fdGlobalIgnorePath, CancellationToken cancellationToken)
    {
        var rules = new List<IgnoreRule>();
        var baseDirectory = hasRepository ? repositoryRoot : workingDirectory;
        if (includeFdIgnore)
        {
            var globalFdIgnoreFile = fdGlobalIgnorePath ?? GetGlobalFdIgnoreFile();
            if (globalFdIgnoreFile is not null)
                rules.AddRange(ReadIgnoreFile(globalFdIgnoreFile, ignoreBaseDirectory));
        }
        var globalIgnoreFile = await GetGitGlobalIgnoreFileAsync(workingDirectory, cancellationToken);
        if (globalIgnoreFile is not null) rules.AddRange(ReadIgnoreFile(globalIgnoreFile, baseDirectory));
        if (hasRepository)
        {
            var infoExcludeFile = await GetGitInfoExcludeFileAsync(repositoryRoot, cancellationToken);
            if (infoExcludeFile is not null) rules.AddRange(ReadIgnoreFile(infoExcludeFile, repositoryRoot));
        }
        return rules;
    }

    private static string? GetGlobalFdIgnoreFile()
    {
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(configRoot))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return null;
            configRoot = Path.Combine(home, ".config");
        }
        return Path.Combine(Path.GetFullPath(configRoot), "fd", "ignore");
    }

    private static async Task<string?> GetGitGlobalIgnoreFileAsync(string workingDirectory,
        CancellationToken cancellationToken)
    {
        var configuredPath = await RunGitOutputAsync(workingDirectory, ["config", "--path", "--get", "core.excludesFile"],
            cancellationToken);
        if (!string.IsNullOrEmpty(configuredPath))
            return Path.GetFullPath(configuredPath, workingDirectory);

        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(configRoot))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return null;
            configRoot = Path.Combine(home, ".config");
        }
        return Path.Combine(Path.GetFullPath(configRoot), "git", "ignore");
    }

    private static async Task<string?> GetGitInfoExcludeFileAsync(string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var path = await RunGitOutputAsync(repositoryRoot, ["rev-parse", "--git-path", "info/exclude"],
            cancellationToken);
        return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path, repositoryRoot);
    }

    private static async Task<string?> RunGitOutputAsync(string workingDirectory, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            using var git = new Process
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            foreach (var argument in arguments) git.StartInfo.ArgumentList.Add(argument);
            if (!git.Start()) return null;
            try
            {
                var output = git.StandardOutput.ReadToEndAsync(cancellationToken);
                var error = git.StandardError.ReadToEndAsync(cancellationToken);
                await git.WaitForExitAsync(cancellationToken);
                await error;
                return git.ExitCode == 0 ? (await output).TrimEnd('\r', '\n') : null;
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
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private static IReadOnlyList<IgnoreRule> ReadIgnoreRules(string directory, bool includeFdIgnore, bool includeRgIgnore)
    {
        var rules = new List<IgnoreRule>();
        string[] ignoreFiles = includeFdIgnore
            ? [".gitignore", ".ignore", ".fdignore"]
            : includeRgIgnore ? [".gitignore", ".ignore", ".rgignore"] : [".gitignore", ".ignore"];
        foreach (var ignoreFile in ignoreFiles)
            rules.AddRange(ReadIgnoreFile(Path.Combine(directory, ignoreFile), directory));
        return rules;
    }

    private static IReadOnlyList<IgnoreRule> ReadIgnoreFile(string filePath, string baseDirectory)
    {
        string[] lines;
        try { lines = File.ReadAllLines(filePath); }
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
            rules.Add(new IgnoreRule(baseDirectory, pattern, negated, directoryOnly));
        }
        return rules;
    }

    private static bool IsIgnored(string path, bool isDirectory, IReadOnlyList<IgnoreRule> rules)
    {
        path = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(path);
        while (parent is not null)
        {
            if (IsPathIgnored(parent, isDirectory: true, rules)) return true;
            var next = Path.GetDirectoryName(parent);
            if (next is null || PathsEqual(next, parent)) break;
            parent = next;
        }
        return IsPathIgnored(path, isDirectory, rules);
    }

    private static bool IsPathIgnored(string path, bool isDirectory, IReadOnlyList<IgnoreRule> rules)
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

    private static string FindGitRoot(string directory)
    {
        var current = Path.GetFullPath(directory);
        while (true)
        {
            if (HasGitMarker(current)) return current;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || PathsEqual(parent, current)) return directory;
            current = parent;
        }
    }

    private static bool HasGitMarker(string directory)
    {
        var gitMarker = Path.Combine(directory, ".git");
        return Directory.Exists(gitMarker) || File.Exists(gitMarker);
    }

    private static bool IsPathWithin(string root, string path)
    {
        root = Path.GetFullPath(root);
        var current = Path.GetFullPath(path);
        while (true)
        {
            if (PathsEqual(root, current)) return true;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || PathsEqual(parent, current)) return false;
            current = parent;
        }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
            else if (current == '{' && TryGetBraceAlternatives(pattern, index, out var braceEnd, out var alternatives))
            {
                result.Append("(?:");
                for (var alternativeIndex = 0; alternativeIndex < alternatives.Count; alternativeIndex++)
                {
                    if (alternativeIndex > 0) result.Append('|');
                    result.Append(IgnoreGlobRegex(alternatives[alternativeIndex]));
                }
                result.Append(')');
                index = braceEnd;
            }
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

    private static bool TryGetBraceAlternatives(string pattern, int openIndex, out int braceEnd,
        out IReadOnlyList<string> alternatives)
    {
        braceEnd = -1;
        alternatives = [];
        var separators = new List<int>();
        var depth = 0;
        for (var index = openIndex + 1; index < pattern.Length; index++)
        {
            if (pattern[index] == '\\' && index + 1 < pattern.Length)
            {
                index++;
                continue;
            }
            if (pattern[index] == '{') depth++;
            else if (pattern[index] == '}')
            {
                if (depth == 0)
                {
                    braceEnd = index;
                    break;
                }
                depth--;
            }
            else if (pattern[index] == ',' && depth == 0) separators.Add(index);
        }

        if (braceEnd < 0 || separators.Count == 0) return false;
        var parts = new List<string>(separators.Count + 1);
        var start = openIndex + 1;
        foreach (var separator in separators)
        {
            parts.Add(pattern[start..separator]);
            start = separator + 1;
        }
        parts.Add(pattern[start..braceEnd]);
        alternatives = parts;
        return true;
    }
}
