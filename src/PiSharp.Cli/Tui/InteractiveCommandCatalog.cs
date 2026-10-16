namespace PiSharp.Cli.Tui;

internal sealed record InteractiveCommandMetadata(string Command, string Description);

/// <summary>Ordered metadata for implemented built-in interactive slash commands.</summary>
internal static class InteractiveCommandCatalog
{
    private static readonly IReadOnlyList<InteractiveCommandMetadata> s_builtIns = Array.AsReadOnly<InteractiveCommandMetadata>(
    [
        new("/tree", "Browse conversation history and switch to another point."),
        new("/branch", "Start a new branch from the current conversation point."),
        new("/fork", "Fork the current conversation."),
        new("/clone", "Clone the current conversation into a new session."),
        new("/new", "Start a new conversation."),
        new("/sessions", "Browse and manage saved sessions."),
        new("/resume", "Resume a saved session."),
        new("/delete-session", "Delete a saved session."),
        new("/import", "Import a conversation from a session file."),
        new("/name", "Set the current session name."),
        new("/model", "Select or change the active model."),
        new("/models", "List available models."),
        new("/scoped-models", "View or change the model selection scope."),
        new("/thinking", "Select the active model's thinking level."),
        new("/login", "Authenticate a provider."),
        new("/logout", "Remove stored provider credentials."),
        new("/llama", "Manage locally hosted llama.cpp models."),
        new("/settings", "View and change interactive settings."),
        new("/compact", "Compact the current conversation context."),
        new("/copy", "Copy the latest assistant response."),
        new("/export", "Export the current session as HTML."),
        new("/share", "Share the current session."),
        new("/export-jsonl", "Export the current session as JSONL."),
        new("/session", "Show information about the current session."),
        new("/trust", "Review project trust and access."),
        new("/reload", "Reload project resources and extensions."),
        new("/hotkeys", "Show configured keyboard shortcuts."),
        new("/mcp", "Inspect and manage MCP servers."),
        new("/quit", "Exit PiSharp."),
        new("/exit", "Exit PiSharp.")
    ]);

    private static readonly IReadOnlyDictionary<string, InteractiveCommandMetadata> s_byName =
        s_builtIns.ToDictionary(command => command.Command, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<InteractiveCommandMetadata> BuiltIns => s_builtIns;

    public static IReadOnlyList<InteractiveCommandMetadata> Merge(IEnumerable<InteractiveCommandMetadata>? dynamicCommands)
    {
        var merged = new List<InteractiveCommandMetadata>(s_builtIns);
        var seen = new HashSet<string>(s_builtIns.Select(command => command.Command), StringComparer.OrdinalIgnoreCase);
        if (dynamicCommands is not null)
        {
            foreach (var command in dynamicCommands)
            {
                if (string.IsNullOrWhiteSpace(command.Command) || !seen.Add(command.Command)) continue;
                merged.Add(command with { Description = command.Description ?? "" });
            }
        }
        return Array.AsReadOnly(merged.ToArray());
    }

    public static string? Describe(string command) =>
        s_byName.TryGetValue(command, out var metadata) ? metadata.Description : null;
}
