using PiSharp.Runtime.Extensions;

namespace PiSharp.Cli.Tui;

/// <summary>Provides Pi-shaped extension dialogs before the main interactive screen is created.</summary>
internal sealed class ProjectTrustStartupUi(bool hasUserInterface, string mode, TextWriter output, TextWriter error)
    : IProjectTrustExtensionUi
{
    private bool CanShowDialog => hasUserInterface && mode == "interactive";

    public Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanShowDialog || options.Count == 0) return Task.FromResult<string?>(null);

        using var screen = CreateScreen(out var editor);
        var terminalOptions = options.Select((option, index) =>
            new TerminalSelectionOption<string>(index.ToString(), option, option)).ToArray();
        var selected = editor.ShowInlineSelectionList(title, terminalOptions,
            footer: "↑↓ navigate  enter select  escape/ctrl+c cancel",
            cancellationToken: cancellationToken, extensionDialog: true);
        return Task.FromResult(selected?.Option.Value);
    }

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanShowDialog) return Task.FromResult(false);

        using var screen = CreateScreen(out var editor);
        var options = new[]
        {
            new TerminalSelectionOption<bool>("yes", true, "Yes"),
            new TerminalSelectionOption<bool>("no", false, "No")
        };
        var selected = editor.ShowInlineSelectionList($"{title}\n{message}", options,
            footer: "↑↓ navigate  enter select  escape/ctrl+c cancel",
            cancellationToken: cancellationToken, extensionDialog: true);
        return Task.FromResult(selected?.Option.Value ?? false);
    }

    public async Task<string?> InputAsync(string title, string? placeholder = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanShowDialog) return null;

        using var screen = CreateScreen(out var editor);
        var values = await editor.PromptSequenceAsync(title, [(title, placeholder)],
            cancellationToken: cancellationToken, extensionDialog: true).ConfigureAwait(false);
        return values?.FirstOrDefault();
    }

    public void Notify(string message, string type = "info")
    {
        if (mode == "interactive") return;
        var color = type switch
        {
            "error" => "31",
            "warning" => "33",
            _ => "36"
        };
        var styledMessage = !Console.IsErrorRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null
            ? $"\u001b[{color}m{message}\u001b[0m"
            : message;
        error.WriteLine(styledMessage);
        error.Flush();
    }

    private TerminalScreen CreateScreen(out TerminalEditor editor)
    {
        var screen = new TerminalScreen(output, error, useAlternateScreen: false, deferInitialRender: true);
        screen.Activate();
        editor = new TerminalEditor();
        editor.AttachScreen(screen);
        return screen;
    }
}
