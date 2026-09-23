using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Observes every actual provider request, including subsequent tool-loop model calls.</summary>
internal sealed class ObservedChatClient(IChatClient inner, Action<AgentLifecycleEvent> publish) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        publish(new("model_request_started"));
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            publish(new("model_request_completed"));
            return response;
        }
        catch (OperationCanceledException) { publish(new("model_request_interrupted")); throw; }
        catch (Exception error) { publish(new("model_request_failed", Error: error.Message)); throw; }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        publish(new("model_request_started"));
        var ended = false;
        await using var enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool next;
                try { next = await enumerator.MoveNextAsync(); }
                catch (OperationCanceledException) { ended = true; publish(new("model_request_interrupted")); throw; }
                catch (Exception error) { ended = true; publish(new("model_request_failed", Error: error.Message)); throw; }
                if (!next) break;
                var update = enumerator.Current;
                if (!string.IsNullOrEmpty(update.Text)) publish(new("model_text_delta", Text: update.Text));
                yield return update;
            }
            ended = true;
            publish(new("model_request_completed"));
        }
        finally { if (!ended) publish(new("model_request_interrupted")); }
    }
}
