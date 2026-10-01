using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Extensions;

/// <summary>Transforms a provider request's conversation without changing canonical session history.</summary>
public delegate ValueTask<IReadOnlyList<ChatMessage>> PiSharpContextTransform(
    IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);

internal sealed class PiSharpContextTransformPipeline(IReadOnlyList<PiSharpContextTransform>? transforms)
{
    private readonly IReadOnlyList<PiSharpContextTransform> _transforms = transforms?.ToArray() ?? [];

    public async Task<IReadOnlyList<ChatMessage>> ApplyAsync(IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        if (_transforms.Count == 0) return messages;

        var current = CloneMessages(messages);
        foreach (var transform in _transforms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transformed = await transform(Array.AsReadOnly(current), cancellationToken).ConfigureAwait(false);
            if (transformed is null)
                throw new InvalidOperationException("Extension context transforms must return a message list.");
            current = CloneMessages(transformed);
        }
        return Array.AsReadOnly(current);
    }

    private static ChatMessage[] CloneMessages(IEnumerable<ChatMessage> messages) =>
        messages.Select(CloneMessage).ToArray();

    private static ChatMessage CloneMessage(ChatMessage message)
    {
        var clone = message.Clone();
        clone.Contents = message.Contents.Select(CloneContent).ToList();
        clone.AdditionalProperties = message.AdditionalProperties?.Clone();
        return clone;
    }

    private static AIContent CloneContent(AIContent content)
    {
        AIContent clone = content switch
        {
            TextContent text => new TextContent(text.Text),
            FunctionCallContent call => new FunctionCallContent(call.CallId, call.Name,
                call.Arguments is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(call.Arguments))
            {
                Exception = call.Exception,
                InformationalOnly = call.InformationalOnly
            },
            FunctionResultContent result => new FunctionResultContent(result.CallId, result.Result)
            {
                Exception = result.Exception
            },
            _ => content
        };

        if (!ReferenceEquals(clone, content))
        {
            clone.Annotations = content.Annotations?.ToList();
            clone.AdditionalProperties = content.AdditionalProperties?.Clone();
            clone.RawRepresentation = content.RawRepresentation;
        }
        return clone;
    }
}
