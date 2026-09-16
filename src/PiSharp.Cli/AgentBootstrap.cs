using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using PiSharp.Core;

namespace PiSharp.Cli;

internal sealed record AgentBootstrap(
    AIAgent Agent,
    IChatClient SummaryClient,
    IReadOnlyList<string> ContextFiles,
    IReadOnlyList<SkillDefinition> Skills,
    IReadOnlyList<PromptTemplate> PromptTemplates,
    PiSharpExtensionHost ExtensionHost,
    RetryPolicyOptions RetryPolicy,
    TurnMessageQueue TurnQueue,
    PiSessionChatHistoryProvider SessionHistory);
