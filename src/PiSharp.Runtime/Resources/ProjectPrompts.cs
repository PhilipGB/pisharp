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
        bool projectTrusted, CancellationToken cancellationToken = default,
        Action<ResourceDiagnostic>? reportDiagnostic = null, string? systemPromptOverride = null,
        IReadOnlyList<string>? appendSystemPromptOverrides = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var global = Path.GetFullPath(agentDirectory);
        var project = Path.Combine(Path.GetFullPath(cwd), ".pi");
        string? Discover(string name)
        {
            var projectPath = Path.Combine(project, name);
            if (projectTrusted && (File.Exists(projectPath) || Directory.Exists(projectPath))) return projectPath;
            var globalPath = Path.Combine(global, name);
            return File.Exists(globalPath) || Directory.Exists(globalPath) ? globalPath : null;
        }
        var system = await PromptFileInput.ResolveAsync(systemPromptOverride ?? Discover("SYSTEM.md"), cwd,
            "system prompt", cancellationToken, reportDiagnostic);
        var discoveredAppend = appendSystemPromptOverrides is null ? Discover("APPEND_SYSTEM.md") : null;
        var appendSources = appendSystemPromptOverrides ?? (discoveredAppend is { } source ? [source] : []);
        var append = await PromptFileInput.ResolveManyAsync(appendSources, cwd, "append system prompt", cancellationToken, reportDiagnostic);
        return new(system.Text, append.Text, system.Path, append.Paths);
    }
}
