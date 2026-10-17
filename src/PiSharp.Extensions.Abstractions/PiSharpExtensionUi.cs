namespace PiSharp.Extensions.Abstractions;

/// <summary>Safe dialog and notification operations supplied by the host.</summary>
public interface IPiSharpExtensionUi
{
    /// <summary>
    /// Shows a selector and returns the chosen option. Returns null when UI is unavailable or the
    /// user dismisses the selector. If <paramref name="cancellationToken"/> is canceled, the
    /// operation propagates cancellation.
    /// </summary>
    Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows a confirmation dialog. Returns false when UI is unavailable or the user declines or
    /// dismisses the dialog. If <paramref name="cancellationToken"/> is canceled, the operation
    /// propagates cancellation.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Shows a text input dialog and returns its value, or null when UI is unavailable or the user
    /// dismisses the dialog. If <paramref name="cancellationToken"/> is canceled, the operation
    /// propagates cancellation.
    /// </summary>
    Task<string?> InputAsync(string title, string? placeholder = null,
        CancellationToken cancellationToken = default);

    /// <summary>Shows a non-interactive notification for an informational, warning, or error message.</summary>
    void Notify(string message, string type = "info");
}

/// <summary>Mode and UI availability shared by live extension contexts.</summary>
public sealed partial record PiSharpExtensionContext
{
    /// <summary>The host mode, such as <c>tui</c>, <c>rpc</c>, or <c>print</c>.</summary>
    public string Mode { get; init; } = "unknown";

    /// <summary>Whether interactive dialogs are available in this host mode.</summary>
    public bool HasUserInterface { get; init; }

    /// <summary>The host-owned safe dialog and notification operations.</summary>
    public IPiSharpExtensionUi Ui { get; init; } = UnavailablePiSharpExtensionUi.Instance;
}

/// <summary>Mode and UI availability on a command's live host context.</summary>
public sealed partial class PiSharpExtensionCommandContext
{
    /// <summary>The host mode, such as <c>tui</c>, <c>rpc</c>, or <c>print</c>.</summary>
    public string Mode { get; init; } = "unknown";

    /// <summary>Whether interactive dialogs are available in this host mode.</summary>
    public bool HasUserInterface { get; init; }

    /// <summary>The host-owned safe dialog and notification operations.</summary>
    public IPiSharpExtensionUi Ui { get; init; } = UnavailablePiSharpExtensionUi.Instance;

    /// <summary>Creates a command context with host mode and UI capabilities.</summary>
    public PiSharpExtensionCommandContext(string workingDirectory, bool isIdle, bool isProjectTrusted,
        bool hasPendingMessages, Func<string, CancellationToken, Task> sendUserMessageAsync,
        Func<PiSharpCustomMessage, bool, PiSharpMessageDeliveryMode, CancellationToken, Task> sendCustomMessageAsync,
        string mode, bool hasUserInterface, IPiSharpExtensionUi? ui)
        : this(workingDirectory, isIdle, isProjectTrusted, hasPendingMessages,
            sendUserMessageAsync, sendCustomMessageAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        Mode = mode;
        HasUserInterface = hasUserInterface;
        Ui = ui ?? UnavailablePiSharpExtensionUi.Instance;
    }
}

internal sealed class UnavailablePiSharpExtensionUi : IPiSharpExtensionUi
{
    internal static UnavailablePiSharpExtensionUi Instance { get; } = new();

    public Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public Task<string?> InputAsync(string title, string? placeholder = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }

    public void Notify(string message, string type = "info") { }
}
