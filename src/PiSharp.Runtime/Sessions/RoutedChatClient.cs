using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;
using PiSharp.Runtime.Providers;

namespace PiSharp.Runtime.Sessions;

/// <summary>Routes logical virtual-model calls before they reach a physical provider client.</summary>
internal sealed class RoutedChatClient(MutableChatClient inner, Action<AgentLifecycleEvent> publish,
    Action<VirtualModelRequestRoute>? physicalResponse = null,
    NativeExtensionEventDispatcher? contextMutationDispatcher = null, bool directBlockImages = false,
    Func<bool>? directSupportsImages = null)
    : DelegatingChatClient(inner), IProviderToolCallDeltaSource
{
    private VirtualModelRequestRouter? _router;
    private ConversationSession? _session;
    private Func<CancellationToken, Task>? _save;
    private string _thinkingLevel = "off";
    private VirtualModelRequestRoute? _currentRoute;
    private Func<IReadOnlyList<ChatMessage>, VirtualModelRequestRoute, bool, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? _prepareContext;

    public void SetContextPreparation(Func<IReadOnlyList<ChatMessage>, VirtualModelRequestRoute, bool, CancellationToken,
        Task<IReadOnlyList<ChatMessage>>>? prepare) => Volatile.Write(ref _prepareContext, prepare);

    public VirtualModelRequestRoute? CurrentRoute => Volatile.Read(ref _currentRoute);
    public string SelectedThinkingLevel => Volatile.Read(ref _thinkingLevel);
    public bool HasRouter => Volatile.Read(ref _router) is not null;
    public bool HasContextMutationHandlers => contextMutationDispatcher?.HasContextMutationHandlers == true;

    public void SetRouter(VirtualModelRequestRouter? router)
    {
        Volatile.Write(ref _router, router);
        if (router is null) Volatile.Write(ref _currentRoute, null);
    }

    public void SetSession(ConversationSession session, string thinkingLevel, Func<CancellationToken, Task>? save)
    {
        Volatile.Write(ref _session, session);
        Volatile.Write(ref _thinkingLevel, thinkingLevel);
        Volatile.Write(ref _save, save);
    }

    public ChatOptions CreateDirectRequestOptions(IReadOnlyList<ChatMessage> messages) => VirtualModelRequestHints.WithHint(null,
        new("direct", Volatile.Read(ref _thinkingLevel), Execution: new VirtualModelRequestExecution(), RoutingMessages: messages));

    public void SetThinkingLevel(string level) => Volatile.Write(ref _thinkingLevel, level);

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var requestMessages = messages.ToArray();
        var (route, routedMessages, routedOptions) = await PrepareRouteAsync(requestMessages, options, cancellationToken);
        var response = await (route?.ChatClient ?? inner).GetResponseAsync(routedMessages, routedOptions, cancellationToken);
        if (route is not null && VirtualModelRequestHints.ReadHint(options)?.Reason is not "direct") physicalResponse?.Invoke(route);
        return route is null ? response : ApplyPhysicalIdentity(response, route);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestMessages = messages.ToArray();
        var (route, routedMessages, routedOptions) = await PrepareRouteAsync(requestMessages, options, cancellationToken);
        await foreach (var update in (route?.ChatClient ?? inner).GetStreamingResponseAsync(routedMessages, routedOptions, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (route is not null)
            {
                update.ModelId = route.Model.Id;
                update.AdditionalProperties = PhysicalProperties(update.AdditionalProperties, route);
            }
            yield return update;
        }
        if (route is not null && VirtualModelRequestHints.ReadHint(options)?.Reason is not "direct") physicalResponse?.Invoke(route);
    }

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        Volatile.Read(ref _router) is not null && serviceType == typeof(VirtualModelRequestRoute)
            ? CurrentRoute
            : base.GetService(serviceType, serviceKey);

    public IProviderToolCallDeltaCapture? BeginToolCallDeltaCapture() =>
        inner.BeginToolCallDeltaCapture();

    public IProviderToolCallDeltaCapture? BeginToolCallDeltaCapture(ChatOptions? options)
    {
        if (VirtualModelRequestHints.ReadHint(options)?.Execution is not { } execution)
            return inner.BeginToolCallDeltaCapture();
        return execution.ToolCallCapture = new RoutedToolCallDeltaCapture();
    }

    private async Task<(VirtualModelRequestRoute? Route, IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> PrepareRouteAsync(
        IReadOnlyList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        var router = Volatile.Read(ref _router);
        if (router is null)
        {
            Volatile.Write(ref _currentRoute, null);
            var (directMessages, directOptions) = await ApplyContextMutationsAsync(messages, options,
                cancellationToken).ConfigureAwait(false);
            var filteredMessages = ObservedChatClient.FilterImagesForModel(directMessages, directBlockImages,
                directSupportsImages?.Invoke() ?? true);
            return (null, filteredMessages, NativeContextMutationRequest.Strip(directOptions));
        }
        var session = Volatile.Read(ref _session) ??
            throw new InvalidOperationException("Virtual model requests require an active conversation session.");
        var hint = VirtualModelRequestHints.ReadHint(options);
        if (hint?.Reason is not null and not "direct") Volatile.Write(ref _currentRoute, null);
        VirtualModelRequestRoute route;
        try
        {
            route = await router(new VirtualModelRequestContext(session, hint?.RoutingMessages ?? messages,
            hint?.Reason ?? "direct", hint?.ThinkingLevel ?? Volatile.Read(ref _thinkingLevel),
                hint?.Failed, cancellationToken)).ConfigureAwait(false);
        }
        catch
        {
            hint?.Execution?.ToolCallCapture?.Bind(null);
            throw;
        }
        if (hint?.Execution is { } execution) execution.Route = route;
        if (hint?.Reason is not null and not "direct") Volatile.Write(ref _currentRoute, route);
        if (route.State is { } state && (hint?.Reason ?? "direct") != "direct")
        {
            session.AppendVirtualModelState(route.LogicalProvider ?? session.Provider ?? "",
                route.LogicalModel ?? session.Model, state);
            if (Volatile.Read(ref _save) is { } save) await save(cancellationToken).ConfigureAwait(false);
        }
        if (hint?.Reason is not null and not "direct") publish(new AgentLifecycleEvent("model_request_routed")
        {
            ProviderModelId = route.Model.Id,
            ProviderProviderId = route.Provider,
            ProviderApi = route.Model.Api,
            ProviderPricing = route.Pricing,
            ProviderContextPolicy = route.ContextPolicy,
            ProviderThinkingLevel = route.ThinkingLevel
        });
        if (hint?.Reason is not null and not "direct" && Volatile.Read(ref _prepareContext) is { } prepare)
            messages = await prepare(messages, route, hint.ForceCompaction, cancellationToken).ConfigureAwait(false);
        var (contextMessages, contextOptions) = await ApplyContextMutationsAsync(messages, options,
            cancellationToken).ConfigureAwait(false);
        if (hint?.Execution?.ToolCallCapture is { } capture)
            capture.Bind((route.ChatClient as IProviderToolCallDeltaSource)?.BeginToolCallDeltaCapture());
        var routedMessages = ObservedChatClient.FilterImagesForModel(contextMessages, blockImages: false,
            supportsImages: route.Model.Input?.Contains("image", StringComparer.Ordinal) != false);
        var physicalOptions = NativeContextMutationRequest.Strip(contextOptions);
        return (route, routedMessages, VirtualModelRequestHints.ForPhysicalModel(physicalOptions, route));
    }

    private async Task<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> ApplyContextMutationsAsync(
        IReadOnlyList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        if (contextMutationDispatcher?.HasContextMutationHandlers != true)
            return (messages, options);

        var tools = options?.Tools?.ToArray() ?? [];
        var toolNames = tools.OfType<AIFunctionDeclaration>().Select(tool => tool.Name).ToArray();
        var scope = NativeContextMutationRequest.From(options);
        NativeContextMutationResult result;
        if (scope is not null && scope.TryGet(messages, options?.Instructions, toolNames, out result))
        {
            // The previous attempt already ran extension code for this exact logical request.
        }
        else
        {
            result = await contextMutationDispatcher.DispatchContextMutationsAsync(messages,
                options?.Instructions, tools, cancellationToken).ConfigureAwait(false);
            scope?.Store(messages, options?.Instructions, toolNames, result);
        }

        if (ReferenceEquals(result.Messages, messages) &&
            string.Equals(result.SystemInstructions, options?.Instructions, StringComparison.Ordinal))
            return (result.Messages, options);

        var updated = options?.Clone() ?? new ChatOptions();
        updated.Instructions = result.SystemInstructions;
        var instructionsChanged = !string.Equals(result.SystemInstructions, options?.Instructions,
            StringComparison.Ordinal);
        var removedSystemContext = result.SystemInstructions is null &&
            !result.Messages.Any(message => message.Role == ChatRole.System) &&
            (options?.Instructions is not null || messages.Any(message => message.Role == ChatRole.System));
        if (updated.AdditionalProperties?.TryGetValue("pisharp.toolTranscript", out var transcriptValue) == true &&
            transcriptValue is JsonElement { ValueKind: JsonValueKind.Array } transcript &&
            (instructionsChanged || result.MessageStructureChanged || removedSystemContext))
        {
            updated.AdditionalProperties = updated.AdditionalProperties.Clone();
            var updatedTranscript = ToolDeclarationTranscript.ApplyContextMutation(transcript, messages, result.Messages,
                result.SystemInstructions, instructionsChanged, result.MessageStructureChanged, removedSystemContext);
            if (updatedTranscript is { } transcriptElement)
                updated.AdditionalProperties["pisharp.toolTranscript"] = transcriptElement;
            else
                updated.AdditionalProperties.Remove("pisharp.toolTranscript");
        }
        return (result.Messages, updated);
    }

    internal static ChatResponse ApplyPhysicalIdentity(ChatResponse response, VirtualModelRequestRoute route)
    {
        response.ModelId = route.Model.Id;
        foreach (var message in response.Messages.Where(message => message.Role == ChatRole.Assistant))
        {
            message.AdditionalProperties = PhysicalProperties(message.AdditionalProperties, route);
        }
        return response;
    }

    private static AdditionalPropertiesDictionary PhysicalProperties(AdditionalPropertiesDictionary? existing,
        VirtualModelRequestRoute route)
    {
        var properties = existing?.Clone() ?? new AdditionalPropertiesDictionary();
        properties["pisharp.provider"] = route.Provider;
        properties["pisharp.model"] = route.Model.Id;
        properties["pisharp.thinkingLevel"] = route.ThinkingLevel;
        properties["pisharp.api"] = route.Model.Api;
        return properties;
    }

}
