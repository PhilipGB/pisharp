using Microsoft.Extensions.AI;

namespace PiSharp.Cli;

/// <summary>Replays unsigned Anthropic-compatible reasoning according to model compatibility metadata.</summary>
internal sealed class AnthropicThinkingSignatureClient(IChatClient inner, bool allowEmptySignature)
    : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(ProjectMessages(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(ProjectMessages(messages), options, cancellationToken);

    private IEnumerable<ChatMessage> ProjectMessages(IEnumerable<ChatMessage> messages) =>
        messages.Select(ProjectMessage).ToArray();

    private ChatMessage ProjectMessage(ChatMessage message)
    {
        if (message.Role != ChatRole.Assistant) return message;

        var projected = new List<AIContent>(message.Contents.Count);
        var changed = false;
        foreach (var content in message.Contents)
        {
            if (content is not TextReasoningContent reasoning)
            {
                projected.Add(content);
                continue;
            }

            var signature = GetSignatureState(reasoning);
            if (signature == SignatureState.Present)
            {
                projected.Add(content);
                continue;
            }

            if (string.IsNullOrWhiteSpace(reasoning.Text))
            {
                changed = true;
                continue;
            }

            if (!allowEmptySignature)
            {
                projected.Add(new TextContent(reasoning.Text));
                changed = true;
                continue;
            }

            if (signature == SignatureState.Whitespace)
            {
                var normalized = new TextReasoningContent(reasoning.Text)
                {
                    ProtectedData = string.Empty,
                    AdditionalProperties = reasoning.AdditionalProperties?.Clone(),
                    Annotations = reasoning.Annotations,
                    RawRepresentation = reasoning.RawRepresentation
                };
                projected.Add(normalized);
                changed = true;
                continue;
            }

            projected.Add(content);
        }

        if (!changed) return message;
        var clone = message.Clone();
        clone.Contents = projected;
        return clone;
    }

    private static SignatureState GetSignatureState(TextReasoningContent reasoning)
    {
        if (reasoning.ProtectedData is null or "") return SignatureState.Missing;
        return string.IsNullOrWhiteSpace(reasoning.ProtectedData)
            ? SignatureState.Whitespace
            : SignatureState.Present;
    }

    private enum SignatureState
    {
        Missing,
        Whitespace,
        Present
    }
}
