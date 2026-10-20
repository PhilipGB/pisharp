using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Cli;

internal sealed class AnthropicSystemMessageHandler(AnthropicTranscriptRequest context) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (context.Updates.Value is { } updates && request.Content is { } content)
        {
            var payload = JsonNode.Parse(await content.ReadAsStringAsync(cancellationToken))!.AsObject();
            var messages = payload["messages"]!.AsArray();
            var baseMessageCount = messages.Count;
            var inserted = 0;
            var orderedUpdates = updates.Select((update, order) => (
                    Boundary: ResolveBoundary(messages, update.BeforeMessage), order, update))
                .OrderBy(item => item.Boundary).ThenBy(item => item.order);
            foreach (var item in orderedUpdates)
            {
                var boundary = Math.Clamp(item.Boundary, 0, baseMessageCount);
                messages.Insert(boundary + inserted++, item.update.Message.DeepClone());
            }
            foreach (var message in messages.OfType<JsonObject>())
            {
                if (message["content"] is not JsonArray contentBlocks) continue;
                foreach (var toolResult in contentBlocks.OfType<JsonObject>())
                {
                    if (toolResult["type"]?.GetValue<string>() != "tool_result" ||
                        toolResult["is_error"]?.GetValue<bool>() != true ||
                        toolResult["content"] is not JsonArray resultBlocks) continue;
                    for (var index = 0; index < resultBlocks.Count; index++)
                        if (resultBlocks[index]?["type"]?.GetValue<string>() == "image")
                            resultBlocks[index] = new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = AnthropicToolResultProjection.ImageOmissionNote
                            };
                    if (resultBlocks.All(block => block?["type"]?.GetValue<string>() == "text"))
                        toolResult["content"] = string.Join('\n', resultBlocks.Select(block => block!["text"]!.GetValue<string>()));
                }
            }
            if (messages.LastOrDefault() is JsonObject last && last["role"]?.GetValue<string>() is "user" or "system")
            {
                if (last["content"] is JsonValue text && text.TryGetValue<string>(out var value))
                    last["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = value });
                if (last["content"] is JsonArray blocks && blocks.LastOrDefault() is JsonObject block)
                    block["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            }
            var replacement = new ByteArrayContent(Encoding.UTF8.GetBytes(payload.ToJsonString()));
            foreach (var header in content.Headers)
                if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Content = replacement;
            content.Dispose();
        }
        var response = await base.SendAsync(request, cancellationToken);
        if (context.Attempt.Value is { } attempt) attempt.StatusCode = response.StatusCode;
        return response;
    }

    private static int ResolveBoundary(JsonArray messages, int requestedBoundary)
    {
        var boundary = Math.Clamp(requestedBoundary, 0, messages.Count);
        if (boundary == 0 || messages[boundary - 1] is not JsonObject assistant ||
            StringValue(assistant["role"]) != "assistant") return boundary;

        var pendingToolCalls = ToolIds(assistant, "tool_use", "id").ToHashSet(StringComparer.Ordinal);
        if (pendingToolCalls.Count == 0) return boundary;

        var resultBoundary = boundary;
        var foundToolResult = false;
        for (var index = boundary; index < messages.Count; index++)
        {
            if (messages[index] is not JsonObject nextMessage) break;
            var role = StringValue(nextMessage["role"]);
            if (role == "assistant") break;
            if (role != "user") continue;
            foreach (var toolId in ToolIds(nextMessage, "tool_result", "tool_use_id"))
                foundToolResult |= pendingToolCalls.Remove(toolId);
            resultBoundary = index + 1;
        }
        return foundToolResult ? resultBoundary : boundary;
    }

    private static IEnumerable<string> ToolIds(JsonObject message, string blockType, string idProperty)
    {
        if (message["content"] is not JsonArray blocks) yield break;
        foreach (var block in blocks.OfType<JsonObject>())
            if (StringValue(block["type"]) == blockType && StringValue(block[idProperty]) is { Length: > 0 } id)
                yield return id;
    }

    private static string? StringValue(JsonNode? value) =>
        value is JsonValue json && json.TryGetValue<string>(out var text) ? text : null;
}
