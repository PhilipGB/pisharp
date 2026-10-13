using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Composition;

internal sealed class ActiveConversationRuntime(ConversationSession conversation, ConversationRun run, string? path)
{
    public ConversationSession Conversation { get; private set; } = conversation;
    public ConversationRun Run { get; private set; } = run;
    public string? Path { get; private set; } = path;

    public void Adopt(ConversationSession session, ConversationRun execution, string? sessionPath)
    {
        Conversation = session;
        Run = execution;
        Path = sessionPath;
    }

    public void ReplaceRun(ConversationRun execution) => Run = execution;
}
