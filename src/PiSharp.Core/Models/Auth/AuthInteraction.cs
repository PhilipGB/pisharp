namespace PiSharp.Core.Models.Auth;

/// <summary>
/// Prompt shown to the user during login (pinned pi-ai: AuthPrompt). Select prompts
/// resolve to the option id.
/// </summary>
public abstract record AuthPromptStep
{
    /// <summary>
    /// Cancellation for this prompt only; out-of-band resolution (e.g. a manual code
    /// prompt raced against a callback server) cancels the pending prompt.
    /// </summary>
    public CancellationToken PromptCancellationToken { get; init; }
}

/// <summary>Plain text prompt.</summary>
public sealed record TextPromptStep(string Message, string? Placeholder = null) : AuthPromptStep;

/// <summary>Secret input (API key, codes).</summary>
public sealed record SecretPromptStep(string Message, string? Placeholder = null) : AuthPromptStep;

/// <summary>Option selection; resolves to the selected option id.</summary>
public sealed record SelectPromptStep(
    string Message,
    IReadOnlyList<SelectableOption> Options) : AuthPromptStep;

/// <summary>Manual entry of a code shown by an out-of-band flow.</summary>
public sealed record ManualCodePromptStep(string Message, string? Placeholder = null) : AuthPromptStep;

/// <summary>One selectable option for <see cref="SelectPromptStep"/>.</summary>
public sealed record SelectableOption(string Id, string Label, string? Description = null);

/// <summary>
/// Events emitted during a login flow (pinned pi-ai: AuthEvent): info, auth_url,
/// device_code, progress.
/// </summary>
public abstract record AuthEvent
{
    /// <summary>Informational message, optionally with links.</summary>
    public sealed record InfoEvent(string Message, IReadOnlyList<InfoLink>? Links = null) : AuthEvent;

    /// <summary>The user should open this URL in a browser.</summary>
    public sealed record AuthUrlEvent(string Url, string? Instructions = null) : AuthEvent;

    /// <summary>A device code is waiting for user approval at the verification URI.</summary>
    public sealed record DeviceCodeEvent(
        string UserCode,
        string VerificationUri,
        int? IntervalSeconds = null,
        int? ExpiresInSeconds = null) : AuthEvent;

    /// <summary>Progress update.</summary>
    public sealed record ProgressEvent(string Message) : AuthEvent;
}

/// <summary>A link attached to an info event.</summary>
public sealed record InfoLink(string Url, string? Label = null);

/// <summary>
/// Login interaction callbacks serving both api-key and OAuth flows (pinned pi-ai:
/// ProviderAuthInteraction). <see cref="PromptCancellationToken"/> aborts the whole
/// flow; per-prompt cancellation uses <see cref="AuthPromptStep.PromptCancellationToken"/>.
/// </summary>
public interface IAuthInteraction
{
    /// <summary>Aborts the whole login flow (pinned AuthInteraction.signal).</summary>
    CancellationToken Signal { get; }

    /// <summary>
    /// Prompts the user and returns the entered/selected string (select returns the
    /// option id). Rejects on cancel/abort.
    /// </summary>
    Task<string> PromptAsync(AuthPromptStep prompt, CancellationToken cancellationToken = default);

    /// <summary>Emits a progress/status event to the user.</summary>
    void Notify(AuthEvent evt);
}
