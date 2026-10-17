using Microsoft.Extensions.AI;

namespace PiSharp.Extensions.Abstractions;

/// <summary>A compiled PiSharp-native extension that contributes behavior to its host.</summary>
public interface IPiSharpExtension
{
    /// <summary>Registers this extension's supported commands, tools, and lifecycle callbacks.</summary>
    void Configure(IPiSharpExtensionBuilder builder);
}

/// <summary>Registers the capabilities supported by the current PiSharp extension API version.</summary>
public partial interface IPiSharpExtensionBuilder
{
    /// <summary>Registers a command. The argument is the text following the command name.</summary>
    void AddCommand(string name, Func<string, CancellationToken, Task<string>> handler,
        string? description = null);

    /// <summary>Registers a command that can submit user or custom messages through its live host context.</summary>
    void AddCommand(string name, PiSharpExtensionCommandHandler handler, string? description = null);

    /// <summary>Registers a keyboard shortcut for the interactive terminal.</summary>
    void AddShortcut(PiSharpShortcutKey shortcut, PiSharpExtensionShortcutHandler handler,
        string? description = null);

    /// <summary>Registers an AI function tool available to the agent.</summary>
    void AddTool(AIFunction tool);

    /// <summary>Registers a provider and its chat models for normal model selection.</summary>
    void AddProvider(PiSharpExtensionProviderDefinition provider);

    /// <summary>Registers an MCP server that joins the normal startup MCP connection flow.</summary>
    void AddMcpServer(string name, PiSharpExtensionMcpServerConfiguration configuration);

    /// <summary>Registers a selectable logical model routed through a physical provider model.</summary>
    void AddVirtualModel(PiSharpExtensionVirtualModelDefinition model);

    /// <summary>Registers a callback that can update the system prompt before an agent turn.</summary>
    void AddBeforeAgentStartHandler(PiSharpBeforeAgentStartHandler handler);

    /// <summary>Registers a transform applied to each provider-bound conversation request.</summary>
    void AddContextTransform(PiSharpContextTransform transform);

    /// <summary>Registers an ordered hook before model-issued and nested tool calls.</summary>
    void AddToolCallHook(PiSharpToolCallHook hook);

    /// <summary>Registers an ordered hook after model-issued and nested tool calls.</summary>
    void AddToolResultHook(PiSharpToolResultHook hook);

    /// <summary>Registers resource paths discovered at startup and after reload.</summary>
    void AddResourceDiscoveryHandler(PiSharpResourceDiscoveryHandler handler);

    /// <summary>Registers an asynchronous callback after a session is opened and restored.</summary>
    void AddSessionStartHandler(PiSharpExtensionSessionStartHandler handler);

    /// <summary>Registers an asynchronous callback before a session is replaced or the host shuts down.</summary>
    void AddSessionShutdownHandler(PiSharpExtensionSessionShutdownHandler handler);

    /// <summary>Observes a committed change to the active session name.</summary>
    void AddSessionInfoChangedHandler(PiSharpExtensionSessionInfoChangedHandler handler);

    /// <summary>Observes successful, persisted session compaction.</summary>
    void AddSessionCompactHandler(PiSharpExtensionSessionCompactHandler handler);

    /// <summary>Observes failed or aborted session compaction.</summary>
    void AddSessionCompactFailedHandler(PiSharpExtensionSessionCompactFailedHandler handler);

    /// <summary>Observes the start of one logical agent run.</summary>
    void AddAgentStartHandler(PiSharpExtensionAgentStartHandler handler);

    /// <summary>Observes the end of one logical agent run.</summary>
    void AddAgentEndHandler(PiSharpExtensionAgentEndHandler handler);

    /// <summary>Observes the start of each logical model turn.</summary>
    void AddTurnStartHandler(PiSharpExtensionTurnStartHandler handler);

    /// <summary>Observes the finalized or interrupted state of each logical model turn.</summary>
    void AddTurnEndHandler(PiSharpExtensionTurnEndHandler handler);

    /// <summary>Observes a tool call when it is about to execute.</summary>
    void AddToolExecutionStartHandler(PiSharpExtensionToolExecutionStartHandler handler);

    /// <summary>Observes actual partial output emitted during tool execution.</summary>
    void AddToolExecutionUpdateHandler(PiSharpExtensionToolExecutionUpdateHandler handler);

    /// <summary>Observes the finalized result of tool execution.</summary>
    void AddToolExecutionEndHandler(PiSharpExtensionToolExecutionEndHandler handler);

    /// <summary>Observes an effective model selection.</summary>
    void AddModelSelectHandler(PiSharpExtensionModelSelectHandler handler);

    /// <summary>Observes an effective thinking-level selection.</summary>
    void AddThinkingLevelSelectHandler(PiSharpExtensionThinkingLevelSelectHandler handler);
}

/// <summary>Prompt information supplied immediately before the agent begins a user turn.</summary>
public sealed record PiSharpBeforeAgentStartEvent(string Prompt, string SystemPrompt)
{
    /// <summary>Gets mutable structured prompt sections for the current run.</summary>
    public PiSharpSystemPromptOptions SystemPromptOptions { get; init; } = new();

    /// <summary>Identifies whether this run starts from user input or an extension custom message.</summary>
    public PiSharpTurnMessageType MessageType { get; init; } = PiSharpTurnMessageType.User;

    /// <summary>The original custom message when <see cref="MessageType"/> is <see cref="PiSharpTurnMessageType.Custom"/>.</summary>
    public PiSharpCustomMessage? StartingCustomMessage { get; init; }

    /// <summary>Read-only image metadata attached to a user turn.</summary>
    public IReadOnlyList<PiSharpImageAttachment> Images { get; init; } = Array.Empty<PiSharpImageAttachment>();
}

/// <summary>A change returned by a before-agent-start callback.</summary>
public sealed record PiSharpBeforeAgentStartResult(string? SystemPrompt = null)
{
    /// <summary>Injects one custom message after the message that starts the run.</summary>
    public PiSharpCustomMessage? InjectedMessage { get; init; }
}

/// <summary>The small amount of live host context available to a lifecycle callback.</summary>
public sealed partial record PiSharpExtensionContext(string WorkingDirectory)
{
    /// <summary>Whether the host is idle when this callback starts.</summary>
    public bool IsIdle { get; init; }

    /// <summary>Whether project-local native extensions are trusted in this working directory.</summary>
    public bool IsProjectTrusted { get; init; }

    /// <summary>Whether ordinary steering or follow-up messages are queued; excludes held NextTurn and context-only messages.</summary>
    public bool HasPendingMessages { get; init; }
}

/// <summary>Updates prompt context before a user turn is sent to the agent.</summary>
public delegate ValueTask<PiSharpBeforeAgentStartResult?> PiSharpBeforeAgentStartHandler(
    PiSharpBeforeAgentStartEvent eventData,
    PiSharpExtensionContext context,
    CancellationToken cancellationToken);
