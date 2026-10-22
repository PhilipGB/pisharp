using PiSharp.Cli.Sessions;

namespace PiSharp.Cli.Tui;

internal sealed class TerminalApplicationActions(TerminalSettingsCommands settingsCommands,
    TerminalModelSelectionCommands modelSelection, TerminalSessionNavigation sessionNavigation, TerminalInputActions inputActions,
    TerminalPresentation presentation)
{
    public async Task HandleAsync(string action)
    {
        try
        {
            switch (action)
            {
                case "app.suspend":
                    await inputActions.SuspendAsync();
                    break;
                case "app.thinking.toggle":
                    await settingsCommands.ToggleThinkingAsync();
                    break;
                case "app.thinking.cycle":
                    await modelSelection.CycleThinkingAsync();
                    break;
                case "app.model.select":
                    await modelSelection.SelectModelAsync();
                    break;
                case "app.settings.open":
                    await settingsCommands.SelectSettingsAsync();
                    break;
                case "app.editor.external":
                    await inputActions.EditExternalAsync();
                    break;
                case "app.message.copy":
                    await inputActions.CopyAsync();
                    break;
                case "app.clipboard.pasteImage":
                    await inputActions.PasteImageAsync();
                    break;
                case "app.session.resume":
                    await sessionNavigation.SelectSessionAsync();
                    break;
                case "app.session.fork":
                    await sessionNavigation.SelectForkAsync();
                    break;
                case "app.model.cycleForward":
                case "app.model.cycleBackward":
                    await modelSelection.CycleModelAsync(action == "app.model.cycleForward" ? 1 : -1);
                    break;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Shortcut action failed: {error.Message}");
        }
        finally { presentation.SetIdleFooter(); }
    }

}
