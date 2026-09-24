using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Observes every actual provider request, including subsequent tool-loop model calls.</summary>
internal sealed class ObservedChatClient(IChatClient inner, Action<AgentLifecycleEvent> publish,
    ProviderRetryPolicy retryPolicy, Func<IEnumerable<ChatMessage>, IReadOnlyList<ChatMessage>> takeSteering,
    bool blockImages = false,
    Func<IReadOnlyList<ChatMessage>, bool, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? projectContext = null) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var original = WithSteering(messages).ToArray();
        var requestMessages = await PrepareRequestAsync(original, force: false, cancellationToken);
        var overflowRecovered = false;
        for (var retries = 0; ; retries++)
        {
            publish(new("model_request_started"));
            try
            {
                var response = await base.GetResponseAsync(requestMessages, options, cancellationToken);
                publish(new("model_request_completed"));
                return response;
            }
            catch (OperationCanceledException) { publish(new("model_request_interrupted")); throw; }
            catch (Exception error)
            {
                publish(new("model_request_failed", Error: error.Message));
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
        var original = WithSteering(messages).ToArray();
        var requestMessages = await PrepareRequestAsync(original, force: false, cancellationToken);
        var overflowRecovered = false;
        for (var retries = 0; ; retries++)
        {
            publish(new("model_request_started"));
            var ended = false;
            var producedOutput = false;
            Exception? failure = null;
            try
            {
                await using var enumerator = base.GetStreamingResponseAsync(requestMessages, options, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);
                while (true)
                {
                    bool next;
                    try { next = await enumerator.MoveNextAsync(); }
                    catch (OperationCanceledException) { ended = true; publish(new("model_request_interrupted")); throw; }
                    catch (Exception error) { failure = error; break; }
                    if (!next) break;
                    var update = enumerator.Current;
                    // A role/model/response-id SSE envelope has no content and must not
                    // suppress a safe retry before the provider emits text, reasoning or tools.
                    producedOutput |= update.Contents is { Count: > 0 } || !string.IsNullOrEmpty(update.Text);
                    if (!string.IsNullOrEmpty(update.Text)) publish(new("model_text_delta", Text: update.Text));
                    yield return update;
                }
                ended = true;
            }
            finally { if (!ended) publish(new("model_request_interrupted")); }

            if (failure is null)
            {
                publish(new("model_request_completed"));
                yield break;
            }

            publish(new("model_request_failed", Error: failure.Message));
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
        var projected = projectContext is null ? messages : await projectContext(messages, force, cancellationToken);
        return FilterImages(projected).ToArray();
    }

    private async Task<IReadOnlyList<ChatMessage>?> RecoverOverflowAsync(IReadOnlyList<ChatMessage> original,
        Exception error, CancellationToken cancellationToken)
    {
        if (projectContext is null || !IsContextOverflow(error)) return null;
        var compacted = await projectContext(original, true, cancellationToken);
        if (ReferenceEquals(compacted, original)) return null;
        publish(new("model_context_overflow_recovery", Text: "Retrying the model request once with a shortened context."));
        return FilterImages(compacted).ToArray();
    }

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
            message.Contains("exceeds the context window", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("exceeds the available context size", StringComparison.OrdinalIgnoreCase);
    }

    // Filter at the final provider boundary, including persisted history and subsequent tool-loop requests.
    // Never mutate the canonical messages: disabled images remain available if settings change later.
    private IEnumerable<ChatMessage> FilterImages(IEnumerable<ChatMessage> messages)
    {
        if (!blockImages) return messages;
        return messages.Select(message =>
        {
            if (message.Role != ChatRole.User && message.Role != ChatRole.Tool ||
                !message.Contents.OfType<DataContent>().Any(IsImage)) return message;
            var contents = new List<AIContent>();
            foreach (var content in message.Contents)
            {
                if (content is DataContent data && IsImage(data))
                {
                    if (contents.LastOrDefault() is not TextContent { Text: "Image reading is disabled." })
                        contents.Add(new TextContent("Image reading is disabled."));
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
}
