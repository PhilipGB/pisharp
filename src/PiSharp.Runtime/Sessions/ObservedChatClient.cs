using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Observes every actual provider request, including subsequent tool-loop model calls.</summary>
internal sealed class ObservedChatClient(IChatClient inner, Action<AgentLifecycleEvent> publish,
    ProviderRetryPolicy retryPolicy, Func<IEnumerable<ChatMessage>, IReadOnlyList<ChatMessage>> takeSteering,
    bool blockImages = false) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var requestMessages = FilterImages(WithSteering(messages));
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
                if (!retryPolicy.CanRetry(error, retries, producedOutput: false)) throw;
                publish(new("model_retry_scheduled", Text: $"{retries + 1}/{retryPolicy.MaxRetries}", Error: error.Message));
                if (retryPolicy.Delay > TimeSpan.Zero) await Task.Delay(retryPolicy.Delay, cancellationToken);
            }
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestMessages = FilterImages(WithSteering(messages));
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
                    producedOutput = true;
                    var update = enumerator.Current;
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
            if (!retryPolicy.CanRetry(failure, retries, producedOutput)) throw failure;
            publish(new("model_retry_scheduled", Text: $"{retries + 1}/{retryPolicy.MaxRetries}", Error: failure.Message));
            if (retryPolicy.Delay > TimeSpan.Zero) await Task.Delay(retryPolicy.Delay, cancellationToken);
        }
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
