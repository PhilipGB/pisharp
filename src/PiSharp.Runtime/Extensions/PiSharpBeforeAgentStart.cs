using Microsoft.Extensions.AI;
using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Extensions;

/// <summary>Identifies whether a turn starts from submitted user input or an extension custom message.</summary>
public enum PiSharpTurnMessageType
{
    User,
    Custom
}

/// <summary>A custom extension message that can start or join an agent turn.</summary>
public sealed record PiSharpCustomMessage(string CustomType, string Content, bool Display = true)
{
    /// <summary>The owning extension source. Assigned by the host and never accepted from extension input.</summary>
    internal string? Source { get; init; }
}

/// <summary>Prompt data and current system text supplied before a new agent turn starts.</summary>
public sealed record PiSharpBeforeAgentStartEvent(string Prompt, IReadOnlyList<DataContent> Images,
    string SystemPrompt)
{
    /// <summary>Creates event data for a turn that starts from user input or a custom message.</summary>
    public PiSharpBeforeAgentStartEvent(string prompt, IReadOnlyList<DataContent> images, string systemPrompt,
        PiSharpTurnMessageType messageType, PiSharpCustomMessage? startingCustomMessage)
        : this(prompt, images, systemPrompt)
    {
        MessageType = messageType;
        StartingCustomMessage = startingCustomMessage;
    }

    /// <summary>Whether this turn starts from submitted user input or an extension custom message.</summary>
    public PiSharpTurnMessageType MessageType { get; init; } = PiSharpTurnMessageType.User;

    /// <summary>The custom message that starts this turn, if any.</summary>
    public PiSharpCustomMessage? StartingCustomMessage { get; init; }
}

/// <summary>A change made by a before-agent-start handler.</summary>
public sealed record PiSharpBeforeAgentStartResult(string? SystemPrompt = null)
{
    /// <summary>Injects an extension custom message after the message that starts the turn.</summary>
    public PiSharpCustomMessage? InjectedMessage { get; init; }
}

/// <summary>Runs before a user or extension custom message is sent to the provider.</summary>
public delegate ValueTask<PiSharpBeforeAgentStartResult?> PiSharpBeforeAgentStartHandler(
    PiSharpBeforeAgentStartEvent eventData, PiSharpExtensionContext context, CancellationToken cancellationToken);

/// <summary>A before-agent-start callback retained with its owning extension source.</summary>
public sealed record PiSharpBeforeAgentStartHandlerRegistration(PiSharpBeforeAgentStartHandler Handler,
    ResourceSourceInfo SourceInfo);
