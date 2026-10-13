using System.ComponentModel;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime;

/// <summary>Opt-in ls tool; alphabetical directory listing with Pi's basic entry/byte notices.</summary>
public sealed class DirectoryListingTool(string workingDirectory)
{
    private readonly string _cwd = Path.GetFullPath(workingDirectory);
    private const int MaxBytes = 50 * 1024;

    [Description("List directory contents, including dotfiles. Entries are sorted case-insensitively; directories end in '/'.")]
    public async Task<string> List(
        [Description("Directory to list (default current directory). ")] string? path = null,
        [Description("Maximum number of entries (default 500). ")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        (await ListForTool(path, limit, cancellationToken)).Text;

    [Description("List directory contents, including dotfiles. Entries are sorted case-insensitively; directories end in '/'.")]
    internal Task<SearchToolOutput> ListForTool(
        [Description("Directory to list (default current directory). ")] string? path = null,
        [Description("Maximum number of entries (default 500). ")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var absolute = Path.GetFullPath(path is null or "" ? "." : path, _cwd);
        if (!Directory.Exists(absolute))
            throw new ToolFailureException(File.Exists(absolute) ? $"Not a directory: {absolute}" : $"Path not found: {absolute}");
        try
        {
            var names = Directory.GetFileSystemEntries(absolute)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var count = Math.Max(0, limit ?? 500);
            var results = new List<string>();
            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (results.Count >= count) break;
                try
                {
                    var target = Path.Combine(absolute, name!);
                    results.Add(name + (Directory.Exists(target) ? "/" : ""));
                }
                catch (IOException) { /* Entry vanished while listing. */ }
            }
            if (results.Count == 0) return Task.FromResult(new SearchToolOutput("(empty directory)"));
            var (output, truncation) = ToolOutputTruncator.TruncateHead(string.Join('\n', results), MaxBytes);
            var notices = new List<string>();
            var entryLimitReached = names.Length > count;
            if (entryLimitReached) notices.Add($"{count} entries limit reached. Use limit={count * 2} for more");
            if (truncation is not null) notices.Add("50.0KB limit reached");
            var text = notices.Count > 0 ? output + "\n\n[" + string.Join(". ", notices) + "]" : output;
            object? details = entryLimitReached || truncation is not null
                ? new LsToolDetails(truncation, entryLimitReached ? count : null) : null;
            return Task.FromResult(new SearchToolOutput(text, details));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ToolFailureException($"Cannot read directory: {error.Message}", inner: error);
        }
    }
}
