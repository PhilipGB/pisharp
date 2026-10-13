namespace PiSharp.Cli.Tui;

/// <summary>Owns captured status messages and the originating sign-in URL/copy state.</summary>
internal sealed class TerminalScreenNotifications(TerminalTranscriptBuffer transcript, Func<TerminalTheme> getTheme)
{
    private string? _authorizationOwner;

    public string? Status { get; private set; }
    public TerminalStatusNotificationKind StatusKind { get; private set; }
    public bool StatusIsExtension { get; private set; }
    public string? AuthorizationUrl { get; private set; }
    public string? CopyStatus { get; private set; }
    public TerminalStatusNotificationKind CopyStatusKind { get; private set; }

    public void PublishStatus(string safe, TerminalStatusNotificationKind kind, bool isExtension)
    {
        var prefix = kind switch
        {
            TerminalStatusNotificationKind.Warning => "Warning: ",
            TerminalStatusNotificationKind.Error => "Error: ",
            _ => ""
        };
        transcript.AppendCaptured(Environment.NewLine + " " + prefix + safe + Environment.NewLine,
            kind == TerminalStatusNotificationKind.Error);
        Status = safe;
        StatusKind = kind;
        StatusIsExtension = isExtension;
    }

    public void PublishAuthorization(string owner, string target, string notification)
    {
        _authorizationOwner = owner;
        AuthorizationUrl = target;
        CopyStatus = null;
        transcript.Append(notification, isError: false);
    }

    public void ClearAuthorization(string owner)
    {
        if (!string.Equals(_authorizationOwner, owner, StringComparison.Ordinal)) return;
        _authorizationOwner = null;
        AuthorizationUrl = null;
        CopyStatus = null;
    }

    public void SetCopyStatus(string safe, TerminalStatusNotificationKind kind)
    {
        CopyStatus = safe;
        CopyStatusKind = kind;
    }

    public string RenderAuthorizationUrl(string target, string heading, string? message,
        string? copyKeyLabel, string? cancelKeyLabel)
    {
        var safeTarget = TerminalSafeText.Normalize(target);
        var clickHint = OperatingSystem.IsMacOS() ? "Cmd+click to open" : "Ctrl+click to open";
        var open = "\u001b]8;;" + safeTarget + "\u001b\\";
        const string close = "\u001b]8;;\u001b\\";
        var keyHints = new List<string>();
        if (!string.IsNullOrWhiteSpace(cancelKeyLabel))
            keyHints.Add(getTheme().Style("dim", TerminalSafeText.Normalize(cancelKeyLabel) + " to cancel"));
        if (!string.IsNullOrWhiteSpace(copyKeyLabel))
            keyHints.Add(getTheme().Style("dim", TerminalSafeText.Normalize(copyKeyLabel) + " to copy"));
        var notification = TerminalSafeText.Normalize(heading) + "\n" +
            (message is null ? "" : message + "\n") +
            getTheme().Style("accent", open + safeTarget + close) + "\n" +
            getTheme().Style("dim", open + clickHint + close) +
            (keyHints.Count == 0 ? "" : "\n" + string.Join(" • ", keyHints)) + "\n";
        return notification;
    }
}
