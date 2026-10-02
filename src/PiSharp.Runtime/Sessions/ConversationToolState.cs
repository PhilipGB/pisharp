using Microsoft.Agents.AI;

namespace PiSharp.Runtime.Sessions;

internal sealed class ConversationToolState(PiAgent agent, ConversationSession conversation)
{
    internal void Restore(AgentSession execution, IReadOnlyList<string>? activeToolNamesOverride = null)
    {
        if ((activeToolNamesOverride ?? conversation.ActiveToolLoadout()) is { } activeTools)
            agent.RestoreToolLoadout(execution, activeTools);
        if (conversation.ActiveCodemodeStore() is { } codemodeStore)
            agent.RestoreCodemodeStore(execution, codemodeStore);
    }

    internal void BeginRun(AgentSession execution)
    {
        var loadout = agent.GetToolLoadout(execution);
        var declaredNames = loadout.BeginRun();
        if (conversation.ActiveToolLoadout() is not null)
            conversation.AppendToolLoadout(declaredNames);
    }

    internal void Observe(AgentLifecycleEvent item)
    {
        if (item.Type == "tool_loadout_changed" && item.ToolLoadoutNames is { } activeTools)
            conversation.AppendToolLoadout(activeTools);
        if (item.Type == "codemode_store_changed" && item.CodemodeStore is { } codemodeStore)
            conversation.AppendCodemodeStore(codemodeStore);
    }
}
