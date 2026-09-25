# Native .NET extensions

PiSharp loads user extensions from `<agent-dir>/extensions` and loads project extensions from `.pi/extensions` only when the project is trusted. `--no-extensions` disables discovery; explicit `--extension` paths remain available for one run. Extensions are trusted in-process .NET code with the application's operating-system permissions.

An extension implements `IPiSharpExtension.Configure(ExtensionRegistration)`. It can register `AIFunction` tools, terminal slash commands, and direct RPC Bash handlers. Tools execute through the same Microsoft.Extensions.AI function loop and durable checkpoint path as built-in tools.

## Tool call and result presentation

An extension can attach optional terminal renderers to a tool:

```csharp
var echo = AIFunctionFactory.Create((string text) => "Echo: " + text, name: "echo_ext");
registration.AddTool(echo, new PiSharpToolRenderer(
    renderCall: (arguments, context) => PiSharpToolRenderView.FromText(
        "Echo " + arguments["text"], PiSharpToolTextStyle.Accent),
    renderResult: (result, context) => PiSharpToolRenderView.FromText(
        result.Text ?? "No result.", PiSharpToolTextStyle.Output)));
```

Call and result renderers receive a stable tool-call ID, working directory, safe snapshot of the call arguments, and lifecycle flags. Result data includes text, structured details, images, error state and error text. A renderer returns a `PiSharpToolRenderView` made from text spans and semantic styles (`Title`, `Output`, `Muted`, `Accent`, `Code`, `Success`, or `Error`). PiSharp sanitizes terminal controls, bounds output size, applies the active theme, wraps text by terminal-cell width and keeps full results compatible with the ten-line collapse/expand behavior. Extensions return text and style intent; they cannot inject ANSI sequences through this API.

Return `null`, an empty view, or throw to use PiSharp's standard presentation. The host catches renderer exceptions and preserves tool execution/results. Render callbacks are synchronous and receive completed results; partial update rendering, stateful invalidation, custom interactive widgets and image-component composition are not implemented yet.

## Current boundaries

The .NET API does not aim for TypeScript source compatibility. Extension lifecycle hooks, context transforms, custom providers, extension keybindings, general extension UI, resource registration and settings remain open parity work. See the [parity ledger](parity/execution-ledger.json) for implementation evidence and current gaps.
