using Microsoft.Agents.AI;

namespace PiSharp.Cli;

internal sealed record AgentBootstrap(
    AIAgent Agent,
    IReadOnlyList<string> ContextFiles);
