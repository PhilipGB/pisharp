using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;

namespace PiSharp.NativeExtensionInteractiveUiFixture;

public sealed class InteractiveUiExtension : IPiSharpExtension
{
    public void Configure(IPiSharpExtensionBuilder builder)
    {
        var ui = builder.Ui;
        var dashboard = new DashboardState();
        ui.SetStatus("dashboard", "native-ui:ready");
        ui.SetWidget("dashboard", new DashboardWidget(dashboard));
        ui.SetHeader(new LabelComponent("native-ui:header"));
        ui.SetFooter(new LabelComponent("native-ui:footer"));
        ui.SetEditorComponent(new DashboardEditorComponent());
        ui.SetTitle("Native UI Dashboard");
        ui.SetWorkingMessage("Native UI dashboard");
        ui.SetWorkingVisible(true);
        ui.SetHiddenThinkingLabel("Native thought");
        ui.SetWorkingIndicator(new(["◆", "◇"], TimeSpan.FromMilliseconds(120)));
        var consumeBurstY = false;
        ui.OnTerminalInput(input =>
        {
            if (input.Text == "@")
                return PiSharpExtensionTerminalInputResult.Rewrite("\u001b[97;1u\u001b[98;1u");
            if (input.Text == "x")
            {
                consumeBurstY = true;
                return PiSharpExtensionTerminalInputResult.PassThrough;
            }
            if (input.Text == "y" && consumeBurstY)
            {
                consumeBurstY = false;
                ui.SetStatus("dashboard", "native-ui:burst-consumed");
                return PiSharpExtensionTerminalInputResult.Consume;
            }
            consumeBurstY = false;
            if (input.Text != "!") return PiSharpExtensionTerminalInputResult.PassThrough;
            dashboard.Increment();
            ui.SetStatus("dashboard", $"native-ui:{dashboard.Count}:updated");
            return PiSharpExtensionTerminalInputResult.Consume;
        });
        ui.AddAutocompleteProvider((request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PiSharpExtensionAutocompleteSuggestion> suggestions =
                request.Query == "nui" ? [new("Native UI completion", "native-ui-autocomplete")] : [];
            return ValueTask.FromResult(suggestions);
        });

        builder.AddShortcut(PiSharpShortcutKey.Parse("ctrl+shift+z"), (context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Ui.Notify("native-ui-shortcut");
            return Task.CompletedTask;
        }, "Exercise ordinary shortcut pass-through from the editor component.");

        builder.AddTool(AIFunctionFactory.Create((string value) => $"native-ui-tool:{value}",
            name: "native_ui_echo", description: "Returns the supplied dashboard fixture value."));

        builder.AddCommand("ui-dashboard", async (_, context, cancellationToken) =>
        {
            var completion = await context.Ui.ShowCustomAsync<string>(
                complete => new DashboardPanel(complete), cancellationToken: cancellationToken);
            if (completion.IsCancelled) return "native-ui-panel-cancelled";

            dashboard.SetValue(completion.Value ?? string.Empty);
            context.Ui.SetEditorText("native-ui accepted: " + completion.Value);
            var theme = context.Ui.SetTheme("light");
            for (var index = 0; index < 10; index++)
                theme = context.Ui.SetTheme(index % 2 == 0 ? "dark" : "light");
            var themeSnapshot = context.Ui.GetTheme("light");
            var availableThemes = context.Ui.GetAllThemes();
            var customEditor = context.Ui.GetEditorComponent() is not null;
            context.Ui.SetToolsExpanded(true);
            return $"native-ui-panel:{completion.Value}:theme={theme.Success}/{themeSnapshot?.Name}/" +
                $"{availableThemes.Count}/{context.Ui.Theme.Name}:editor={customEditor}:" +
                $"tools={context.Ui.GetToolsExpanded()}";
        }, "Open the interactive native extension dashboard.");

        builder.AddCommand("ui-dashboard-cleanup", (_, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ui.ClearWidget("dashboard");
            ui.SetStatus("dashboard", null);
            ui.SetHeader(null);
            ui.SetFooter(null);
            ui.SetEditorComponent(null);
            ui.SetTitle(string.Empty);
            ui.SetWorkingMessage(null);
            ui.SetWorkingVisible(true);
            ui.SetWorkingIndicator(null);
            ui.SetHiddenThinkingLabel(null);
            context.Ui.SetToolsExpanded(false);
            return Task.FromResult("native-ui-cleanup-complete");
        }, "Remove this package's persistent dashboard contributions.");

        builder.AddCommand("ui-modal", async (_, context, cancellationToken) =>
        {
            context.Ui.SetEditorText("modal-draft-preserved");
            var draftBeforeModal = context.Ui.GetEditorText();
            var completion = await context.Ui.ShowCustomAsync<string>(
                complete => new DashboardPanel(complete),
                new(PiSharpExtensionUiHostingMode.Overlay,
                    new(Left: 4, Top: 2, Width: 52, Height: 8)), cancellationToken);
            var draftAfterModal = context.Ui.GetEditorText();
            var draftPreserved = draftBeforeModal == "modal-draft-preserved" &&
                draftAfterModal == "modal-draft-preserved";
            return completion.IsCancelled
                ? $"native-ui-modal-cancelled:draft={draftPreserved}"
                : $"native-ui-modal:{completion.Value}:draft={draftPreserved}";
        }, "Open an overlay component and verify editor focus restoration.");

        builder.AddCommand("ui-dialogs", async (_, context, cancellationToken) =>
        {
            var selection = await context.Ui.SelectAsync("Choose a dashboard region", ["North", "South"],
                cancellationToken);
            if (selection is null) return "native-ui-dialogs-cancelled";
            var confirmed = await context.Ui.ConfirmAsync("Confirm dashboard action", "Continue?", cancellationToken);
            var label = await context.Ui.InputAsync("Dashboard label", "Type a label", cancellationToken);
            return $"native-ui-dialog:{selection}:{confirmed}:{label}";
        }, "Exercise the host selection, confirmation and text input dialogs.");

        builder.AddCommand("ui-editor", async (_, context, cancellationToken) =>
        {
            var result = await context.Ui.EditAsync("Extension multiline editor", "prefilled text", cancellationToken);
            return $"native-ui-editor-result:{result}";
        }, "Exercise the existing multiline editor through the extension UI capability.");
    }

    private sealed class DashboardState
    {
        private readonly object _gate = new();
        private string _value = "idle";
        private int _count;
        private Action? _redraw;

        public void SetRedraw(Action redraw)
        {
            lock (_gate) _redraw = redraw;
        }

        public void SetValue(string value)
        {
            Action? redraw;
            lock (_gate)
            {
                _value = value;
                redraw = _redraw;
            }
            redraw?.Invoke();
        }

        public void Increment()
        {
            Action? redraw;
            lock (_gate)
            {
                _count++;
                redraw = _redraw;
            }
            redraw?.Invoke();
        }

        public string Snapshot()
        {
            lock (_gate) return $"native-ui-widget:{_count}:{_value}";
        }

        public int Count
        {
            get { lock (_gate) return _count; }
        }
    }

    private sealed class DashboardWidget(DashboardState state) : IPiSharpExtensionUiComponent
    {
        private PiSharpExtensionUiSize? _size;

        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context) =>
        [
            new(state.Snapshot(), "success"),
            new($"native-ui-theme:{context.Theme.Name}"),
            new($"native-ui-size:{_size?.Width}x{_size?.Height}")
        ];
        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input) =>
            PiSharpExtensionUiInputDisposition.PassThrough;
        public void AttachRedraw(Action requestRedraw) => state.SetRedraw(requestRedraw);
        public void OnResize(PiSharpExtensionUiSize size) => _size = size;
        public void Dispose() { }
    }

    private sealed class DashboardEditorComponent : IPiSharpExtensionEditorComponent
    {
        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context) =>
            [new(" native-ui-editor:pass-through", "muted")];
        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input) =>
            PiSharpExtensionUiInputDisposition.PassThrough;
        public void Dispose() { }
    }

    private sealed class LabelComponent(string text) : IPiSharpExtensionUiComponent
    {
        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context) => [new(text)];
        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input) =>
            PiSharpExtensionUiInputDisposition.PassThrough;
        public void Dispose() { }
    }

    private sealed class DashboardPanel(Action<string> complete) : IPiSharpExtensionUiComponent
    {
        private readonly System.Text.StringBuilder _value = new();
        private PiSharpExtensionUiSize? _size;

        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context) =>
        [
            new("native-ui-panel", "accent"),
            new($"native-ui-panel-size:{_size?.Width}x{_size?.Height}"),
            new("Type a dashboard value and press Enter."),
            new("Value: " + _value)
        ];

        public void OnResize(PiSharpExtensionUiSize size) => _size = size;

        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input)
        {
            if (input.Key == nameof(ConsoleKey.Enter))
            {
                complete(_value.ToString());
                return PiSharpExtensionUiInputDisposition.Handled;
            }
            if (input.Text is { Length: > 0 } text)
            {
                _value.Append(text);
                return PiSharpExtensionUiInputDisposition.Handled;
            }
            return PiSharpExtensionUiInputDisposition.PassThrough;
        }

        public void Dispose() { }
    }
}

public sealed class FailingConfigureUiExtension : IPiSharpExtension
{
    public void Configure(IPiSharpExtensionBuilder builder)
    {
        builder.Ui.SetStatus("rollback", "must be removed");
        builder.Ui.SetWidget("rollback", ["rollback-widget"]);
        throw new InvalidOperationException("expected UI configure failure");
    }
}
