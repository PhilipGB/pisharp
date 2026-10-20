using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli.Tui;

/// <summary>Owns one active custom component request and routes its lifecycle and input.</summary>
internal sealed class NativeExtensionCustomComponentHost(
    object gate,
    Func<TerminalScreen?> getScreen,
    Func<TerminalEditor?> getEditor,
    Func<bool> canShowDialog,
    Action refresh,
    Action<string> reportInputFailure)
{
    private NativeExtensionCustomComponentRequest? _active;

    public NativeExtensionCustomComponentRequest? Active
    {
        get { lock (gate) return _active; }
    }

    public async Task<NativeExtensions.PiSharpExtensionUiCompletion<T>> ShowAsync<T>(Guid ownerId,
        Func<Action<T>, NativeExtensions.IPiSharpExtensionUiComponent> factory,
        NativeExtensions.PiSharpExtensionUiCustomOptions? options, CancellationToken cancellationToken,
        CancellationToken packageLifetime)
    {
        ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();
        if (!canShowDialog()) return NativeExtensions.PiSharpExtensionUiCompletion<T>.Cancelled;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, packageLifetime);
        var request = new NativeExtensionCustomComponentRequest<T>(ownerId, options ?? new(), Finish,
            reportInputFailure);
        var component = factory(request.Complete);
        ArgumentNullException.ThrowIfNull(component);
        request.Attach(component);
        if (request.IsCompleted) return await request.Completion.ConfigureAwait(false);

        NativeExtensionCustomComponentRequest? replaced;
        bool hasInteractiveScreen;
        lock (gate)
        {
            hasInteractiveScreen = getScreen() is { IsActive: true } && !linked.IsCancellationRequested;
            if (hasInteractiveScreen)
            {
                replaced = _active;
                _active = request;
            }
            else replaced = null;
        }
        if (!hasInteractiveScreen)
        {
            request.Cancel();
            linked.Token.ThrowIfCancellationRequested();
            return await request.Completion.ConfigureAwait(false);
        }

        replaced?.Cancel();
        request.AttachRedraw(refresh);
        refresh();
        using var cancellation = linked.Token.Register(static state =>
            ((NativeExtensionCustomComponentRequest)state!).Cancel(), request);
        try
        {
            var editor = getEditor();
            if (editor?.ShouldRunExtensionModalInputPump == true)
                await editor.RunExtensionModalInputPumpAsync(() => !request.IsCompleted, linked.Token)
                    .ConfigureAwait(false);
            return await request.Completion.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally { request.Cancel(); }
    }

    public IReadOnlyList<NativeExtensions.PiSharpExtensionUiLine>? Render(
        NativeExtensions.PiSharpExtensionUiRenderContext context, Action<string> reportFailure) =>
        Active?.Render(context, reportFailure);

    public bool CancelOnEndOfStream()
    {
        var active = Active;
        if (active is null) return false;
        active.Cancel();
        return true;
    }

    public bool HandleReservedInput(TerminalInputEvent input)
    {
        var active = Active;
        if (active is null) return false;
        if (input.Key?.Key == ConsoleKey.Escape)
        {
            active.Cancel();
            return true;
        }
        active.Cancel();
        return false;
    }

    public void CancelActive() => Active?.Cancel();

    public bool InterceptRoutedInput(TerminalInputEvent input, TerminalEditor? editor)
    {
        var active = Active;
        if (active is null) return false;
        if (input.Key?.Key == ConsoleKey.Escape)
        {
            active.Cancel();
            return true;
        }
        var hostAction = input.Key is { } key && editor?.IsHostActionKey(key) == true;
        return !hostAction && active.HandleInput(input);
    }

    public void RemoveOwner(Guid ownerId)
    {
        NativeExtensionCustomComponentRequest? active;
        lock (gate) active = _active?.OwnerId == ownerId ? _active : null;
        active?.Cancel();
    }

    private void Finish(NativeExtensionCustomComponentRequest request)
    {
        bool wasActive;
        lock (gate)
        {
            wasActive = ReferenceEquals(_active, request);
            if (wasActive) _active = null;
        }
        request.DisposeComponent();
        if (wasActive) refresh();
    }
}
