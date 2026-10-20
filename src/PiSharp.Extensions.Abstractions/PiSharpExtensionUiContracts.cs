namespace PiSharp.Extensions.Abstractions;

/// <summary>Where a persistent extension widget is placed relative to the editor.</summary>
public enum PiSharpExtensionWidgetPlacement
{
    /// <summary>Render before the editor.</summary>
    AboveEditor,
    /// <summary>Render after the editor.</summary>
    BelowEditor
}

/// <summary>How a focused extension component is hosted.</summary>
public enum PiSharpExtensionUiHostingMode
{
    /// <summary>Temporarily owns editor focus.</summary>
    Focused,
    /// <summary>Renders in the host overlay layer and owns focus while visible.</summary>
    Overlay
}

/// <summary>The result of one extension component input callback.</summary>
public enum PiSharpExtensionUiInputDisposition
{
    /// <summary>Let normal editor or shortcut routing process the input.</summary>
    PassThrough,
    /// <summary>The component handled the input.</summary>
    Handled
}

/// <summary>Terminal event data decoded by the host parser, with the original input bytes as UTF-8 text.</summary>
public sealed record PiSharpExtensionUiInputEvent(string? Key, string? Text, bool IsPaste = false,
    bool Shift = false, bool Alt = false, bool Control = false)
{
    /// <summary>The original terminal data for this event, when available.</summary>
    public string? RawData { get; init; }

    /// <summary>Mouse coordinates and button data for a parsed terminal mouse report.</summary>
    public PiSharpExtensionUiMouseEvent? Mouse { get; init; }
}

/// <summary>Parsed SGR mouse report coordinates are one-based terminal cells.</summary>
public sealed record PiSharpExtensionUiMouseEvent(int Button, int Column, int Row, bool IsRelease);

/// <summary>Result of an extension raw-input listener.</summary>
public sealed record PiSharpExtensionTerminalInputResult(bool Consumed, string? ReplacementText = null)
{
    /// <summary>Pass the decoded event to subsequent listeners and host routing.</summary>
    public static PiSharpExtensionTerminalInputResult PassThrough { get; } = new(false);
    /// <summary>Stop subsequent listeners and host routing for this event.</summary>
    public static PiSharpExtensionTerminalInputResult Consume { get; } = new(true);
    /// <summary>Replace the event's text before it reaches later listeners or host routing.</summary>
    public static PiSharpExtensionTerminalInputResult Rewrite(string text) => new(false, text);
}

/// <summary>Callback for decoded terminal input. Internal terminal protocol replies are filtered by the host.</summary>
public delegate PiSharpExtensionTerminalInputResult? PiSharpExtensionTerminalInputHandler(
    PiSharpExtensionUiInputEvent input);

/// <summary>Size of the interactive terminal in character cells.</summary>
public sealed record PiSharpExtensionUiSize(int Width, int Height);

/// <summary>A line of component output with an optional host theme style name.</summary>
public sealed record PiSharpExtensionUiLine(string Text, string? Style = null);

/// <summary>Detached theme style data safe to expose to an extension package.</summary>
public sealed record PiSharpExtensionUiThemeStyle(string? Foreground, string? Background,
    bool Bold = false, bool Dim = false, bool Italic = false);

/// <summary>Detached theme information; it does not expose the CLI terminal theme implementation.</summary>
public sealed record PiSharpExtensionUiTheme(string Name,
    IReadOnlyDictionary<string, PiSharpExtensionUiThemeStyle> Styles)
{
    /// <summary>An empty detached theme snapshot.</summary>
    public static PiSharpExtensionUiTheme Empty { get; } = new("default",
        new Dictionary<string, PiSharpExtensionUiThemeStyle>(StringComparer.Ordinal));
}

/// <summary>Data available to a dynamic extension component while it renders.</summary>
public sealed record PiSharpExtensionUiRenderContext(int Width, int Height, PiSharpExtensionUiTheme Theme,
    IReadOnlyDictionary<string, string> Statuses, bool IsFocused = false);

/// <summary>Small component contract hosted by the existing PiSharp terminal screen.</summary>
public interface IPiSharpExtensionUiComponent : IDisposable
{
    /// <summary>Renders current lines for the supplied terminal size, theme, statuses and focus state.</summary>
    IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context);

    /// <summary>Processes one decoded key or paste and reports whether it was handled.</summary>
    PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input);

    /// <summary>Notifies the component after terminal dimensions change.</summary>
    void OnResize(PiSharpExtensionUiSize size) { }

    /// <summary>The host supplies a bounded render invalidation callback while the component is installed.</summary>
    /// <param name="requestRedraw">Requests an invalidation through the host render scheduler.</param>
    void AttachRedraw(Action requestRedraw) { }
}

/// <summary>Component that can replace selected input-editor behavior while delegating other keys to the host.</summary>
public interface IPiSharpExtensionEditorComponent : IPiSharpExtensionUiComponent
{
    /// <summary>Replaces the built-in editor row while this component is installed.</summary>
    bool ReplacesDefaultEditor => false;
}

/// <summary>Positioning and sizing hints for an extension modal component.</summary>
public sealed record PiSharpExtensionUiOverlayOptions(int? Left = null, int? Top = null,
    int? Width = null, int? Height = null);

/// <summary>Options for a focused extension component.</summary>
public sealed record PiSharpExtensionUiCustomOptions(PiSharpExtensionUiHostingMode HostingMode =
    PiSharpExtensionUiHostingMode.Focused, PiSharpExtensionUiOverlayOptions? Overlay = null);

/// <summary>Typed completion result for a focused extension component.</summary>
public sealed record PiSharpExtensionUiCompletion<T>(bool IsCancelled, T? Value)
{
    /// <summary>Represents Escape, dismissal, or another non-result completion.</summary>
    public static PiSharpExtensionUiCompletion<T> Cancelled { get; } = new(true, default);
    /// <summary>Creates a successful completion carrying a typed value.</summary>
    public static PiSharpExtensionUiCompletion<T> Completed(T value) => new(false, value);
}

/// <summary>Options for extension dialogs; timeout dismissal returns the same result as user cancellation.</summary>
public sealed record PiSharpExtensionUiDialogOptions(TimeSpan? Timeout = null);

/// <summary>Working-indicator frames and animation interval.</summary>
public sealed record PiSharpExtensionWorkingIndicatorOptions(IReadOnlyList<string> Frames,
    TimeSpan? Interval = null);

/// <summary>Autocomplete request over the current editor draft.</summary>
public sealed record PiSharpExtensionAutocompleteRequest(string Text, int Cursor,
    string Query, CancellationToken CancellationToken);

/// <summary>An autocomplete row separates visible text from the text inserted into the draft.</summary>
public sealed record PiSharpExtensionAutocompleteSuggestion(string DisplayText, string ReplacementText,
    string? Description = null);

/// <summary>Provider that can contribute editor autocomplete rows.</summary>
public delegate ValueTask<IReadOnlyList<PiSharpExtensionAutocompleteSuggestion>>
    PiSharpExtensionAutocompleteProvider(PiSharpExtensionAutocompleteRequest request,
        CancellationToken cancellationToken);

/// <summary>Result of selecting a theme by name.</summary>
public sealed record PiSharpExtensionUiThemeResult(bool Success, string? Error = null);

/// <summary>Persistent UI registrations owned by one loaded native extension package.</summary>
public interface IPiSharpExtensionUiRegistration
{
    /// <summary>Replaces or removes a named footer status owned by this package.</summary>
    void SetStatus(string key, string? text) { }

    /// <summary>Replaces or removes a static-line widget owned by this package.</summary>
    void SetWidget(string key, IReadOnlyList<string>? lines,
        PiSharpExtensionWidgetPlacement placement = PiSharpExtensionWidgetPlacement.AboveEditor)
    {
    }

    /// <summary>Replaces or removes a component widget owned by this package.</summary>
    void SetWidget(string key, IPiSharpExtensionUiComponent? component,
        PiSharpExtensionWidgetPlacement placement = PiSharpExtensionWidgetPlacement.AboveEditor)
    {
        component?.Dispose();
    }

    /// <summary>Clears a widget without requiring a cast to disambiguate the two <c>SetWidget</c> overloads.</summary>
    void ClearWidget(string key) => SetWidget(key, (IReadOnlyList<string>?)null);

    /// <summary>Registers a decoded input listener until it is disposed or the package is removed.</summary>
    IDisposable OnTerminalInput(PiSharpExtensionTerminalInputHandler handler) => EmptyUiRegistration.Instance;

    /// <summary>Replaces this package's header contribution, or clears it.</summary>
    void SetHeader(IPiSharpExtensionUiComponent? component) => component?.Dispose();

    /// <summary>Replaces this package's footer contribution, or clears it.</summary>
    void SetFooter(IPiSharpExtensionUiComponent? component) => component?.Dispose();

    /// <summary>Sets the terminal title owned by this package.</summary>
    void SetTitle(string title) { }

    /// <summary>Sets or clears the working message override.</summary>
    void SetWorkingMessage(string? message) { }

    /// <summary>Controls the built-in streaming working row.</summary>
    void SetWorkingVisible(bool visible) { }

    /// <summary>Sets animation frames or restores the built-in indicator with null.</summary>
    void SetWorkingIndicator(PiSharpExtensionWorkingIndicatorOptions? options) { }

    /// <summary>Sets the label shown for hidden reasoning blocks, without changing visibility policy.</summary>
    void SetHiddenThinkingLabel(string? label) { }

    /// <summary>Adds a package-owned autocomplete provider in deterministic registration order.</summary>
    IDisposable AddAutocompleteProvider(PiSharpExtensionAutocompleteProvider provider) =>
        EmptyUiRegistration.Instance;

    /// <summary>Installs or clears this package's custom editor contribution.</summary>
    void SetEditorComponent(IPiSharpExtensionEditorComponent? component) => component?.Dispose();

    /// <summary>Gets the active custom editor contribution, if one is installed.</summary>
    IPiSharpExtensionEditorComponent? GetEditorComponent() => null;

    /// <summary>Gets a detached snapshot of the active terminal theme.</summary>
    PiSharpExtensionUiTheme Theme => PiSharpExtensionUiTheme.Empty;

    /// <summary>Lists the host's available themes without switching the active theme.</summary>
    IReadOnlyList<(string Name, string? Path)> GetAllThemes() => Array.Empty<(string, string?)>();

    /// <summary>Loads a detached theme snapshot by name without selecting it.</summary>
    PiSharpExtensionUiTheme? GetTheme(string name) => null;

    /// <summary>Selects an available theme by name.</summary>
    PiSharpExtensionUiThemeResult SetTheme(string name) => new(false, "UI not available");

    /// <summary>Gets the current tool-output expansion state.</summary>
    bool GetToolsExpanded() => false;

    /// <summary>Changes the interactive transcript's tool-output expansion state.</summary>
    void SetToolsExpanded(bool expanded) { }
}

internal sealed class EmptyUiRegistration : IDisposable
{
    internal static EmptyUiRegistration Instance { get; } = new();
    public void Dispose() { }
}
