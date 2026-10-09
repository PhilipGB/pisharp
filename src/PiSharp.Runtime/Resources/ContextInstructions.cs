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
    private const int MaxFileBytes = 64 * 1024;
    private const int MaxTotalBytes = 256 * 1024;

    public static async Task<string> LoadAsync(string workingDirectory, string agentDirectory,
        CancellationToken cancellationToken = default)
    {
        var folders = new List<string> { Path.GetFullPath(agentDirectory) };
        var parents = new Stack<string>();
        for (var folder = new DirectoryInfo(Path.GetFullPath(workingDirectory)); folder is not null; folder = folder.Parent)
            parents.Push(folder.FullName);
        while (parents.TryPop(out var directory))
            if (!folders.Contains(directory, StringComparer.Ordinal)) folders.Add(directory);
        var contents = new StringBuilder();
        var seenPaths = new HashSet<string>(s_pathComparer);
        var shadowedContextFile = FindShadowedContextFile(workingDirectory);
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = s_names.Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
            if (file is null) continue;
            var isShadowed = shadowedContextFile is not null &&
                PathsEqual(Path.Combine(CanonicalizeDirectory(folder), Path.GetFileName(file)), shadowedContextFile);
            if (isShadowed || !seenPaths.Add(file)) continue;
            var info = new FileInfo(file);
            if (info.Length > MaxFileBytes) throw new InvalidDataException($"Instruction file exceeds 64KB: {file}");
            var text = await File.ReadAllTextAsync(file, cancellationToken);
            if (Encoding.UTF8.GetByteCount(text) > MaxFileBytes) throw new InvalidDataException($"Instruction file exceeds 64KB: {file}");
            var section = $"<project_instructions path=\"{XmlEscape(file)}\">\n{text.TrimStart('\uFEFF')}\n</project_instructions>\n\n";
            if (Encoding.UTF8.GetByteCount(contents.ToString()) + Encoding.UTF8.GetByteCount(section) > MaxTotalBytes)
                throw new InvalidDataException("Combined context instructions exceed 256KB.");
            contents.Append(section);
        }
        return contents.ToString();
    }

    private sealed record GitPaths(string RepositoryDirectory, string CommonGitDirectory);

    /// <summary>
    /// A nested linked worktree shares its repository scope with the main worktree. If
    /// the nested worktree has an instruction file, the main worktree's same-named file
    /// is a duplicate; ancestor directories above the main repo remain independent.
    /// </summary>
    private static string? FindShadowedContextFile(string workingDirectory)
    {
        var gitPaths = FindGitPaths(Path.GetFullPath(workingDirectory));
        if (gitPaths is null) return null;

        var commonGitDirectory = CanonicalizeDirectory(gitPaths.CommonGitDirectory);
        var worktreeRoot = CanonicalizeDirectory(gitPaths.RepositoryDirectory);
        var mainRepositoryRoot = Path.GetDirectoryName(commonGitDirectory);
        if (mainRepositoryRoot is null || !IsDescendantPath(worktreeRoot, mainRepositoryRoot)) return null;

        // For ordinary linked worktrees, the common git directory is <main>/.git.
        // This excludes bare layouts and submodules, whose common git directory does
        // not identify an ancestor main worktree.
        if (!PathsEqual(CanonicalizeDirectory(Path.Combine(mainRepositoryRoot, ".git")), commonGitDirectory))
            return null;

        var worktreeContextFile = FindContextFilePath(worktreeRoot);
        return worktreeContextFile is null
            ? null
            : Path.Combine(mainRepositoryRoot, Path.GetFileName(worktreeContextFile));
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

    private static string? FindContextFilePath(string directory) =>
        s_names.Select(name => Path.Combine(directory, name)).FirstOrDefault(File.Exists);

    private static bool IsDescendantPath(string path, string parent)
    {
        var relativePath = Path.GetRelativePath(parent, path);
        return relativePath != "." && !Path.IsPathRooted(relativePath) && relativePath != ".." &&
            !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, s_pathComparison);
    }

    private static bool PathsEqual(string left, string right) => s_pathComparer.Equals(left, right);

    private static string CanonicalizeDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (root is null) return fullPath;
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current)) continue;
            try
            {
                current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
            }
            catch (IOException)
            {
                // Match Pi's canonicalizePath fallback for inaccessible or broken paths.
            }
            catch (UnauthorizedAccessException)
            {
                // The caller will continue with the normalized path when realpath is unavailable.
            }
        }
        return Path.GetFullPath(current);
    }

    private static string XmlEscape(string input) => System.Security.SecurityElement.Escape(input) ?? "";
}
