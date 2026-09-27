using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;

namespace PiSharp.Cli.Protocols;

internal static class RpcSystemMessageProjector
{
    public static JsonObject? Project(PiAgent agent)
    {
        if (agent.SystemMessageTimestamp is not long timestamp) return null;

        var message = new JsonObject
        {
            ["role"] = "system",
            ["content"] = "",
            ["sections"] = new JsonObject { ["preamble"] = agent.SystemInstructions },
            ["timestamp"] = timestamp
        };
        var tools = new JsonArray();
        foreach (var function in agent.ToolDeclarations)
        {
            var tool = new JsonObject
            {
                ["name"] = function.Name,
                ["label"] = function.Name,
                ["description"] = function.Description,
                ["parameters"] = JsonNode.Parse(function.JsonSchema.GetRawText())
            };
            tools.Add(tool);
        }
        if (tools.Count > 0) message["toolsAdded"] = tools;
        return message;
    }
}
