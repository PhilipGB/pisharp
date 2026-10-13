using PiSharp.Cli.Composition;

namespace PiSharp.Cli.Tui;

internal sealed class TerminalInputActions(ActiveProjectRuntime projectBinding, ActiveConversationRuntime conversationBinding,
    TerminalPresentation presentation)
{
    public async Task EditExternalAsync()
    {
        var editorCommand = ExternalEditor.ResolveCommand(projectBinding.Settings.ExternalEditor, Environment.GetEnvironmentVariable);
        Console.WriteLine($"Launching external editor: {editorCommand}");
        var result = await ExternalEditor.EditAsync(presentation.Editor?.Draft ?? "", editorCommand);
        if (result.Success) presentation.Editor?.Prefill(result.Content);
        else Console.Error.WriteLine($"External editor exited with status {result.ExitCode}; the draft was preserved.");
    }

    public async Task CopyAsync()
    {
        if (presentation.Screen?.ActiveAuthorizationUrl is { } authorizationUrl)
            await TerminalClipboardActions.CopyAuthorizationUrlAsync(presentation.Clipboard, presentation.Screen, authorizationUrl);
        else
            await TerminalClipboardActions.CopyLastAssistantAsync(presentation.Clipboard, conversationBinding.Conversation, presentation.Screen, Console.Out);
    }

    public async Task PasteImageAsync()
    {
        await TerminalClipboardActions.PasteImageAsync(presentation.Clipboard, presentation.Editor);
    }

}
