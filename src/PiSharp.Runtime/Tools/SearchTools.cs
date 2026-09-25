using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Runtime.Tools;

/// <summary>Opt-in managed search tools. Git supplies ignore rules inside repositories.</summary>
public sealed class SearchTools(string workingDirectory)
{
    private readonly string _cwd = Path.GetFullPath(workingDirectory);
    private const int MaxBytes = 50 * 1024;

    [Description("Find files and directories matching a glob pattern. Paths are relative to the search root. Respects Git ignore rules inside a repository.")]
    public async Task<string> Find(
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
        var files = await SearchInventory.EnumerateAsync(root, cancellationToken, includeDirectories: true, includeFdIgnore: true);
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
        var output = new StringBuilder();
        var count = 0;
        var limited = false;
        var bytesLimited = false;
        foreach (var candidate in candidates.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count >= max) { limited = true; break; }
            var bytes = Encoding.UTF8.GetByteCount(candidate) + (count == 0 ? 0 : 1);
            if (Encoding.UTF8.GetByteCount(output.ToString()) + bytes > MaxBytes) { bytesLimited = true; break; }
            if (count++ > 0) output.Append('\n');
            output.Append(candidate);
        }
        if (count == 0) return "No files found matching pattern";
        var notices = new List<string>();
        if (limited || count >= max) notices.Add($"{max} results limit reached. Use limit={max * 2} for more, or refine pattern");
        if (bytesLimited) notices.Add("50.0KB limit reached");
        if (notices.Count > 0) output.Append("\n\n[").Append(string.Join(". ", notices)).Append(']');
        return output.ToString();
    }

    [Description("Search file contents with a regex or literal string. Returns matching lines with file paths and line numbers. Respects Git ignore rules in repositories.")]
    public async Task<string> Grep(
        [Description("Regex or literal pattern to search.")] string pattern,
        [Description("Search directory or file (default working directory).")] string? path = null,
        [Description("Filter files by glob, e.g. '*.cs'.")] string? glob = null,
        [Description("Case-insensitive match (default false).")] bool ignoreCase = false,
        [Description("Interpret pattern literally (default false).")] bool literal = false,
        [Description("Number of surrounding lines (default 0).")] int context = 0,
        [Description("Maximum matches (default 100).")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path is null or "" ? "." : path, _cwd);
        if (context < 0) throw new ToolFailureException("context must not be negative.");
        var max = Math.Max(1, limit ?? 100);
        Regex regex;
        Regex? filter = null;
        try
        {
            regex = new Regex(literal ? Regex.Escape(pattern) : pattern,
                (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
            if (glob is not null) filter = GlobRegex(glob);
        }
        catch (ArgumentException error) { throw new ToolFailureException($"Invalid search pattern: {error.Message}", inner: error); }
        var matches = new List<string>();
        var matchLimitReached = false;
        var truncated = false;
        foreach (var file in await SearchInventory.EnumerateAsync(root, cancellationToken, includeRgIgnore: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = (File.Exists(root) ? Path.GetFileName(file) : Path.GetRelativePath(root, file)).Replace('\\', '/');
            if (filter is not null && !filter.IsMatch(glob!.Contains('/') ? relative : Path.GetFileName(relative))) continue;
            // A managed fallback must never allocate an unbounded whole file or huge lines.
            if (new FileInfo(file).Length > 10 * 1024 * 1024)
                throw new ToolFailureException($"File exceeds 10MB managed search limit: {relative}. Narrow the search path.");
            string[] lines;
            try { lines = (await File.ReadAllLinesAsync(file, cancellationToken)).Select(line => line.TrimEnd('\r')).ToArray(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException) { continue; }
            for (var index = 0; index < lines.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool isMatch;
                try { isMatch = regex.IsMatch(lines[index]); }
                catch (RegexMatchTimeoutException error) { throw new ToolFailureException("Search regex timed out.", inner: error); }
                if (!isMatch) continue;
                if (matches.Count >= max) { matchLimitReached = true; break; }
                var block = new StringBuilder();
                for (var current = Math.Max(0, index - context); current <= Math.Min(lines.Length - 1, index + context); current++)
                {
                    var line = lines[current];
                    if (line.Length > 500) { line = line[..500] + "... [truncated]"; truncated = true; }
                    if (block.Length > 0) block.Append('\n');
                    block.Append(relative).Append(current == index ? ':' : '-').Append(current + 1)
                        .Append(current == index ? ": " : "- ").Append(line);
                }
                matches.Add(block.ToString());
                if (matches.Count >= max) { matchLimitReached = true; break; }
            }
            if (matchLimitReached) break;
        }
        if (matches.Count == 0) return "No matches found";
        var (result, bytesTruncated) = TruncateHead(string.Join('\n', matches), MaxBytes);
        var notices = new List<string>();
        if (matchLimitReached) notices.Add($"{max} matches limit reached. Use limit={max * 2} for more, or refine pattern");
        if (bytesTruncated) notices.Add("50.0KB limit reached");
        if (truncated) notices.Add("Some lines truncated to 500 chars. Use read tool to see full lines");
        if (notices.Count > 0) result += "\n\n[" + string.Join(". ", notices) + "]";
        return result;
    }

    private static (string Content, bool Truncated) TruncateHead(string content, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(content) <= maxBytes) return (content, false);
        var lines = content.Split('\n');
        if (lines[^1].Length == 0 && content.EndsWith('\n')) lines = lines[..^1];
        var output = new List<string>();
        var bytes = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var lineBytes = Encoding.UTF8.GetByteCount(lines[index]) + (index > 0 ? 1 : 0);
            if (bytes + lineBytes > maxBytes) break;
            output.Add(lines[index]);
            bytes += lineBytes;
        }
        return (string.Join('\n', output), true);
    }

    private static Regex GlobRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException("Pattern cannot be empty.");
        var source = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/') { source.Append("(?:.*/)?"); i++; }
                    else source.Append(".*");
                }
                else source.Append("[^/]*");
            }
            else if (pattern[i] == '?') source.Append("[^/]");
            else source.Append(Regex.Escape(pattern[i].ToString()));
        }
        return new Regex(source.Append('$').ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}
