using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;

namespace PiSharp.Runtime.Extensions;

/// <summary>UI operations available to an extension in interactive and headless modes.</summary>
public interface IPiSharpExtensionUi
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

/// <summary>Project-scoped configuration used to build the live context exposed during tool execution.</summary>
public sealed record PiSharpExtensionContextConfiguration(string WorkingDirectory, string Mode,
    bool HasUserInterface, bool IsProjectTrusted, IPiSharpExtensionUi Ui);

/// <summary>Read-only project and conversation state available to a native extension tool invocation.</summary>
public sealed class PiSharpExtensionContext
{
    private readonly Func<ConversationSession?> _getConversation;
    private readonly Func<IReadOnlyList<string>> _getActiveToolNames;
    private readonly Func<bool> _isIdle;

    internal PiSharpExtensionContext(PiSharpExtensionContextConfiguration configuration,
        Func<ConversationSession?> getConversation, Func<IReadOnlyList<string>> getActiveToolNames,
        Func<bool> isIdle)
    {
        Configuration = configuration;
        _getConversation = getConversation;
        _getActiveToolNames = getActiveToolNames;
        _isIdle = isIdle;
    }

    private PiSharpExtensionContextConfiguration Configuration { get; }

    public string WorkingDirectory => Configuration.WorkingDirectory;
    public string Mode => Configuration.Mode;
    public bool HasUserInterface => Configuration.HasUserInterface;
    public bool IsProjectTrusted => Configuration.IsProjectTrusted;
    public IPiSharpExtensionUi Ui => Configuration.Ui;
    public bool IsIdle => _isIdle();
    public string? CurrentModelId => _getConversation()?.Model;
    public string? CurrentProviderId => _getConversation()?.Provider;
    public string? SessionId => _getConversation()?.Id;
    public string? SessionName => _getConversation()?.Name;
    public string? ConversationHeadId => _getConversation()?.Tree.HeadId;
    public IReadOnlyList<string> ActiveToolNames => _getActiveToolNames();
}

/// <summary>Receives a lifecycle event and live extension context at the point the event is emitted.</summary>
public delegate void PiSharpExtensionLifecycleHandler(AgentLifecycleEvent eventData,
    PiSharpExtensionContext context);

/// <summary>A lifecycle callback registered by one extension and retained with source provenance.</summary>
public sealed record ExtensionLifecycleHandlerRegistration(PiSharpExtensionLifecycleHandler Handler,
    ResourceSourceInfo SourceInfo);
