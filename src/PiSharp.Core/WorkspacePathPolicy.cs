namespace PiSharp.Core;

public sealed class WorkspacePathPolicy
{
    private readonly string _rootWithSeparator;
    private readonly StringComparison _comparison;

    public WorkspacePathPolicy(string workspaceRoot)
    {
        Root = Path.GetFullPath(workspaceRoot);
        _rootWithSeparator = Root.EndsWith(Path.DirectorySeparatorChar)
            ? Root
            : Root + Path.DirectorySeparatorChar;
        _comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    public string Root { get; }

    public string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var candidate = Path.GetFullPath(
            Path.IsPathRooted(path) ? path : Path.Combine(Root, path));

        if (!candidate.Equals(Root, _comparison) &&
            !candidate.StartsWith(_rootWithSeparator, _comparison))
        {
            throw new InvalidOperationException(
                $"Path '{path}' resolves outside workspace '{Root}'.");
        }

        return candidate;
    }
}
