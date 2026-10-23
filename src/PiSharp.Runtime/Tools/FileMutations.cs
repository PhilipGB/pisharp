using System.Text;
using PiSharp.Core;

namespace PiSharp.Runtime.Tools;

/// <summary>Coordinates atomic writes and exact edits across facade instances.</summary>
internal sealed class FileMutations(string workingDirectory, FileMutationQueue? mutationQueue = null,
    Func<Task>? beforeCommit = null)
{
    private static readonly FileMutationQueue s_mutations = new();
    private readonly FileMutationQueue _mutations = mutationQueue ?? s_mutations;
    private string Resolve(string path) => CodingToolPaths.Resolve(path, workingDirectory);

    private static string? FindBlockingFileAncestor(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if (File.Exists(current)) return current;
            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return null;
            current = parent;
        }
    }

    private static async Task<byte[]> ReadEditableFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new MemoryStream();
        await stream.CopyToAsync(content, cancellationToken);
        return content.ToArray();
    }

    public async Task<string> WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        var absolute = Resolve(path);
        var parent = Path.GetDirectoryName(absolute)!;
        try
        {
            return await _mutations.RunAsync(absolute, async () =>
            {
                if (FindBlockingFileAncestor(parent) is { } blockingFile)
                {
                    var pathIsFile = string.Equals(blockingFile, parent,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                    var code = pathIsFile ? "EEXIST" : "ENOTDIR";
                    var description = pathIsFile ? "file already exists" : "not a directory";
                    throw new ToolFailureException($"{code}: {description}, mkdir '{parent}'");
                }
                try { Directory.CreateDirectory(parent); }
                catch (UnauthorizedAccessException error)
                {
                    throw new ToolFailureException($"EACCES: permission denied, mkdir '{parent}'", inner: error);
                }
                if (Directory.Exists(absolute))
                    throw new ToolFailureException($"EISDIR: illegal operation on a directory, open '{absolute}'");

                LinuxFileAccess.RequireWritableIfPresent(absolute);
                await AtomicFileWriter.ReplaceAsync(absolute, Encoding.UTF8.GetBytes(content), cancellationToken, beforeCommit);
                return $"Successfully wrote to {path}";
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Operation aborted", cancellationToken);
        }
        catch (DirectoryNotFoundException error) when (OperatingSystem.IsLinux())
        {
            LinuxFileAccess.Require(absolute, 2, "open");
            throw new ToolFailureException($"ENOENT: no such file or directory, open '{absolute}'", inner: error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new ToolFailureException($"EACCES: permission denied, open '{absolute}'", inner: error);
        }
        catch (IOException e)
        {
            throw new ToolFailureException($"Error writing {path}: {e.Message}", inner: e);
        }
    }

    public async Task<EditToolOutput> EditAsync(string path, List<TextEdit> edits,
        CancellationToken cancellationToken)
    {
        if (edits is null || edits.Count == 0) throw new ToolFailureException("Edit tool input is invalid. edits must contain at least one replacement.");
        try
        {
            var absolute = Resolve(path);
            return await _mutations.RunAsync(absolute, async () =>
            {
                byte[] bytes;
                try { bytes = await ReadEditableFileAsync(absolute, cancellationToken); }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    throw new ToolFailureException($"Could not edit file: {path}. Error code: ENOENT.", inner: e);
                }
                catch (UnauthorizedAccessException e)
                {
                    throw new ToolFailureException($"Could not edit file: {path}. Error code: EACCES.", inner: e);
                }
                var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                var source = new UTF8Encoding(false, true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
                var lf = source.IndexOf('\n');
                var ending = lf > 0 && source[lf - 1] == '\r' ? "\r\n" : "\n";
                var plan = FileEdits.Plan(source, edits, path);
                var restored = ending == "\r\n" ? plan.NewContent.Replace("\n", "\r\n", StringComparison.Ordinal) : plan.NewContent;
                var payload = Encoding.UTF8.GetBytes(restored);
                if (bom) payload = [0xEF, 0xBB, 0xBF, .. payload];
                var details = FileEditDiff.Create(path, plan.OriginalContent, plan.NewContent);
                await AtomicFileWriter.ReplaceAsync(absolute, payload, cancellationToken, beforeCommit);
                return new EditToolOutput($"Successfully replaced {edits.Count} block(s) in {path}.",
                    details.Diff, details.Patch, details.FirstChangedLine);
            }, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or DecoderFallbackException)
        {
            throw new ToolFailureException(e.Message, inner: e);
        }
    }

}
