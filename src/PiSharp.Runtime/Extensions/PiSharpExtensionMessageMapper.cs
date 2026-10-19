using System.Text.Json;
using Microsoft.Extensions.AI;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal static class PiSharpExtensionMessageMapper
{
    internal static NativeExtensions.PiSharpExtensionMessage Snapshot(ChatMessage message,
        NativeExtensions.PiSharpExtensionMessageResponseMetadata? responseMetadata = null)
    {
        var custom = PiSharpCustomMessageProjection.TryRead(message, out _);
        var role = custom ? NativeExtensions.PiSharpExtensionMessageRole.Custom : message.Role.Value switch
        {
            "system" => NativeExtensions.PiSharpExtensionMessageRole.System,
            "user" => NativeExtensions.PiSharpExtensionMessageRole.User,
            "assistant" => NativeExtensions.PiSharpExtensionMessageRole.Assistant,
            "tool" => NativeExtensions.PiSharpExtensionMessageRole.ToolResult,
            _ => NativeExtensions.PiSharpExtensionMessageRole.Other
        };
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (message.AdditionalProperties is not null)
        {
            foreach (var (key, value) in message.AdditionalProperties)
            {
                try { properties[key] = JsonSerializer.SerializeToElement(value, AIJsonUtilities.DefaultOptions); }
                catch (NotSupportedException) { }
                catch (JsonException) { }
            }
        }

        return new NativeExtensions.PiSharpExtensionMessage(role,
            message.Contents?.Select(SnapshotContent),
            role == NativeExtensions.PiSharpExtensionMessageRole.Other ? message.Role.Value : null,
            message.MessageId, message.AuthorName, message.CreatedAt, properties, responseMetadata);
    }

    internal static ChatMessage Restore(NativeExtensions.PiSharpExtensionMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var role = message.Role switch
        {
            NativeExtensions.PiSharpExtensionMessageRole.System => ChatRole.System,
            NativeExtensions.PiSharpExtensionMessageRole.User or NativeExtensions.PiSharpExtensionMessageRole.Custom => ChatRole.User,
            NativeExtensions.PiSharpExtensionMessageRole.Assistant => ChatRole.Assistant,
            NativeExtensions.PiSharpExtensionMessageRole.ToolResult => ChatRole.Tool,
            NativeExtensions.PiSharpExtensionMessageRole.Other => new ChatRole(message.RoleName!),
            _ => throw new ArgumentOutOfRangeException(nameof(message))
        };
        var properties = new AdditionalPropertiesDictionary();
        foreach (var (key, value) in message.AdditionalProperties) properties[key] = value.Clone();
        var restored = new ChatMessage(role, message.Contents.Select(RestoreContent).ToList())
        {
            MessageId = message.MessageId,
            AuthorName = message.AuthorName,
            CreatedAt = message.CreatedAt,
            AdditionalProperties = properties.Count == 0 ? null : properties
        };
        if (message.Role == NativeExtensions.PiSharpExtensionMessageRole.Custom &&
            !PiSharpCustomMessageProjection.TryRead(restored, out _))
            throw new InvalidOperationException("A custom-message replacement must preserve its custom message metadata.");
        return restored;
    }

    internal static NativeExtensions.PiSharpExtensionMessageContent SnapshotContent(AIContent content) => content switch
    {
        TextContent text => new(NativeExtensions.PiSharpExtensionMessageContentKind.Text, text.Text,
            additionalProperties: SnapshotProperties(text)),
        TextReasoningContent reasoning => new(NativeExtensions.PiSharpExtensionMessageContentKind.Reasoning,
            reasoning.Text, reasoning.ProtectedData, SnapshotProperties(reasoning)),
        DataContent data => new(NativeExtensions.PiSharpExtensionMessageContentKind.Data, data.Uri, data.MediaType,
            data.Data.ToArray(), data.Name, SnapshotProperties(data)),
        UriContent uri => new(NativeExtensions.PiSharpExtensionMessageContentKind.Uri, uri.Uri.ToString(), uri.MediaType,
            additionalProperties: SnapshotProperties(uri)),
        FunctionCallContent call => new(call.CallId, call.Name,
            JsonSerializer.SerializeToElement(call.Arguments ?? new Dictionary<string, object?>(), AIJsonUtilities.DefaultOptions),
            call.Exception?.Message, call.InformationalOnly, SnapshotProperties(call)),
        FunctionResultContent result => new(result.CallId,
            JsonSerializer.SerializeToElement(result.Result, AIJsonUtilities.DefaultOptions), result.Exception is not null,
            result.Exception?.Message, SnapshotProperties(result)),
        _ => new(NativeExtensions.PiSharpExtensionMessageContentKind.Opaque,
            content.GetType().FullName ?? content.GetType().Name,
            JsonSerializer.SerializeToElement(content, content.GetType(), AIJsonUtilities.DefaultOptions),
            SnapshotProperties(content))
    };

    private static AIContent RestoreContent(NativeExtensions.PiSharpExtensionMessageContent content)
    {
        AIContent restored = content.Kind switch
        {
            NativeExtensions.PiSharpExtensionMessageContentKind.Text => new TextContent(content.Text ?? string.Empty),
            NativeExtensions.PiSharpExtensionMessageContentKind.Reasoning => new TextReasoningContent(content.Text ?? string.Empty)
            {
                ProtectedData = content.ProtectedData
            },
            NativeExtensions.PiSharpExtensionMessageContentKind.Data => new DataContent(content.Data!, content.MediaType!)
            {
                Name = content.Name
            },
            NativeExtensions.PiSharpExtensionMessageContentKind.Uri => new UriContent(content.Uri!, content.MediaType),
            NativeExtensions.PiSharpExtensionMessageContentKind.FunctionCall => RestoreFunctionCall(content),
            NativeExtensions.PiSharpExtensionMessageContentKind.FunctionResult => RestoreFunctionResult(content),
            NativeExtensions.PiSharpExtensionMessageContentKind.Opaque => throw new NotSupportedException(
                $"Message replacement cannot recreate unsupported provider content '{content.ContentType}'."),
            _ => throw new ArgumentOutOfRangeException(nameof(content))
        };
        if (content.AdditionalProperties.Count > 0)
        {
            var properties = new AdditionalPropertiesDictionary();
            foreach (var (key, value) in content.AdditionalProperties) properties[key] = value.Clone();
            restored.AdditionalProperties = properties;
        }
        return restored;
    }

    private static FunctionCallContent RestoreFunctionCall(NativeExtensions.PiSharpExtensionMessageContent content)
    {
        var arguments = content.JsonValue is { ValueKind: JsonValueKind.Object } value
            ? value.Deserialize<Dictionary<string, object?>>(AIJsonUtilities.DefaultOptions) ?? []
            : throw new InvalidOperationException("Function-call arguments must be a JSON object.");
        return new FunctionCallContent(content.CallId!, content.FunctionName!, arguments)
        {
            Exception = content.ErrorMessage is null ? null : new InvalidOperationException(content.ErrorMessage),
            InformationalOnly = content.InformationalOnly
        };
    }

    private static FunctionResultContent RestoreFunctionResult(NativeExtensions.PiSharpExtensionMessageContent content)
    {
        var result = content.JsonValue is { } value ? (object)value.Clone() : null!;
        return new FunctionResultContent(content.CallId!, result)
        {
            Exception = content.ErrorMessage is null && !content.IsError ? null :
                new InvalidOperationException(content.ErrorMessage ?? "The message result was marked as an error.")
        };
    }

    private static IReadOnlyDictionary<string, JsonElement> SnapshotProperties(AIContent content)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (content.AdditionalProperties is not null)
        {
            foreach (var (key, value) in content.AdditionalProperties)
            {
                try { properties[key] = JsonSerializer.SerializeToElement(value, AIJsonUtilities.DefaultOptions); }
                catch (NotSupportedException) { }
                catch (JsonException) { }
            }
        }
        return properties;
    }
}
