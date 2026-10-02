using Microsoft.Agents.AI;
using System.Text.Json;

namespace PiSharp.Runtime.Sessions;

internal sealed class ConversationToolState(PiAgent agent, ConversationSession conversation)
{
    private AgentSession? _execution;

    internal void Restore(AgentSession execution, IReadOnlyList<string>? activeToolNamesOverride = null)
    {
        var path = conversation.Tree.ActivePath();
        var boundary = path.ToList().FindLastIndex(node => node.Type == "compaction");
        var saved = path.Skip(boundary + 1).Select(TranscriptPayload).LastOrDefault(value => value is not null);
        agent.GetToolTranscript(execution).Restore(saved);
        if ((activeToolNamesOverride ?? conversation.ActiveToolLoadout()) is { } activeTools)
            agent.RestoreToolLoadout(execution, activeTools);
        if (conversation.ActiveCodemodeStore() is { } codemodeStore)
            agent.RestoreCodemodeStore(execution, codemodeStore);
    }

    internal void BeginRun(AgentSession execution)
    {
        _execution = execution;
        var loadout = agent.GetToolLoadout(execution);
        var declaredNames = loadout.BeginRun();
        if (conversation.ActiveToolLoadout() is not null)
            conversation.AppendToolLoadout(declaredNames);
    }

    internal void Observe(AgentLifecycleEvent item)
    {
        if (_execution is not null && item.Type is "model_request_completed" or "model_request_failed" or "model_request_interrupted")
        {
            var snapshot = agent.GetToolTranscript(_execution).Serialize();
            var previous = conversation.Tree.ActivePath().Select(TranscriptPayload).LastOrDefault(value => value is not null);
            if (previous is null || !JsonElement.DeepEquals(previous.Value, snapshot))
                conversation.Tree.Append("tool_transcript", snapshot);
        }
        if (item.Type == "tool_loadout_changed" && item.ToolLoadoutNames is { } activeTools)
            conversation.AppendToolLoadout(activeTools);
        if (item.Type == "codemode_store_changed" && item.CodemodeStore is { } codemodeStore)
            conversation.AppendCodemodeStore(codemodeStore);
    }

    private static JsonElement? TranscriptPayload(PiSharp.Core.ConversationNode node)
    {
        if (node.Type == "tool_transcript") return node.Payload;
        if (PiJsonlSessionInterchange.OriginalEntry(node) is not { } original ||
            PiJsonlSessionInterchange.StringProperty(original, "customType") != "pisharp.tool_transcript" ||
            !original.TryGetProperty("data", out var data)) return null;
        return data.Clone();
    }
}
