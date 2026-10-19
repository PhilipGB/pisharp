using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal sealed partial class NativeExtensionRegistrationAdapter
{
    public void AddInputHandler(NativeExtensions.PiSharpExtensionInputHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddInputHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            NativeExtensions.PiSharpInputResult? result = null;
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken, async () =>
            {
                var publicEvent = new NativeExtensions.PiSharpInputEvent(eventData.Text, eventData.Images,
                    eventData.Source, eventData.StreamingBehavior);
                result = await handler(publicEvent, publicContext, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return Snapshot(result);
        });
    }

    public void AddUserBashHandler(NativeExtensions.PiSharpExtensionUserBashHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddNativeUserBashHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            NativeExtensions.PiSharpUserBashResult? result = null;
            await InvokeWithRevocableContextAsync(publicContext, cancellationToken, async () =>
            {
                result = await handler(eventData, publicContext, cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return result;
        });
    }

    private static NativeExtensions.PiSharpInputResult? Snapshot(NativeExtensions.PiSharpInputResult? result) =>
        result?.Action switch
        {
            NativeExtensions.PiSharpInputResultAction.Continue => NativeExtensions.PiSharpInputResult.Continue,
            NativeExtensions.PiSharpInputResultAction.Handled => NativeExtensions.PiSharpInputResult.Handled,
            NativeExtensions.PiSharpInputResultAction.Transform => NativeExtensions.PiSharpInputResult.Transform(
                result.Text ?? throw new InvalidOperationException("An input transform must include replacement text."),
                result.Images),
            _ => result is null ? null : throw new InvalidOperationException("Unknown input handler result action.")
        };
}
