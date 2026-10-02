using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

internal static class StoredToolResultReplay
{
    internal static void Restore(ChatMessage message)
    {
        foreach (var result in message.Contents.OfType<FunctionResultContent>())
        {
            if (result.Result is JsonElement { ValueKind: JsonValueKind.String } text)
                result.Result = text.GetString();
            else if (result.Result is JsonElement { ValueKind: JsonValueKind.Array } blocks &&
                blocks.EnumerateArray().All(block => block.ValueKind == JsonValueKind.Object && block.TryGetProperty("$type", out _)))
            {
                var parts = blocks.Deserialize<List<AIContent>>(AIJsonUtilities.DefaultOptions)!;
                result.Result = parts.All(part => part is TextContent)
                    ? string.Join('\n', parts.OfType<TextContent>().Select(part => part.Text)) : parts;
            }
        }
    }
}
