using PiSharp.Runtime.Sessions;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli.Composition;

internal sealed class ActiveConversationRuntime(ConversationSession conversation, ConversationRun run, string? path)
{
    private Func<ConversationRun,
        Func<string?, PiSharpExtensionSessionContext?, PiSharpExtensionCapabilities?>>? _extensionCapabilitiesFactory;

    public ConversationSession Conversation { get; private set; } = conversation;
    public ConversationRun Run { get; private set; } = run;
    public string? Path { get; private set; } = path;

    public void Adopt(ConversationSession session, ConversationRun execution, string? sessionPath)
    {
        Conversation = session;
        Run = execution;
        Path = sessionPath;
        ApplyExtensionCapabilitiesFactory(execution);
    }

    public void ReplaceRun(ConversationRun execution)
    {
        Run = execution;
        ApplyExtensionCapabilitiesFactory(execution);
    }

    public void SetExtensionCapabilitiesFactory(
        Func<ConversationRun,
            Func<string?, PiSharpExtensionSessionContext?, PiSharpExtensionCapabilities?>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _extensionCapabilitiesFactory = factory;
        ApplyExtensionCapabilitiesFactory(Run);
    }

    private void ApplyExtensionCapabilitiesFactory(ConversationRun run)
    {
        if (_extensionCapabilitiesFactory is { } factory)
            run.SetExtensionCapabilitiesFactory(factory(run));
    }
}
