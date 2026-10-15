using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Runtime.Providers;

namespace PiSharp.Cli;

internal sealed record MistralChatRequestMetadata(string? ReasoningEffort, string? PromptMode);

internal sealed class MistralChatRequestContext
{
    private readonly AsyncLocal<MistralChatRequestMetadata?> _current = new();

    public MistralChatRequestMetadata? Current => _current.Value;

    public IDisposable Push(MistralChatRequestMetadata metadata)
    {
        var previous = _current.Value;
        _current.Value = metadata;
        return new Scope(this, previous);
    }

    private sealed class Scope(MistralChatRequestContext owner, MistralChatRequestMetadata? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner._current.Value = previous;
        }
    }
}

/// <summary>Maps MAF reasoning options onto the current Mistral request fields.</summary>
internal sealed class MistralChatOptionsClient(IChatClient inner, ModelDescriptor model,
    MistralChatRequestContext requestContext) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var scope = requestContext.Push(CreateMetadata(model, options));
        return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var scope = requestContext.Push(CreateMetadata(model, options));
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    private static MistralChatRequestMetadata CreateMetadata(ModelDescriptor model, ChatOptions? options)
    {
        if (model.Reasoning != true) return new(null, null);
        if (model.ThinkingLevelMap is { ValueKind: JsonValueKind.Object } map)
        {
            if (options?.Reasoning?.Effort is not { } requestedEffort)
                return new(ReadString(map, "off"), null);

            foreach (var level in ThinkingLevels.All.Where(level => level != "off"))
            {
                if (!map.TryGetProperty(level, out var value) || value.ValueKind != JsonValueKind.String) continue;
                if (ThinkingLevels.ToOptions(level, map)?.Effort == requestedEffort)
                    return new(value.GetString(), null);
            }

            // Current Mistral reasoning models with an effort map default unsupported requests to high.
            return new("high", null);
        }

        return new(null, options?.Reasoning?.Effort is null ? null : "reasoning");
    }

    private static string? ReadString(JsonElement map, string property) =>
        map.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Applies Mistral's request-only Chat Completions constraints at the HTTP boundary.</summary>
internal sealed class MistralChatCompatibilityHandler(MistralChatRequestContext requestContext,
    HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    private const int ToolCallIdLength = 9;

    internal static string CreateToolCallId(string id) => new ToolCallIdNormalizer().Normalize(id);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) == true &&
            request.Content is { } content)
        {
            var text = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var payload = JsonNode.Parse(text) as JsonObject ??
                throw new InvalidDataException("Mistral Chat Completions request must be a JSON object.");
            NormalizeToolCallIds(payload["messages"] as JsonArray);
            if (requestContext.Current is { } metadata)
            {
                payload.Remove("reasoning_effort");
                payload.Remove("prompt_mode");
                if (metadata.ReasoningEffort is not null) payload["reasoning_effort"] = metadata.ReasoningEffort;
                if (metadata.PromptMode is not null) payload["prompt_mode"] = metadata.PromptMode;
            }

            var replacement = new ByteArrayContent(Encoding.UTF8.GetBytes(payload.ToJsonString()));
            foreach (var header in content.Headers)
            {
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            request.Content = replacement;
            content.Dispose();
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static void NormalizeToolCallIds(JsonArray? messages)
    {
        if (messages is null) return;
        var normalizer = new ToolCallIdNormalizer();
        foreach (var message in messages.OfType<JsonObject>())
        {
            if (message["tool_calls"] is JsonArray calls)
            {
                foreach (var call in calls.OfType<JsonObject>())
                    if (ReadString(call["id"]) is { } id) call["id"] = normalizer.Normalize(id);
            }
            if (ReadString(message["tool_call_id"]) is { } resultId)
                message["tool_call_id"] = normalizer.Normalize(resultId);
        }
    }

    private static string? ReadString(JsonNode? value)
    {
        try { return value?.GetValue<string>(); }
        catch (InvalidOperationException) { return null; }
    }

    private sealed class ToolCallIdNormalizer
    {
        private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);

        public string Normalize(string id)
        {
            if (_ids.TryGetValue(id, out var existing)) return existing;
            var alphanumeric = new string(id.Where(char.IsAsciiLetterOrDigit).ToArray());
            var seedBase = alphanumeric.Length > 0 ? alphanumeric : id;
            for (var attempt = 0; ; attempt++)
            {
                var candidate = attempt == 0 && alphanumeric.Length == ToolCallIdLength
                    ? alphanumeric
                    : ShortHash(attempt == 0 ? seedBase : $"{seedBase}:{attempt}")[..ToolCallIdLength];
                if (!_used.Add(candidate)) continue;
                _ids.Add(id, candidate);
                return candidate;
            }
        }

        private static string ShortHash(string value)
        {
            uint first = 0xdeadbeef;
            uint second = 0x41c6ce57;
            unchecked
            {
                foreach (var character in value)
                {
                    first = (first ^ character) * 2654435761u;
                    second = (second ^ character) * 1597334677u;
                }
                first = ((first ^ (first >> 16)) * 2246822507u) ^ ((second ^ (second >> 13)) * 3266489909u);
                second = ((second ^ (second >> 16)) * 2246822507u) ^ ((first ^ (first >> 13)) * 3266489909u);
            }
            return ToBase36(second) + ToBase36(first);
        }

        private static string ToBase36(uint value)
        {
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
            Span<char> buffer = stackalloc char[7];
            var index = buffer.Length;
            do
            {
                buffer[--index] = digits[(int)(value % 36)];
                value /= 36;
            } while (value > 0);
            return new string(buffer[index..]);
        }
    }
}
