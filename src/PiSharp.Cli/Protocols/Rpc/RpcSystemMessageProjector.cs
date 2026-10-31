using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.Runtime;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal static class RpcSystemMessageProjector
{
    public static JsonObject? Project(PiAgent agent, bool includeBeforeFirstRun = false)
    {
        var timestamp = agent.SystemMessageTimestamp;
        if (timestamp is null && !includeBeforeFirstRun) return null;

        var message = new JsonObject
        {
            ["role"] = "system",
            ["content"] = "",
            ["sections"] = new JsonObject(),
            ["timestamp"] = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        foreach (var (name, content) in agent.SystemMessageSections)
            message["sections"]![name] = content;
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

    /// <summary>
    /// The system state the model already knows from the restored session, in the same semantic
    /// shape as <see cref="Project"/> but without a timestamp. Pinned Pi rebuilds its next system
    /// update by comparing the desired prompt/tool state with what the conversation already carries,
    /// so an unchanged resumed session produces no new system message while a changed section or
    /// tool loadout does.
    /// </summary>
    public static JsonObject? ProjectPersisted(IReadOnlyList<ChatMessage> systemRevisions)
    {
        if (systemRevisions.Count == 0) return null;
        // The transcript keeps the prompt as text; the live projection splits it into sections.
        // Emitting the persisted prompt as text lets the writer compare the model-facing text of
        // both shapes instead of comparing how each one chose to lay the prompt out.
        var message = new JsonObject
        {
            ["role"] = "system",
            ["content"] = SystemMessageTranscript.CurrentText(systemRevisions)
        };
        var tools = new JsonArray();
        foreach (var tool in SystemMessageTranscript.CurrentTools(systemRevisions))
            tools.Add(JsonNode.Parse(tool.GetRawText()));
        if (tools.Count > 0) message["toolsAdded"] = tools;
        return message;
    }
}
