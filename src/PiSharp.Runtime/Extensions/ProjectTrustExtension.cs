using PiSharp.Runtime.Resources;

namespace PiSharp.Runtime.Extensions;

/// <summary>Information available to a native extension while deciding whether project resources may load.</summary>
/// <param name="WorkingDirectory">The canonical project directory being evaluated.</param>
/// <param name="Mode">The Pi mode name: tui, print, json, or rpc.</param>
/// <param name="HasUserInterface">Whether interactive terminal dialogs are available.</param>
/// <param name="Ui">The startup UI methods, with Pi's headless return behavior.</param>
public sealed record ProjectTrustExtensionContext(string WorkingDirectory, string Mode, bool HasUserInterface,
    IProjectTrustExtensionUi Ui);

/// <summary>The startup UI operations available to a project-trust extension.</summary>
public interface IProjectTrustExtensionUi
{
    /// <summary>Show a selector and return the selected option; returns null when unavailable or cancelled.</summary>
    Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
        CancellationToken cancellationToken = default);

    /// <summary>Show a confirmation dialog; returns false when unavailable or cancelled.</summary>
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);

    /// <summary>Show a text input dialog; returns null when unavailable or cancelled.</summary>
    Task<string?> InputAsync(string title, string? placeholder = null,
        CancellationToken cancellationToken = default);

    /// <summary>Show a non-interactive notification for info, warning, or error messages.</summary>
    void Notify(string message, string type = "info");
}

public enum ProjectTrustExtensionDecision
{
    Undecided,
    Yes,
    No
}

public sealed record ProjectTrustExtensionResult(ProjectTrustExtensionDecision Decision, bool Remember = false);

public delegate Task<ProjectTrustExtensionResult?> ProjectTrustExtensionHandler(
    ProjectTrustExtensionContext context, CancellationToken cancellationToken);

internal sealed record RegisteredProjectTrustHandler(ProjectTrustExtensionHandler Handler,
    ResourceSourceInfo SourceInfo);

internal sealed record ProjectTrustExtensionError(string ExtensionPath, string Message);

internal sealed record ProjectTrustExtensionResolution(ProjectTrustExtensionResult? Result,
    IReadOnlyList<ProjectTrustExtensionError> Errors);

internal sealed class UnavailableProjectTrustExtensionUi : IProjectTrustExtensionUi
{
    public static UnavailableProjectTrustExtensionUi Instance { get; } = new();

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
