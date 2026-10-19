using Microsoft.Extensions.AI;

namespace PiSharp.Extensions.Abstractions;

/// <summary>A detached, request-local view supplied to a native <c>context</c> handler.</summary>
public sealed class PiSharpContextEvent(IList<ChatMessage> messages)
{
    /// <summary>
    /// Gets or replaces the current message list. The host supplies detached messages without
    /// system-role entries. In-place list and message edits are accepted when valid.
    /// </summary>
    public IList<ChatMessage> Messages { get; set; } = messages;
}

/// <summary>A detached, request-local view supplied after all <c>context</c> handlers.</summary>
public sealed class PiSharpContextWithSystemEvent(IList<ChatMessage> messages, string? systemInstructions,
    IReadOnlyList<string> toolNames)
{
    /// <summary>Gets or replaces the full provider-bound message list.</summary>
    public IList<ChatMessage> Messages { get; set; } = messages;

    /// <summary>
    /// Gets or replaces the provider instructions held separately from <see cref="Messages"/> by
    /// Microsoft.Extensions.AI. Assign <see langword="null"/> to remove the instructions.
    /// </summary>
    public string? SystemInstructions { get; set; } = systemInstructions;

    /// <summary>
    /// Gets the names of the tools in the host's authoritative ChatOptions. This snapshot is
    /// informational; handlers do not replace the executable tool configuration.
    /// </summary>
    public IReadOnlyList<string> ToolNames { get; } = Array.AsReadOnly(toolNames.ToArray());
}

/// <summary>Mutates provider-bound conversation messages while system state stays protected.</summary>
public delegate ValueTask PiSharpContextHandler(PiSharpContextEvent eventData,
    PiSharpExtensionContext context, CancellationToken cancellationToken);

/// <summary>Mutates the full provider-bound context after all <c>context</c> handlers.</summary>
public delegate ValueTask PiSharpContextWithSystemHandler(PiSharpContextWithSystemEvent eventData,
    PiSharpExtensionContext context, CancellationToken cancellationToken);
