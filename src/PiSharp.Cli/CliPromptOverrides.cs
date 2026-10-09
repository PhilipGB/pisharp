using System.Text;
using PiSharp.Runtime.Resources;

namespace PiSharp.Cli;

/// <summary>Resolve per-run literal or file-backed system prompt overrides without persisting them.</summary>
public static class CliPromptOverrides
{
    public static async Task<(string? System, string? Append)> ResolveAsync(CliArguments cli,
        (string? System, string? Append) discovered, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveWithSourcesAsync(cli,
            new(discovered.System, discovered.Append, null, []), workingDirectory, cancellationToken);
        return (resolved.System, resolved.Append);
    }

    public static async Task<PromptResourceSet> ResolveWithSourcesAsync(CliArguments cli,
        PromptResourceSet discovered, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var system = discovered.System;
        var systemPath = discovered.SystemPath;
        if (cli.SystemPrompt is not null)
        {
            var resolved = await ResolveInputAsync(cli.SystemPrompt, workingDirectory, cancellationToken);
            system = resolved.Text;
            systemPath = resolved.Path;
        }
        if (cli.AppendSystemPrompts is not { Count: > 0 })
            return discovered with { System = system, SystemPath = systemPath };
        var sections = new List<string>();
        var appendPaths = new List<string>();
        foreach (var source in cli.AppendSystemPrompts)
        {
            var resolved = await ResolveInputAsync(source, workingDirectory, cancellationToken);
            sections.Add(resolved.Text);
            if (resolved.Path is { } path) appendPaths.Add(path);
        }
        // An explicit append list replaces file discovery, matching the upstream resource loader.
        return new(system, string.Join("\n\n", sections), systemPath, appendPaths);
    }

    private static async Task<(string Text, string? Path)> ResolveInputAsync(string input, string workingDirectory,
        CancellationToken cancellationToken)
    {
        string path;
        try { path = Path.GetFullPath(input, workingDirectory); }
        catch (ArgumentException) { return (input, null); } // A literal prompt need not be a valid path.
        if (!File.Exists(path)) return (input, null);
        var info = new FileInfo(path);
        if (info.Length > 64 * 1024) throw new InvalidDataException($"System prompt file exceeds 64KB: {path}");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return (new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'), path);
    }
}
