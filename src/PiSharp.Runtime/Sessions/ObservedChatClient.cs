using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Tools;

namespace PiSharp.Runtime.Sessions;

/// <summary>Observes every actual provider request, including subsequent tool-loop model calls.</summary>
internal sealed class ObservedChatClient(IChatClient inner, Action<AgentLifecycleEvent> publish,
    ProviderRetryPolicy retryPolicy, Func<IEnumerable<ChatMessage>, IReadOnlyList<ChatMessage>> takeSteering,
    bool blockImages = false,
    Func<IReadOnlyList<ChatMessage>, bool, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? projectContext = null,
    bool supportsImages = true, Func<ReasoningOptions?>? getReasoning = null,
    Func<bool>? getSupportsImages = null, IProviderToolCallDeltaSource? toolCallDeltaSource = null,
    Func<IReadOnlyList<AITool>>? getToolsForRequest = null, RoutedChatClient? routedChatClient = null) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        publish(new("model_request_started"));
        var original = WithSteering(messages).ToArray();
        var requestMessages = await PrepareRequestAsync(original, force: false, cancellationToken);
        options = ApplyCurrentToolLoadout(ApplyCurrentReasoning(options));
        var overflowRecovered = false;
        var routeReason = GetRouteReason(requestMessages);
        VirtualModelFailedRequest? failedRequest = null;
        for (var retries = 0; ; retries++)
        {
            var requestOptions = AddRouteHint(options, routeReason, failedRequest, overflowRecovered);
            try
            {
                var response = await base.GetResponseAsync(requestMessages, requestOptions, cancellationToken);
                publish(new("model_request_completed")
                {
                    ProviderResponse = response.Messages.LastOrDefault(),
                    ProviderUsage = response.Usage,
                    ProviderResponseId = response.ResponseId,
                    ProviderModelId = response.ModelId,
                    ProviderProviderId = VirtualModelRequestHints.ReadHint(requestOptions)?.Execution?.Route?.Provider,
                    ProviderFinishReason = response.FinishReason?.Value
                });
                return response;
            }
            catch (OperationCanceledException) { publish(new("model_request_interrupted")); throw; }
            catch (Exception error)
            {
                publish(new("model_request_failed", Error: error.Message) { ProviderException = error });
                failedRequest = FailedRequest(requestOptions, error);
                routeReason = "retry";
                if (!overflowRecovered && await RecoverOverflowAsync(original, error, cancellationToken) is { } shorter)
                {
                    requestMessages = shorter;
                    overflowRecovered = true;
                    continue;
                }
                if (!retryPolicy.CanRetry(error, retries, producedOutput: false)) throw;
                publish(new("model_retry_scheduled", Text: $"{retries + 1}/{retryPolicy.MaxRetries}", Error: error.Message));
                if (retryPolicy.Delay > TimeSpan.Zero) await Task.Delay(retryPolicy.Delay, cancellationToken);
            }
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        publish(new("model_request_started"));
        var original = WithSteering(messages).ToArray();
        var requestMessages = await PrepareRequestAsync(original, force: false, cancellationToken);
        options = ApplyCurrentToolLoadout(ApplyCurrentReasoning(options));
        var overflowRecovered = false;
        var routeReason = GetRouteReason(requestMessages);
        VirtualModelFailedRequest? failedRequest = null;
        for (var retries = 0; ; retries++)
        {
            var requestOptions = AddRouteHint(options, routeReason, failedRequest, overflowRecovered);
            var ended = false;
            var producedOutput = false;
            Exception? failure = null;
            var responseUpdates = new List<ChatResponseUpdate>();
            IProviderToolCallDeltaCapture? toolCallCapture = null;
            IAsyncEnumerator<ProviderToolCallDelta>? toolCallEnumerator = null;
            Task<bool>? toolCallMove = null;
            IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
            Exception? streamError = null;

            void PublishToolCallDelta()
            {
                producedOutput = true;
                publish(new AgentLifecycleEvent("model_content_update")
                {
                    StreamedToolCallDelta = toolCallEnumerator!.Current
                });
            }

            try
            {
                toolCallCapture = routedChatClient is { HasRouter: true }
                    ? routedChatClient.BeginToolCallDeltaCapture(requestOptions)
                    : toolCallDeltaSource?.BeginToolCallDeltaCapture();
                toolCallEnumerator = toolCallCapture?.ReadAllAsync(cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                toolCallMove = toolCallEnumerator?.MoveNextAsync().AsTask();

                try
                {
                    // Some SDK streams start the request while creating the enumerator, before
                    // MoveNextAsync. Keep that setup failure inside the provider-error boundary.
                    enumerator = base.GetStreamingResponseAsync(requestMessages, requestOptions, cancellationToken)
                        .GetAsyncEnumerator(cancellationToken);
                }
                catch (OperationCanceledException error)
                {
                    streamError = error;
                    ended = true;
                    publish(new("model_request_interrupted"));
                    throw;
                }
                catch (Exception error)
                {
                    streamError = error;
                    failure = toolCallCapture?.ResponseFailure ?? error;
                }

                try
                {
                    while (enumerator is not null)
                    {
                        bool next;
                        try
                        {
                            var responseMove = enumerator.MoveNextAsync().AsTask();
                            while (toolCallMove is not null)
                            {
                                var completed = await Task.WhenAny(responseMove, toolCallMove);
                                if (completed == responseMove) break;
                                if (await toolCallMove)
                                {
                                    PublishToolCallDelta();
                                    toolCallMove = toolCallEnumerator!.MoveNextAsync().AsTask();
                                }
                                else toolCallMove = null;
                            }
                            next = await responseMove;
                        }
                        catch (OperationCanceledException error)
                        {
                            streamError = error;
                            ended = true;
                            publish(new("model_request_interrupted"));
                            throw;
                        }
                        catch (Exception error)
                        {
                            streamError = error;
                            failure = toolCallCapture?.ResponseFailure ?? error;
                            while (toolCallMove is { IsCompletedSuccessfully: true })
                            {
                                if (!toolCallMove.Result) { toolCallMove = null; break; }
                                PublishToolCallDelta();
                                toolCallMove = toolCallEnumerator!.MoveNextAsync().AsTask();
                            }
                            break;
                        }
                        if (!next)
                        {
                            while (toolCallMove is not null)
                            {
                                if (!await toolCallMove) { toolCallMove = null; break; }
                                PublishToolCallDelta();
                                toolCallMove = toolCallEnumerator!.MoveNextAsync().AsTask();
                            }
                            break;
                        }
                        var update = enumerator.Current;
                        responseUpdates.Add(update);
                        while (toolCallMove is { IsCompletedSuccessfully: true })
                        {
                            if (!toolCallMove.Result) { toolCallMove = null; break; }
                            PublishToolCallDelta();
                            toolCallMove = toolCallEnumerator!.MoveNextAsync().AsTask();
                        }
                        // A role/model/response-id SSE envelope has no content and must not
                        // suppress a safe retry before the provider emits text, reasoning or tools.
                        producedOutput |= update.Contents?.Any(content => content is not UsageContent) == true ||
                            !string.IsNullOrEmpty(update.Text);
                        publish(new AgentLifecycleEvent(string.IsNullOrEmpty(update.Text)
                            ? "model_content_update" : "model_text_delta", Text: update.Text)
                        {
                            ProviderUpdate = update
                        });
                        yield return update;
                    }
                }
                finally
                {
                    try
                    {
                        if (enumerator is not null) await enumerator.DisposeAsync();
                    }
                    catch when (streamError is not null) { }
                    catch (Exception error)
                    {
                        streamError = error;
                        failure = toolCallCapture?.ResponseFailure ?? error;
                    }
                }
                ended = true;
            }
            finally
            {
                if (toolCallCapture is not null)
                {
                    // Complete the channel, then finish any pending MoveNext before disposing the
                    // async iterator. Concurrent MoveNextAsync/DisposeAsync can throw and mask the
                    // provider's original pre-content response failure.
                    toolCallCapture.Dispose();
                    while (toolCallMove is not null)
                    {
                        bool hasNext;
                        try { hasNext = await toolCallMove; }
                        catch when (streamError is not null || failure is not null)
                        {
                            toolCallMove = null;
                            break;
                        }
                        if (!hasNext)
                        {
                            toolCallMove = null;
                            break;
                        }
                        PublishToolCallDelta();
                        toolCallMove = toolCallEnumerator!.MoveNextAsync().AsTask();
                    }
                }
                if (toolCallEnumerator is not null)
                {
                    try { await toolCallEnumerator.DisposeAsync(); }
                    catch when (streamError is not null || failure is not null) { }
                }
                if (!ended) publish(new("model_request_interrupted"));
            }

            if (failure is null)
            {
                var response = responseUpdates.ToChatResponse();
                var physicalRoute = VirtualModelRequestHints.ReadHint(requestOptions)?.Execution?.Route;
                if (physicalRoute is not null) RoutedChatClient.ApplyPhysicalIdentity(response, physicalRoute);
                publish(new("model_request_completed")
                {
                    ProviderResponse = response.Messages.LastOrDefault(),
                    ProviderUsage = response.Usage,
                    ProviderResponseId = response.ResponseId,
                    ProviderModelId = response.ModelId,
                    ProviderProviderId = VirtualModelRequestHints.ReadHint(requestOptions)?.Execution?.Route?.Provider,
                    ProviderFinishReason = response.FinishReason?.Value
                });
                yield break;
            }

            publish(new("model_request_failed", Error: failure.Message) { ProviderException = failure });
            failedRequest = FailedRequest(requestOptions, failure);
            routeReason = "retry";
            if (!producedOutput && !overflowRecovered &&
                await RecoverOverflowAsync(original, failure, cancellationToken) is { } shorter)
            {
                requestMessages = shorter;
                overflowRecovered = true;
                continue;
            }
            if (!retryPolicy.CanRetry(failure, retries, producedOutput)) throw failure;
            publish(new("model_retry_scheduled", Text: $"{retries + 1}/{retryPolicy.MaxRetries}", Error: failure.Message));
            if (retryPolicy.Delay > TimeSpan.Zero) await Task.Delay(retryPolicy.Delay, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<ChatMessage>> PrepareRequestAsync(IReadOnlyList<ChatMessage> messages, bool force, CancellationToken cancellationToken)
    {
        // A routed request owns its budget after physical selection; earlier policies may describe a different model.
        var projected = projectContext is null || routedChatClient?.HasRouter == true
            ? messages : await projectContext(messages, force, cancellationToken);
        return FilterImages(AttachReadImages(AddMissingFunctionResults(projected))).ToArray();
    }

    private async Task<IReadOnlyList<ChatMessage>?> RecoverOverflowAsync(IReadOnlyList<ChatMessage> original,
        Exception error, CancellationToken cancellationToken)
    {
        if (!IsContextOverflow(error)) return null;
        if (routedChatClient?.HasRouter == true)
        {
            publish(new("model_context_overflow_recovery", Text: "Retrying with route-aware context compaction."));
            return original;
        }
        if (projectContext is null) return null;
        var compacted = await projectContext(original, true, cancellationToken);
        if (ReferenceEquals(compacted, original)) return null;
        publish(new("model_context_overflow_recovery", Text: "Retrying the model request once with a shortened context."));
        return FilterImages(AttachReadImages(AddMissingFunctionResults(compacted))).ToArray();
    }

    // Keep uncertain tool outcomes out of canonical history, but close incomplete calls in the
    // provider-only projection so adapters receive a valid conversation after a process restart.
    private static IReadOnlyList<ChatMessage> AddMissingFunctionResults(IReadOnlyList<ChatMessage> messages)
    {
        var projected = new List<ChatMessage>(messages.Count + 1);
        var pendingCallIds = new List<string>();
        var pendingCallSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Tool && pendingCallIds.Count > 0)
                AppendMissingResults();

            projected.Add(message);
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call && !string.IsNullOrEmpty(call.CallId) && pendingCallSet.Add(call.CallId))
                    pendingCallIds.Add(call.CallId);
                else if (content is FunctionResultContent result && pendingCallSet.Remove(result.CallId))
                    pendingCallIds.Remove(result.CallId);
            }
        }

        if (pendingCallIds.Count > 0)
            AppendMissingResults();

        return projected.Count == messages.Count ? messages : projected;

        void AppendMissingResults()
        {
            foreach (var callId in pendingCallIds)
                projected.Add(new ChatMessage(ChatRole.Tool,
                    [new FunctionResultContent(callId, "No result provided")]));
            pendingCallIds.Clear();
            pendingCallSet.Clear();
        }
    }

    private ChatOptions? ApplyCurrentReasoning(ChatOptions? options)
    {
        if (getReasoning is null) return options;
        var requestOptions = options?.Clone() ?? new ChatOptions();
        requestOptions.Reasoning = getReasoning();
        return requestOptions;
    }

    // Keep the persisted function result intact. Provider clients see its text plus image content
    // in a following user message, matching Pi's image-bearing tool result on the wire.
    private IReadOnlyList<ChatMessage> AttachReadImages(IReadOnlyList<ChatMessage> messages) =>
        ExpandReadImages(messages, getSupportsImages?.Invoke() ?? supportsImages, flattenStructuredResults: true);

    internal static IReadOnlyList<ChatMessage> NormalizeReadImagesForHistory(IReadOnlyList<ChatMessage> messages) =>
        ExpandReadImages(messages, supportsImages: true, flattenStructuredResults: false);

    private static IReadOnlyList<ChatMessage> ExpandReadImages(IReadOnlyList<ChatMessage> messages, bool supportsImages,
        bool flattenStructuredResults)
    {
        var expanded = new List<ChatMessage>(messages.Count);
        var index = 0;
        while (index < messages.Count)
        {
            if (messages[index].Role != ChatRole.Tool)
            {
                expanded.Add(messages[index++]);
                continue;
            }

            var attachments = new List<AIContent>();
            while (index < messages.Count && messages[index].Role == ChatRole.Tool)
            {
                var message = messages[index++];
                var contents = new List<AIContent>(message.Contents.Count);
                var replaced = false;
                foreach (var content in message.Contents)
                {
                    if (content is FunctionResultContent result && TryGetReadOutput(result.Result, out var output))
                    {
                        replaced = true;
                        var resultText = output.Text;
                        if (output.ImageMimeType is not null || output.ImageDataBase64 is not null)
                        {
                            if (!supportsImages)
                                resultText += "\n[Current model does not support images. The image will be omitted from this request.]";
                            else if (TryCreateImage(output, out var image))
                                attachments.Add(image);
                            else
                                resultText += "\n[Image content is unavailable.]";
                        }
                        contents.Add(new FunctionResultContent(result.CallId, resultText) { Exception = result.Exception });
                        continue;
                    }

                    if (flattenStructuredResults && content is FunctionResultContent structuredResult &&
                        ToolResultOutput.TryRead(structuredResult.Result, out var structuredText, out _))
                    {
                        replaced = true;
                        contents.Add(new FunctionResultContent(structuredResult.CallId, structuredText)
                        { Exception = structuredResult.Exception });
                        if (ToolResultOutput.TryReadContract(structuredResult.Result, out var contract) &&
                            contract.Images is { Count: > 0 })
                            attachments.AddRange(contract.Images.Select(image => image.ToDataContent()).OfType<DataContent>());
                    }
                    else contents.Add(content);
                }

                expanded.Add(replaced ? new ChatMessage(message.Role, contents) : message);
            }

            if (attachments.Count > 0)
                expanded.Add(new ChatMessage(ChatRole.User,
                    [new TextContent("Attached image(s) from tool result:"), .. attachments]));
        }
        return expanded;
    }

    private static bool TryGetReadOutput(object? value, out ReadToolOutput output)
        => ReadToolOutput.TryRead(value, out output);

    private static bool TryCreateImage(ReadToolOutput output, out DataContent image)
        => output.TryCreateImageContent(out image);

    private static bool IsContextOverflow(Exception error)
    {
        var status = error switch
        {
            ClientResultException result => result.Status,
            HttpRequestException request => (int?)request.StatusCode,
            _ => null
        };
        if (status is not (400 or 413)) return false;
        var message = error.Message;
        if (message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("too many requests", StringComparison.OrdinalIgnoreCase)) return false;
        return message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("context length exceeded", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("prompt exceeds max length", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("exceeds the context window", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("exceeds the available context size", StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(message,
                @"exceeds (?:the )?(?:model'?s )?maximum context length(?: of [\d,]+ tokens?|\s*\([\d,]+\))",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)) ||
            System.Text.RegularExpressions.Regex.IsMatch(message,
                @"prompt too long; exceeded (?:max )?context length",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    }

    private static string GetRouteReason(IReadOnlyList<ChatMessage> messages)
    {
        var lastAssistant = messages.ToList().FindLastIndex(message => message.Role == ChatRole.Assistant);
        return messages.Skip(lastAssistant + 1).Any(message => message.Role == ChatRole.User)
            ? "user" : "continuation";
    }

    private static VirtualModelFailedRequest? FailedRequest(ChatOptions? options, Exception error)
    {
        if (VirtualModelRequestHints.ReadHint(options)?.Execution?.Route is not { } route) return null;
        return new(route.Provider, route.Model, route.ThinkingLevel,
            new ChatMessage(ChatRole.Assistant, error.Message), error.Message);
    }

    private ChatOptions? AddRouteHint(ChatOptions? options, string reason,
        VirtualModelFailedRequest? failed, bool forceCompaction = false) => routedChatClient is { HasRouter: true }
        ? VirtualModelRequestHints.WithHint(options, new(reason, routedChatClient.SelectedThinkingLevel, failed, new VirtualModelRequestExecution(), ForceCompaction: forceCompaction))
        : options;

    // Filter at the final provider boundary, including persisted history and subsequent tool-loop requests.
    // Never mutate the canonical messages: disabled images remain available if settings change later.
    private IEnumerable<ChatMessage> FilterImages(IEnumerable<ChatMessage> messages) =>
        FilterImagesForModel(messages, blockImages, getSupportsImages?.Invoke() ?? supportsImages);

    internal static IReadOnlyList<ChatMessage> FilterImagesForModel(IEnumerable<ChatMessage> messages,
        bool blockImages, bool supportsImages)
    {
        if (!blockImages && supportsImages) return messages.ToArray();
        return messages.Select(message =>
        {
            if (message.Role != ChatRole.User && message.Role != ChatRole.Tool ||
                !message.Contents.OfType<DataContent>().Any(IsImage)) return message;
            var notice = blockImages ? "Image reading is disabled." :
                "Current model does not support images. The image will be omitted from this request.";
            var contents = new List<AIContent>();
            foreach (var content in message.Contents)
            {
                if (content is DataContent data && IsImage(data))
                {
                    if (contents.LastOrDefault() is not TextContent previous || previous.Text != notice)
                        contents.Add(new TextContent(notice));
                }
                else contents.Add(content);
            }
            return new ChatMessage(message.Role, contents);
        }).ToArray();
    }

    private static bool IsImage(DataContent content) =>
        content.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
    private IEnumerable<ChatMessage> WithSteering(IEnumerable<ChatMessage> messages)
    {
        var steering = takeSteering(messages);
        if (steering.Count == 0) return messages;
        if (messages is ICollection<ChatMessage> mutable && !mutable.IsReadOnly)
        {
            foreach (var message in steering) mutable.Add(message);
            return messages;
        }
        return messages.Concat(steering);
    }

    private ChatOptions? ApplyCurrentToolLoadout(ChatOptions? options)
    {
        if (getToolsForRequest is null) return options;
        var requestOptions = options?.Clone() ?? new ChatOptions();
        requestOptions.Tools = getToolsForRequest().ToList();
        return requestOptions;
    }
}
