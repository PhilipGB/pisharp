using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli.Tui;

internal sealed class WidgetEntry(long order,
    NativeExtensions.PiSharpExtensionWidgetPlacement placement,
    IReadOnlyList<NativeExtensions.PiSharpExtensionUiLine> lines, ComponentEntry? component) : IDisposable
{
    public long Order { get; } = order;
    public NativeExtensions.PiSharpExtensionWidgetPlacement Placement { get; } = placement;
    public IReadOnlyList<NativeExtensions.PiSharpExtensionUiLine> Lines { get; private set; } = lines;
    public ComponentEntry? Component { get; } = component;

    public void Dispose() => Component?.Dispose();
}

internal sealed record PresentationContribution(
    long WorkingMessageOrder = 0,
    string? WorkingMessage = null,
    long WorkingVisibleOrder = 0,
    bool? WorkingVisible = null,
    long WorkingIndicatorOrder = 0,
    NativeExtensions.PiSharpExtensionWorkingIndicatorOptions? WorkingIndicator = null,
    long HiddenThinkingLabelOrder = 0,
    string? HiddenThinkingLabel = null);

internal sealed class EditorEditComponent(TerminalEditor editor, string title, Action<string> complete)
    : NativeExtensions.IPiSharpExtensionUiComponent
{
    private bool _disposed;

    public IReadOnlyList<NativeExtensions.PiSharpExtensionUiLine> Render(
        NativeExtensions.PiSharpExtensionUiRenderContext context)
    {
        if (_disposed) return [];
        var lines = new List<NativeExtensions.PiSharpExtensionUiLine>
        {
            new(TerminalSafeText.Normalize(title), "accent")
        };
        lines.AddRange(editor.Draft.Split('\n').Take(Math.Max(1, Math.Min(context.Height - 2, 100)))
            .Select(line => new NativeExtensions.PiSharpExtensionUiLine(line)));
        lines.Add(new("Enter: newline  Ctrl+Enter: save  Escape: cancel", "muted"));
        return lines;
    }

    public NativeExtensions.PiSharpExtensionUiInputDisposition HandleInput(
        NativeExtensions.PiSharpExtensionUiInputEvent input)
    {
        if (_disposed) return NativeExtensions.PiSharpExtensionUiInputDisposition.PassThrough;
        if (input.Key == nameof(ConsoleKey.Enter))
        {
            if (input.Control)
            {
                complete(editor.Draft);
                return NativeExtensions.PiSharpExtensionUiInputDisposition.Handled;
            }
            editor.InsertTextAtCursor("\n");
            return NativeExtensions.PiSharpExtensionUiInputDisposition.Handled;
        }
        if (input.IsPaste && input.Text is { } pasted)
        {
            editor.InsertTextAtCursor(pasted);
            return NativeExtensions.PiSharpExtensionUiInputDisposition.Handled;
        }
        if (input.Text is { Length: > 0 } text && !input.Control && !input.Alt)
        {
            editor.InsertTextAtCursor(text);
            return NativeExtensions.PiSharpExtensionUiInputDisposition.Handled;
        }
        if (input.Key is null || !Enum.TryParse<ConsoleKey>(input.Key, out var key))
            return NativeExtensions.PiSharpExtensionUiInputDisposition.PassThrough;

        var consoleKey = new ConsoleKeyInfo('\0', key, input.Shift, input.Alt, input.Control);
        if (editor.IsHostActionKey(consoleKey))
            return NativeExtensions.PiSharpExtensionUiInputDisposition.PassThrough;
        return editor.HandleExtensionEditKey(consoleKey)
            ? NativeExtensions.PiSharpExtensionUiInputDisposition.Handled
            : NativeExtensions.PiSharpExtensionUiInputDisposition.PassThrough;
    }

    public void Dispose() => _disposed = true;
}

internal sealed class NativeExtensionSelectionComponent<T> : NativeExtensions.IPiSharpExtensionUiComponent
{
    private sealed record RenderState(IReadOnlyList<string> Lines, int Width, int Height);

    private readonly TerminalSelectionList<T> _selection;
    private readonly Action<T> _complete;
    private Action? _redraw;
    private RenderState? _renderState;
    private bool _disposed;

    internal NativeExtensionSelectionComponent(string title,
        IReadOnlyList<TerminalSelectionOption<T>> options, Action<T> complete)
    {
        _selection = new(title, options);
        _complete = complete;
    }

    public IReadOnlyList<NativeExtensions.PiSharpExtensionUiLine> Render(
        NativeExtensions.PiSharpExtensionUiRenderContext context)
    {
        if (_disposed) return [];
        var lines = _selection.Render(context.Width, context.Height);
        Volatile.Write(ref _renderState, new(lines, context.Width, context.Height));
        return lines.Select(line => new NativeExtensions.PiSharpExtensionUiLine(line)).ToArray();
    }

    public NativeExtensions.PiSharpExtensionUiInputDisposition HandleInput(
        NativeExtensions.PiSharpExtensionUiInputEvent input)
    {
        if (_disposed) return NativeExtensions.PiSharpExtensionUiInputDisposition.PassThrough;
        var terminalInput = NativeExtensionInteractiveUiHost.ToTerminalInput(input);
        int? contentLine = null;
        if (terminalInput.Mouse is { } mouse && Volatile.Read(ref _renderState) is { } rendered)
            contentLine = TerminalOverlayLayout.ContentLineAt(rendered.Lines, rendered.Width, rendered.Height,
                mouse.Column, mouse.Row);
        var action = _selection.HandleInput(terminalInput, contentLine);
        if (action is TerminalSelectionAction.Accept or TerminalSelectionAction.AcceptAsDefault &&
            _selection.Selected is { } selected)
            _complete(selected.Option.Value);
        else
            Volatile.Read(ref _redraw)?.Invoke();
        return NativeExtensions.PiSharpExtensionUiInputDisposition.Handled;
    }

    public void AttachRedraw(Action requestRedraw) => Volatile.Write(ref _redraw, requestRedraw);

    public void Dispose()
    {
        _disposed = true;
        Volatile.Write(ref _redraw, null);
        Volatile.Write(ref _renderState, null);
    }
}

internal sealed class NativeExtensionInputDialogComponent(EditorBuffer buffer, string title,
    string? placeholder, Action<string> complete) : NativeExtensions.IPiSharpExtensionUiComponent
{
    private Action? _redraw;
    private bool _disposed;

    public IReadOnlyList<NativeExtensions.PiSharpExtensionUiLine> Render(
        NativeExtensions.PiSharpExtensionUiRenderContext context)
    {
        if (_disposed) return [];
        var titleLine = new NativeExtensions.PiSharpExtensionUiLine(TerminalSafeText.Normalize(title), "accent");
        var value = buffer.Text.Insert(buffer.Cursor, "▏");
        if (buffer.Text.Length == 0 && !string.IsNullOrWhiteSpace(placeholder))
            value = TerminalSafeText.Normalize(placeholder) + "▏";
        var layout = EditorViewport.Layout(value, Math.Min(buffer.Cursor, value.Length), context.Width,
            Math.Max(1, context.Height - 2), showPrompt: false);
        return new[] { titleLine }
            .Concat(layout.Rows.Select(line => new NativeExtensions.PiSharpExtensionUiLine(line)))
            .Append(new("Enter: accept  Escape: cancel", "muted"))
            .ToArray();
    }

    public NativeExtensions.PiSharpExtensionUiInputDisposition HandleInput(
        NativeExtensions.PiSharpExtensionUiInputEvent input)
    {
        if (_disposed) return NativeExtensions.PiSharpExtensionUiInputDisposition.PassThrough;
        if (input.Key == nameof(ConsoleKey.Enter) && !input.Control)
        {
            complete(buffer.Text);
            return NativeExtensions.PiSharpExtensionUiInputDisposition.Handled;
        }
        if (input.Text is { Length: > 0 } text)
            _ = buffer.InsertText(text);
        else if (input.Key is not null)
        {
            var terminalInput = NativeExtensionInteractiveUiHost.ToTerminalInput(input);
            if (terminalInput.Key is { } keyInfo)
            {
                var action = buffer.Handle(keyInfo);
                if (action == EditorAction.Submit)
                {
                    complete(buffer.Text);
                    return NativeExtensions.PiSharpExtensionUiInputDisposition.Handled;
                }
            }
        }
        Volatile.Read(ref _redraw)?.Invoke();
        return NativeExtensions.PiSharpExtensionUiInputDisposition.Handled;
    }

    public void AttachRedraw(Action requestRedraw) => Volatile.Write(ref _redraw, requestRedraw);

    public void Dispose()
    {
        _disposed = true;
        Volatile.Write(ref _redraw, null);
    }
}

internal sealed class ComponentEntry(NativeExtensions.IPiSharpExtensionUiComponent component) : IDisposable
{
    private readonly object _gate = new();
    private bool _disposed;
    private bool _disposePending;
    private int _callbackDepth;
    private NativeExtensions.PiSharpExtensionUiSize? _lastSize;
    private int _failureReported;

    public NativeExtensions.IPiSharpExtensionUiComponent Component => component;

    public NativeExtensions.IPiSharpExtensionEditorComponent? EditorComponent =>
        component as NativeExtensions.IPiSharpExtensionEditorComponent;

    public void AttachRedraw(Action redraw)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _callbackDepth++;
            try
            {
                component.AttachRedraw(() =>
                {
                    lock (_gate)
                    {
                        if (!_disposed) redraw();
                    }
                });
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _failureReported, 1) == 0)
                    TryWriteError($"Extension UI redraw registration failed: {exception.Message}");
            }
            finally
            {
                ExitCallbackUnsafe();
            }
        }
    }

    public IReadOnlyList<NativeExtensions.PiSharpExtensionUiLine> Render(
        NativeExtensions.PiSharpExtensionUiRenderContext context, Action<string> reportFailure)
    {
        lock (_gate)
        {
            if (_disposed) return [];
            _callbackDepth++;
            try
            {
                var size = new NativeExtensions.PiSharpExtensionUiSize(context.Width, context.Height);
                if (_lastSize != size)
                {
                    _lastSize = size;
                    component.OnResize(size);
                    if (_disposed) return [];
                }
                return (component.Render(context) ?? []).Take(100)
                    .Select(line => line with
                    {
                        Text = (line.Text ?? string.Empty)[..Math.Min((line.Text ?? string.Empty).Length, 4096)]
                    }).ToArray();
            }
            catch (Exception exception)
            {
                if (Interlocked.Exchange(ref _failureReported, 1) == 0)
                    reportFailure(exception.Message);
                return [new("[extension component failed]")];
            }
            finally
            {
                ExitCallbackUnsafe();
            }
        }
    }

    public NativeExtensions.PiSharpExtensionUiInputDisposition HandleInput(
        NativeExtensions.PiSharpExtensionUiInputEvent input, Action<string> reportFailure,
        out bool failed)
    {
        failed = false;
        lock (_gate)
        {
            if (_disposed) return NativeExtensions.PiSharpExtensionUiInputDisposition.PassThrough;
            _callbackDepth++;
            try { return component.HandleInput(input); }
            catch (Exception exception)
            {
                failed = true;
                if (Interlocked.Exchange(ref _failureReported, 1) == 0) reportFailure(exception.Message);
                return NativeExtensions.PiSharpExtensionUiInputDisposition.PassThrough;
            }
            finally
            {
                ExitCallbackUnsafe();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_callbackDepth != 0)
            {
                _disposePending = true;
                return;
            }
            DisposeComponentUnsafe();
        }
    }

    private void ExitCallbackUnsafe()
    {
        _callbackDepth--;
        if (_callbackDepth == 0 && _disposePending) DisposeComponentUnsafe();
    }

    private void DisposeComponentUnsafe()
    {
        _disposePending = false;
        try { component.Dispose(); }
        catch (Exception exception)
        {
            TryWriteError($"Extension UI component disposal failed: {exception.Message}");
        }
    }

    private static void TryWriteError(string message)
    {
        try { Console.Error.WriteLine(TerminalSafeText.Normalize(message)); }
        catch (Exception) { }
    }
}
