namespace PiSharp.Runtime.Resources;

public sealed record PromptResourceSet(string? System, string? Append, string? SystemPath,
    IReadOnlyList<string> AppendPaths);

/// <summary>System-prompt resources are privileged: project copies require an explicit trust decision.</summary>
public static class ProjectPrompts
{
    public static async Task<(string? System, string? Append)> LoadAsync(string cwd, string agentDirectory,
        bool projectTrusted, CancellationToken cancellationToken = default)
    {
        var resources = await LoadWithSourcesAsync(cwd, agentDirectory, projectTrusted, cancellationToken);
        return (resources.System, resources.Append);
    }

    public static async Task<PromptResourceSet> LoadWithSourcesAsync(string cwd, string agentDirectory,
        bool projectTrusted, CancellationToken cancellationToken = default)
    {
        async Task<(string? Text, string? Path)> Read(string directory, string name)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) return (null, null);
            if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException($"Prompt exceeds 64KB: {path}");
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            if (System.Text.Encoding.UTF8.GetByteCount(text) > 64 * 1024)
                throw new InvalidDataException($"Prompt exceeds 64KB: {path}");
            return (text.TrimStart('\uFEFF'), Path.GetFullPath(path));
        }
        var global = Path.GetFullPath(agentDirectory);
        var project = Path.Combine(Path.GetFullPath(cwd), ".pi");
        (string? Text, string? Path) system = projectTrusted
            ? await Read(project, "SYSTEM.md") : (null, null);
        var selectedSystem = system.Text is not null ? system : await Read(global, "SYSTEM.md");
        var globalAppend = await Read(global, "APPEND_SYSTEM.md");
        (string? Text, string? Path) projectAppend = projectTrusted
            ? await Read(project, "APPEND_SYSTEM.md") : (null, null);
        var appendResources = new[] { globalAppend, projectAppend }
            .Where(resource => resource.Path is not null).ToArray();
        return new(selectedSystem.Text,
            string.Join("\n\n", appendResources.Select(resource => resource.Text).Where(value => !string.IsNullOrEmpty(value))),
            selectedSystem.Path, appendResources.Select(resource => resource.Path!).ToArray());
    }
}
