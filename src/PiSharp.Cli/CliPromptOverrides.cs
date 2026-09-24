using System.Text;

namespace PiSharp.Cli;

/// <summary>Resolve per-run literal or file-backed system prompt overrides without persisting them.</summary>
public static class CliPromptOverrides
{
    public static async Task<(string? System, string? Append)> ResolveAsync(CliArguments cli,
        (string? System, string? Append) discovered, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var system = cli.SystemPrompt is null ? discovered.System : await ResolveInputAsync(cli.SystemPrompt, workingDirectory, cancellationToken);
        if (cli.AppendSystemPrompts is not { Count: > 0 }) return (system, discovered.Append);
        var sections = new List<string>();
        foreach (var source in cli.AppendSystemPrompts)
            sections.Add(await ResolveInputAsync(source, workingDirectory, cancellationToken));
        // An explicit append list replaces file discovery, matching the upstream resource loader.
        return (system, string.Join("\n\n", sections));
    }

    private static async Task<string> ResolveInputAsync(string input, string workingDirectory, CancellationToken cancellationToken)
    {
        string path;
        try { path = Path.GetFullPath(input, workingDirectory); }
        catch (ArgumentException) { return input; } // A literal prompt need not be a valid path.
        if (!File.Exists(path)) return input;
        var info = new FileInfo(path);
        if (info.Length > 64 * 1024) throw new InvalidDataException($"System prompt file exceeds 64KB: {path}");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
    }
}
