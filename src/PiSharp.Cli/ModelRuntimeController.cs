using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli;

internal sealed class ModelRuntimeController(
    ProviderModelRuntime providers,
    Func<UserSettings> getSettings,
    Func<string, string?> getEnvironment)
{
    public async Task<ModelSelection> ResolveRpcModelAsync(ModelDescriptor model, CancellationToken cancellationToken)
    {
        var providerId = model.Provider ??
            throw new InvalidOperationException("The model catalogue did not identify the selected provider.");
        var provider = providers.GetProvider(providerId);
        var auth = await providers.ResolveAuthAsync(providerId, useRuntimeOverride: true, cancellationToken);
        return new ModelSelection(provider, model, auth.Key, auth.Authenticated, auth.Source);
    }

    public PreparedModelRuntime Prepare(ModelSelection selection, string thinkingLevel, bool requireAuthenticated = true)
    {
        if (requireAuthenticated && !selection.Authenticated)
            throw new InvalidOperationException($"Provider '{selection.Provider.Id}' is not authenticated. Use /login {selection.Provider.Id}.");
        var thinking = ThinkingLevels.ValidateForModel(thinkingLevel, selection.Model.Reasoning);
        var settings = getSettings();
        var policy = settings.ResolveCompactionPolicy(selection.Model.ContextLength, getEnvironment,
            $"{selection.Provider.Id}/{selection.Model.Id}");
        var pricing = ModelPricing.FromEnvironment(getEnvironment) ?? selection.Model.Pricing;
        return new PreparedModelRuntime(selection, thinking, selection.Connection,
            ProviderChatClientFactory.Create(selection), policy, pricing);
    }

    public async Task ApplySelectionAsync(ModelSelection selection, string thinkingLevel,
        PiAgent activeAgent, ConversationRun activeRun, Action<PreparedModelRuntime> updateCurrent,
        Func<PreparedModelRuntime, Task> replaceIdle)
    {
        var prepared = Prepare(selection, thinkingLevel);
        if (activeRun.TrySetModelDuringRun(prepared.Selection.Model.Id,
            prepared.Connection.Endpoint?.ToString(), prepared.Selection.Provider.Id,
            prepared.Pricing, prepared.ContextPolicy, prepared.Thinking, prepared.ReasoningOptions,
            () => activeAgent.SetModelRuntime(prepared.ChatClient, prepared.SupportsImages, prepared.ImageResizeOptions)))
        {
            updateCurrent(prepared);
            return;
        }
        await replaceIdle(prepared);
    }
}

internal sealed record PreparedModelRuntime(ModelSelection Selection, string Thinking,
    ConnectionSettings Connection, IChatClient ChatClient, AutoCompactionPolicy? ContextPolicy, ModelPricing? Pricing)
{
    public bool SupportsImages => Selection.Model.Input?.Contains("image", StringComparer.Ordinal) != false;
    public ModelImageResizeOptions? ImageResizeOptions => Selection.Model.InputLimits?.Images?.Resize;
    public ReasoningOptions? ReasoningOptions => ThinkingLevels.ToOptions(Thinking);
}
