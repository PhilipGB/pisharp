using PiSharp.Runtime.Extensions;
using PiSharp.Cli.Protocols;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.Cli.Tui;

/// <summary>Adapts project-trust dialogs and compiled-extension UI hosting to the CLI startup boundary.</summary>
internal sealed class ProjectTrustStartupUi : IProjectTrustExtensionUi, IPiSharpExtensionUiPackageFactory
{
    private readonly NativeExtensionInteractiveUiHost _interactiveUi;

    internal ProjectTrustStartupUi(bool hasUserInterface, string mode, TextWriter output, TextWriter error) =>
        _interactiveUi = new(hasUserInterface, mode, output, error);

    internal void SetInteractiveNotificationHandler(Action<string, string> handler) =>
        _interactiveUi.SetInteractiveNotificationHandler(handler);

    internal IDisposable BindInteractiveScreen(TerminalScreen screen, TerminalEditor? editor = null,
        TerminalPresentation? presentation = null) =>
        _interactiveUi.BindInteractiveScreen(screen, editor, presentation);

    internal void BindRpcUiTransport(RpcExtensionUiTransport transport) =>
        _interactiveUi.BindRpcUiTransport(transport);

    internal void UnbindRpcUiTransport(RpcExtensionUiTransport transport) =>
        _interactiveUi.UnbindRpcUiTransport(transport);

    public IPiSharpExtensionUiPackageScope CreatePackageScope(string sourcePath) =>
        _interactiveUi.CreatePackageScope(sourcePath);

    internal TerminalInputInterception InterceptTerminalInput(TerminalInputEvent input, bool reserved) =>
        _interactiveUi.InterceptTerminalInput(input, reserved);

    public Task<string?> SelectAsync(string title, IReadOnlyList<string> options,
        CancellationToken cancellationToken = default) =>
        _interactiveUi.SelectAsync(title, options, cancellationToken);

    public Task<string?> SelectWithOptionsAsync(string title, IReadOnlyList<string> options,
        PiSharpExtensionUiDialogOptions? dialogOptions = null, CancellationToken cancellationToken = default) =>
        _interactiveUi.SelectWithOptionsAsync(Guid.Empty, title, options, dialogOptions,
            cancellationToken, CancellationToken.None);

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) =>
        _interactiveUi.ConfirmAsync(title, message, cancellationToken);

    public Task<bool> ConfirmWithOptionsAsync(string title, string message,
        PiSharpExtensionUiDialogOptions? dialogOptions = null, CancellationToken cancellationToken = default) =>
        _interactiveUi.ConfirmWithOptionsAsync(Guid.Empty, title, message, dialogOptions,
            cancellationToken, CancellationToken.None);

    public Task<string?> InputAsync(string title, string? placeholder = null,
        CancellationToken cancellationToken = default) =>
        _interactiveUi.InputAsync(title, placeholder, cancellationToken);

    public Task<string?> InputWithOptionsAsync(string title, string? placeholder = null,
        PiSharpExtensionUiDialogOptions? dialogOptions = null, CancellationToken cancellationToken = default) =>
        _interactiveUi.InputWithOptionsAsync(Guid.Empty, title, placeholder, dialogOptions,
            cancellationToken, CancellationToken.None);

    public Task<string?> EditAsync(string title, string? prefill = null,
        CancellationToken cancellationToken = default) =>
        _interactiveUi.EditAsync(Guid.Empty, title, prefill, cancellationToken, CancellationToken.None);

    public Task<PiSharpExtensionUiCompletion<T>> ShowCustomAsync<T>(
        Func<Action<T>, IPiSharpExtensionUiComponent> factory,
        PiSharpExtensionUiCustomOptions? options = null, CancellationToken cancellationToken = default) =>
        _interactiveUi.ShowCustomAsync(Guid.Empty, factory, options, cancellationToken, CancellationToken.None);

    public void PasteToEditor(string text) => _interactiveUi.PasteToEditor(text);

    public void SetEditorText(string text) => _interactiveUi.SetEditorText(text);

    public string GetEditorText() => _interactiveUi.GetEditorText();

    public void SetStatus(string key, string? text) => _interactiveUi.SetStatus(Guid.Empty, 0, key, text);

    public void SetWidget(string key, IReadOnlyList<string>? lines,
        PiSharpExtensionWidgetPlacement placement = PiSharpExtensionWidgetPlacement.AboveEditor) =>
        _interactiveUi.SetWidget(Guid.Empty, 0, key, lines, placement);

    public void SetWidget(string key, IPiSharpExtensionUiComponent? component,
        PiSharpExtensionWidgetPlacement placement = PiSharpExtensionWidgetPlacement.AboveEditor) =>
        _interactiveUi.SetWidget(Guid.Empty, 0, key, component, placement);

    public void SetHeader(IPiSharpExtensionUiComponent? component) => _interactiveUi.SetHeader(Guid.Empty, component);

    public void SetFooter(IPiSharpExtensionUiComponent? component) => _interactiveUi.SetFooter(Guid.Empty, component);

    public IDisposable OnTerminalInput(PiSharpExtensionTerminalInputHandler handler) =>
        _interactiveUi.AddTerminalInputListener(Guid.Empty, 0, handler);

    public void SetTitle(string title) => _interactiveUi.SetTitle(Guid.Empty, title);

    public void SetWorkingMessage(string? message) => _interactiveUi.SetWorkingMessage(Guid.Empty, message);

    public void SetWorkingVisible(bool visible) => _interactiveUi.SetWorkingVisible(Guid.Empty, visible);

    public void SetWorkingIndicator(PiSharpExtensionWorkingIndicatorOptions? options) =>
        _interactiveUi.SetWorkingIndicator(Guid.Empty, options);

    public void SetHiddenThinkingLabel(string? label) => _interactiveUi.SetHiddenThinkingLabel(Guid.Empty, label);

    public IDisposable AddAutocompleteProvider(PiSharpExtensionAutocompleteProvider provider) =>
        _interactiveUi.AddAutocompleteProvider(Guid.Empty, 0, provider);

    public void SetEditorComponent(IPiSharpExtensionEditorComponent? component) =>
        _interactiveUi.SetEditorComponent(Guid.Empty, component);

    public IPiSharpExtensionEditorComponent? GetEditorComponent() => _interactiveUi.GetEditorComponent();

    public PiSharpExtensionUiTheme Theme => _interactiveUi.GetCurrentTheme();

    public IReadOnlyList<(string Name, string? Path)> GetAllThemes() => _interactiveUi.GetAllThemes();

    public PiSharpExtensionUiTheme? GetTheme(string name) => _interactiveUi.GetTheme(name);

    public PiSharpExtensionUiThemeResult SetTheme(string name) => _interactiveUi.SetTheme(name);

    public bool GetToolsExpanded() => _interactiveUi.GetToolsExpanded();

    public void SetToolsExpanded(bool expanded) => _interactiveUi.SetToolsExpanded(expanded);

    public void Notify(string message, string type = "info") => _interactiveUi.Notify(message, type);
}
