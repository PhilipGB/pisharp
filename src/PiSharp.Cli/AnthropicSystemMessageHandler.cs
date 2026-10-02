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
            var assistant = 0;
            var inserted = 0;
            for (var index = 0; index < messages.Count; index++)
            {
                if (messages[index]?["role"]?.GetValue<string>() != "assistant") continue;
                while (inserted < updates.Count && updates[inserted].BeforeAssistant == assistant)
                    messages.Insert(index++, updates[inserted++].Message.DeepClone());
                assistant++;
            }
            while (inserted < updates.Count) messages.Add(updates[inserted++].Message.DeepClone());
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
        return await base.SendAsync(request, cancellationToken);
    }
}
