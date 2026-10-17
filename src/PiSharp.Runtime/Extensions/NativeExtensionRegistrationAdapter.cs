using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

/// <summary>Adapts the versioned public authoring contract to PiSharp's existing extension runtime.</summary>
internal sealed partial class NativeExtensionRegistrationAdapter(ExtensionRegistration registration)
    : NativeExtensions.IPiSharpExtensionBuilder
{
    private int _isConfiguring = 1;

    public void AddCommand(string name, Func<string, CancellationToken, Task<string>> handler,
        string? description = null)
    {
        EnsureConfiguring();
        registration.AddCommand(name, handler, description);
    }

    public void AddCommand(string name, NativeExtensions.PiSharpExtensionCommandHandler handler,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        registration.AddContextCommand(name, handler, description);
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
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddBeforeAgentStartHandler(async (eventData, context, cancellationToken) =>
        {
            var publicEvent = new NativeExtensions.PiSharpBeforeAgentStartEvent(
                eventData.Prompt, eventData.SystemPrompt)
            {
                MessageType = eventData.MessageType == PiSharpTurnMessageType.Custom
                    ? NativeExtensions.PiSharpTurnMessageType.Custom
                    : NativeExtensions.PiSharpTurnMessageType.User,
                StartingCustomMessage = ToPublicMessage(eventData.StartingCustomMessage),
                Images = Array.AsReadOnly(eventData.Images.Select(image =>
                    new NativeExtensions.PiSharpImageAttachment(image.MediaType, image.Data.Length)).ToArray())
            };
            var publicContext = NativeExtensionPublicContextMapper.Create(context);
            var result = await handler(publicEvent, publicContext, cancellationToken);
            return result is null
                ? null
                : new PiSharpBeforeAgentStartResult(result.SystemPrompt)
                {
                    InjectedMessage = result.InjectedMessage is { } message
                        ? ToRuntimeMessage(message, source)
                        : null
                };
        });
    }

    private static NativeExtensions.PiSharpCustomMessage? ToPublicMessage(PiSharpCustomMessage? message) =>
        message is null ? null : new(message.CustomType, message.Content, message.Display);

    private static PiSharpCustomMessage ToRuntimeMessage(NativeExtensions.PiSharpCustomMessage message, string? source) =>
        new(message.CustomType, message.Content, message.Display) { Source = source };

    public void Close() => Interlocked.Exchange(ref _isConfiguring, 0);

    private void EnsureConfiguring()
    {
        if (Volatile.Read(ref _isConfiguring) == 0)
            throw new InvalidOperationException("Extension registrations are only available during Configure.");
    }
}
