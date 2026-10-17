using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

/// <summary>UI operations available to an extension in interactive and headless modes.</summary>
public interface IPiSharpExtensionUi : NativeExtensions.IPiSharpExtensionUi
{
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
    private Func<bool> _hasPendingMessages = static () => false;

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
    public bool HasPendingMessages => Volatile.Read(ref _hasPendingMessages)();

    internal void SetPendingMessageState(Func<bool> getPendingMessages)
    {
        ArgumentNullException.ThrowIfNull(getPendingMessages);
        Volatile.Write(ref _hasPendingMessages, getPendingMessages);
    }
}

/// <summary>Receives a lifecycle event and live extension context at the point the event is emitted.</summary>
public delegate void PiSharpExtensionLifecycleHandler(AgentLifecycleEvent eventData,
    PiSharpExtensionContext context);

/// <summary>A lifecycle callback registered by one extension and retained with source provenance.</summary>
public sealed record ExtensionLifecycleHandlerRegistration(PiSharpExtensionLifecycleHandler Handler,
    ResourceSourceInfo SourceInfo);
