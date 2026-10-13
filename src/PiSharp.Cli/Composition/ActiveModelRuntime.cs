using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Composition;

internal sealed class ActiveModelRuntime(ModelSelection selection, IChatClient chat, PiAgent agent,
    string thinking, AutoCompactionPolicy? contextPolicy, ModelPricing? pricing)
{
    public ModelSelection Selection { get; private set; } = selection;
    public ConnectionSettings Connection => Selection.Connection;
    public IChatClient Chat { get; private set; } = chat;
    public PiAgent Agent { get; private set; } = agent;
    public string Thinking { get; private set; } = thinking;
    public AutoCompactionPolicy? ContextPolicy { get; private set; } = contextPolicy;
    public ModelPricing? Pricing { get; private set; } = pricing;

    public void AdoptPrepared(PreparedModelRuntime prepared, PiAgent? replacementAgent = null)
    {
        Selection = prepared.Selection;
        Chat = prepared.ChatClient;
        if (replacementAgent is not null) Agent = replacementAgent;
        Thinking = prepared.Thinking;
        ContextPolicy = prepared.ContextPolicy;
        Pricing = prepared.Pricing;
    }

    public void AdoptProject(Sessions.ProjectSessionRuntime replacement)
    {
        Selection = replacement.Selection;
        Chat = replacement.Chat;
        Agent = replacement.Agent;
        Thinking = replacement.Thinking;
        ContextPolicy = replacement.ContextPolicy;
        Pricing = replacement.Pricing;
    }

    public void SetThinking(string level) => Thinking = level;
    public void SetContext(AutoCompactionPolicy? policy, ModelPricing? modelPricing)
    {
        ContextPolicy = policy;
        Pricing = modelPricing;
    }
    public void RestoreContextPolicy(AutoCompactionPolicy? policy) => ContextPolicy = policy;
    public void ReplaceAgent(PiAgent replacement) => Agent = replacement;
}
