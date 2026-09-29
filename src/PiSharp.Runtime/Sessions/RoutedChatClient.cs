using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Providers;

namespace PiSharp.Runtime.Sessions;

/// <summary>Routes logical virtual-model calls before they reach a physical provider client.</summary>
internal sealed class RoutedChatClient(MutableChatClient inner, Action<AgentLifecycleEvent> publish)
    : DelegatingChatClient(inner), IProviderToolCallDeltaSource
{
    private VirtualModelRequestRouter? _router;
    private ConversationSession? _session;
    private Func<CancellationToken, Task>? _save;
    private string _thinkingLevel = "off";
    private VirtualModelRequestRoute? _currentRoute;

    public VirtualModelRequestRoute? CurrentRoute => Volatile.Read(ref _currentRoute);
    public bool HasRouter => Volatile.Read(ref _router) is not null;

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

    public void SetThinkingLevel(string level) => Volatile.Write(ref _thinkingLevel, level);

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var requestMessages = messages.ToArray();
        var (route, routedMessages, routedOptions) = await PrepareRouteAsync(requestMessages, options, cancellationToken);
        var response = await base.GetResponseAsync(routedMessages, routedOptions, cancellationToken);
        return route is null ? response : ApplyPhysicalIdentity(response, route);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestMessages = messages.ToArray();
        var (route, routedMessages, routedOptions) = await PrepareRouteAsync(requestMessages, options, cancellationToken);
        await foreach (var update in base.GetStreamingResponseAsync(routedMessages, routedOptions, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (route is not null && string.IsNullOrWhiteSpace(update.ModelId)) update.ModelId = route.Model.Id;
            yield return update;
        }
    }

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        Volatile.Read(ref _router) is not null && serviceType == typeof(VirtualModelRequestRoute)
            ? CurrentRoute
            : base.GetService(serviceType, serviceKey);

    public IProviderToolCallDeltaCapture? BeginToolCallDeltaCapture() =>
        inner.BeginToolCallDeltaCapture();

    private async Task<(VirtualModelRequestRoute? Route, IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> PrepareRouteAsync(
        IReadOnlyList<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
    {
        var router = Volatile.Read(ref _router);
        if (router is null)
        {
            Volatile.Write(ref _currentRoute, null);
            return (null, messages, options);
        }
        var session = Volatile.Read(ref _session) ??
            throw new InvalidOperationException("Virtual model requests require an active conversation session.");
        var hint = VirtualModelRequestHints.ReadHint(options);
        Volatile.Write(ref _currentRoute, null);
        var route = await router(new VirtualModelRequestContext(session, messages,
            hint?.Reason ?? "direct", hint?.ThinkingLevel ?? Volatile.Read(ref _thinkingLevel),
            hint?.Failed, cancellationToken)).ConfigureAwait(false);
        inner.SetClient(route.ChatClient);
        Volatile.Write(ref _currentRoute, route);
        if (route.State is { } state && (hint?.Reason ?? "direct") != "direct")
        {
            session.AppendVirtualModelState(route.LogicalProvider ?? session.Provider ?? "",
                route.LogicalModel ?? session.Model, state);
            if (Volatile.Read(ref _save) is { } save) await save(cancellationToken).ConfigureAwait(false);
        }
        publish(new AgentLifecycleEvent("model_request_routed")
        {
            ProviderModelId = route.Model.Id,
            ProviderProviderId = route.Provider,
            ProviderPricing = route.Pricing,
            ProviderContextPolicy = route.ContextPolicy,
            ProviderThinkingLevel = route.ThinkingLevel
        });
        var routedMessages = ObservedChatClient.FilterImagesForModel(messages, blockImages: false,
            supportsImages: route.Model.Input?.Contains("image", StringComparer.Ordinal) != false);
        return (route, routedMessages, VirtualModelRequestHints.ForPhysicalModel(options, route));
    }

    private static ChatResponse ApplyPhysicalIdentity(ChatResponse response, VirtualModelRequestRoute route)
    {
        response.ModelId = route.Model.Id;
        foreach (var message in response.Messages.Where(message => message.Role == ChatRole.Assistant))
        {
            var properties = new AdditionalPropertiesDictionary();
            if (message.AdditionalProperties is { } existing)
                foreach (var (key, value) in existing) properties[key] = value;
            properties["pisharp.provider"] = route.Provider;
            properties["pisharp.model"] = route.Model.Id;
            properties["pisharp.thinkingLevel"] = route.ThinkingLevel;
            properties["pisharp.api"] = route.Model.Api;
            message.AdditionalProperties = properties;
        }
        return response;
    }
}
