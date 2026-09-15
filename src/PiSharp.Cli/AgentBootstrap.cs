using Microsoft.Agents.AI;
using PiSharp.Core;

namespace PiSharp.Cli;

internal sealed record AgentBootstrap(
    AIAgent Agent,
    IReadOnlyList<string> ContextFiles,
    TurnMessageQueue TurnQueue);
