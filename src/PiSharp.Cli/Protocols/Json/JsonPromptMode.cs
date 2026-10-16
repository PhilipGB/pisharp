using Microsoft.Extensions.AI;
using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Protocols;

internal static class JsonPromptMode
{
    public static async Task RunAsync(ConversationSession conversation, ConversationRun conversationRun,
        ConversationStore store, string? sessionPath, ResourceCatalog resources, string prompt, string stdinContent,
        CliArguments cli, string currentDirectory, TextWriter output, TextWriter error)
    {
        var protocol = new JsonEventMode(output);
        await protocol.HeaderAsync(conversation);
        if (string.IsNullOrWhiteSpace(stdinContent + prompt) && (cli.FileArguments?.Count ?? 0) == 0) { error.WriteLine("A prompt is required in JSON mode."); Environment.ExitCode = 2; }
        else
        {
            CliFileArguments.PromptFiles? expanded = null;
            try
            {
                var resolved = await resources.ResolveInputAsync(prompt, reportError: diagnostic =>
                    error.WriteLineAsync($"Extension error ({diagnostic.ExtensionPath}): {diagnostic.Error}"));
                expanded = await CliFileArguments.ProcessFilesAsync(resolved, cli.FileArguments, currentDirectory);
                expanded = expanded with { Text = stdinContent + expanded.Text };
            }
            catch (Exception e) when (e is ArgumentException or IOException)
            {
                await protocol.RejectAsync(e.Message);
                Environment.ExitCode = 1;
            }
            if (expanded is not null && string.IsNullOrWhiteSpace(expanded.Text) && expanded.Images.Count == 0)
            {
                await protocol.RejectAsync("A nonempty prompt is required.");
                Environment.ExitCode = 2;
            }
            else if (expanded is not null)
            {
                var contents = new List<AIContent> { new TextContent(expanded.Text) };
                contents.AddRange(expanded.Images);
                if (!await protocol.RunAsync(conversationRun, new ChatMessage(ChatRole.User, contents), expanded.Text)) Environment.ExitCode = 1;
            }
            if (sessionPath is not null)
                try { await store.SaveAsync(conversation, sessionPath); }
                catch (Exception e) { error.WriteLine($"Could not save session: {e.Message}"); Environment.ExitCode = 1; }
        }
    }
}
