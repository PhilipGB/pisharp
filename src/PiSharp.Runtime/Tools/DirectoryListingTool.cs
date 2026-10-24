using System.ComponentModel;
using System.Text.Json.Serialization;
using PiSharp.Core;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime;

/// <summary>Opt-in ls tool with source-compatible ordering, stat filtering and limits.</summary>
public sealed class DirectoryListingTool
{
    private readonly string _cwd;
    private readonly Action? _entriesRead;

    public DirectoryListingTool(string workingDirectory) : this(workingDirectory, null) { }

    internal DirectoryListingTool(string workingDirectory, Action? entriesRead)
    {
        _cwd = Path.GetFullPath(workingDirectory);
        _entriesRead = entriesRead;
    }

    [Description("List directory contents, including dotfiles. Entries are sorted case-insensitively; directories end in '/'.")]
    public async Task<string> List(
        [Description("Directory to list (default current directory). ")] string? path = null,
        [Description("Maximum number of entries (default 500). ")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        (await ListForTool(path, limit, cancellationToken)).Text;

    [Description("List directory contents, including dotfiles. Entries are sorted case-insensitively; directories end in '/'.")]
    internal async Task<SearchToolOutput> ListForTool(
        [Description("Directory to list (default current directory).")] string? path = null,
        [Description("Maximum number of entries (default 500).")] double? limit = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(() => ListCore(path, limit ?? 500, cancellationToken), cancellationToken)
                .WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Operation aborted", cancellationToken); }
    }

    private SearchToolOutput ListCore(string? path, double limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var absolute = CodingToolPaths.Resolve(path is null or "" ? "." : path, _cwd, allowNullBytes: true);
        if (!Exists(absolute)) throw new ToolFailureException($"Path not found: {absolute}");
        LinuxFileAccess.Require(absolute, 0, "stat");
        if (!Directory.Exists(absolute)) throw new ToolFailureException($"Not a directory: {absolute}");
        string[] names;
        try
        {
            LinuxFileAccess.Require(absolute, 4, "scandir");
            names = Directory.GetFileSystemEntries(absolute).Select(file => Path.GetFileName(file)!).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ToolFailureException)
        { throw new ToolFailureException($"Cannot read directory: {error.Message}", inner: error); }
        _entriesRead?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        var count = 0L;
        var entryLimitReached = false;
        var output = new SearchOutputBuilder();
        foreach (var name in DirectoryEntryOrdering.Sort(names))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count >= limit) { entryLimitReached = true; break; }
            var target = Path.Combine(absolute, name);
            if (!Exists(target)) continue;
            output.Append(name + (Directory.Exists(target) ? "/" : ""));
            count++;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (count == 0) return new("(empty directory)");
        var (content, truncation) = output.Finish();
        var notices = new List<string>();
        if (entryLimitReached) notices.Add($"{JavaScriptNumberText.Format(limit)} entries limit reached. Use limit={JavaScriptNumberText.Format(limit * 2)} for more");
        if (truncation is not null) notices.Add("50.0KB limit reached");
        object? details = notices.Count == 0 ? null : limit >= int.MinValue && limit <= int.MaxValue && limit == Math.Truncate(limit)
            ? new LsToolDetails(truncation, entryLimitReached ? (int)limit : null)
            : new NumericLsDetails(truncation, entryLimitReached ? limit : null);
        return new(content + (notices.Count > 0 ? "\n\n[" + string.Join(". ", notices) + "]" : ""), details);
    }

    private static bool Exists(string path) => !path.Contains('\0') && (OperatingSystem.IsLinux()
        ? LinuxFileAccess.AccessError(path, 0) == 0 : File.Exists(path) || Directory.Exists(path));

    private sealed record NumericLsDetails(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ToolTruncationDetails? Truncation,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? EntryLimitReached);
}
