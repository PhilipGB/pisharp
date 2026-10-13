using PiSharp.Cli.Sessions;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Composition;

internal sealed class ConversationRunComposition(ActiveProjectRuntime projectBinding, ModelRuntimeController modelRuntimeController)
{
    public Task<ConversationRun> OpenAsync(ActiveModelRuntime modelBinding, PiAgent runningAgent, ConversationSession session, string? path,
        string? runProvider = null, string? reasoningLevel = null,
        ProjectRuntimeContext? targetProject = null,
        IReadOnlyList<string>? activeToolNamesOverride = null, PreparedModelRuntime? targetRuntime = null)
    {
        var runProject = targetProject ?? projectBinding.Runtime;
        var runModel = targetRuntime?.Selection ?? modelBinding.Selection;
        var runSettings = targetProject is null ? projectBinding.Settings : targetProject.Settings;
        var runCompaction = targetRuntime is not null ? targetRuntime.ContextPolicy : targetProject is null ? modelBinding.ContextPolicy : runSettings.ResolveCompactionPolicy(runModel.Model.ContextLength,
            Environment.GetEnvironmentVariable, $"{runModel.Provider.Id}/{runModel.Model.Id}");
        var runPricing = targetRuntime is not null ? targetRuntime.Pricing : targetProject is null ? modelBinding.Pricing :
            ModelPricing.FromEnvironment(Environment.GetEnvironmentVariable) ?? runModel.Model.Pricing;
        return ConversationRun.OpenAsync(runningAgent, session, save: path is null ? null :
            token => runProject.Store.SaveAsync(session, path, token), autoCompaction: runCompaction, pricing: runPricing,
            sessionFile: path, provider: runProvider ?? runModel.Provider.Id, reasoningLevel: reasoningLevel ?? modelBinding.Thinking,
            retryPolicy: runSettings.Retry?.ResolvePolicy() ?? AgentRunRetryPolicy.Default,
            steeringMode: runSettings.SteeringMode ?? PromptDeliveryMode.OneAtATime,
            followUpMode: runSettings.FollowUpMode ?? PromptDeliveryMode.OneAtATime,
            autoCompactionEnabled: runSettings.AutoCompactionEnabled(Environment.GetEnvironmentVariable),
            keepRecentTokens: runSettings.ResolveCompactionKeepRecentTokens(
                $"{runModel.Provider.Id}/{runModel.Model.Id}"),
            physicalContextResolver: modelRuntimeController.ResolvePhysicalContextAsync,
            providerApi: ProviderChatClientFactory.ResolveProtocol(runModel),
            activeToolNamesOverride: activeToolNamesOverride);
    }
}
