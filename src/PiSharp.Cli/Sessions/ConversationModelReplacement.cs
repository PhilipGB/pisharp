using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Sessions;

internal static class ConversationModelReplacement
{
    public static async Task<ConversationRun> ReplaceAsync(ConversationSession conversation,
        ModelSelection previousSelection, string previousThinking, PreparedModelRuntime prepared,
        bool recordModelChange, Func<Task<ConversationRun>> openRun, Func<Task>? save)
    {
        var previousHead = conversation.Tree.HeadId;
        var previousConnection = previousSelection.Connection;
        try
        {
            if (recordModelChange)
                conversation.SelectModel(prepared.Connection.Model, prepared.Connection.Endpoint?.ToString(),
                    prepared.Selection.Provider.Id);
            if (!prepared.Thinking.Equals(previousThinking, StringComparison.Ordinal))
                conversation.AppendThinkingLevelChange(prepared.Thinking);
            var nextRun = await openRun();
            if (save is not null) await save();
            return nextRun;
        }
        catch
        {
            if (recordModelChange)
                conversation.RevertModel(previousConnection.Model, previousConnection.Endpoint?.ToString(),
                    previousHead, previousSelection.Provider.Id);
            else conversation.Tree.Select(previousHead);
            throw;
        }
    }
}
