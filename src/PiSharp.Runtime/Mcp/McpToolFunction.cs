using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Runtime.Mcp;

internal sealed class McpToolFunction(McpClientTool tool, McpServerConnection connection, string name,
    TimeSpan timeout) : DelegatingAIFunction(tool)
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public override string Name => name;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var values = arguments.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var wireResult = await connection.CallToolAsync(tool.Name, values, deadline.Token);
        var resultNode = JsonNode.Parse(wireResult.GetRawText())?.AsObject() ??
            throw new JsonException("Invalid MCP tools/call result.");
        if (!resultNode.ContainsKey("content")) resultNode["content"] = new JsonArray();
        var result = resultNode.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions) ??
            throw new JsonException("Invalid MCP tools/call result.");
        if (result.Content is null) throw new JsonException("Invalid MCP tools/call content.");
        var lines = new List<string>();
        var images = new List<PiSharpToolImage>();
        foreach (var content in result.Content)
            switch (content)
            {
                case TextContentBlock text:
                    lines.Add(text.Text);
                    break;
                case ImageContentBlock image:
                    images.Add(new PiSharpToolImage(image.MimeType, Convert.ToBase64String(image.DecodedData.Span)));
                    break;
                default:
                    lines.Add(JsonSerializer.Serialize(content));
                    break;
            }
        var output = string.Join("\n", lines);
        if (result.Content.Count == 0 && wireResult.TryGetProperty("structuredContent", out var structured))
            output = JsonSerializer.Serialize(structured, s_json);
        resultNode.Remove("_meta");
        var scriptResult = JsonSerializer.SerializeToElement(resultNode, s_json);
        return new PiSharpToolResult(output, StructuredContent: scriptResult,
            IsError: result.IsError == true, Error: result.IsError == true ? output : null, Images: images);
    }
}
