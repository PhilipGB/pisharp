using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal sealed partial class NativeExtensionRegistrationAdapter
{
    public void AddSessionInfoChangedHandler(NativeExtensions.PiSharpExtensionSessionInfoChangedHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddSessionInfoChangedHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try { await handler(eventData, publicContext, cancellationToken).ConfigureAwait(false); }
            finally { publicContext.Dispose(); }
        });
    }

    public void AddSessionCompactHandler(NativeExtensions.PiSharpExtensionSessionCompactHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddSessionCompactHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try { await handler(eventData, publicContext, cancellationToken).ConfigureAwait(false); }
            finally { publicContext.Dispose(); }
        });
    }

    public void AddSessionCompactFailedHandler(NativeExtensions.PiSharpExtensionSessionCompactFailedHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddSessionCompactFailedHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try { await handler(eventData, publicContext, cancellationToken).ConfigureAwait(false); }
            finally { publicContext.Dispose(); }
        });
    }

    public void AddAgentStartHandler(NativeExtensions.PiSharpExtensionAgentStartHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddAgentStartHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try { await handler(eventData, publicContext, cancellationToken).ConfigureAwait(false); }
            finally { publicContext.Dispose(); }
        });
    }

    public void AddAgentEndHandler(NativeExtensions.PiSharpExtensionAgentEndHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddAgentEndHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try { await handler(eventData, publicContext, cancellationToken).ConfigureAwait(false); }
            finally { publicContext.Dispose(); }
        });
    }

    public void AddTurnStartHandler(NativeExtensions.PiSharpExtensionTurnStartHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddTurnStartHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try { await handler(eventData, publicContext, cancellationToken).ConfigureAwait(false); }
            finally { publicContext.Dispose(); }
        });
    }

    public void AddTurnEndHandler(NativeExtensions.PiSharpExtensionTurnEndHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddTurnEndHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try { await handler(eventData, publicContext, cancellationToken).ConfigureAwait(false); }
            finally { publicContext.Dispose(); }
        });
    }

    public void AddToolExecutionStartHandler(NativeExtensions.PiSharpExtensionToolExecutionStartHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddToolExecutionStartHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try { await handler(eventData, publicContext, cancellationToken).ConfigureAwait(false); }
            finally { publicContext.Dispose(); }
        });
    }

    public void AddToolExecutionUpdateHandler(NativeExtensions.PiSharpExtensionToolExecutionUpdateHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddToolExecutionUpdateHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try { await handler(eventData, publicContext, cancellationToken).ConfigureAwait(false); }
            finally { publicContext.Dispose(); }
        });
    }

    public void AddToolExecutionEndHandler(NativeExtensions.PiSharpExtensionToolExecutionEndHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddToolExecutionEndHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try { await handler(eventData, publicContext, cancellationToken).ConfigureAwait(false); }
            finally { publicContext.Dispose(); }
        });
    }

    public void AddModelSelectHandler(NativeExtensions.PiSharpExtensionModelSelectHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddModelSelectHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try { await handler(eventData, publicContext, cancellationToken).ConfigureAwait(false); }
            finally { publicContext.Dispose(); }
        });
    }

    public void AddThinkingLevelSelectHandler(NativeExtensions.PiSharpExtensionThinkingLevelSelectHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureConfiguring();
        var source = registration.CurrentSourceInfo?.Path;
        registration.AddThinkingLevelSelectHandler(async (eventData, context, cancellationToken) =>
        {
            var publicContext = NativeExtensionPublicContextMapper.Create(context, source);
            try { await handler(eventData, publicContext, cancellationToken).ConfigureAwait(false); }
            finally { publicContext.Dispose(); }
        });
    }
}
