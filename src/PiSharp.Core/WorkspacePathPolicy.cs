namespace PiSharp.Core;

public sealed class WorkspacePathPolicy
{
    public WorkspacePathPolicy(string workspaceRoot)
    {
        Root = Path.GetFullPath(workspaceRoot);
    }

    public string Root { get; }

    public string Resolve(string path)
    {
        var candidate = ResolveCandidate(path);
        if (!IsUnder(candidate, Root))
        {
            throw new InvalidOperationException(
                $"Path '{path}' resolves outside workspace '{Root}'.");
        }

        return candidate;
    }

    public string ResolveRead(string path, IEnumerable<string> additionalRoots)
    {
        var candidate = ResolveCandidate(path);
        if (IsUnder(candidate, Root) || additionalRoots.Any(root => IsUnder(candidate, root)))
        {
            return candidate;
        }

        throw new InvalidOperationException(
            $"Read path '{path}' is outside the workspace and configured read-only roots.");
    }

    private string ResolveCandidate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));
    }

    private bool IsUnder(string candidate, string root)
    {
        var resolvedRoot = Path.GetFullPath(root);
        var rootWithSeparator = resolvedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedRoot
            : resolvedRoot + Path.DirectorySeparatorChar;
        return candidate.Equals(resolvedRoot, StringComparison.Ordinal) || candidate.StartsWith(rootWithSeparator, StringComparison.Ordinal);
    }
}
