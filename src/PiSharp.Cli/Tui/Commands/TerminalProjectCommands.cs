using PiSharp.Cli.Composition;
using PiSharp.Runtime.Resources;

namespace PiSharp.Cli.Tui;

internal sealed class TerminalProjectCommands(ProjectTrust trust, ActiveProjectRuntime project,
    TerminalTrustPicker? picker, TerminalEditor editor, TerminalScreen? screen)
{
    public async Task TrustAsync(string argument, Func<Task> reload)
    {
        if (argument.Length == 0)
        {
            var saved = picker is null ? null : await picker.ShowAsync(trust, project.WorkingDirectory, project.Trusted);
            if (saved.HasValue)
                Console.WriteLine($" Saved trust decision: {(saved.Value ? "trusted" : "untrusted")}. Restart PiSharp for this to take effect.");
            return;
        }
        bool? decision = argument switch
        {
            "yes" or "allow" => true,
            "no" or "deny" => false,
            "forget" => null,
            _ => throw new ArgumentException("Use /trust yes, /trust no, or /trust forget.")
        };
        await trust.SetAsync(project.WorkingDirectory, decision);
        project.SetTrust(decision ?? await trust.ResolveAsync(project.WorkingDirectory, null,
            false, Console.In, Console.Error));
        await reload();
        var status = $"Project resources: {(project.Trusted ? "trusted" : "not trusted")}";
        if (screen is null) Console.WriteLine(status);
        else screen.AppendThemedStatusLine(status);
    }

    public async Task ReloadAsync(Func<Task> reload)
    {
        const string progress = "Reloading keybindings, extensions, skills, prompts, themes, and context files...";
        const string status = "Reloaded keybindings, extensions, skills, prompts, themes, and context files";
        var activeScreen = screen is { IsActive: true } current ? current : null;
        activeScreen?.ShowReloadProgressPanel(progress);
        try
        {
            await reload();
            editor.ReloadKeybindings();
        }
        catch
        {
            activeScreen?.DismissReloadProgressPanel();
            throw;
        }
        if (activeScreen is not null) activeScreen.DismissEditorPanelAndAppendThemedStatusLine(status);
        else if (screen is null) Console.WriteLine(status);
    }
}
