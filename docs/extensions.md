# Native .NET extensions

PiSharp loads user extensions from `<agent-dir>/extensions` and loads project extensions from `.pi/extensions` only when the project is trusted. `--no-extensions` disables discovery; explicit `--extension` paths remain available for one run. Extensions are trusted in-process .NET code with the application's operating-system permissions.

Built-in `tool-search` and `codemode` use the same extension catalog. They have `builtin:` source identities and support normal enable/disable rules and explicit `--extension builtin:<name>` selection. `codemode` is registered inactive by default; select it with `--tools` or activate it through the session loadout. It requires Node.js 22.19 or newer; it runs model JavaScript inside bundled QuickJS/WASM in a separate process and exposes only session-callable tools through the shared nested execution path.

## MCP namespaces in Codemode

MCP servers use `<agent-dir>/mcp.json` or a trusted project's `.pi/mcp.json`. A server `description` appears in the bounded `mcp_servers` request section after the server connects; the Codemode function description stays server-agnostic. The server's initialize instructions and callable tool names are returned by `await describeNamespace("mcp__<name>")`; find a tool with `await searchTools(query, { namespace: "mcp__<name>" })`. This keeps individual MCP tool schemas out of provider instructions while preserving the normal callable-tool policy. The legacy `codemode-deferred` exposure value is accepted as an alias for `codemode`; `deferred` continues to use `tool-search`.

```json
{
  "mcpServers": {
    "docs": {
      "command": "your-mcp-server",
      "description": "Search the product manuals.",
      "exposure": "codemode"
    }
  }
}
```

An extension implements `IPiSharpExtension.Configure(ExtensionRegistration)`. It can register `AIFunction` tools, terminal slash commands, and direct RPC Bash handlers. Tools execute through the same Microsoft.Extensions.AI function loop and durable checkpoint path as built-in tools.

## Extension resources

An extension can make skills, prompt templates, and terminal themes available on startup and after `/reload`:

```csharp
registration.AddResourceDiscoveryHandler((context, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    var phase = context.Reason == ExtensionResourceDiscoveryReason.Reload ? "reloaded" : "startup";
    var resources = Path.Combine(context.WorkingDirectory, ".pi", "generated-resources", phase);
    return Task.FromResult<ExtensionResourceDiscoveryResult?>(new(
        SkillPaths: [Path.Combine(resources, "skills")],
        PromptPaths: [Path.Combine(resources, "prompts")],
        ThemePaths: [Path.Combine(resources, "themes")]));
});
```

Returned paths may be files or directories and may be absolute, relative to the working directory, or file URLs. Skill and prompt resources use the same parsing and slash-command flow as local resources; theme paths join the active theme catalog. `--no-skills`, `--no-prompt-templates`, and `--no-themes` suppress their normal scans while explicitly contributed paths remain available. Handlers run in registration order after extension setup. A handler failure is reported and does not discard paths returned by other handlers; cancellation stops discovery.

For HTTP MCP OAuth, `oauth.clientName` sets the name sent during dynamic client registration and defaults to `pi`. Some servers accept registrations only from known clients:

```json
{
  "mcpServers": {
    "figma": {
      "url": "https://mcp.figma.com/mcp",
      "oauth": { "clientName": "Claude Code" }
    }
  }
}
```

The `pisharp mcp add` command also accepts `--oauth-client-name`. The name is sent only when a client is dynamically registered; sign out before registering again under a different name. In the interactive TUI, `/mcp login <server>` renders the authorization URL and a short linked Ctrl+click/Cmd+click hint; the standalone `pisharp mcp login <server>` command keeps the URL as plain text.

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

## Tool call and result hooks

Extensions can register asynchronous hooks that run for both model-issued and nested tool calls:

```csharp
registration.AddToolCallHook(async (context, cancellationToken) =>
{
    if (context.ToolName == "deploy" && !await IsApprovedAsync(context.Arguments, cancellationToken))
        return PiSharpToolCallDecision.Block("Deployment was not approved.");

    // Changes are passed through normal tool argument binding and execution.
    context.Arguments["environment"] = "staging";
    return PiSharpToolCallDecision.Allow;
});

registration.AddToolResultHook((context, cancellationToken) =>
{
    if (!context.IsError && context.ToolName == "deploy")
        context.Result = "Deployment request recorded.";
    return ValueTask.CompletedTask;
});
```

Call hooks run in registration order before tool execution. Each receives the tool name, stable call ID, optional parent call ID and mutable argument dictionary; the first blocked decision prevents the tool from running and returns its error through the normal tool lifecycle. Result hooks also run in registration order after execution, including failed or blocked calls. They can replace the result or set `IsError` and `Error`; nested callers receive that explicit status. The host passes cancellation to every hook. These hooks are a policy extension point, not a built-in approval dialog or security sandbox. Native extensions remain trusted in-process code with the application's operating-system permissions.

## Provider context transforms

An extension can transform the conversation sent to the provider on each model request:

```csharp
registration.AddContextTransform((messages, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    var projected = messages.ToList();
    var promptIndex = projected.FindLastIndex(message => message.Role == ChatRole.User);
    if (promptIndex >= 0)
    {
        var prompt = projected[promptIndex].Clone();
        prompt.Contents = prompt.Contents.ToList();
        prompt.Contents.Add(new TextContent("Apply the extension's repository-specific guidance."));
        projected[promptIndex] = prompt;
    }
    return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(projected);
});
```

Transforms run in registration order after PiSharp projects the conversation and before provider-only repair and image preparation. They also run for follow-up requests in a tool loop and after context-overflow compaction. The host copies messages and text, tool-call, and tool-result content between transforms; other content payloads should be treated as read-only. Transformed messages are used for that provider request and do not enter saved conversation history. Cancellation is passed through, and a transform failure fails the provider request. Transforms are captured when an agent is created, so registering one later does not change existing agents.

## Current boundaries

The .NET API does not aim for TypeScript source compatibility. Session lifecycle hooks and per-session extension state, late dynamic registration, custom providers, extension keybindings, general extension UI and settings remain open parity work. Resource discovery is currently a startup/reload callback rather than a full resource-management API. Tool output schemas and a complete structured-result contract are also open. See the [parity ledger](parity/execution-ledger.json) for implementation evidence and current gaps.
