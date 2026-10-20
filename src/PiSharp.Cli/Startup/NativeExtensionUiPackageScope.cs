using NativeExtensions = PiSharp.Extensions.Abstractions;
using PiSharp.Runtime.Extensions;

namespace PiSharp.Cli.Tui;

internal sealed class NativeExtensionUiPackageScope(NativeExtensionInteractiveUiHost owner, string sourcePath,
Guid ownerId, long order)
: IPiSharpExtensionUiPackageScope
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _active = 1;

    public async Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
        CancellationToken cancellationToken = default)
    {
        var (source, lifetimeToken) = CreateLinkedSource(cancellationToken);
        using var linked = source;
        return await owner.SelectAsync(ownerId, title, options, linked.Token, lifetimeToken).ConfigureAwait(false);
    }

    public async Task<string?> SelectWithOptionsAsync(string title, IReadOnlyList<string> options,
        NativeExtensions.PiSharpExtensionUiDialogOptions? dialogOptions = null,
        CancellationToken cancellationToken = default)
    {
        var (source, lifetimeToken) = CreateLinkedSource(cancellationToken);
        using var linked = source;
        return await owner.SelectWithOptionsAsync(ownerId, title, options, dialogOptions,
            linked.Token, lifetimeToken).ConfigureAwait(false);
    }

    public async Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        var (source, lifetimeToken) = CreateLinkedSource(cancellationToken);
        using var linked = source;
        return await owner.ConfirmAsync(ownerId, title, message, linked.Token, lifetimeToken).ConfigureAwait(false);
    }

    public async Task<bool> ConfirmWithOptionsAsync(string title, string message,
        NativeExtensions.PiSharpExtensionUiDialogOptions? dialogOptions = null,
        CancellationToken cancellationToken = default)
    {
        var (source, lifetimeToken) = CreateLinkedSource(cancellationToken);
        using var linked = source;
        return await owner.ConfirmWithOptionsAsync(ownerId, title, message, dialogOptions,
            linked.Token, lifetimeToken).ConfigureAwait(false);
    }

    public async Task<string?> InputAsync(string title, string? placeholder = null,
        CancellationToken cancellationToken = default)
    {
        var (source, lifetimeToken) = CreateLinkedSource(cancellationToken);
        using var linked = source;
        return await owner.InputAsync(ownerId, title, placeholder, linked.Token, lifetimeToken).ConfigureAwait(false);
    }

    public async Task<string?> InputWithOptionsAsync(string title, string? placeholder = null,
        NativeExtensions.PiSharpExtensionUiDialogOptions? dialogOptions = null,
        CancellationToken cancellationToken = default)
    {
        var (source, lifetimeToken) = CreateLinkedSource(cancellationToken);
        using var linked = source;
        return await owner.InputWithOptionsAsync(ownerId, title, placeholder, dialogOptions,
            linked.Token, lifetimeToken).ConfigureAwait(false);
    }

    public void Notify(string message, string type = "info")
    {
        InvokeActive(() => owner.Notify(ownerId, message, type));
    }

    public void SetStatus(string key, string? text)
    {
        InvokeActive(() => owner.SetStatus(ownerId, order, key, text));
    }

    public Task<NativeExtensions.PiSharpExtensionUiCompletion<T>> ShowCustomAsync<T>(
        Func<Action<T>, NativeExtensions.IPiSharpExtensionUiComponent> factory,
        NativeExtensions.PiSharpExtensionUiCustomOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return InvokeActive(() => owner.ShowCustomAsync(ownerId, factory, options,
            cancellationToken, _lifetime.Token));
    }

    public Task<string?> EditAsync(string title, string? prefill = null,
        CancellationToken cancellationToken = default)
    {
        return InvokeActive(() => owner.EditAsync(ownerId, title, prefill,
            cancellationToken, _lifetime.Token));
    }

    public void SetWidget(string key, IReadOnlyList<string>? lines,
        NativeExtensions.PiSharpExtensionWidgetPlacement placement = NativeExtensions.PiSharpExtensionWidgetPlacement.AboveEditor)
    {
        InvokeActive(() => owner.SetWidget(ownerId, order, key, lines, placement));
    }

    public void SetWidget(string key, NativeExtensions.IPiSharpExtensionUiComponent? component,
        NativeExtensions.PiSharpExtensionWidgetPlacement placement = NativeExtensions.PiSharpExtensionWidgetPlacement.AboveEditor)
    {
        InvokeActive(() => owner.SetWidget(ownerId, order, key, component, placement));
    }

    public void SetHeader(NativeExtensions.IPiSharpExtensionUiComponent? component)
    {
        InvokeActive(() => owner.SetHeader(ownerId, component));
    }

    public void SetFooter(NativeExtensions.IPiSharpExtensionUiComponent? component)
    {
        InvokeActive(() => owner.SetFooter(ownerId, component));
    }

    public IDisposable AddAutocompleteProvider(NativeExtensions.PiSharpExtensionAutocompleteProvider provider)
    {
        return InvokeActive(() => owner.AddAutocompleteProvider(ownerId, order, provider));
    }

    public void SetEditorComponent(NativeExtensions.IPiSharpExtensionEditorComponent? component)
    {
        InvokeActive(() => owner.SetEditorComponent(ownerId, component));
    }

    public NativeExtensions.IPiSharpExtensionEditorComponent? GetEditorComponent()
    {
        return InvokeActive(owner.GetEditorComponent);
    }

    public void SetTitle(string title)
    {
        InvokeActive(() => owner.SetTitle(ownerId, title));
    }

    public void SetWorkingMessage(string? message)
    {
        InvokeActive(() => owner.SetWorkingMessage(ownerId, message));
    }

    public void SetWorkingVisible(bool visible)
    {
        InvokeActive(() => owner.SetWorkingVisible(ownerId, visible));
    }

    public void SetWorkingIndicator(NativeExtensions.PiSharpExtensionWorkingIndicatorOptions? options)
    {
        InvokeActive(() => owner.SetWorkingIndicator(ownerId, options));
    }

    public void SetHiddenThinkingLabel(string? label)
    {
        InvokeActive(() => owner.SetHiddenThinkingLabel(ownerId, label));
    }

    public void PasteToEditor(string text)
    {
        InvokeActive(() => owner.PasteToEditor(ownerId, text));
    }

    public void SetEditorText(string text)
    {
        InvokeActive(() => owner.SetEditorText(ownerId, text));
    }

    public string GetEditorText()
    {
        return InvokeActive(owner.GetEditorText);
    }

    public NativeExtensions.PiSharpExtensionUiTheme Theme
    {
        get => InvokeActive(owner.GetCurrentTheme);
    }

    public IReadOnlyList<(string Name, string? Path)> GetAllThemes()
    {
        return InvokeActive(owner.GetAllThemes);
    }

    public NativeExtensions.PiSharpExtensionUiTheme? GetTheme(string name)
    {
        return InvokeActive(() => owner.GetTheme(name));
    }

    public NativeExtensions.PiSharpExtensionUiThemeResult SetTheme(string name)
    {
        return InvokeActive(() => owner.SetTheme(name));
    }

    public bool GetToolsExpanded()
    {
        return InvokeActive(owner.GetToolsExpanded);
    }

    public void SetToolsExpanded(bool expanded)
    {
        InvokeActive(() => owner.SetToolsExpanded(expanded));
    }

    public IDisposable OnTerminalInput(NativeExtensions.PiSharpExtensionTerminalInputHandler handler)
    {
        return InvokeActive(() => owner.AddTerminalInputListener(ownerId, order, handler));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_active == 0) return;
            _active = 0;
        }
        try
        {
            try { _lifetime.Cancel(); }
            catch (Exception error) { owner.ReportLifecycleFailure("Extension UI cancellation callback failed", error); }
            try { owner.RemoveOwner(ownerId); }
            catch (Exception error) { owner.ReportLifecycleFailure("Extension UI cleanup failed", error); }
        }
        finally { _lifetime.Dispose(); }
    }

    private (CancellationTokenSource Linked, CancellationToken Lifetime) CreateLinkedSource(
        CancellationToken cancellationToken) => InvokeActive(() =>
    {
        var lifetimeToken = _lifetime.Token;
        return (CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken), lifetimeToken);
    });

    private T InvokeActive<T>(Func<T> action)
    {
        lock (_gate)
        {
            EnsureActiveUnsafe();
            return action();
        }
    }

    private void InvokeActive(Action action)
    {
        lock (_gate)
        {
            EnsureActiveUnsafe();
            action();
        }
    }

    private void EnsureActiveUnsafe()
    {
        if (_active == 0)
            throw new ObjectDisposedException(nameof(NativeExtensionUiPackageScope),
                $"The UI capability for extension package '{sourcePath}' has been revoked.");
    }
}
