namespace PiSharp.Runtime.Tools;

internal static partial class SearchInventory
{
    internal static bool IsWithinGitRepository(string directory) => HasGitMarker(FindGitRoot(directory));

    internal static async Task<Func<string, bool>?> CreateRepositoryExclusionGuardAsync(string directory,
        CancellationToken cancellationToken)
    {
        try
        {
            var configured = await RunGitOutputAsync(directory,
                ["config", "--local", "--path", "--get", "core.excludesFile"], cancellationToken);
            if (string.IsNullOrEmpty(configured)) return null;
            var repository = FindGitRoot(directory);
            var globalRules = ReadIgnoreFile(Path.GetFullPath(configured, directory), repository, IgnoreRuleSource.Global);
            if (globalRules.Count == 0) return null;
            var baseRules = await ReadBaseIgnoreRulesAsync(directory, repository, HasGitMarker(repository),
                includeFdIgnore: false, directory, fdGlobalIgnorePath: null, cancellationToken);
            return file =>
            {
                // Retain private-file exclusions even though rg/fd ignore repository-local core.excludesFile.
                var isDirectory = Directory.Exists(file);
                if (!IsIgnored(file, isDirectory, globalRules)) return false;
                var directories = new Stack<string>();
                for (var parent = Path.GetDirectoryName(file); parent is not null && IsPathWithin(repository, parent);
                     parent = Path.GetDirectoryName(parent))
                {
                    directories.Push(parent);
                    if (PathsEqual(parent, repository)) break;
                }
                var rules = new List<IgnoreRule>(baseRules);
                foreach (var parent in directories) rules.AddRange(ReadIgnoreRules(parent, includeFdIgnore: false, includeRgIgnore: true));
                return IsIgnored(file, isDirectory, rules);
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Operation aborted", cancellationToken); }
    }
}
