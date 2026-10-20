using PiSharp.Runtime.Resources;
using PiSharp.Runtime.Sessions;
using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

/// <summary>UI operations available to an extension in interactive and headless modes.</summary>
public interface IPiSharpExtensionUi : NativeExtensions.IPiSharpExtensionUi
{
}

/// <summary>A package-owned UI capability that is revoked and cleaned up with its loaded extension package.</summary>
public interface IPiSharpExtensionUiPackageScope : IPiSharpExtensionUi, IDisposable
{
}

/// <summary>Creates one independent retained UI scope for a compiled extension package.</summary>
public interface IPiSharpExtensionUiPackageFactory
{
    IPiSharpExtensionUiPackageScope CreatePackageScope(string sourcePath);
}

/// <summary>Project-scoped configuration used to build the live context exposed during tool execution.</summary>
public sealed record PiSharpExtensionContextConfiguration(string WorkingDirectory, string Mode,
    bool HasUserInterface, bool IsProjectTrusted, IPiSharpExtensionUi Ui)
{
    /// <summary>Creates a callback-scoped UI handle for the extension source being invoked.</summary>
    public Func<string?, NativeExtensions.IPiSharpExtensionUi>? CreateCallbackUi { get; init; }
}

/// <summary>Read-only project and conversation state available to a native extension tool invocation.</summary>
public sealed class PiSharpExtensionContext
{
    private readonly Func<ConversationSession?> _getConversation;
    private readonly Func<IReadOnlyList<string>> _getActiveToolNames;
    private readonly Func<bool> _isIdle;
    private Func<bool> _hasPendingMessages = static () => false;
    private Func<string?, NativeExtensions.PiSharpExtensionSessionContext?> _getSessionContext = static _ => null;
    private Func<string?, NativeExtensions.PiSharpExtensionSessionContext?,
        NativeExtensions.PiSharpExtensionCapabilities?> _getCapabilities = static (_, _) => null;

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

    internal NativeExtensions.IPiSharpExtensionUi CreateCallbackUi(string? sourcePath) =>
        Configuration.CreateCallbackUi?.Invoke(sourcePath) ?? new NativeExtensionUiCallbackScope(Configuration.Ui);
    public bool IsIdle => _isIdle();
    public string? CurrentModelId => _getConversation()?.Model;
    public string? CurrentProviderId => _getConversation()?.Provider;
    public string? SessionId => _getConversation()?.Id;
    public string? SessionName => _getConversation()?.Name;
    public string? ConversationHeadId => _getConversation()?.Tree.HeadId;
    public IReadOnlyList<string> ActiveToolNames => _getActiveToolNames();
    public bool HasPendingMessages => Volatile.Read(ref _hasPendingMessages)();

    internal NativeExtensions.PiSharpExtensionSessionContext? CreatePublicSessionContext(string? sourcePath) =>
        Volatile.Read(ref _getSessionContext)(sourcePath);

    internal NativeExtensions.PiSharpExtensionCapabilities? CreatePublicCapabilities(string? sourcePath,
        NativeExtensions.PiSharpExtensionSessionContext? session) =>
        Volatile.Read(ref _getCapabilities)(sourcePath, session);

    internal void SetSessionContextFactory(
        Func<string?, NativeExtensions.PiSharpExtensionSessionContext?> getSessionContext)
    {
        ArgumentNullException.ThrowIfNull(getSessionContext);
        Volatile.Write(ref _getSessionContext, getSessionContext);
    }

    internal void SetCapabilitiesFactory(
        Func<string?, NativeExtensions.PiSharpExtensionSessionContext?,
            NativeExtensions.PiSharpExtensionCapabilities?> getCapabilities)
    {
        ArgumentNullException.ThrowIfNull(getCapabilities);
        Volatile.Write(ref _getCapabilities, getCapabilities);
    }

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
