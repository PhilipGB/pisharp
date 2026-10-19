using System.Net.Http.Headers;
using System.Text.Json;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal sealed partial class NativeExtensionEventDispatcher
{
    internal async Task<JsonElement> DispatchProviderRequestAsync(PiSharpProviderRequestEvent eventData,
        CancellationToken cancellationToken)
    {
        var payload = eventData.Payload;
        foreach (var handler in registration.BeforeProviderRequestHandlers)
        {
            var (success, replacement) = await InvokeProviderHandlerAsync(
                token => handler.Handler(new(eventData.ProviderId, eventData.ModelId, eventData.Protocol, payload),
                    context, token), handler.SourceInfo, "before_provider_request", cancellationToken)
                .ConfigureAwait(false);
            if (!success || replacement is not { } value) continue;
            if (value.ValueKind == JsonValueKind.Undefined)
            {
                Report(new InvalidDataException("Provider payload replacement is undefined."),
                    handler.SourceInfo, "before_provider_request", null);
                continue;
            }
            if (eventData.Protocol == "bedrock-converse-stream" && value.ValueKind != JsonValueKind.Object)
            {
                Report(new InvalidDataException("Bedrock ConverseStream payload replacement must be a JSON object."),
                    handler.SourceInfo, "before_provider_request", null);
                continue;
            }
            payload = value.Clone();
        }
        return payload;
    }

    internal async Task<IReadOnlyDictionary<string, string?>> DispatchProviderHeadersAsync(
        PiSharpProviderHeadersEvent eventData, CancellationToken cancellationToken)
    {
        var sharedEvent = new PiSharpProviderHeadersEvent(eventData.ProviderId, eventData.ModelId, eventData.Protocol,
            new Dictionary<string, string?>(eventData.Headers, StringComparer.OrdinalIgnoreCase));
        var headers = sharedEvent.Headers;
        foreach (var handler in registration.BeforeProviderHeadersHandlers)
        {
            var before = new Dictionary<string, string?>(headers, StringComparer.OrdinalIgnoreCase);
            _ = await InvokeProviderHandlerAsync(async token =>
            {
                await handler.Handler(sharedEvent, context, token).ConfigureAwait(false);
                return true;
            }, handler.SourceInfo, "before_provider_headers", cancellationToken)
                .ConfigureAwait(false);

            var invalid = false;
            foreach (var (name, value) in headers.ToArray())
            {
                if (!IsValidProviderHeader(name, value))
                {
                    invalid = true;
                    if (before.TryGetValue(name, out var previous)) headers[name] = previous;
                    else headers.Remove(name);
                    continue;
                }
                if (value is null) headers.Remove(name);
            }
            if (invalid)
                Report(new InvalidDataException("Provider header mutation contains an invalid name or value."),
                    handler.SourceInfo, "before_provider_headers", null);
        }
        return new Dictionary<string, string?>(headers, StringComparer.OrdinalIgnoreCase);
    }

    internal Task DispatchProviderResponseAsync(PiSharpProviderResponseEvent eventData,
        CancellationToken cancellationToken) => DispatchProviderObserversAsync(
            registration.AfterProviderResponseHandlers,
            handler => token => handler.Handler(eventData, context, token),
            "after_provider_response", cancellationToken);

    internal Task DispatchProviderStreamEventAsync(PiSharpProviderStreamEvent eventData,
        CancellationToken cancellationToken) => DispatchProviderObserversAsync(
            registration.ProviderStreamEventHandlers,
            handler => token => handler.Handler(eventData, context, token),
            "provider_stream_event", cancellationToken);

    private async Task DispatchProviderObserversAsync<THandler>(
        IReadOnlyList<ExtensionHandlerRegistration<THandler>> handlers,
        Func<ExtensionHandlerRegistration<THandler>, Func<CancellationToken, ValueTask>> invoke,
        string eventType, CancellationToken cancellationToken) where THandler : Delegate
    {
        foreach (var handler in handlers)
            _ = await InvokeProviderHandlerAsync(async token =>
            {
                await invoke(handler)(token).ConfigureAwait(false);
                return true;
            }, handler.SourceInfo, eventType, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Success, TResult? Result)> InvokeProviderHandlerAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> invoke, PiSharp.Runtime.Resources.ResourceSourceInfo sourceInfo,
        string eventType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<TResult> pending;
        try
        {
            pending = invoke(cancellationToken).AsTask();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            Report(error, sourceInfo, eventType, null);
            return (false, default);
        }

        try
        {
            return (true, await pending.WaitAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { await pending.ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception error) { Report(error, sourceInfo, eventType, null); }
            throw;
        }
        catch (Exception error)
        {
            Report(error, sourceInfo, eventType, null);
            return (false, default);
        }
    }

    private static bool IsValidProviderHeader(string? name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(character => !IsHeaderTokenCharacter(character))) return false;
        if (value is not null && (value.Contains('\r') || value.Contains('\n'))) return false;
        using var request = new HttpRequestMessage();
        if (request.Headers.TryAddWithoutValidation(name, value ?? "")) return true;
        using var content = new ByteArrayContent([]);
        return content.Headers.TryAddWithoutValidation(name, value ?? "");
    }

    private static bool IsHeaderTokenCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
}
