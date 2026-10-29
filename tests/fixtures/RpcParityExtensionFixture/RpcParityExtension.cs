using PiSharp.Extensions.Abstractions;
using System.Text.Json;

namespace PiSharp.TestExtensions.RpcParity;

public sealed class RpcParityExtension : IPiSharpExtension
{
    public void Configure(IPiSharpExtensionBuilder builder)
    {
        builder.AddSessionBeforeCompactHandler((_, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(null);
        });
        builder.AddCommand("rpc-fixture-navigate-tree", async (targetId, context, cancellationToken) =>
        {
            var result = await context.NavigateTreeAsync(targetId.Trim(),
                new PiSharpTreeNavigationOptions(Summarize: true, CustomInstructions: "Summarize for RPC parity."),
                cancellationToken);
            return result.Cancelled ? "navigation cancelled" : "tree navigation completed";
        }, "Navigate to a branch and request a deterministic summary.");

        if (!string.Equals(Environment.GetEnvironmentVariable("PISHARP_RPC_UI_FIXTURE"), "true",
                StringComparison.OrdinalIgnoreCase)) return;

        if (string.Equals(Environment.GetEnvironmentVariable("PISHARP_RPC_UI_STREAM_EDITOR"), "true",
                StringComparison.OrdinalIgnoreCase))
        {
            var editorUpdated = false;
            builder.AddMessageUpdateHandler((_, context, _) =>
            {
                if (!editorUpdated)
                {
                    editorUpdated = true;
                    context.Ui.SetEditorText("R14 editor text");
                }
                return ValueTask.CompletedTask;
            });
        }

        builder.AddCommand("rpc-fixture-ui", async (argument, context, cancellationToken) =>
        {
            var ui = context.Ui;
            void Emit(string name, object? value) => ui.Notify($"R14:{name}:{JsonSerializer.Serialize(value)}");
            switch (argument.Trim())
            {
                case "select":
                    Emit("select", await ui.SelectAsync("R14 select", ["red", "blue"], cancellationToken));
                    break;
                case "confirm":
                    Emit("confirm", await ui.ConfirmAsync("R14 confirm", "Proceed?", cancellationToken));
                    break;
                case "input":
                    {
                        var withPlaceholder = await ui.InputAsync("R14 input", "type here", cancellationToken);
                        var withoutPlaceholder = await ui.InputAsync("R14 input no placeholder",
                            cancellationToken: cancellationToken);
                        Emit("input", new[] { withPlaceholder, withoutPlaceholder });
                        break;
                    }
                case "editor":
                    Emit("editor", await ui.EditAsync("R14 editor", "prefill text", cancellationToken));
                    break;
                case "notify":
                    ui.Notify("R14 notify info", "info");
                    ui.Notify("R14 notify warning", "warning");
                    ui.Notify("R14 notify error", "error");
                    break;
                case "status":
                    ui.SetStatus("r14", "working");
                    ui.SetStatus("r14", null);
                    break;
                case "widget":
                    ui.SetWidget("r14", ["first", "second"], PiSharpExtensionWidgetPlacement.AboveEditor);
                    ui.SetWidget("r14", ["below"], PiSharpExtensionWidgetPlacement.BelowEditor);
                    ui.SetWidget("r14", (IReadOnlyList<string>?)null);
                    ui.SetWidget("r14-component", new NoOpComponent());
                    break;
                case "title":
                    ui.SetTitle("R14 title");
                    break;
                case "editor-text":
                    ui.SetEditorText("R14 editor text");
                    break;
                case "concurrent":
                    {
                        var select = ui.SelectAsync("R14 concurrent select", ["one", "two"], cancellationToken);
                        var input = ui.InputAsync("R14 concurrent input", "concurrent placeholder", cancellationToken);
                        await Task.WhenAll(select, input);
                        Emit("concurrent", new[] { select.Result, input.Result });
                        break;
                    }
                case "timeout":
                    Emit("timeout", await ui.InputWithOptionsAsync("R14 timeout", "wait",
                        new PiSharpExtensionUiDialogOptions(TimeSpan.FromMilliseconds(40)), cancellationToken));
                    break;
                case "cancel":
                    Emit("cancel", await ui.InputAsync("R14 cancel", "cancel me", cancellationToken));
                    break;
                case "pending":
                    await ui.InputAsync("R14 pending", "shutdown", cancellationToken);
                    break;
                case "unsupported":
                    {
                        using var inputListener = ui.OnTerminalInput(static _ =>
                            PiSharpExtensionTerminalInputResult.PassThrough);
                        ui.SetWorkingMessage("ignored");
                        ui.SetWorkingVisible(false);
                        ui.SetWorkingIndicator(new PiSharpExtensionWorkingIndicatorOptions(["ignored"]));
                        ui.SetHiddenThinkingLabel("ignored");
                        ui.SetFooter(new NoOpComponent());
                        ui.SetHeader(new NoOpComponent());
                        ui.SetEditorComponent(new NoOpEditorComponent());
                        using var autocomplete = ui.AddAutocompleteProvider(static (_, _) =>
                            ValueTask.FromResult<IReadOnlyList<PiSharpExtensionAutocompleteSuggestion>>([]));
                        var custom = await ui.ShowCustomAsync<string>(static _ => new NoOpComponent(),
                            cancellationToken: cancellationToken);
                        var editorText = ui.GetEditorText();
                        ui.PasteToEditor("R14 pasted text");
                        ui.SetToolsExpanded(true);
                        var themeSwitch = ui.SetTheme("missing");
                        ui.Notify("R14:unsupported:" + JsonSerializer.Serialize(new
                        {
                            custom = custom.IsCancelled,
                            editor = editorText,
                            toolsExpanded = ui.GetToolsExpanded(),
                            theme = ui.GetTheme("missing") is null,
                            themeSwitchFailed = !themeSwitch.Success,
                            editorComponent = ui.GetEditorComponent() is null,
                            themes = ui.GetAllThemes().Count
                        }));
                        break;
                    }
                case "correlation":
                    Emit("correlation", await ui.SelectAsync("R14 correlation", ["ok"], cancellationToken));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown R14 UI operation: {argument}");
            }

            return string.Empty;
        }, "Exercise RPC extension UI parity.");

        if (string.Equals(Environment.GetEnvironmentVariable("PISHARP_RPC_UI_ERROR"), "true",
                StringComparison.OrdinalIgnoreCase))
        {
            builder.AddBeforeAgentStartHandler((_, _, _) =>
                ValueTask.FromException<PiSharpBeforeAgentStartResult?>(
                    new InvalidOperationException("R14 extension handler failed")));
        }
    }

    private sealed class NoOpComponent : IPiSharpExtensionUiComponent
    {
        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context) => [];
        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input) =>
            PiSharpExtensionUiInputDisposition.PassThrough;
        public void Dispose() { }
    }

    private sealed class NoOpEditorComponent : IPiSharpExtensionEditorComponent
    {
        public IReadOnlyList<PiSharpExtensionUiLine> Render(PiSharpExtensionUiRenderContext context) => [];
        public PiSharpExtensionUiInputDisposition HandleInput(PiSharpExtensionUiInputEvent input) =>
            PiSharpExtensionUiInputDisposition.PassThrough;
        public void Dispose() { }
    }
}
