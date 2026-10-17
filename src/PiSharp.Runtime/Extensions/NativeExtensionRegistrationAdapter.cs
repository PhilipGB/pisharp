using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

/// <summary>Adapts the versioned public authoring contract to PiSharp's existing extension runtime.</summary>
internal sealed class NativeExtensionRegistrationAdapter(ExtensionRegistration registration)
    : NativeExtensions.IPiSharpExtensionBuilder
{
    private int _isConfiguring = 1;

    public void AddCommand(string name, Func<string, CancellationToken, Task<string>> handler,
        string? description = null)
    {
        EnsureConfiguring();
        registration.AddCommand(name, handler, description);
    }

    public void AddTool(Microsoft.Extensions.AI.AIFunction tool)
    {
        EnsureConfiguring();
        registration.AddTool(tool);
    }

    public void AddBeforeAgentStartHandler(NativeExtensions.PiSharpBeforeAgentStartHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        registration.AddBeforeAgentStartHandler(async (eventData, context, cancellationToken) =>
        {
            var publicEvent = new NativeExtensions.PiSharpBeforeAgentStartEvent(
                eventData.Prompt, eventData.SystemPrompt);
            var publicContext = new NativeExtensions.PiSharpExtensionContext(context.WorkingDirectory);
            var result = await handler(publicEvent, publicContext, cancellationToken);
            return result is null
                ? null
                : new PiSharpBeforeAgentStartResult(result.SystemPrompt);
        });
    }

    public void Close() => Interlocked.Exchange(ref _isConfiguring, 0);

    private void EnsureConfiguring()
    {
        if (Volatile.Read(ref _isConfiguring) == 0)
            throw new InvalidOperationException("Extension registrations are only available during Configure.");
    }
}
