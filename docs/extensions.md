# Native .NET extensions

PiSharp loads user extensions from `<agent-dir>/extensions` and loads project extensions from `.pi/extensions` only when the project is trusted. `--no-extensions` disables discovery; explicit `--extension` paths remain available for one run. Extensions are trusted in-process .NET code with the application's operating-system permissions.

Built-in `tool-search` and `codemode` use the same extension catalog. They have `builtin:` source identities and support normal enable/disable rules and explicit `--extension builtin:<name>` selection. `codemode` is registered inactive by default; select it with `--tools` or activate it through the session loadout. It requires Node.js 22.19 or newer; it runs model JavaScript inside bundled QuickJS/WASM in a separate process and exposes only session-callable tools through the shared nested execution path.

## Public compiled PiSharp extension API

Third-party compiled extensions reference only the lightweight `PiSharp.Extensions.Abstractions` NuGet package plus their normal dependencies. Its API v1 contract includes slash commands and `Microsoft.Extensions.AI.AIFunction` tools. The additive runtime bridge also exposes user/custom turn-start data, extension message sending, resource discovery, provider-context transforms, and tool-call/result hooks. Existing API-v1 packages keep their type identities and constructor signatures. Packages do not reference PiSharp Runtime or CLI assemblies.

The compatibility fixture under `tests/fixtures/NativeExtensionApiV1` is compiled against the PR #7 API-v1 contract snapshot, packed as a normal NuGet package, then loaded by the current host in a fresh runtime. It verifies the original command, tool and before-agent-start callbacks. The host continues to share the existing MEAI assembly contract for `AIFunction`; long-term binary compatibility across future MEAI versions remains open.

```csharp
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;

public sealed class GreetingExtension : IPiSharpExtension
{
    public void Configure(IPiSharpExtensionBuilder builder)
    {
        builder.AddCommand("hello", (name, _) => Task.FromResult("Hello " + name));
        builder.AddTool(AIFunctionFactory.Create((string name) => "Hello " + name, name: "greet"));
        builder.AddBeforeAgentStartHandler((turn, context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var startingText = turn.MessageType == PiSharpTurnMessageType.User
                ? turn.Prompt
                : turn.StartingCustomMessage?.Content;
            var imageCount = turn.Images.Count;
            var systemPrompt = turn.SystemPrompt + $"\nWorking directory: {context.WorkingDirectory}; " +
                $"starting text length: {startingText?.Length ?? 0}; images: {imageCount}.";
            return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(new(systemPrompt)
            {
                InjectedMessage = new("greeting-context", "The greeting extension is active.", Display: false)
            });
        });
        builder.AddCommand("greet-user", async (_, command, cancellationToken) =>
        {
            await command.SendUserMessageAsync("Please greet me.", cancellationToken);
            return "Submitted a user turn.";
        });
        builder.AddCommand("greet-custom", async (_, command, cancellationToken) =>
        {
            await command.SendCustomMessageAsync(new("greeting-request", "Please greet the user."),
                triggerTurn: true, PiSharpMessageDeliveryMode.Steer, cancellationToken);
            return "Submitted a custom turn.";
        });
        builder.AddResourceDiscoveryHandler((context, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.Combine(context.WorkingDirectory, ".pi", "greeting-resources");
            return Task.FromResult<PiSharpResourceDiscoveryResult?>(new(
                SkillPaths: [Path.Combine(root, "skills")],
                PromptPaths: [Path.Combine(root, "prompts")],
                ThemePaths: [Path.Combine(root, "themes")]));
        });
        builder.AddContextTransform((messages, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projected = messages.Select(message => message.Clone()).ToArray();
            var lastUser = projected.LastOrDefault(message => message.Role == ChatRole.User);
            if (lastUser is not null)
            {
                lastUser.Contents = lastUser.Contents.ToList();
                lastUser.Contents.Add(new TextContent("Use the greeting extension's guidance."));
            }
            return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(projected);
        });
        builder.AddToolCallHook((call, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (call.ToolName == "greet") call.Arguments["name"] = "PiSharp user";
            return ValueTask.FromResult(PiSharpToolCallDecision.Allow);
        });
        builder.AddToolResultHook((result, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.IsError && result.ToolName == "greet") result.Result = "Greeting completed.";
            return ValueTask.CompletedTask;
        });
    }
}
```

`PiSharpBeforeAgentStartEvent.MessageType` distinguishes submitted user turns from custom-message turns. User turns expose the expanded `Prompt` and image metadata (`MediaType` and optional byte length); custom turns expose `StartingCustomMessage` and leave `Prompt` empty. Returning `InjectedMessage` adds one durable custom message after the starting message for that turn. `SystemPrompt` replacement applies only to that turn.

The context-taking `AddCommand` overload provides a small snapshot of working directory, idle/trust state and queued-message state, plus `SendUserMessageAsync` and `SendCustomMessageAsync`. User messages use the normal user-turn path. Custom messages retain their extension source and are persisted as custom messages. The default custom send starts a turn when idle; `triggerTurn: false` stores the message for the next ordinary turn. While a run is active, `PiSharpMessageDeliveryMode` selects steering, follow-up or next-turn delivery through the host's existing run queue.

An active run keeps ownership of provider execution. A custom message submitted while it is active joins that run's selected steering/follow-up queue even when `triggerTurn` is true; it cannot start a competing provider run. An idle `triggerTurn: true` message starts one normal run whose before-agent-start event identifies the custom starting message.

Resource callbacks return paths to the existing skill, prompt-template and theme catalogs. Context transforms receive the provider-bound MEAI conversation and their output is request-local. Tool-call hooks run before normal binding/execution and can mutate arguments or block a call; result hooks can replace the value and explicit error state. All asynchronous callbacks receive the cancellation token for the run or tool operation that owns them.

Pack the extension as a normal `.nupkg` and include a `pisharp-extension.json` file at the package root. The manifest declares the package identity, extension API version, and every entry assembly and public type explicitly:

```json
{
  "id": "Example.Greeting",
  "version": "1.0.0",
  "apiVersion": 1,
  "extensions": [
    {
      "assembly": "lib/net10.0/Example.Greeting.dll",
      "type": "Example.Greeting.GreetingExtension"
    }
  ]
}
```

An entry type must be public, concrete, implement `IPiSharpExtension`, and have a public parameterless constructor. Package-private managed dependencies must be included with the extension assembly and its `.deps.json`. PiSharp validates the manifest and host API version before constructing extension objects, then loads the package in its own collectible .NET load context. Conflicting private dependency versions can coexist in separate packages. Each package configures through the same host command, tool, and lifecycle registration path as the internal extension runtime.

Install a local package with `pisharp install ./Example.Greeting.1.0.0.nupkg`. Add `--local` to store it in the project settings and package directory; project packages execute only after project trust is granted. User packages are stored under the configured agent directory. Settings keep the package identity and source path in a separate collection:

```json
{
  "nativeExtensions": [
    {
      "id": "Example.Greeting",
      "version": "1.0.0",
      "source": "../../packages/Example.Greeting.1.0.0.nupkg"
    }
  ]
}
```

The source path lets `pisharp update --extension Example.Greeting` read a new local package for deterministic offline updates. `pisharp list` shows configured native packages, and `pisharp remove Example.Greeting` removes the selected package. `/reload` re-reads settings and replaces package registrations; a failed package load or configuration produces a warning and leaves the rest of the agent available. A failed update validation retains the prior active package version.

PiSharp does not run Pi TypeScript or JavaScript extension source and does not compile package source at install or startup. Native extension assemblies execute as trusted in-process .NET code with the application's operating-system permissions; a collectible load context supports dependency isolation and reload, not a security sandbox.

## Internal PiSharp extension runtime

The examples below use `PiSharp.Runtime.Extensions.ExtensionRegistration`, which is an internal host surface for built-ins and first-party integrations. External compiled packages use `IPiSharpExtensionBuilder` from `PiSharp.Extensions.Abstractions`; they cannot call `ExtensionRegistration`, Runtime session types, CLI types or services.

## MCP namespaces in Codemode

MCP servers use `<agent-dir>/mcp.json` or a trusted project's `.pi/mcp.json`. A server `description` appears in the bounded `mcp_servers` request section after the server connects; the Codemode function description stays server-agnostic. The server's initialize instructions and callable tool names are returned by `await describeNamespace("mcp__<name>")`; the configured server name is also an alias, so `await describeNamespace("docs")` works for `mcp__docs`. Find a tool with `await searchTools(query, { namespace: "docs" })` or the full namespace. This keeps individual MCP tool schemas out of provider instructions while preserving the normal callable-tool policy. The legacy `codemode-deferred` exposure value is accepted as an alias for `codemode`; `deferred` continues to use `tool-search`.

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

Tools that invoke other tools can declare their reach with `ReachesTools`. The call hook receives that declaration on `PiSharpToolCallContext`, allowing a runtime such as MCP to wait for only the services the call may use. Use `PiSharpToolReach.All` when the tool searches or enumerates a changing tool set, or `FromWords` when its arguments identify the tools or namespaces it may call:

```csharp
registration.AddTool(new PiSharpToolRegistration(
    AIFunctionFactory.Create(LoadAsync, name: "load_docs"),
    ToolExposure.ModelOnly,
    ReachesTools: _ => PiSharpToolReach.FromWords(["mcp__docs__search"])));
```

`PiSharpToolNamespace` accepts aliases for lookup from Codemode's `searchTools` and `describeNamespace`. MCP tool results retain the model-facing text and error state; nested Codemode calls receive the full `CallToolResult` (`content`, `structuredContent`, and `isError`, without protocol `_meta`) and its output schema. A returned MCP error therefore resolves to its result object so the script can inspect `isError` and the content, while the model-facing tool call remains marked as an error.

Set `PiSharpToolRegistration.ConstrainedSampling` to `JsonSerializer.SerializeToElement(new { type = "json_schema", strict = "prefer" })` to request strict JSON-schema sampling. Anthropic uses this when the selected model's `supportsStrictTools` compatibility flag is true. Unsupported preferred schemas use their original non-strict definitions; known strict-tool capacity rejections retry once with those original definitions. Use `strict = "require"` to reject unsupported schemas or models and retain the constraint during capacity fallback. Policies survive tool transcript revisions and native/Pi session persistence, and Pi Messages forwards them to its gateway. Strict sampling on other provider APIs remains in progress.

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

## Project trust decisions

An extension loaded from a user/global path or an explicit `--extension` path can register a callback before PiSharp loads protected project resources:

```csharp
registration.AddProjectTrustHandler((context, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    if (!context.HasUserInterface)
        return Task.FromResult<ProjectTrustExtensionResult?>(null);

    return Task.FromResult<ProjectTrustExtensionResult?>(new(
        ProjectTrustExtensionDecision.Yes, Remember: true));
});
```

The first `Yes` or `No` result decides trust; `Undecided`, a null result, or a handler error continues to the next handler and then the saved decision/default/prompt flow. `Remember` persists a decisive result. PiSharp loads user/global and explicit CLI extensions for this bootstrap while the project is untrusted, then reuses those instances after the decision; project-local extensions load only after trust is granted. Callbacks run only when the project has protected resources and no explicit trust override short-circuits resolution.

The .NET context exposes the canonical working directory and whether the host has an interactive UI. It does not yet expose Pi's project-trust `select`, `confirm`, `input`, or `notify` methods, so handlers cannot show their own startup dialogs. See the [current-Pi trust callback evidence](parity/fixtures/project-trust-extension-current-pi-2026-10-10.md) for the paired behavior and remaining UI gap.

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

Call and result renderers receive a stable tool-call ID, working directory, safe snapshot of the call arguments, lifecycle flags, and a completed result's `DurationMs` when recorded. Result data includes text, structured details, images, error state and error text. A renderer returns a `PiSharpToolRenderView` made from text spans and semantic styles (`Title`, `Output`, `Muted`, `Accent`, `Code`, `Success`, or `Error`). PiSharp sanitizes terminal controls, bounds output size, applies the active theme, wraps text by terminal-cell width and keeps full results compatible with the ten-line collapse/expand behavior. Extensions return text and style intent; they cannot inject ANSI sequences through this API.

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

### Internal before-agent-start handler

Use `AddBeforeAgentStartHandler` to inspect the expanded user prompt and replace the
rendered system prompt for that turn:

```csharp
registration.AddBeforeAgentStartHandler((eventData, context, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    var systemPrompt = eventData.SystemPrompt + "\nFollow the user's deployment policy.";
    return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(new(systemPrompt));
});
```

Handlers run in extension order before the prompt is saved or sent to the
provider. Each handler sees changes made by earlier handlers. `SystemPrompt` is
the full current text, including additional MCP server instructions; a returned
value replaces it for the rest of the turn. Returning `null` leaves the current
text unchanged. The prompt override does not enter conversation history. Handler
exceptions emit `extension_error` and allow later handlers to run; cancellation
stops setup. The event also contains image attachments and the live extension
context.

The internal runtime hook distinguishes submitted user prompts from custom-message runs, receives custom-message metadata and user image attachments, and can return a durable injected custom message. The public compiled equivalent is `IPiSharpExtensionBuilder.AddBeforeAgentStartHandler`, documented above. Structured mutation of individual system-prompt options remains deferred.

## Current boundaries

The .NET API does not aim for TypeScript source compatibility. Per-session extension state, late dynamic registration, custom providers, extension keybindings, general extension UI and settings remain open parity work. Resource discovery is currently a startup/reload callback rather than a full resource-management API. Tool output schemas and a complete structured-result contract are also open. See the [parity ledger](parity/execution-ledger.json) for implementation evidence and current gaps.
