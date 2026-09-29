using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Sessions;
using PiSharp.Runtime.VirtualModels;

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
        var thinking = ThinkingLevels.ValidateForModel(thinkingLevel, selection.Model.Reasoning,
            selection.Model.ThinkingLevelMap);
        var settings = getSettings();
        var modelKey = $"{selection.Provider.Id}/{selection.Model.Id}";
        var policy = settings.ResolveCompactionPolicy(selection.Model.ContextLength, getEnvironment, modelKey);
        var keepRecentTokens = settings.ResolveCompactionKeepRecentTokens(modelKey);
        var pricing = ModelPricing.FromEnvironment(getEnvironment) ?? selection.Model.Pricing;
        return new PreparedModelRuntime(selection, thinking, selection.Connection, keepRecentTokens,
            ProviderChatClientFactory.Create(selection, settings.Retry?.Provider, settings.HttpIdleTimeoutMs), policy, pricing,
            CreateVirtualModelRouter(selection));
    }

    public VirtualModelRequestRouter? CreateVirtualModelRouter(ModelSelection logicalSelection)
    {
        if (logicalSelection.Model.Api != VirtualModelContract.Api) return null;
        var registry = providers.VirtualModels ?? throw new InvalidOperationException(
            $"Virtual model {logicalSelection.Provider.Id}/{logicalSelection.Model.Id} is not registered.");
        var registered = registry.Get(logicalSelection.Provider.Id, logicalSelection.Model.Id) ??
            throw new InvalidOperationException($"Virtual model {logicalSelection.Provider.Id}/{logicalSelection.Model.Id} is not registered.");
        return async request =>
        {
            var previous = await FindPreviousPhysicalAsync(request.Messages, request.CancellationToken).ConfigureAwait(false);
            var state = request.Session.ActiveVirtualModelState(logicalSelection.Provider.Id, logicalSelection.Model.Id);
            var routingRequest = new VirtualModelRouteRequest(logicalSelection.Model, request.ThinkingLevel,
                request.Reason, request.Messages, previous.Model, previous.ThinkingLevel,
                request.Failed?.Model, request.Failed?.ThinkingLevel, request.Failed?.Message, state);
            var route = await registry.RouteAsync(registered, routingRequest, request.CancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(route.Provider) || string.IsNullOrWhiteSpace(route.Model))
                throw new InvalidOperationException("Virtual model router returned an empty physical model identity.");
            var targetModel = await providers.FindPhysicalModelAsync(route.Provider, route.Model,
                request.CancellationToken).ConfigureAwait(false) ??
                throw new InvalidOperationException($"Virtual model {logicalSelection.Provider.Id}/{logicalSelection.Model.Id} routed to {route.Provider}/{route.Model}, which is not an available physical model.");
            var physicalSelection = await providers.ResolveAsync(route.Provider, route.Model,
                request.CancellationToken, includeOutOfScope: true).ConfigureAwait(false);
            if (!physicalSelection.Authenticated)
                throw new InvalidOperationException($"Virtual model {logicalSelection.Provider.Id}/{logicalSelection.Model.Id} routed to {route.Provider}/{route.Model}, which has no credentials.");
            var physical = physicalSelection with { Model = targetModel };
            var thinking = ThinkingLevels.ValidateForModel(route.ThinkingLevel, targetModel.Reasoning,
                targetModel.ThinkingLevelMap);
            var settings = getSettings();
            var policy = settings.ResolveCompactionPolicy(targetModel.ContextLength, getEnvironment,
                $"{physical.Provider.Id}/{targetModel.Id}");
            var pricing = ModelPricing.FromEnvironment(getEnvironment) ?? targetModel.Pricing;
            return new VirtualModelRequestRoute(
                ProviderChatClientFactory.Create(physical, settings.Retry?.Provider, settings.HttpIdleTimeoutMs),
                targetModel, physical.Provider.Id, thinking,
                ThinkingLevels.ToOptions(thinking, targetModel.ThinkingLevelMap), pricing, policy, route.State,
                logicalSelection.Provider.Id, logicalSelection.Model.Id);
        };
    }

    private async Task<(ModelDescriptor? Model, string? ThinkingLevel)> FindPreviousPhysicalAsync(
        IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var message in messages.Reverse())
        {
            if (message.Role != ChatRole.Assistant || message.AdditionalProperties is not { } properties) continue;
            var provider = PropertyString(properties, "pisharp.provider");
            var model = PropertyString(properties, "pisharp.model");
            if (provider is null || model is null) continue;
            var descriptor = await providers.FindPhysicalModelAsync(provider, model, cancellationToken).ConfigureAwait(false);
            if (descriptor is not null)
                return (descriptor, PropertyString(properties, "pisharp.thinkingLevel"));
        }
        return (null, null);
    }

    private static string? PropertyString(IDictionary<string, object?> properties, string name) =>
        properties.TryGetValue(name, out var value) ? value as string : null;

    public string ResolveThinkingLevelForModelSwitch(ModelSelection selection, string currentLevel)
    {
        var settings = getSettings();
        var preferred = settings.GetModelThinkingLevel(selection) ?? settings.DefaultThinkingLevel ?? currentLevel;
        return ThinkingLevels.ValidateForModel(preferred, selection.Model.Reasoning,
            selection.Model.ThinkingLevelMap);
    }

    public async Task ApplySelectionAsync(ModelSelection selection, string thinkingLevel,
        PiAgent activeAgent, ConversationRun activeRun, Action<PreparedModelRuntime> updateCurrent,
        Func<PreparedModelRuntime, Task> replaceIdle)
    {
        var prepared = Prepare(selection, thinkingLevel);
        if (activeRun.TrySetModelDuringRun(prepared.Selection.Model.Id,
            prepared.Connection.Endpoint?.ToString(), prepared.Selection.Provider.Id,
            prepared.Pricing, prepared.ContextPolicy, prepared.KeepRecentTokens, prepared.Thinking, prepared.ReasoningOptions,
            () => activeAgent.SetModelRuntime(prepared.ChatClient, prepared.SupportsImages, prepared.ImageResizeOptions,
                prepared.VirtualModelRouter)))
        {
            updateCurrent(prepared);
            return;
        }
        await replaceIdle(prepared);
    }
}

internal sealed record PreparedModelRuntime(ModelSelection Selection, string Thinking,
    ConnectionSettings Connection, int KeepRecentTokens, IChatClient ChatClient,
    AutoCompactionPolicy? ContextPolicy, ModelPricing? Pricing, VirtualModelRequestRouter? VirtualModelRouter)
{
    public bool SupportsImages => Selection.Model.Input?.Contains("image", StringComparer.Ordinal) != false;
    public ModelImageResizeOptions? ImageResizeOptions => Selection.Model.InputLimits?.Images?.Resize;
    public ReasoningOptions? ReasoningOptions => ThinkingLevels.ToOptions(Thinking, Selection.Model.ThinkingLevelMap);
}
