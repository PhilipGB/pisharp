using System.ComponentModel;
using System.Text.RegularExpressions;

namespace PiSharp.Runtime.Tools;

/// <summary>Opt-in discovery and ripgrep search with shared result and process ownership.</summary>
public sealed class SearchTools(string workingDirectory)
{
    private readonly string _cwd = Path.GetFullPath(workingDirectory);
    private const int MaxBytes = 50 * 1024;

    [Description("Find files and directories matching a glob pattern. Paths are relative to the search root. Respects Git ignore rules inside a repository.")]
    public async Task<string> Find(
        [Description("Glob pattern, e.g. '*.cs' or 'src/**/*.cs'.")] string pattern,
        [Description("Search directory (defaults to current working directory). ")] string? path = null,
        [Description("Maximum results (default 1000). ")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        (await FindForTool(pattern, path, limit, cancellationToken)).Text;

    [Description("Find files and directories matching a glob pattern. Paths are relative to the search root. Respects Git ignore rules inside a repository.")]
    internal async Task<SearchToolOutput> FindForTool(
        [Description("Glob pattern, e.g. '*.cs' or 'src/**/*.cs'.")] string pattern,
        [Description("Search directory (defaults to current working directory).")] string? path = null,
        [Description("Maximum results (default 1000).")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path is null or "" ? "." : path, _cwd);
        if (File.Exists(root)) throw new ToolFailureException($"Not a directory: {root}");
        var max = Math.Max(1, limit ?? 1000);
        Regex regex;
        try { regex = GlobRegex(pattern); }
        catch (ArgumentException error) { throw new ToolFailureException($"Invalid glob pattern: {error.Message}", inner: error); }
        var files = await SearchInventory.EnumerateAsync(root, cancellationToken, includeDirectories: true,
            includeFdIgnore: true, ignoreBaseDirectory: _cwd);
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var isDirectory = Directory.Exists(file);
            if (regex.IsMatch(pattern.Contains('/') ? relative : Path.GetFileName(relative)))
                candidates.Add(isDirectory ? relative + "/" : relative);
            // fd includes matching directories. Synthesize ancestors of the discovered files.
            var dir = Path.GetDirectoryName(relative)?.Replace('\\', '/');
            while (!string.IsNullOrEmpty(dir) && dir != ".")
            {
                var name = Path.GetFileName(dir);
                if (regex.IsMatch(pattern.Contains('/') ? dir : name)) candidates.Add(dir + "/");
                dir = Path.GetDirectoryName(dir)?.Replace('\\', '/');
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = candidates.Order(StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0) return new SearchToolOutput("No files found matching pattern");
        var resultLimitReached = ordered.Length >= max;
        var rawOutput = string.Join('\n', ordered.Take(max));
        var (output, truncation) = ToolOutputTruncator.TruncateHead(rawOutput, MaxBytes);
        var notices = new List<string>();
        if (resultLimitReached) notices.Add($"{max} results limit reached. Use limit={max * 2} for more, or refine pattern");
        if (truncation is not null) notices.Add("50.0KB limit reached");
        var text = notices.Count > 0 ? output + "\n\n[" + string.Join(". ", notices) + "]" : output;
        return new SearchToolOutput(text, resultLimitReached || truncation is not null
            ? new FindToolDetails(truncation, resultLimitReached ? max : null) : null);
    }

    [Description("Search file contents with a regex or literal string. Returns matching lines with file paths and line numbers. Respects Git ignore rules in repositories.")]
    public async Task<string> Grep(
        [Description("Regex or literal pattern to search.")] string pattern,
        [Description("Search directory or file (default working directory). ")] string? path = null,
        [Description("Filter files by glob, e.g. '*.cs'.")] string? glob = null,
        [Description("Case-insensitive match (default false). ")] bool ignoreCase = false,
        [Description("Interpret pattern literally (default false). ")] bool literal = false,
        [Description("Number of surrounding lines (default 0). ")] int context = 0,
        [Description("Maximum matches (default 100). ")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        (await GrepForTool(pattern, path, glob, ignoreCase, literal, context, limit, cancellationToken)).Text;

    [Description("Search file contents with a regex or literal string. Returns matching lines with file paths and line numbers. Respects Git ignore rules in repositories.")]
    internal Task<SearchToolOutput> GrepForTool(
        [Description("Regex or literal pattern to search.")] string pattern,
        [Description("Search directory or file (default working directory).")] string? path = null,
        [Description("Filter files by glob, e.g. '*.cs'.")] string? glob = null,
        [Description("Case-insensitive match (default false).")] bool ignoreCase = false,
        [Description("Interpret pattern literally (default false).")] bool literal = false,
        [Description("Number of surrounding lines (default 0).")] double context = 0,
        [Description("Maximum matches (default 100).")] double? limit = null,
        CancellationToken cancellationToken = default) =>
        RipgrepSearch.ExecuteAsync(_cwd, pattern, path, glob, ignoreCase, literal, context, limit, cancellationToken);

    private static Regex GlobRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("Pattern cannot be empty.");
        return new Regex("^" + SearchInventory.GlobRegex(pattern) + "$", RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }
}
