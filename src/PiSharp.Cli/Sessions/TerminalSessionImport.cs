using PiSharp.Cli.Composition;
using PiSharp.Cli.Tui;

namespace PiSharp.Cli.Sessions;

internal sealed class TerminalSessionImport(ActiveProjectRuntime project, ActiveModelRuntime model,
    ActiveConversationRuntime conversation, Func<Task<string?>> readConfirmation,
    Func<ProjectSessionRuntimeFactory> getFactory, Action<ProjectSessionRuntime> adopt)
{
    public async Task ImportAsync(string argument)
    {
        if (argument.Length == 0) throw new ArgumentException("Usage: /import <path.jsonl>");
        var path = Path.GetFullPath(SessionPathArgument.Parse(argument), project.WorkingDirectory);
        if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Import requires a Pi .jsonl session file.");
        Console.WriteLine($"Replace the active session with {TerminalSafeText.Normalize(path)}? Type import to confirm:");
        var confirmation = await readConfirmation();
        if (confirmation?.Trim() != "import")
        {
            Console.WriteLine("Import cancelled.");
            return;
        }
        if (conversation.Path is not null) await project.Store.SaveAsync(conversation.Conversation, conversation.Path);
        using var imported = await getFactory().OpenAsync(path, project.WorkingDirectory, model.Selection, model.Thinking);
        adopt(imported);
        Console.WriteLine($"Imported Pi session {conversation.Conversation.Id} · {conversation.Conversation.ActiveMessages().Count} active messages · {conversation.Path ?? "(ephemeral)"}");
    }
}
