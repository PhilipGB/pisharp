using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Core;

public sealed class CodingTools
{
    private const int MaxReadCharacters = 200_000;
    private const int DefaultListLimit = 500;
    private const int DefaultFindLimit = 1_000;
    private const int DefaultGrepLimit = 100;
    private const int MaxSearchOutputBytes = 50 * 1024;
    private const int MaximumSearchLimit = 10_000;
    private const int MaximumGrepContext = 100;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private readonly WorkspacePathPolicy _paths;
    private readonly EditEngine _editEngine = new();
    private readonly HashSet<string> _readOnlyRoots;

    public CodingTools(string workspaceRoot)
    {
        _paths = new WorkspacePathPolicy(workspaceRoot);
        _readOnlyRoots = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Allows the read tool to inspect a trusted read-only resource directory.</summary>
    public void AddReadOnlyRoot(string path)
    {
        _readOnlyRoots.Add(Path.GetFullPath(path));
    }

    public string WorkspaceRoot => _paths.Root;

    [Description("Read a UTF-8 text file from the workspace. Returns line-numbered text. Use offset and limit for large files.")]
    public async Task<string> ReadAsync(
        [Description("Workspace-relative file path.")] string path,
        [Description("1-based first line to return.")] int offset = 1,
        [Description("Maximum number of lines to return.")] int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (offset < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "offset must be at least 1.");
        }
        if (limit is < 1 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be between 1 and 5000.");
        }

        var absolutePath = _paths.ResolveRead(path, _readOnlyRoots);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"File not found: {path}", absolutePath);
        }

        var lines = await File.ReadAllLinesAsync(absolutePath, cancellationToken);
        var start = Math.Min(offset - 1, lines.Length);
        var end = Math.Min(start + limit, lines.Length);
        var builder = new StringBuilder();

        for (var index = start; index < end; index++)
        {
            builder.Append(index + 1).Append("\t").AppendLine(lines[index]);
            if (builder.Length >= MaxReadCharacters)
            {
                builder.AppendLine("[output truncated]");
                break;
            }
        }

        if (end < lines.Length)
        {
            builder.Append($"[showing lines {start + 1}-{end} of {lines.Length}]");
        }

        return builder.ToString();
    }

    [Description("Write an entire UTF-8 text file in the workspace. Creates parent directories when needed and replaces existing content.")]
    public async Task<string> WriteAsync(
        [Description("Workspace-relative file path.")] string path,
        [Description("Complete new file contents.")] string content,
        CancellationToken cancellationToken = default)
    {
        var absolutePath = _paths.Resolve(path);
        var parent = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await File.WriteAllTextAsync(absolutePath, content, new UTF8Encoding(false), cancellationToken);
        return $"Wrote {content.Length} characters to {path}.";
    }

    [Description("Edit one file using exact text replacements. Each oldText must be non-empty, unique in the original file, and edits must not overlap.")]
    public async Task<string> EditAsync(
        [Description("Workspace-relative file path.")] string path,
        [Description("Exact replacements to apply against the original file contents.")] IReadOnlyList<EditOperation> edits,
        CancellationToken cancellationToken = default)
    {
        await EditWithDetailsAsync(path, edits, cancellationToken);
        return $"Successfully replaced {edits.Count} block(s) in {path}.";
    }

    /// <summary>Applies edits and returns the diff and unified patch used by terminal renderers.</summary>
    public async Task<EditResult> EditWithDetailsAsync(
        string path,
        IReadOnlyList<EditOperation> edits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
        {
            throw new ArgumentException("At least one edit is required.", nameof(edits));
        }

        var absolutePath = _paths.Resolve(path);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"File not found: {path}", absolutePath);
        }

        var content = await File.ReadAllTextAsync(absolutePath, cancellationToken);
        var result = _editEngine.Apply(path, content, edits);
        await File.WriteAllTextAsync(absolutePath, result.UpdatedContent, new UTF8Encoding(false), cancellationToken);
        return result;
    }

    /// <summary>Applies an edit and returns only the metadata needed by an AI tool renderer.</summary>
    public async Task<EditToolResult> EditForAgentAsync(
        string path,
        IReadOnlyList<EditOperation> edits,
        CancellationToken cancellationToken = default)
    {
        var result = await EditWithDetailsAsync(path, edits, cancellationToken);
        return new EditToolResult(
            $"Successfully replaced {edits.Count} block(s) in {path}.",
            result.Diff,
            result.Patch,
            result.FirstChangedLine,
            result.UsedFuzzyMatch);
    }

    /// <summary>Lists workspace directory entries in deterministic order.</summary>
    [Description("List directory contents alphabetically, including dotfiles. Directories have a trailing slash.")]
    public async Task<string> LsAsync(
        [Description("Workspace-relative directory path.")] string path = ".",
        [Description("Maximum number of entries to return.")] int limit = DefaultListLimit,
        CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit, nameof(limit));
        var directory = _paths.Resolve(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Directory not found: {path}");
        }

        var entries = Directory.EnumerateFileSystemEntries(directory)
            .Select(FormatDirectoryEntry)
            .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)
            .Take(limit + 1)
            .ToArray();
        var truncated = entries.Length > limit;
        var output = string.Join('\n', entries.Take(limit));
        return await Task.FromResult(AddLimitNotice(output, truncated, limit, "entries"));
    }

    /// <summary>Finds workspace files matching a glob pattern.</summary>
    [Description("Find workspace files by a glob pattern such as '*.cs' or '**/*.json'.")]
    public async Task<string> FindAsync(
        [Description("Glob pattern to match files.")] string pattern,
        [Description("Workspace-relative directory to search.")] string path = ".",
        [Description("Maximum number of results to return.")] int limit = DefaultFindLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ValidateLimit(limit, nameof(limit));
        var directory = _paths.Resolve(path);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Directory not found: {path}");
        }

        var matcher = CreateGlobMatcher(pattern);
        var results = EnumerateFiles(directory, cancellationToken)
            .Where(file => matcher.IsMatch(GetSearchRelativePath(directory, file)))
            .Select(file => GetSearchRelativePath(directory, file))
            .Take(limit + 1)
            .ToArray();
        var truncated = results.Length > limit;
        var output = string.Join('\n', results.Take(limit));
        return await Task.FromResult(results.Length == 0
            ? "No files found matching pattern"
            : AddLimitNotice(output, truncated, limit, "results"));
    }

    /// <summary>Searches workspace file contents with regex, literal, glob, and context support.</summary>
    [Description("Search workspace file contents using a regular expression or literal pattern.")]
    public async Task<string> GrepAsync(
        [Description("Regex or literal search pattern.")] string pattern,
        [Description("Workspace-relative file or directory to search.")] string path = ".",
        [Description("Optional glob filter such as '*.cs'.")] string? glob = null,
        [Description("Use case-insensitive matching.")] bool ignoreCase = false,
        [Description("Treat the pattern as a literal string.")] bool literal = false,
        [Description("Number of context lines before and after each match.")] int context = 0,
        [Description("Maximum number of matching lines.")] int limit = DefaultGrepLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ValidateLimit(limit, nameof(limit));
        if (context is < 0 or > MaximumGrepContext)
        {
            throw new ArgumentOutOfRangeException(nameof(context), $"context must be between 0 and {MaximumGrepContext}.");
        }

        var searchPath = _paths.Resolve(path);
        if (!File.Exists(searchPath) && !Directory.Exists(searchPath))
        {
            throw new FileNotFoundException($"Search path not found: {path}", searchPath);
        }
        var matcher = CreateContentMatcher(pattern, literal, ignoreCase);
        var files = File.Exists(searchPath)
            ? new[] { searchPath }
            : EnumerateFiles(searchPath, cancellationToken);
        return await SearchFilesAsync(files, searchPath, matcher, glob, context, limit, cancellationToken);
    }

    [Description("Run a shell command in the workspace and return stdout, stderr, and the exit code. Use for builds, tests, git, search, and other repository operations.")]
    public async Task<string> BashAsync(
        [Description("Shell command to execute.")] string command,
        [Description("Command timeout in seconds, from 1 to 600.")] int timeoutSeconds = 120,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (timeoutSeconds is < 1 or > 600)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "timeoutSeconds must be between 1 and 600.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            WorkingDirectory = WorkspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(command);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Command exceeded {timeoutSeconds} seconds.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return FormatCommandResult(process.ExitCode, stdout, stderr);
    }

    private static void ValidateLimit(int limit, string parameterName)
    {
        if (limit is < 1 or > MaximumSearchLimit)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"limit must be between 1 and {MaximumSearchLimit}.");
        }
    }

    private static string FormatDirectoryEntry(string path) =>
        Path.GetFileName(path) + (Directory.Exists(path) ? "/" : string.Empty);

    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var entry in entries.OrderBy(value => value, StringComparer.Ordinal))
            {
                if (Directory.Exists(entry))
                {
                    var name = Path.GetFileName(entry);
                    if (name is ".git" or "node_modules" || IsReparsePoint(entry))
                    {
                        continue;
                    }
                    pending.Push(entry);
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string GetSearchRelativePath(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static Regex CreateGlobMatcher(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var builder = new StringBuilder("^");
        if (!normalized.Contains('/', StringComparison.Ordinal))
        {
            builder.Append("(?:.*/)?");
        }
        for (var index = 0; index < normalized.Length; index++)
        {
            if (normalized[index] == '*' && index + 1 < normalized.Length && normalized[index + 1] == '*')
            {
                if (index + 2 < normalized.Length && normalized[index + 2] == '/')
                {
                    builder.Append("(?:.*/)?");
                    index += 2;
                }
                else
                {
                    builder.Append(".*");
                    index++;
                }
            }
            else if (normalized[index] == '*')
            {
                builder.Append("[^/]*");
            }
            else if (normalized[index] == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(normalized[index].ToString()));
            }
        }
        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    }

    private static Func<string, bool> CreateContentMatcher(string pattern, bool literal, bool ignoreCase)
    {
        if (literal)
        {
            var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return line => line.Contains(pattern, comparison);
        }

        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        var regex = new Regex(pattern, options, RegexTimeout);
        return regex.IsMatch;
    }

    private static async Task<string> SearchFilesAsync(
        IEnumerable<string> files,
        string searchPath,
        Func<string, bool> matcher,
        string? glob,
        int context,
        int limit,
        CancellationToken cancellationToken)
    {
        var root = File.Exists(searchPath) ? Path.GetDirectoryName(searchPath)! : searchPath;
        var globMatcher = string.IsNullOrWhiteSpace(glob) ? null : CreateGlobMatcher(glob);
        var output = new List<string>();
        var matches = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = GetSearchRelativePath(root, file);
            if (globMatcher is not null && !globMatcher.IsMatch(relative))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, cancellationToken);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            var emitted = new HashSet<int>();
            for (var index = 0; index < lines.Length; index++)
            {
                if (!matcher(lines[index]))
                {
                    continue;
                }
                matches++;
                var start = Math.Max(0, index - context);
                var end = Math.Min(lines.Length - 1, index + context);
                for (var current = start; current <= end; current++)
                {
                    if (!emitted.Add(current))
                    {
                        continue;
                    }
                    var separator = current == index ? ":" : "-";
                    output.Add($"{relative}{separator}{current + 1}{separator} {lines[current]}");
                }
                if (matches >= limit)
                {
                    break;
                }
            }
            if (matches >= limit || Encoding.UTF8.GetByteCount(string.Join('\n', output)) > MaxSearchOutputBytes)
            {
                break;
            }
        }

        var content = string.Join('\n', output);
        var truncation = OutputTruncator.Head(content, maxBytes: MaxSearchOutputBytes);
        return matches >= limit
            ? AddLimitNotice(truncation.Content, true, limit, "matches")
            : truncation.Truncated
                ? $"{truncation.Content}\n\n[{MaxSearchOutputBytes / 1024}KB output limit reached]"
                : content;
    }

    private static string AddLimitNotice(string output, bool truncated, int limit, string noun) =>
        truncated
            ? $"{output}\n\n[{limit} {noun} limit reached]"
            : output;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort during cancellation/timeout.
        }
    }

    private static string FormatCommandResult(int exitCode, string stdout, string stderr)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"exit_code: {exitCode}");
        builder.AppendLine("stdout:");
        AppendTruncated(builder, stdout);
        builder.AppendLine();
        builder.AppendLine("stderr:");
        AppendTruncated(builder, stderr);
        return builder.ToString();
    }

    private static void AppendTruncated(StringBuilder builder, string value)
    {
        var result = OutputTruncator.Tail(value);
        builder.Append(result.Content);
        if (!result.Truncated)
        {
            return;
        }

        builder.AppendLine();
        builder.Append($"[output truncated: showing {result.OutputLines} of {result.TotalLines} lines]");
    }
}
