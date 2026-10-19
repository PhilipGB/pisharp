using System.Text.Json;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal delegate ValueTask<JsonElement?> RegisteredBeforeProviderRequestHandler(
    PiSharpProviderRequestEvent eventData, PiSharpExtensionContext context, CancellationToken cancellationToken);
internal delegate ValueTask<IReadOnlyDictionary<string, string?>?> RegisteredBeforeProviderHeadersHandler(
    PiSharpProviderHeadersEvent eventData, PiSharpExtensionContext context, CancellationToken cancellationToken);
internal delegate ValueTask RegisteredAfterProviderResponseHandler(
    PiSharpProviderResponseEvent eventData, PiSharpExtensionContext context, CancellationToken cancellationToken);
internal delegate ValueTask RegisteredProviderStreamEventHandler(
    PiSharpProviderStreamEvent eventData, PiSharpExtensionContext context, CancellationToken cancellationToken);

public sealed partial class ExtensionRegistration
{
    private readonly List<ExtensionHandlerRegistration<RegisteredBeforeProviderRequestHandler>>
        _beforeProviderRequestHandlers = [];
    private readonly List<ExtensionHandlerRegistration<RegisteredBeforeProviderHeadersHandler>>
        _beforeProviderHeadersHandlers = [];
    private readonly List<ExtensionHandlerRegistration<RegisteredAfterProviderResponseHandler>>
        _afterProviderResponseHandlers = [];
    private readonly List<ExtensionHandlerRegistration<RegisteredProviderStreamEventHandler>>
        _providerStreamEventHandlers = [];

    internal IReadOnlyList<ExtensionHandlerRegistration<RegisteredBeforeProviderRequestHandler>>
        BeforeProviderRequestHandlers => _beforeProviderRequestHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<RegisteredBeforeProviderHeadersHandler>>
        BeforeProviderHeadersHandlers => _beforeProviderHeadersHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<RegisteredAfterProviderResponseHandler>>
        AfterProviderResponseHandlers => _afterProviderResponseHandlers.ToArray();
    internal IReadOnlyList<ExtensionHandlerRegistration<RegisteredProviderStreamEventHandler>>
        ProviderStreamEventHandlers => _providerStreamEventHandlers.ToArray();

    internal void AddBeforeProviderRequestHandler(RegisteredBeforeProviderRequestHandler handler) =>
        Add(_beforeProviderRequestHandlers, handler);
    internal void AddBeforeProviderHeadersHandler(RegisteredBeforeProviderHeadersHandler handler) =>
        Add(_beforeProviderHeadersHandlers, handler);
    internal void AddAfterProviderResponseHandler(RegisteredAfterProviderResponseHandler handler) =>
        Add(_afterProviderResponseHandlers, handler);
    internal void AddProviderStreamEventHandler(RegisteredProviderStreamEventHandler handler) =>
        Add(_providerStreamEventHandlers, handler);
}
