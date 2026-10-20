using Microsoft.Extensions.AI;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;

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

    internal static ChatMessage CloneMessage(ChatMessage message)
        => CloneMessage(message, requireDetachedOpaqueContent: false);

    internal static ChatMessage CloneMessageForNativeContext(ChatMessage message)
        => CloneMessage(message, requireDetachedOpaqueContent: true);

    internal static ChatMessage[] CloneMessagesForNativeContext(IEnumerable<ChatMessage> messages) =>
        messages.Select(CloneMessageForNativeContext).ToArray();

    private static ChatMessage CloneMessage(ChatMessage message, bool requireDetachedOpaqueContent)
    {
        var clone = message.Clone();
        clone.Contents = message.Contents.Select(content => CloneContent(content, requireDetachedOpaqueContent)).ToList();
        clone.AdditionalProperties = CloneProperties(message.AdditionalProperties, requireDetachedOpaqueContent);
        clone.RawRepresentation = CloneOpaqueValue(message.RawRepresentation, requireDetachedOpaqueContent);
        return clone;
    }

    private static AIContent CloneContent(AIContent content, bool requireDetachedOpaqueContent)
    {
        AIContent clone = content switch
        {
            TextContent text => new TextContent(text.Text),
            TextReasoningContent reasoning => new TextReasoningContent(reasoning.Text)
            {
                ProtectedData = reasoning.ProtectedData
            },
            DataContent data => CloneDataContent(data),
            UriContent uri => new UriContent(uri.Uri, uri.MediaType),
            FunctionCallContent call => new FunctionCallContent(call.CallId, call.Name,
                call.Arguments is null ? new Dictionary<string, object?>() : call.Arguments.ToDictionary(
                    pair => pair.Key, pair => CloneOpaqueValue(pair.Value, requireDetachedOpaqueContent),
                    StringComparer.Ordinal))
            {
                Exception = CloneException(call.Exception, requireDetachedOpaqueContent),
                InformationalOnly = call.InformationalOnly
            },
            FunctionResultContent result => new FunctionResultContent(result.CallId,
                CloneOpaqueValue(result.Result, requireDetachedOpaqueContent))
            {
                Exception = CloneException(result.Exception, requireDetachedOpaqueContent)
            },
            _ => CloneOpaqueContent(content, requireDetachedOpaqueContent)
        };

        if (!ReferenceEquals(clone, content))
        {
            clone.Annotations = content.Annotations?.Select(annotation => requireDetachedOpaqueContent
                ? (AIAnnotation)CloneOpaqueValue(annotation, true)!
                : annotation).ToList();
            clone.AdditionalProperties = CloneProperties(content.AdditionalProperties, requireDetachedOpaqueContent);
            clone.RawRepresentation = CloneOpaqueValue(content.RawRepresentation, requireDetachedOpaqueContent);
        }
        return clone;
    }

    private static AIContent CloneOpaqueContent(AIContent content, bool requireDetachedOpaqueContent)
    {
        if (!requireDetachedOpaqueContent) return content;
        try
        {
            var json = JsonSerializer.SerializeToElement(content, content.GetType(), AIJsonUtilities.DefaultOptions);
            return (AIContent?)JsonSerializer.Deserialize(json, content.GetType(), AIJsonUtilities.DefaultOptions)
                ?? throw new InvalidDataException("The content serializer returned null.");
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidDataException($"Content type '{content.GetType().FullName}' cannot be detached safely.", error);
        }
    }

    private static AdditionalPropertiesDictionary? CloneProperties(AdditionalPropertiesDictionary? properties,
        bool requireDetachedOpaqueContent)
    {
        if (properties is null) return null;
        var clone = new AdditionalPropertiesDictionary();
        foreach (var (key, value) in properties)
            clone[key] = CloneOpaqueValue(value, requireDetachedOpaqueContent);
        return clone;
    }

    private static object? CloneOpaqueValue(object? value, bool requireDetachedOpaqueContent)
    {
        if (!requireDetachedOpaqueContent || value is null) return value;
        switch (value)
        {
            case string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or char or Guid or DateTime or DateTimeOffset or TimeSpan:
                return value;
            case JsonElement json:
                return json.Clone();
            case JsonDocument document:
                return JsonDocument.Parse(document.RootElement.GetRawText());
            case JsonNode node:
                return node.DeepClone();
            case byte[] bytes:
                return bytes.ToArray();
            case IDictionary dictionary when Activator.CreateInstance(value.GetType()) is IDictionary clone:
                {
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (entry.Key is null) throw new InvalidDataException("Opaque metadata contains a null key.");
                        clone.Add(entry.Key, CloneOpaqueValue(entry.Value, requireDetachedOpaqueContent));
                    }
                    return clone;
                }
            case Array array when array.Rank == 1:
                {
                    var clone = Array.CreateInstance(value.GetType().GetElementType()!, array.Length);
                    for (var index = 0; index < array.Length; index++)
                        clone.SetValue(CloneOpaqueValue(array.GetValue(index), requireDetachedOpaqueContent), index);
                    return clone;
                }
            case IList list when Activator.CreateInstance(value.GetType()) is IList clone:
                foreach (var item in list) clone.Add(CloneOpaqueValue(item, requireDetachedOpaqueContent));
                return clone;
            case IEnumerable when value is not string:
                return CloneJsonValue(value);
            default:
                return CloneJsonValue(value);
        }
    }

    private static object CloneJsonValue(object value)
    {
        try
        {
            var json = JsonSerializer.SerializeToElement(value, value.GetType(), AIJsonUtilities.DefaultOptions);
            return JsonSerializer.Deserialize(json, value.GetType(), AIJsonUtilities.DefaultOptions)
                ?? throw new InvalidDataException("The metadata serializer returned null.");
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new InvalidDataException($"Opaque value type '{value.GetType().FullName}' cannot be detached safely.", error);
        }
    }

    private static Exception? CloneException(Exception? exception, bool requireDetachedOpaqueContent) =>
        exception is null || !requireDetachedOpaqueContent
            ? exception
            : new InvalidOperationException(exception.Message,
                CloneException(exception.InnerException, requireDetachedOpaqueContent));

    private static DataContent CloneDataContent(DataContent content)
    {
        var clone = content.Data is { } data
            ? new DataContent(data.ToArray(), content.MediaType)
            : new DataContent(content.Uri, content.MediaType);
        clone.Name = content.Name;
        return clone;
    }
}
