using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli.Tui;

internal abstract class NativeExtensionCustomComponentRequest(Guid ownerId,
    NativeExtensions.PiSharpExtensionUiCustomOptions options,
    Action<NativeExtensionCustomComponentRequest> finish,
    Action<string> reportInputFailure)
{
    private readonly object _componentGate = new();
    private ComponentEntry? _component;
    private bool _componentDisposed;

    internal Guid OwnerId { get; } = ownerId;
    internal NativeExtensions.PiSharpExtensionUiCustomOptions Options { get; } = options;
    internal abstract bool IsCompleted { get; }
    internal abstract void Cancel();

    internal void Attach(NativeExtensions.IPiSharpExtensionUiComponent component)
    {
        var entry = new ComponentEntry(component);
        var dispose = false;
        lock (_componentGate)
        {
            if (IsCompleted || _componentDisposed) dispose = true;
            else _component = entry;
        }
        if (dispose) entry.Dispose();
    }

    internal void AttachRedraw(Action redraw)
    {
        ComponentEntry? component;
        lock (_componentGate) component = _component;
        component?.AttachRedraw(redraw);
    }

    internal IReadOnlyList<NativeExtensions.PiSharpExtensionUiLine> Render(
        NativeExtensions.PiSharpExtensionUiRenderContext context, Action<string> reportFailure)
    {
        ComponentEntry? component;
        lock (_componentGate) component = _component;
        return component?.Render(context with { IsFocused = true }, message =>
        {
            reportFailure(message);
            Cancel();
        }) ?? [];
    }

    internal bool HandleInput(TerminalInputEvent input)
    {
        ComponentEntry? component;
        lock (_componentGate) component = _component;
        if (component is null || IsCompleted) return false;
        var result = component.HandleInput(NativeExtensionInteractiveUiHost.ToExtensionInput(input),
            reportInputFailure, out var failed);
        if (failed)
        {
            Cancel();
            return true;
        }
        return result == NativeExtensions.PiSharpExtensionUiInputDisposition.Handled;
    }

    internal void DisposeComponent()
    {
        ComponentEntry? component;
        lock (_componentGate)
        {
            if (_componentDisposed) return;
            _componentDisposed = true;
            component = _component;
            _component = null;
        }
        component?.Dispose();
    }

    protected void Finish() => finish(this);
}

internal sealed class NativeExtensionCustomComponentRequest<T> : NativeExtensionCustomComponentRequest
{
    private readonly TaskCompletionSource<NativeExtensions.PiSharpExtensionUiCompletion<T>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completed;

    internal NativeExtensionCustomComponentRequest(Guid ownerId,
        NativeExtensions.PiSharpExtensionUiCustomOptions options,
        Action<NativeExtensionCustomComponentRequest> finish,
        Action<string> reportInputFailure)
        : base(ownerId, options, finish, reportInputFailure)
    {
    }

    internal Task<NativeExtensions.PiSharpExtensionUiCompletion<T>> Completion => _completion.Task;
    internal override bool IsCompleted => Volatile.Read(ref _completed) != 0;

    internal void Complete(T value) => CompleteWith(NativeExtensions.PiSharpExtensionUiCompletion<T>.Completed(value));

    internal override void Cancel() => CompleteWith(NativeExtensions.PiSharpExtensionUiCompletion<T>.Cancelled);

    private void CompleteWith(NativeExtensions.PiSharpExtensionUiCompletion<T> completion)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        Finish();
        _completion.TrySetResult(completion);
    }
}
