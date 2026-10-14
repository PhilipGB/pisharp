using System.Text;
namespace PiSharp.Runtime.Resources;

/// <summary>Context-file discovery like Pi: one prioritized file per directory from home/agent
/// and each ancestor of cwd. Does not execute or load project extensions/settings.</summary>
public static class ContextInstructions
{
    private static readonly string[] s_names = ["AGENTS.override.md", "AGENTS.md", "AGENTS.MD", "CLAUDE.md", "CLAUDE.MD"];
    private static readonly StringComparison s_pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private static readonly StringComparer s_pathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    public static async Task<string> LoadAsync(string workingDirectory, string agentDirectory,
        CancellationToken cancellationToken = default, ICollection<string>? loadedFiles = null,
        Action<ResourceDiagnostic>? reportDiagnostic = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = new List<ContextFile>();
        var seenPaths = new HashSet<string>(s_pathComparer);
        var global = await ReadContextFileAsync(Path.GetFullPath(agentDirectory), cancellationToken, reportDiagnostic);
        if (global is not null)
        {
            files.Add(global);
            seenPaths.Add(global.Path);
        }
        var shadowedContextFile = await FindShadowedContextFileAsync(workingDirectory, cancellationToken, reportDiagnostic);
        var ancestors = new Stack<ContextFile>();
        for (var folder = new DirectoryInfo(Path.GetFullPath(workingDirectory)); folder is not null; folder = folder.Parent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contextFile = await ReadContextFileAsync(folder.FullName, cancellationToken, reportDiagnostic);
            if (contextFile is null) continue;
            var isShadowed = shadowedContextFile is not null &&
                PathsEqual(Path.Combine(CanonicalDirectoryPath.Resolve(folder.FullName), Path.GetFileName(contextFile.Path)), shadowedContextFile);
            if (isShadowed || !seenPaths.Add(contextFile.Path)) continue;
            ancestors.Push(contextFile);
        }
        files.AddRange(ancestors);
        var contents = new StringBuilder();
        foreach (var (file, text) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var section = $"<project_instructions path=\"{XmlEscape(file)}\">\n{text}\n</project_instructions>\n\n";
            contents.Append(section);
            loadedFiles?.Add(file);
        }
        return contents.ToString();
    }

    private sealed record ContextFile(string Path, string Content);

    private static async Task<ContextFile?> ReadContextFileAsync(string directory, CancellationToken cancellationToken,
        Action<ResourceDiagnostic>? reportDiagnostic)
    {
        foreach (var name in s_names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            try
            {
                if (!LocalResourceFileType.IsRegularFile(path)) continue;
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                var text = Encoding.UTF8.GetString(bytes);
                return new(path, text.StartsWith('\uFEFF') ? text[1..] : text);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                reportDiagnostic?.Invoke(new("warning", $"Warning: Could not read {path}: {error.Message}", path));
            }
        }
        return null;
    }

    private sealed record GitPaths(string RepositoryDirectory, string CommonGitDirectory);

    /// <summary>
    /// A nested linked worktree shares its repository scope with the main worktree. If
    /// the nested worktree has an instruction file, the main worktree's same-named file
    /// is a duplicate; ancestor directories above the main repo remain independent.
    /// </summary>
    private static async Task<string?> FindShadowedContextFileAsync(string workingDirectory,
        CancellationToken cancellationToken, Action<ResourceDiagnostic>? reportDiagnostic)
    {
        var gitPaths = FindGitPaths(Path.GetFullPath(workingDirectory));
        if (gitPaths is null) return null;

        var commonGitDirectory = CanonicalDirectoryPath.Resolve(gitPaths.CommonGitDirectory);
        var worktreeRoot = CanonicalDirectoryPath.Resolve(gitPaths.RepositoryDirectory);
        var mainRepositoryRoot = Path.GetDirectoryName(commonGitDirectory);
        if (mainRepositoryRoot is null || !IsDescendantPath(worktreeRoot, mainRepositoryRoot)) return null;

        // For ordinary linked worktrees, the common git directory is <main>/.git.
        // This excludes bare layouts and submodules, whose common git directory does
        // not identify an ancestor main worktree.
        if (!PathsEqual(CanonicalDirectoryPath.Resolve(Path.Combine(mainRepositoryRoot, ".git")), commonGitDirectory))
            return null;

        var worktreeContextFile = await ReadContextFileAsync(worktreeRoot, cancellationToken, reportDiagnostic);
        return worktreeContextFile is null
            ? null
            : Path.Combine(mainRepositoryRoot, Path.GetFileName(worktreeContextFile.Path));
    }

    private static GitPaths? FindGitPaths(string workingDirectory)
    {
        for (var directory = workingDirectory; ;)
        {
            var gitPath = Path.Combine(directory, ".git");
            if (Directory.Exists(gitPath))
            {
                var headPath = Path.Combine(gitPath, "HEAD");
                return File.Exists(headPath) ? new GitPaths(directory, gitPath) : null;
            }

            if (File.Exists(gitPath))
            {
                try
                {
                    var gitFile = File.ReadAllText(gitPath).Trim();
                    const string prefix = "gitdir: ";
                    if (!gitFile.StartsWith(prefix, StringComparison.Ordinal)) return null;

                    var gitDirectory = Path.GetFullPath(gitFile[prefix.Length..].Trim(), directory);
                    if (!File.Exists(Path.Combine(gitDirectory, "HEAD"))) return null;

                    var commonDirectoryFile = Path.Combine(gitDirectory, "commondir");
                    var commonGitDirectory = Directory.Exists(commonDirectoryFile) || File.Exists(commonDirectoryFile)
                        ? Path.GetFullPath(File.ReadAllText(commonDirectoryFile).Trim(), gitDirectory)
                        : gitDirectory;
                    return new GitPaths(directory, commonGitDirectory);
                }
                catch (IOException)
                {
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }

            var parent = Directory.GetParent(directory)?.FullName;
            if (parent is null) return null;
            directory = parent;
        }
    }

    private static bool IsDescendantPath(string path, string parent)
    {
        var relativePath = Path.GetRelativePath(parent, path);
        return relativePath != "." && !Path.IsPathRooted(relativePath) && relativePath != ".." &&
            !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, s_pathComparison);
    }

    private static bool PathsEqual(string left, string right) => s_pathComparer.Equals(left, right);

    private static string XmlEscape(string input) => System.Security.SecurityElement.Escape(input) ?? "";
}
