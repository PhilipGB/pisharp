using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Sessions;

/// <summary>Shares a single typed context dispatch across the physical retry loop of one request.</summary>
internal sealed class NativeContextMutationRequest
{
    internal const string PropertyName = "pisharp.nativeContextMutation";

    private JsonElement? _sourceMessages;
    private string? _sourceInstructions;
    private string[]? _sourceToolNames;
    private NativeContextMutationResult? _result;

    internal bool TryGet(IReadOnlyList<ChatMessage> messages, string? instructions,
        IReadOnlyList<string> toolNames, out NativeContextMutationResult result)
    {
        if (_result is not null && string.Equals(_sourceInstructions, instructions, StringComparison.Ordinal) &&
            _sourceToolNames is not null && _sourceToolNames.SequenceEqual(toolNames, StringComparer.Ordinal) &&
            _sourceMessages is { } source && MessagesEqual(source, messages))
        {
            result = new(PiSharpContextTransformPipeline.CloneMessagesForNativeContext(_result.Messages),
                _result.SystemInstructions, _result.MessagesChanged, _result.MessageStructureChanged);
            return true;
        }
        result = null!;
        return false;
    }

    internal void Store(IReadOnlyList<ChatMessage> sourceMessages, string? sourceInstructions,
        IReadOnlyList<string> sourceToolNames, NativeContextMutationResult result)
    {
        try { _sourceMessages = JsonSerializer.SerializeToElement(sourceMessages, AIJsonUtilities.DefaultOptions); }
        catch (Exception) { _sourceMessages = null; }
        _sourceInstructions = sourceInstructions;
        _sourceToolNames = sourceToolNames.ToArray();
        _result = new(PiSharpContextTransformPipeline.CloneMessagesForNativeContext(result.Messages),
            result.SystemInstructions, result.MessagesChanged, result.MessageStructureChanged);
    }

    internal static NativeContextMutationRequest? From(ChatOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(PropertyName, out var value) == true
            ? value as NativeContextMutationRequest : null;

    internal static ChatOptions Attach(ChatOptions? options, NativeContextMutationRequest request)
    {
        var copy = options?.Clone() ?? new ChatOptions();
        var properties = new AdditionalPropertiesDictionary();
        if (copy.AdditionalProperties is { } existing)
            foreach (var (key, value) in existing) properties[key] = value;
        properties[PropertyName] = request;
        copy.AdditionalProperties = properties;
        return copy;
    }

    internal static ChatOptions? Strip(ChatOptions? options)
    {
        if (options is null) return null;
        var copy = options.Clone();
        if (copy.AdditionalProperties?.ContainsKey(PropertyName) != true) return copy;
        var properties = new AdditionalPropertiesDictionary();
        foreach (var (key, value) in copy.AdditionalProperties)
            if (key != PropertyName) properties[key] = value;
        copy.AdditionalProperties = properties;
        return copy;
    }

    private static bool MessagesEqual(JsonElement source, IReadOnlyList<ChatMessage> messages)
    {
        try
        {
            return JsonElement.DeepEquals(source,
                JsonSerializer.SerializeToElement(messages, AIJsonUtilities.DefaultOptions));
        }
        catch (Exception) { return false; }
    }
}
