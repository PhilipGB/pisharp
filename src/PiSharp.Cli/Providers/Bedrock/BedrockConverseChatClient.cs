using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.EventStreams;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Providers;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Cli;

internal static class BedrockConverseChatClientFactory
{
    public static IChatClient Create(ModelSelection selection)
    {
        var providerOptions = selection.Provider.Bedrock ??
            BedrockProviderOptions.FromEnvironment(Environment.GetEnvironmentVariable);
        var modelEndpoint = selection.Model.BaseUrl ?? selection.Connection.Endpoint?.ToString() ??
            selection.Provider.Endpoint.ToString();
        var model = selection.Model with { BaseUrl = modelEndpoint };
        var config = providerOptions.CreateClientConfig(model, selection.ApiKey);
        var runtime = new AmazonBedrockRuntimeClient(config);
        BedrockProviderWirePipelineCustomizer.Install(runtime);
        return new BedrockConverseChatClient(new AwsBedrockConverseTransport(runtime), model,
            providerOptions.EnablePromptCaching, providerOptions.OneHourPromptCache,
            providerOptions.ForcePromptCaching, providerOptions.Region);
    }
}

internal sealed class BedrockConverseChatClient(IBedrockConverseTransport transport, ModelDescriptor model,
    bool enablePromptCaching, bool oneHourCache, bool forcePromptCaching, string? region) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var updates = GetStreamingResponseAsync(messages, options, cancellationToken);
        return await updates.ToChatResponseAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = BedrockRequestMapper.Build(model, messages, options, enablePromptCaching,
            oneHourCache, forcePromptCaching, region);
        using var response = await transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseId = string.IsNullOrWhiteSpace(response.RequestId) ? Guid.NewGuid().ToString("N") : response.RequestId;
        var blocks = new Dictionary<int, ResponseBlock>();

        await foreach (var item in response.Events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (ProviderWireInvocation.Current is { } invocation)
                await invocation.ObserveStreamEventAsync("amazon-bedrock", model.Id,
                    "bedrock-converse-stream", GetEventType(item), ProjectEvent(item), cancellationToken)
                    .ConfigureAwait(false);

            switch (item)
            {
                case MessageStartEvent start when start.Role != ConversationRole.Assistant:
                    throw new InvalidDataException("Amazon Bedrock returned a non-assistant message start.");
                case ContentBlockStartEvent blockStart:
                    if (blockStart.Start?.ToolUse is { } toolUse)
                        blocks[blockStart.ContentBlockIndex ?? 0] = ResponseBlock.ForTool(toolUse.ToolUseId ?? "",
                            toolUse.Name ?? "");
                    break;
                case ContentBlockDeltaEvent blockDelta:
                    foreach (var update in HandleDelta(blockDelta, blocks, responseId, cancellationToken))
                        yield return update;
                    break;
                case ContentBlockStopEvent blockStop:
                    if (blocks.Remove(blockStop.ContentBlockIndex ?? 0, out var completedBlock))
                    {
                        FinalizeReasoning(completedBlock);
                        if (completedBlock.ToolName is not null)
                        {
                            var call = new FunctionCallContent(completedBlock.ToolId!, completedBlock.ToolName, new Dictionary<string, object?>());
                            ProviderToolArgumentChatClient.Normalize(call, completedBlock.ToolArguments.ToString());
                            if (call.Exception is null && call.Arguments is not null)
                                call.Arguments = ToDictionary(JsonSerializer.SerializeToElement(call.Arguments));
                            yield return CreateUpdate(responseId, [call]);
                        }
                    }
                    break;
                case MessageStopEvent messageStop:
                    var finish = MapFinishReason(messageStop.StopReason);
                    var finishUpdate = CreateUpdate(responseId, []);
                    finishUpdate.FinishReason = finish;
                    finishUpdate.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                    if (!string.IsNullOrWhiteSpace(messageStop.StopReason))
                        finishUpdate.AdditionalProperties["pisharp.bedrock.rawFinishReason"] = messageStop.StopReason;
                    yield return finishUpdate;
                    break;
                case ConverseStreamMetadataEvent metadata when metadata.Usage is { } usage:
                    var cacheReadTokens = usage.CacheReadInputTokens ?? 0;
                    var cacheWriteTokens = usage.CacheWriteInputTokens ?? 0;
                    var details = new UsageDetails
                    {
                        InputTokenCount = usage.InputTokens + cacheReadTokens,
                        OutputTokenCount = usage.OutputTokens,
                        CachedInputTokenCount = cacheReadTokens,
                        TotalTokenCount = usage.TotalTokens + cacheReadTokens + cacheWriteTokens
                    };
                    if (BedrockRequestMapper.ReadCacheWriteCounts(usage) is { } counts)
                        details.AdditionalCounts = new AdditionalPropertiesDictionary<long>(counts);
                    yield return CreateUpdate(responseId, [new UsageContent(details)]);
                    break;
            }
        }

        foreach (var block in blocks.Values)
            FinalizeReasoning(block);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => transport.Dispose();

    private IEnumerable<ChatResponseUpdate> HandleDelta(ContentBlockDeltaEvent eventValue,
        IDictionary<int, ResponseBlock> blocks, string responseId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var blockIndex = eventValue.ContentBlockIndex ?? 0;
        var delta = eventValue.Delta;
        if (delta?.Text is { } text)
        {
            var update = CreateUpdate(responseId, [new TextContent(text)]);
            return [update];
        }
        if (delta?.ToolUse is { } toolDelta)
        {
            if (!blocks.TryGetValue(blockIndex, out var toolBlock))
                blocks[blockIndex] = toolBlock = ResponseBlock.ForTool("", "");
            toolBlock.ToolArguments.Append(toolDelta.Input);
            return [];
        }
        if (delta?.ReasoningContent is { } reasoning)
        {
            if (!blocks.TryGetValue(blockIndex, out var reasoningBlock))
                blocks[blockIndex] = reasoningBlock = ResponseBlock.ForReasoning();
            if (!string.IsNullOrEmpty(reasoning.Text))
            {
                var content = new TextReasoningContent(reasoning.Text);
                reasoningBlock.ReasoningFragments.Add(content);
                reasoningBlock.ReasoningText.Append(reasoning.Text);
                return [CreateUpdate(responseId, [content])];
            }
            if (!string.IsNullOrEmpty(reasoning.Signature) && !reasoningBlock.IsRedacted)
                reasoningBlock.ReasoningSignature.Append(reasoning.Signature);
            if (reasoning.RedactedContent is { Length: > 0 })
            {
                reasoningBlock.IsRedacted = true;
                reasoningBlock.ReasoningSignature.Clear();
                reasoningBlock.RedactedContent.Write(reasoning.RedactedContent.ToArray());
                if (!reasoningBlock.RedactedPlaceholderSent)
                {
                    reasoningBlock.RedactedPlaceholderSent = true;
                    var placeholder = new TextReasoningContent("[Reasoning redacted]");
                    BedrockRequestMapper.MarkRedactedReasoning(placeholder);
                    reasoningBlock.ReasoningFragments.Add(placeholder);
                    return [CreateUpdate(responseId, [placeholder])];
                }
            }
        }
        return [];
    }

    private void FinalizeReasoning(ResponseBlock block)
    {
        if (block.ReasoningFragments.Count == 0) return;
        if (block.IsRedacted)
        {
            var payload = block.RedactedContent.ToArray();
            if (payload.Length == 0) return;
            var placeholder = block.ReasoningFragments[^1];
            placeholder.ProtectedData = Convert.ToBase64String(payload);
            BedrockRequestMapper.MarkRedactedReasoning(placeholder);
        }
        else if (block.ReasoningSignature.Length > 0)
        {
            block.ReasoningFragments[^1].ProtectedData = block.ReasoningSignature.ToString();
        }
    }

    private ChatResponseUpdate CreateUpdate(string responseId, IList<AIContent> contents)
    {
        return new ChatResponseUpdate(ChatRole.Assistant, contents)
        {
            ModelId = model.Id,
            MessageId = responseId,
            ResponseId = responseId
        };
    }

    private static ChatFinishReason MapFinishReason(string? reason) => reason switch
    {
        "end_turn" or "stop_sequence" => ChatFinishReason.Stop,
        "max_tokens" or "model_context_window_exceeded" => ChatFinishReason.Length,
        "tool_use" => ChatFinishReason.ToolCalls,
        null or "" => new ChatFinishReason("error"),
        _ => new ChatFinishReason("error")
    };

    private static Dictionary<string, object?> ToDictionary(JsonElement element) => element.EnumerateObject()
        .ToDictionary(property => property.Name, property => ConvertJsonValue(property.Value), StringComparer.Ordinal);

    private static object? ConvertJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ToDictionary(element),
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonValue).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static string GetEventType(IEventStreamEvent item) => item switch
    {
        MessageStartEvent => "messageStart",
        ContentBlockStartEvent => "contentBlockStart",
        ContentBlockDeltaEvent => "contentBlockDelta",
        ContentBlockStopEvent => "contentBlockStop",
        MessageStopEvent => "messageStop",
        ConverseStreamMetadataEvent => "metadata",
        _ => item.GetType().Name
    };

    private static JsonElement ProjectEvent(IEventStreamEvent item) => item switch
    {
        MessageStartEvent start => JsonSerializer.SerializeToElement(new { role = start.Role.ToString() }),
        ContentBlockStartEvent start => JsonSerializer.SerializeToElement(new
        {
            contentBlockIndex = start.ContentBlockIndex,
            toolUse = start.Start?.ToolUse is { } toolUse
                ? new { toolUseId = toolUse.ToolUseId, name = toolUse.Name }
                : null
        }),
        ContentBlockDeltaEvent delta => JsonSerializer.SerializeToElement(new
        {
            contentBlockIndex = delta.ContentBlockIndex,
            text = delta.Delta?.Text,
            toolUse = delta.Delta?.ToolUse is { } toolUse
                ? new { input = toolUse.Input?.ToString() }
                : null,
            reasoningContent = delta.Delta?.ReasoningContent is { } reasoning
                ? new { text = reasoning.Text, signature = reasoning.Signature, redactedContent = reasoning.RedactedContent }
                : null
        }),
        ContentBlockStopEvent stop => JsonSerializer.SerializeToElement(new
        {
            contentBlockIndex = stop.ContentBlockIndex
        }),
        MessageStopEvent stop => JsonSerializer.SerializeToElement(new { stopReason = stop.StopReason }),
        ConverseStreamMetadataEvent metadata => JsonSerializer.SerializeToElement(new
        {
            usage = metadata.Usage is { } usage
                ? new
                {
                    inputTokens = usage.InputTokens,
                    outputTokens = usage.OutputTokens,
                    totalTokens = usage.TotalTokens,
                    cacheReadInputTokens = usage.CacheReadInputTokens
                }
                : null
        }),
        _ => JsonSerializer.SerializeToElement(new
        {
            eventType = item.GetType().Name,
            value = item.ToString()
        })
    };

    private sealed class ResponseBlock
    {
        private ResponseBlock() { }

        public string? ToolId { get; private init; }
        public string? ToolName { get; private init; }
        public StringBuilder ToolArguments { get; } = new();
        public StringBuilder ReasoningText { get; } = new();
        public StringBuilder ReasoningSignature { get; } = new();
        public List<TextReasoningContent> ReasoningFragments { get; } = [];
        public MemoryStream RedactedContent { get; } = new();
        public bool IsRedacted { get; set; }
        public bool RedactedPlaceholderSent { get; set; }

        public static ResponseBlock ForTool(string id, string name) => new() { ToolId = id, ToolName = name };
        public static ResponseBlock ForReasoning() => new();
    }
}

internal interface IBedrockConverseTransport : IDisposable
{
    Task<BedrockConverseStreamResponse> SendAsync(ConverseStreamRequest request, CancellationToken cancellationToken);
}

internal sealed class BedrockConverseStreamResponse(IAsyncEnumerable<IEventStreamEvent> events, string? requestId,
    IDisposable? owner = null) : IDisposable
{
    public IAsyncEnumerable<IEventStreamEvent> Events { get; } = events;
    public string? RequestId { get; } = requestId;
    public void Dispose() => owner?.Dispose();
}

internal sealed class AwsBedrockConverseTransport(IAmazonBedrockRuntime runtime) : IBedrockConverseTransport
{
    public async Task<BedrockConverseStreamResponse> SendAsync(ConverseStreamRequest request,
        CancellationToken cancellationToken)
    {
        var response = await runtime.ConverseStreamAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Stream is null)
        {
            response.Dispose();
            throw new InvalidDataException("Amazon Bedrock returned an empty event stream.");
        }
        return new BedrockConverseStreamResponse(response.Stream, response.ResponseMetadata?.RequestId, response);
    }

    public void Dispose() => runtime.Dispose();
}
