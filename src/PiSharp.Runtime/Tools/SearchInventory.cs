using System.Diagnostics;

namespace PiSharp.Runtime.Tools;

/// <summary>Bounded file discovery; Git handles tracked/untracked ignore rules when available.</summary>
internal static class SearchInventory
{
    private const int MaxEntries = 20_000;

    public static async Task<IReadOnlyList<string>> EnumerateAsync(string root, CancellationToken cancellationToken)
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
                if (git.ExitCode == 0) return results;
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
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory) && files.Count < MaxEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(directory).ToArray(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (files.Count >= MaxEntries) break;
                try
                {
                    if (Directory.Exists(entry))
                    {
                        if (Path.GetFileName(entry) is ".git" or "node_modules" ||
                            File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)) continue;
                        pending.Push(entry);
                    }
                    else if (File.Exists(entry)) files.Add(entry);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        if (files.Count >= MaxEntries) throw new ToolFailureException("Search exceeds 20000 files; narrow the search path.");
        return files;
    }
}
