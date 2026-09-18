namespace PiSharp.Core.Models.Providers;

/// <summary>
/// The single seam that decides which catalogued models this build can actually execute.
///
/// Catalogue breadth (the providers/models Pi knows about) is intentionally wider than
/// execution breadth (the wire APIs this runtime's provider bridge can send requests
/// over). Today the bridge executes only the OpenAI-compatible chat-completions API
/// (<see cref="ModelApi.OpenAiCompletions"/>); models on other APIs are catalogued,
/// listed, and authenticated, but must never be selectable. Every model-selection
/// surface (startup resolution, /model, model cycling, scoped models, post-login
/// selection, --list-models) filters or guards through <see cref="CanExecute"/> so the
/// user is never offered a model this runtime cannot run.
/// </summary>
public static class ModelExecutionSupport
{
    /// <summary>Wire APIs this build's provider bridge can execute.</summary>
    public static IReadOnlyCollection<string> SupportedApis { get; } = [ModelApi.OpenAiCompletions];

    /// <summary>True when this build can send a request for <paramref name="model"/>.</summary>
    public static bool CanExecute(ModelInfo model) => SupportedApis.Contains(model.Api);

    /// <summary>
    /// The executable subset of <paramref name="models"/> (selection surfaces use this);
    /// ordering is preserved.
    /// </summary>
    public static IEnumerable<ModelInfo> Executable(IEnumerable<ModelInfo> models) =>
        models.Where(CanExecute);

    /// <summary>
    /// Why a model cannot be executed (null when it can). Keeps "the catalogue knows this
    /// model" and "this build can run this model" distinct in user-facing messages.
    /// </summary>
    public static string? InexecutableReason(ModelInfo model) =>
        CanExecute(model)
            ? null
            : $"provider API '{model.Api}' is not supported by this build (supported APIs: {string.Join(", ", SupportedApis)})";
}

/// <summary>
/// Raised when a model whose wire API this build cannot execute is selected. Thrown by the
/// selection boundary (<c>ModelSessionState.SetModelAsync</c>) and by CLI model resolution;
/// a model on an unsupported API is rejected before any session entry is appended.
/// </summary>
public sealed class ModelNotExecutableException : InvalidOperationException
{
    public ModelNotExecutableException(ModelInfo model)
        : base($"Model {model.Reference} cannot be executed by this build: {ModelExecutionSupport.InexecutableReason(model)}.")
    {
        Model = model;
    }

    /// <summary>The model that could not be selected.</summary>
    public ModelInfo Model { get; }
}
