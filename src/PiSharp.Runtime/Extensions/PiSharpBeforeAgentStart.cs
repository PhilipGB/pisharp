using Microsoft.Extensions.AI;
using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Extensions;

/// <summary>Prompt data and current system text supplied before a new agent turn starts.</summary>
public sealed record PiSharpBeforeAgentStartEvent(string Prompt, IReadOnlyList<DataContent> Images,
    string SystemPrompt);

/// <summary>A change made by a before-agent-start handler.</summary>
public sealed record PiSharpBeforeAgentStartResult(string? SystemPrompt = null);

/// <summary>Runs before a user prompt is sent to the provider.</summary>
public delegate ValueTask<PiSharpBeforeAgentStartResult?> PiSharpBeforeAgentStartHandler(
    PiSharpBeforeAgentStartEvent eventData, PiSharpExtensionContext context, CancellationToken cancellationToken);

/// <summary>A before-agent-start callback retained with its owning extension source.</summary>
public sealed record PiSharpBeforeAgentStartHandlerRegistration(PiSharpBeforeAgentStartHandler Handler,
    ResourceSourceInfo SourceInfo);
