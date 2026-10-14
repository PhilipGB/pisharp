using PiSharp.Cli.Tui;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Cli.Sessions;

internal sealed class TerminalSessionShare(ProviderModelRuntime providers, HttpClient http, TerminalEditor editor,
    Func<ConversationRun> getRun, Func<UserSettings> getSettings)
{
    internal async Task ShowAsync(string argument)
    {
        if (argument.Length != 0) throw new ArgumentException("/share does not accept arguments.");
        var run = getRun();
        var share = new SessionShare(http, RadiusProviderOptions.FromEnvironment(Environment.GetEnvironmentVariable),
            ct => providers.GetApiKeyForProviderAsync("radius", ct),
            (path, ct) => File.WriteAllTextAsync(path, run.ExportForShare(), ct),
            (path, ct) =>
            {
                if (run.SessionFile is null) throw new InvalidOperationException("Cannot export in-memory session to HTML");
                if (!File.Exists(run.SessionFile)) throw new InvalidOperationException("Nothing to export yet - start a conversation first");
                return SessionExport.ExportHtmlAsync(run.Conversation, path, ct, run.SharePresentation);
            }, viewerBaseUrl: Environment.GetEnvironmentVariable("PI_SHARE_VIEWER_URL") is { Length: > 0 } viewer
                ? viewer : "https://pi.dev/session/");
        try
        {
            var result = await share.ShareAsync(getSettings().ShareDestination, RunProgressAsync);
            var message = result.ShareUrl is { } url
                ? $"Share URL: {Link(url)}" + (result.GistUrl is { } gist ? $"\nGist: {Link(gist)}" : "")
                : result.Message;
            editor.DismissEditorPanelAndAppendStatus(message,
                result.IsError ? TerminalStatusNotificationKind.Error : TerminalStatusNotificationKind.Info);
        }
        finally { editor.DismissEditorPanel(); }
    }

    private async Task<SessionShareResult> RunProgressAsync(string label,
        Func<CancellationToken, Task<SessionShareResult>> operation)
    {
        var outcome = await editor.RunProgressAsync(label, "", "", "",
            (ct, update) => operation(ct), () => Task.CompletedTask, confirmCancellation: false);
        return outcome.Cancelled ? new("Share cancelled") : outcome.Value!;
    }

    private static string Link(string url)
    {
        var safe = TerminalSafeText.Normalize(url);
        return $"\u001b]8;;{safe}\u0007{safe}\u001b]8;;\u0007";
    }
}
