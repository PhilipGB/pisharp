using Microsoft.Agents.AI;
using PiSharp.Core;

namespace PiSharp.Cli;

internal sealed record AgentBootstrap(
    AIAgent Agent,
    IReadOnlyList<string> ContextFiles,
    IReadOnlyList<SkillDefinition> Skills,
    IReadOnlyList<PromptTemplate> PromptTemplates,
    PiSharpExtensionHost ExtensionHost,
    RetryPolicyOptions RetryPolicy,
    TurnMessageQueue TurnQueue);
