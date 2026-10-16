using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace PiSharp.Runtime.Sessions;

/// <summary>Preserves declaration policy across live requests and persisted tool transcript revisions.</summary>
internal static class ToolDeclarationJson
{
    internal static JsonElement Create(AIFunctionDeclaration tool)
    {
        var declaration = new JsonObject
        {
            ["name"] = tool.Name,
            ["description"] = tool.Description,
            ["parameters"] = JsonNode.Parse(tool.JsonSchema.GetRawText())
        };
        if (tool.AdditionalProperties.TryGetValue("constrainedSampling", out var value) && value is JsonElement policy)
            declaration["constrainedSampling"] = JsonNode.Parse(policy.GetRawText());
        return JsonSerializer.SerializeToElement(declaration);
    }
}
