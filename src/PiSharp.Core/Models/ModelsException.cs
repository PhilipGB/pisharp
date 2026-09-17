namespace PiSharp.Core.Models;

/// <summary>
/// Error codes for the model/auth/stream domain (pinned pi-ai: ModelsErrorCode).
/// </summary>
public enum ModelsErrorCode
{
    ModelSource,
    ModelValidation,
    Provider,
    Stream,
    Auth,
    Oauth,
}

/// <summary>
/// Domain error carrying a code (pinned pi-ai: ModelsError). Callers surface
/// <see cref="Exception.Message"/> only, so the underlying reason is folded into it.
/// </summary>
public sealed class ModelsException : Exception
{
    public ModelsErrorCode Code { get; }

    public ModelsException(ModelsErrorCode code, string message, Exception? inner = null)
        : base(WithCauseDetail(message, inner), inner)
    {
        Code = code;
    }

    private static string WithCauseDetail(string message, Exception? cause)
    {
        if (cause is null)
        {
            return message;
        }

        var detail = cause.Message.Trim();
        if (detail.Length == 0 || message.Contains(detail, StringComparison.Ordinal))
        {
            return message;
        }

        return $"{message}: {detail}";
    }
}
