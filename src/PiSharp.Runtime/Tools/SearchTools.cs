using System.ComponentModel;

namespace PiSharp.Runtime.Tools;

/// <summary>Opt-in discovery and ripgrep search with shared result and process ownership.</summary>
public sealed class SearchTools(string workingDirectory)
{
    private readonly string _cwd = Path.GetFullPath(workingDirectory);

    [Description("Find files and directories matching a glob pattern. Paths are relative to the search root. Respects Git ignore rules inside a repository.")]
    public async Task<string> Find(
        [Description("Glob pattern, e.g. '*.cs' or 'src/**/*.cs'.")] string pattern,
        [Description("Search directory (defaults to current working directory). ")] string? path = null,
        [Description("Maximum results (default 1000). ")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        (await FindForTool(pattern, path, limit, cancellationToken)).Text;

    [Description("Find files and directories matching a glob pattern. Paths are relative to the search root. Respects Git ignore rules inside a repository.")]
    internal Task<SearchToolOutput> FindForTool(
        [Description("Glob pattern, e.g. '*.cs' or 'src/**/*.cs'.")] string pattern,
        [Description("Search directory (defaults to current working directory).")] string? path = null,
        [Description("Maximum results (default 1000).")] double? limit = null,
        CancellationToken cancellationToken = default) => FdSearch.ExecuteAsync(_cwd, pattern, path, limit, cancellationToken);

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
        (await RipgrepSearch.ExecuteAsync(_cwd, pattern, path, glob, ignoreCase, literal, context, limit,
            cancellationToken, respectGitIgnoreOutsideRepository: true)).Text;

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

}
