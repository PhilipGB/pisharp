using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Core;

namespace PiSharp.Runtime;

/// <summary>
/// Converts a projected Pi branch to MAF chat history. Reject unsupported blocks rather than
/// silently dropping image/reasoning/signature data; this is not yet full provider replay.
/// </summary>
public static class PiHistoryBridge
{
    public static List<ChatMessage> ToChatMessages(PiSessionContext context)
    {
        var messages = new List<ChatMessage>();
        foreach (var pi in context.Messages)
        {
            var role = pi.GetProperty("role").GetString();
            switch (role)
            {
                case "system":
                case "user":
                case "custom":
                case "assistant":
                    {
                        if (role == "system" && (pi.TryGetProperty("sections", out _) || pi.TryGetProperty("toolsAdded", out _) || pi.TryGetProperty("toolsRemoved", out _)))
                            throw new NotSupportedException("Pi system prompt/tool patches cannot be replayed by MAF yet.");
                        var contents = ReadContents(pi.GetProperty("content"), allowCalls: role == "assistant");
                        messages.Add(new ChatMessage(role == "system" ? ChatRole.System : role == "assistant" ? ChatRole.Assistant : ChatRole.User, contents));
                        break;
                    }
                case "toolResult":
                    {
                        if (pi.TryGetProperty("isError", out var error) && error.GetBoolean())
                            throw new NotSupportedException("Pi failed tool results cannot be replayed by MAF yet.");
                        var id = pi.GetProperty("toolCallId").GetString()!;
                        var content = ReadContents(pi.GetProperty("content"), allowCalls: false);
                        if (content.Count != 1 || content[0] is not TextContent text)
                            throw new NotSupportedException("Only one text block per Pi tool result can be replayed yet.");
                        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(id, text.Text)]));
                        break;
                    }
                case "branchSummary":
                    messages.Add(new ChatMessage(ChatRole.User, "The following is a summary of a branch that this conversation came back from:\n\n<summary>\n" + pi.GetProperty("summary").GetString() + "</summary>"));
                    break;
                case "compactionSummary":
                    messages.Add(new ChatMessage(ChatRole.User, "The conversation history before this point was compacted into the following summary:\n\n<summary>\n" + pi.GetProperty("summary").GetString() + "\n</summary>"));
                    break;
                default:
                    throw new NotSupportedException($"Pi message role {role} cannot be replayed by MAF yet.");
            }
        }
        return messages;
    }

    private static List<AIContent> ReadContents(JsonElement content, bool allowCalls)
    {
        if (content.ValueKind == JsonValueKind.String) return [new TextContent(content.GetString()!)];
        if (content.ValueKind != JsonValueKind.Array) throw new NotSupportedException("Unsupported Pi message content.");
        var blocks = new List<AIContent>();
        foreach (var item in content.EnumerateArray())
        {
            switch (item.GetProperty("type").GetString())
            {
                case "text":
                    if (item.TryGetProperty("textSignature", out _))
                        throw new NotSupportedException("Provider-signed Pi text cannot be replayed by MAF yet.");
                    blocks.Add(new TextContent(item.GetProperty("text").GetString()!));
                    break;
                case "toolCall" when allowCalls:
                    if (item.TryGetProperty("thoughtSignature", out _) || item.TryGetProperty("namespace", out _))
                        throw new NotSupportedException("Provider-signed or namespaced Pi tool calls cannot be replayed by MAF yet.");
                    var arguments = item.GetProperty("arguments").EnumerateObject()
                        .ToDictionary(field => field.Name, field => (object?)field.Value.Clone(), StringComparer.Ordinal);
                    blocks.Add(new FunctionCallContent(item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!, arguments)
                    {
                        InformationalOnly = true
                    });
                    break;
                default:
                    throw new NotSupportedException($"Pi content block {item.GetProperty("type").GetString()} cannot be replayed by MAF yet.");
            }
        }
        return blocks;
    }
}
