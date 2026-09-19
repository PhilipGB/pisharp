using System.Text;

namespace PiSharp.Core;

public sealed class AgentsFileLoader
{
    public async Task<string> LoadAsync(
        string workspaceRoot,
        string? homeDirectory = null,
        string? contextRoot = null,
        CancellationToken cancellationToken = default)
    {
        var result = await LoadWithSourcesAsync(workspaceRoot, homeDirectory, contextRoot, cancellationToken);
        return result.Content;
    }

    public async Task<AgentsContext> LoadWithSourcesAsync(
        string workspaceRoot,
        string? homeDirectory = null,
        string? contextRoot = null,
        CancellationToken cancellationToken = default)
    {
        var files = Discover(workspaceRoot, homeDirectory, contextRoot);
        if (files.Count == 0)
        {
            return new AgentsContext(files, string.Empty);
        }

        var builder = new StringBuilder();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(file, cancellationToken);
            builder.AppendLine($"# Instructions from {file}");
            builder.AppendLine(text.Trim());
            builder.AppendLine();
        }

        return new AgentsContext(files, builder.ToString().TrimEnd());
    }

    public IReadOnlyList<string> Discover(
        string workspaceRoot,
        string? homeDirectory = null,
        string? contextRoot = null)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var stop = contextRoot is null ? null : Path.GetFullPath(contextRoot);
        if (stop is not null && !IsAncestorOrSame(stop, root))
        {
            throw new ArgumentException($"Context root must be the workspace or one of its parents: {stop}", nameof(contextRoot));
        }

        var discovered = new List<string>();

        homeDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            var global = Path.Combine(homeDirectory, ".pi", "agent", "AGENTS.md");
            if (File.Exists(global))
            {
                discovered.Add(global);
            }
        }

        var chain = new Stack<string>();
        for (DirectoryInfo? current = new(root); current is not null; current = current.Parent)
        {
            chain.Push(current.FullName);
            if (stop is not null && PathsEqual(current.FullName, stop))
            {
                break;
            }
        }

        while (chain.Count > 0)
        {
            var directory = chain.Pop();
            var selected = SelectContextFile(directory);
            if (selected is not null)
            {
                discovered.Add(selected);
            }
        }

        return discovered;
    }

    private static string? SelectContextFile(string directory)
    {
        var overrideFile = Path.Combine(directory, "AGENTS.override.md");
        if (File.Exists(overrideFile))
        {
            return overrideFile;
        }

        var agents = Path.Combine(directory, "AGENTS.md");
        if (File.Exists(agents))
        {
            return agents;
        }

        var claude = Path.Combine(directory, "CLAUDE.md");
        return File.Exists(claude) ? claude : null;
    }

    private static bool IsAncestorOrSame(string ancestor, string path)
    {
        var relative = Path.GetRelativePath(ancestor, path);
        return relative == "." || (!relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && relative != "..");
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.Ordinal);
}
