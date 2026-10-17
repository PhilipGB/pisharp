using Microsoft.Extensions.AI;

namespace PiSharp.Extensions.Abstractions;

/// <summary>A compiled PiSharp-native extension that contributes behavior to its host.</summary>
public interface IPiSharpExtension
{
    /// <summary>Registers this extension's supported commands, tools, and lifecycle callbacks.</summary>
    void Configure(IPiSharpExtensionBuilder builder);
}

/// <summary>Registers the capabilities supported by the current PiSharp extension API version.</summary>
public interface IPiSharpExtensionBuilder
{
    /// <summary>Registers a command. The argument is the text following the command name.</summary>
    void AddCommand(string name, Func<string, CancellationToken, Task<string>> handler,
        string? description = null);

    /// <summary>Registers an AI function tool available to the agent.</summary>
    void AddTool(AIFunction tool);

    /// <summary>Registers a callback that can update the system prompt before an agent turn.</summary>
    void AddBeforeAgentStartHandler(PiSharpBeforeAgentStartHandler handler);
}

/// <summary>Prompt information supplied immediately before the agent begins a user turn.</summary>
public sealed record PiSharpBeforeAgentStartEvent(string Prompt, string SystemPrompt);

/// <summary>A change returned by a before-agent-start callback.</summary>
public sealed record PiSharpBeforeAgentStartResult(string? SystemPrompt = null);

/// <summary>The small amount of live host context available to a lifecycle callback.</summary>
public sealed record PiSharpExtensionContext(string WorkingDirectory);

/// <summary>Updates prompt context before a user turn is sent to the agent.</summary>
public delegate ValueTask<PiSharpBeforeAgentStartResult?> PiSharpBeforeAgentStartHandler(
    PiSharpBeforeAgentStartEvent eventData,
    PiSharpExtensionContext context,
    CancellationToken cancellationToken);
