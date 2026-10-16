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
        PromptResourceSet discovered, string workingDirectory, CancellationToken cancellationToken = default,
        Action<ResourceDiagnostic>? reportDiagnostic = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var system = discovered.System;
        var systemPath = discovered.SystemPath;
        if (cli.SystemPrompt is not null)
        {
            var resolved = await PromptFileInput.ResolveAsync(cli.SystemPrompt, workingDirectory, "system prompt", cancellationToken, reportDiagnostic);
            system = resolved.Text;
            systemPath = resolved.Path;
        }
        if (cli.AppendSystemPrompts is null)
            return discovered with { System = system, SystemPath = systemPath };
        var append = await PromptFileInput.ResolveManyAsync(cli.AppendSystemPrompts, workingDirectory,
            "append system prompt", cancellationToken, reportDiagnostic);
        return new(system, append.Text, systemPath, append.Paths);
    }
}
