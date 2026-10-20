using NativeExtensions = PiSharp.Extensions.Abstractions;

namespace PiSharp.Runtime.Extensions;

internal sealed class NativeExtensionUiCallbackScope(NativeExtensions.IPiSharpExtensionUi inner)
    : NativeExtensions.IPiSharpExtensionUi, IDisposable
{
    private readonly CancellationTokenSource _revoked = new();
    private int _active = 1;

    public Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
        CancellationToken cancellationToken = default) => InvokeAsync(
        token => inner.SelectAsync(title, options, token), cancellationToken);

    public Task<bool> ConfirmAsync(string title, string message,
        CancellationToken cancellationToken = default) => InvokeAsync(
        token => inner.ConfirmAsync(title, message, token), cancellationToken);

    public Task<string?> InputAsync(string title, string? placeholder = null,
        CancellationToken cancellationToken = default) => InvokeAsync(
        token => inner.InputAsync(title, placeholder, token), cancellationToken);

    public void Notify(string message, string type = "info")
    {
        EnsureActive();
        inner.Notify(message, type);
    }

    public Task<string?> SelectWithOptionsAsync(string title, IReadOnlyList<string> options,
        NativeExtensions.PiSharpExtensionUiDialogOptions? dialogOptions = null,
        CancellationToken cancellationToken = default) => InvokeAsync(
        token => inner.SelectWithOptionsAsync(title, options, dialogOptions, token), cancellationToken);

    public Task<bool> ConfirmWithOptionsAsync(string title, string message,
        NativeExtensions.PiSharpExtensionUiDialogOptions? dialogOptions = null,
        CancellationToken cancellationToken = default) => InvokeAsync(
        token => inner.ConfirmWithOptionsAsync(title, message, dialogOptions, token), cancellationToken);

    public Task<string?> InputWithOptionsAsync(string title, string? placeholder = null,
        NativeExtensions.PiSharpExtensionUiDialogOptions? dialogOptions = null,
        CancellationToken cancellationToken = default) => InvokeAsync(
        token => inner.InputWithOptionsAsync(title, placeholder, dialogOptions, token), cancellationToken);

    public Task<NativeExtensions.PiSharpExtensionUiCompletion<T>> ShowCustomAsync<T>(
        Func<Action<T>, NativeExtensions.IPiSharpExtensionUiComponent> factory,
        NativeExtensions.PiSharpExtensionUiCustomOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return InvokeAsync(token => inner.ShowCustomAsync<T>(done =>
        {
            EnsureActive();
            return factory(value =>
            {
                EnsureActive();
                done(value);
            });
        }, options, token), cancellationToken);
    }

    public void PasteToEditor(string text)
    {
        EnsureActive();
        inner.PasteToEditor(text);
    }

    public void SetEditorText(string text)
    {
        EnsureActive();
        inner.SetEditorText(text);
    }

    public string GetEditorText()
    {
        EnsureActive();
        return inner.GetEditorText();
    }

    public Task<string?> EditAsync(string title, string? prefill = null,
        CancellationToken cancellationToken = default) => InvokeAsync(
        token => inner.EditAsync(title, prefill, token), cancellationToken);

    public void SetStatus(string key, string? text)
    {
        EnsureActive();
        inner.SetStatus(key, text);
    }

    public void SetWidget(string key, IReadOnlyList<string>? lines,
        NativeExtensions.PiSharpExtensionWidgetPlacement placement =
            NativeExtensions.PiSharpExtensionWidgetPlacement.AboveEditor)
    {
        EnsureActive();
        inner.SetWidget(key, lines, placement);
    }

    public void SetWidget(string key, NativeExtensions.IPiSharpExtensionUiComponent? component,
        NativeExtensions.PiSharpExtensionWidgetPlacement placement =
            NativeExtensions.PiSharpExtensionWidgetPlacement.AboveEditor)
    {
        EnsureActive();
        inner.SetWidget(key, component, placement);
    }

    public IDisposable OnTerminalInput(NativeExtensions.PiSharpExtensionTerminalInputHandler handler)
    {
        EnsureActive();
        return inner.OnTerminalInput(handler);
    }

    public void SetHeader(NativeExtensions.IPiSharpExtensionUiComponent? component)
    {
        EnsureActive();
        inner.SetHeader(component);
    }

    public void SetFooter(NativeExtensions.IPiSharpExtensionUiComponent? component)
    {
        EnsureActive();
        inner.SetFooter(component);
    }

    public void SetTitle(string title)
    {
        EnsureActive();
        inner.SetTitle(title);
    }

    public void SetWorkingMessage(string? message)
    {
        EnsureActive();
        inner.SetWorkingMessage(message);
    }

    public void SetWorkingVisible(bool visible)
    {
        EnsureActive();
        inner.SetWorkingVisible(visible);
    }

    public void SetWorkingIndicator(NativeExtensions.PiSharpExtensionWorkingIndicatorOptions? options)
    {
        EnsureActive();
        inner.SetWorkingIndicator(options);
    }

    public void SetHiddenThinkingLabel(string? label)
    {
        EnsureActive();
        inner.SetHiddenThinkingLabel(label);
    }

    public IDisposable AddAutocompleteProvider(NativeExtensions.PiSharpExtensionAutocompleteProvider provider)
    {
        EnsureActive();
        return inner.AddAutocompleteProvider(provider);
    }

    public void SetEditorComponent(NativeExtensions.IPiSharpExtensionEditorComponent? component)
    {
        EnsureActive();
        inner.SetEditorComponent(component);
    }

    public NativeExtensions.IPiSharpExtensionEditorComponent? GetEditorComponent()
    {
        EnsureActive();
        return inner.GetEditorComponent();
    }

    public NativeExtensions.PiSharpExtensionUiTheme Theme
    {
        get
        {
            EnsureActive();
            return inner.Theme;
        }
    }

    public IReadOnlyList<(string Name, string? Path)> GetAllThemes()
    {
        EnsureActive();
        return inner.GetAllThemes();
    }

    public NativeExtensions.PiSharpExtensionUiTheme? GetTheme(string name)
    {
        EnsureActive();
        return inner.GetTheme(name);
    }

    public NativeExtensions.PiSharpExtensionUiThemeResult SetTheme(string name)
    {
        EnsureActive();
        return inner.SetTheme(name);
    }

    public bool GetToolsExpanded()
    {
        EnsureActive();
        return inner.GetToolsExpanded();
    }

    public void SetToolsExpanded(bool expanded)
    {
        EnsureActive();
        inner.SetToolsExpanded(expanded);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _active, 0) == 0) return;
        try { _revoked.Cancel(); }
        catch (Exception error)
        {
            var safeMessage = string.Concat(error.Message.Where(character => !char.IsControl(character)));
            try { Console.Error.WriteLine($"Extension UI callback cancellation failed: {safeMessage}"); }
            catch (Exception) { }
        }
        finally { _revoked.Dispose(); }
    }

    private async Task<T> InvokeAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        EnsureActive();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _revoked.Token);
        return await operation(linked.Token).ConfigureAwait(false);
    }

    private void EnsureActive()
    {
        if (Volatile.Read(ref _active) == 0)
            throw new ObjectDisposedException(nameof(NativeExtensionUiCallbackScope),
                "The extension callback that supplied this UI capability has ended.");
    }
}
