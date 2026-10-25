using Microsoft.Extensions.AI;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Tui;

internal static class TerminalClipboardActions
{
    public static async Task CopyLastAssistantAsync(TerminalClipboard clipboard, ConversationSession conversation,
        TerminalScreen? screen, TextWriter output)
    {
        var selected = screen?.SelectedText;
        var text = selected ?? conversation.ActiveMessages()
            .LastOrDefault(message => message.Role == ChatRole.Assistant)?.Text?.Trim();
        if (string.IsNullOrEmpty(text)) throw new InvalidOperationException("No selected text or agent messages to copy yet.");
        await clipboard.CopyTextAsync(text);
        output.WriteLine(selected is null ? "Copied last agent message to clipboard" : "Copied selected text to clipboard");
    }

    public static async Task CopyAuthorizationUrlAsync(TerminalClipboard clipboard, TerminalScreen? screen,
        string authorizationUrl)
    {
        try
        {
            await clipboard.CopyTextAsync(authorizationUrl);
            if (screen?.SetAuthorizationUrlCopyStatus("Copied URL to clipboard",
                    TerminalStatusNotificationKind.Info) != true)
                screen?.AppendThemedStatusLine("Copied URL to clipboard");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var status = "Could not copy sign-in URL to clipboard: " + error.Message;
            if (screen?.SetAuthorizationUrlCopyStatus(status, TerminalStatusNotificationKind.Error) != true)
                screen?.AppendStatusMessage(status, TerminalStatusNotificationKind.Error);
        }
    }

    private static async Task PasteTextAsync(TerminalClipboard clipboard, TerminalEditor? editor)
    {
        var text = await clipboard.ReadTextAsync();
        if (!string.IsNullOrEmpty(text)) editor?.InsertTextAtCursor(text);
    }

    public static async Task PasteImageAsync(TerminalClipboard clipboard, TerminalEditor? editor)
    {
        var filePaths = await clipboard.ReadFilePathsAsync();
        if (filePaths is { Length: > 0 })
        {
            if (filePaths.Any(path => path.Any(char.IsControl)))
                throw new InvalidDataException("Clipboard file path contains control characters.");
            if (editor is not null)
            {
                var snapshot = editor.CaptureDraftState();
                var beforeLine = snapshot.Cursor > 0
                    ? snapshot.Text[..snapshot.Cursor].Split('\n')[^1]
                    : string.Empty;
                var afterText = snapshot.Text[snapshot.Cursor..];
                var afterLine = afterText.Split('\n')[0];
                var leadingSpace = beforeLine.Length > 0 && !char.IsWhiteSpace(beforeLine[^1]) ? " " : "";
                var trailingSpace = afterLine.Length > 0 && !char.IsWhiteSpace(afterLine[0]) ? " " : "";
                editor.InsertTextAtCursor(leadingSpace + string.Join(Environment.NewLine, filePaths) + trailingSpace);
            }
            return;
        }

        var image = await clipboard.ReadImageAsync();
        if (image is not null)
        {
            editor?.InsertTextAtCursor(await ClipboardImageStore.SaveAsync(image));
            return;
        }
        await PasteTextAsync(clipboard, editor);
    }
}
