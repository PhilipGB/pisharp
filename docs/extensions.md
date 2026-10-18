# Native .NET extensions

PiSharp loads user extensions from `<agent-dir>/extensions` and loads project extensions from `.pi/extensions` only when the project is trusted. `--no-extensions` disables discovery; explicit `--extension` paths remain available for one run. Extensions are trusted in-process .NET code with the application's operating-system permissions.

Built-in `tool-search` and `codemode` use the same extension catalog. They have `builtin:` source identities and support normal enable/disable rules and explicit `--extension builtin:<name>` selection. `codemode` is registered inactive by default; select it with `--tools` or activate it through the session loadout. It requires Node.js 22.19 or newer; it runs model JavaScript inside bundled QuickJS/WASM in a separate process and exposes only session-callable tools through the shared nested execution path.

## Public compiled PiSharp extension API

Third-party compiled extensions reference only the lightweight `PiSharp.Extensions.Abstractions` NuGet package plus their normal dependencies. Its API v1 contract includes slash commands and `Microsoft.Extensions.AI.AIFunction` tools. The additive runtime bridge also exposes user/custom turn-start data, extension message sending, resource discovery, provider-context transforms, tool-call/result hooks, durable session state, session lifecycle callbacks, basic UI operations, and TUI shortcuts. Existing API-v1 packages keep their type identities and constructor signatures. Packages do not reference PiSharp Runtime or CLI assemblies.

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

The context-taking `AddCommand` overload provides a small snapshot of working directory, idle/trust state and queued-message state, plus `SendUserMessageAsync` and `SendCustomMessageAsync`. User messages use the normal user-turn path. Custom messages retain their extension source and are persisted as custom messages. The default custom send starts a turn when idle. `triggerTurn: false` appends to idle conversation context without starting a model run; when sent during a run, it waits until the active turn's tool results are recorded, then enters context before any naturally required tool-loop continuation. It does not create an additional model request.

The public command and lifecycle contexts also expose `Mode`, `HasUserInterface`, `Ui`, and the active `Session` projection. The mode is a stable host-mode string such as `tui`, `rpc`, `json`, or `print`. The session projection contains the session ID, optional name, active head ID, and extension state entries on the current branch in tree order. Each entry includes its canonical ID, custom type, cloned `JsonElement` data, timestamp, and source path. This projection does not expose `ConversationSession` or any other Runtime type.

### Settings, flags, session metadata and host controls

The command context exposes `GetSettings()` as a fresh, detached, lower-camel-case JSON snapshot of the current effective settings. It is read-only; extensions cannot update host settings through this API. `RegisterFlag` adds an extension-owned Boolean or string CLI option. The flag name omits `--`; Boolean flags accept a bare `--name` or explicit `--name=false`, while string flags accept `--name value` or `--name=value`. Registered defaults are returned by `GetFlag` when the user omits the option. A command context can read only flags owned by its extension. Startup rejects conflicts with built-in options and unknown options still fail normally. Project extensions are loaded for flag discovery only after project trust permits their code to run.

The session projection also supports `Session.SetSessionNameAsync(name, cancellationToken)` and `Session.SetLabelAsync(entryId, label, cancellationToken)`. Passing `null` or an empty label clears it. Each mutation saves through the current session store before returning; invalid entry IDs fail, and failed saves preserve the prior value. Names and labels remain session metadata when the package is updated or removed. Labels also keep their normal Pi JSONL interchange representation.

Command contexts can inspect the current command catalogue (`GetCommands()`), model-declared active tools (`GetActiveTools()`), and configured tools with source, exposure and active state (`GetConfiguredTools()`). `SetActiveTools(names)` uses the session loadout: unknown and hidden tools are ignored, and the next provider request sees the new declarations and matching tool guidance. The public tool projection does not expose MEAI functions or Runtime descriptors.

`GetModel()` returns a stable provider/model identity and available display/capability metadata. `SetModelAsync(providerId, modelId, cancellationToken)` returns `Selected`, `UnknownModel` or `AuthenticationUnavailable`; selection affects the current session and leaves configured defaults alone. `GetThinkingLevel()` and `SetThinkingLevelAsync(level, cancellationToken)` use `PiSharpThinkingLevel`; the host applies its normal model-capability clamping. These controls share the same safe transition path as built-in commands and never replace the provider client during an in-flight request.

The bounded run controls are `AbortCurrentRun()`, `CompactCurrentSessionAsync(cancellationToken)` and `RequestShutdown()`. Abort is harmless while idle and uses the host's cancellation owner. Compaction returns `Compacted`, `NotNeeded`, `Cancelled` or `Failed` through `PiSharpCompactionResult`, and uses the current session compaction/save path. Shutdown is idempotent and follows the normal TUI/RPC exit path so lifecycle callbacks and session saving still run.

```csharp
builder.RegisterFlag("plan", PiSharpExtensionFlagType.Boolean,
    "Create a plan before making changes.", PiSharpExtensionFlagValue.FromBoolean(false));
builder.RegisterFlag("target", PiSharpExtensionFlagType.String,
    "Deployment target.", PiSharpExtensionFlagValue.FromString("staging"));

builder.AddCommand("extension-status", (_, context, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    var settings = context.GetSettings();
    var plan = context.GetFlag("plan")?.BooleanValue ?? false;
    var target = context.GetFlag("target")?.StringValue ?? "staging";
    return Task.FromResult($"shell prefix: {settings.GetProperty("shellCommandPrefix")}; plan: {plan}; target: {target}");
});
```

The `before_agent_start` event exposes request-local `SystemPromptOptions.Sections`, an ordered map of the named prompt sections PiSharp already builds (`preamble`, `tools`, `rules`, `docs`, `addendum`, `project_context`, `skills`, and `cwd`). Assign text to add or replace a section; remove its key or assign `null`/empty text to omit it. Added keys follow insertion order, and later handlers observe earlier mutations. The existing full `PiSharpBeforeAgentStartResult.SystemPrompt` replacement remains supported and takes precedence over structured section output for that turn. Structured edits apply to user and custom-triggered turns, reset on the next request, and are not duplicated by automatic retry.

```csharp
builder.AddBeforeAgentStartHandler((turn, _, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    turn.SystemPromptOptions.Sections["addendum"] = "Keep the response concise.";
    return ValueTask.FromResult<PiSharpBeforeAgentStartResult?>(null);
});
```

Extensions can persist state on the canonical session tree and reconstruct it later:

```csharp
using System.Text.Json;

builder.AddCommand("counter-add", async (_, context, cancellationToken) =>
{
    var session = context.Session ?? throw new InvalidOperationException("No active session.");
    var count = session.Entries
        .Where(entry => entry.CustomType == "example.counter")
        .Select(entry => entry.Data.GetProperty("value").GetInt32())
        .DefaultIfEmpty(0)
        .Last() + 1;
    await session.AppendEntryAsync("example.counter",
        JsonSerializer.SerializeToElement(new { value = count }), cancellationToken);
    return $"Counter: {count}";
});
```

`AppendEntryAsync` saves through the owning session store before it returns. A failed save rolls back the tree append and propagates the error. Repeated identical entries remain distinct. Entries follow normal branch, fork, resume, and compaction behavior. They are durable session data, not model-context messages: state values do not enter provider requests or compaction summaries. Disabling, updating, or removing the package leaves its historical entries in the session tree; the extension may no longer be available to interpret them. Callback-scoped session bridges are revoked when the callback ends, so retained contexts cannot mutate a later session.

Use the specific session lifecycle callbacks to observe host transitions and rebuild extension-local in-memory state:

```csharp
builder.AddSessionStartHandler((eventData, context, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    var values = context.Session?.Entries
        .Where(entry => entry.CustomType == "example.counter")
        .Select(entry => entry.Data.GetProperty("value").GetInt32()) ?? [];
    context.Ui.Notify($"Loaded {values.Count()} counter entries ({eventData.Reason}).");
    return ValueTask.CompletedTask;
});

builder.AddSessionShutdownHandler((eventData, context, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    context.Ui.Notify($"Closing {context.Session?.SessionId} ({eventData.Reason}).");
    return ValueTask.CompletedTask;
});
```

Start reasons are `Startup`, `Reload`, `New`, `Resume`, and `Fork`; shutdown reasons are `Quit`, `Reload`, `New`, `Resume`, and `Fork`. A new session has its own state branch, while a resumed or forked session projects only entries on its active branch. Lifecycle callbacks run in extension registration order. One handler failure is reported through extension diagnostics and does not prevent later handlers from running. The host owns callback cancellation and shutdown; lifecycle handlers are not background tasks.

The public `context.Ui` adapts the existing safe host UI and supports only `SelectAsync`, `ConfirmAsync`, `InputAsync`, and `Notify`. Select/input return `null` and confirm returns `false` when dialogs are unavailable or dismissed. Cancellation propagates. TUI notifications appear in the terminal; headless host notifications use the host's diagnostic output. RPC and print modes do not start terminal dialogs.

Compiled extensions can register an independent TUI shortcut:

```csharp
builder.AddShortcut(PiSharpShortcutKey.Parse("ctrl+shift+g"), (context, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    context.Ui.Notify($"Shortcut in {context.Mode}; session {context.Session?.SessionId}.");
    return Task.CompletedTask;
}, description: "Show the active session ID.");
```

Shortcut handlers receive the same safe command context, including message sending, UI, and branch-aware session state. They do not start a competing provider run. Reserved editor and application shortcuts keep their built-in behavior and produce a source-aware diagnostic; conflicts with ordinary built-in keys are reported and the extension binding is used. If extensions register the same key, the later handler in resolved extension order wins with a source-aware diagnostic. Reload and package removal remove the old handler and restore the remaining keymap behavior.

`PiSharpMessageDeliveryMode` and `triggerTurn` control separate behaviors. During an active run, `Steer` and `FollowUp` select the active-run queue when `triggerTurn` is true. `NextTurn` is held aside whether the agent is idle or busy; it does not start a run, and the next actual user or custom run includes its starting message first, followed by the held custom message. An active run keeps ownership of provider execution, so a custom message cannot start a competing provider run. An idle custom message with `triggerTurn: true` starts one normal run whose before-agent-start event identifies the custom starting message, except when the delivery mode is `NextTurn`.

`HasPendingMessages` reports only queued steering/follow-up input, matching Pi. Held `NextTurn` messages and busy context-only messages do not contribute to this flag or the ordinary prompt count and queue display.

Repeated sends with identical type, content, display flag and source remain separate custom-message entries. For context-only messages delivered during tool execution, the session save completes after tool results are recorded and before the continuation provider request sees the messages. A failed save prevents that provider request from being sent.

Resource callbacks return paths to the existing skill, prompt-template and theme catalogs. Context transforms receive the provider-bound MEAI conversation and their output is request-local. Tool-call hooks run before normal binding/execution and can mutate arguments or block a call; result hooks can replace the value and explicit error state. All asynchronous callbacks receive the cancellation token for the run or tool operation that owns them.

### Lifecycle event observers and callback capabilities

Register typed observers during `Configure`. These are observational callbacks;
the existing tool-call/result hooks and model/session owners remain responsible
for changing execution.

```csharp
builder.AddAgentStartHandler((eventData, context, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    var capabilities = context.Capabilities!;
    var model = capabilities.GetModel();
    var sessionId = capabilities.GetSessionSnapshot()?.SessionId;
    context.Ui.Notify($"Run started in {context.Mode}: {model?.ProviderId}/{model?.ModelId}, session {sessionId}.");
    return ValueTask.CompletedTask;
});

builder.AddToolExecutionEndHandler((eventData, context, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    context.Ui.Notify($"{eventData.ToolName} finished; error={eventData.IsError}.");
    return ValueTask.CompletedTask;
});
```

The event families and their observed boundaries are:

| Registration | Trigger boundary and payload | Cancellation behavior | Host modes | Semantics |
| --- | --- | --- | --- | --- |
| `AddSessionInfoChangedHandler` | After a session name change saves and commits; clearing the name reports `Name == null`. | A canceled or failed save does not commit and emits no event. Cancellation during dispatch stops the current observer and later observers. | TUI, RPC, print/JSON and direct Runtime session owners. | Observation only; host and extension rename paths share the committed-name publisher. |
| `AddSessionCompactHandler` / `AddSessionCompactFailedHandler` | After persisted success, or after failure/abort. Payloads include reason (`Manual`, `Threshold`, `Overflow`), result/entry metadata on success, and retry/abort/error details on failure. `FromExtension` is false because there is no custom-summary path. | The owning compaction token controls the observer sequence; cancellation stops observers. A failed or aborted operation emits no success event. | TUI, RPC, print/JSON compaction owners and direct Runtime compaction. | Observation only. Threshold and virtual-route overflow paths preserve their existing continuation/retry behavior. |
| `AddAgentStartHandler` / `AddAgentEndHandler` | Once at the beginning and end of one logical agent event pump; end includes outcome and detached completed messages. Queued steering/follow-up within that pump does not start another lifecycle. | Ordinary completion and provider failure produce an end callback. A host cancellation that interrupts the stream or cancels observer dispatch may suppress terminal observers so the owning operation can settle. Pinned Pi emits `agent_end` when the agent loop returns an aborted response. | TUI, RPC, print/JSON and direct Runtime runs. | Observation only; messages are detached snapshots. |
| `AddTurnStartHandler` / `AddTurnEndHandler` | Per logical model/tool continuation. Start has a zero-based index and timestamp; end has detached assistant/tool messages and canonical entry IDs where available. A provider retry without a logical response is not a new turn. | The run token owns callbacks. Cancellation may stop a terminal observer and still settles the run. | TUI, RPC, print/JSON and direct Runtime runs. | Observation only. PiSharp does not expose Pi's mutable turn-boundary state. |
| `AddToolExecutionStartHandler` / `AddToolExecutionUpdateHandler` / `AddToolExecutionEndHandler` | Around actual model-issued, nested, extension, built-in or MCP execution. Start sees effective arguments after existing call hooks; updates contain only real partial output; end sees the finalized result after result hooks and has duration when execution started. Nested calls retain parent IDs. | The tool execution token owns callbacks. A call blocked before execution has no execution start/end event; cancellation stops the in-flight observer and tool operation. | TUI, RPC, print/JSON and direct Runtime tool loops. | Observation only. Existing tool-call/result hooks remain the mutation and blocking surface. |
| `AddModelSelectHandler` | After an effective model selection becomes authoritative; includes selected/previous model and source (`Set`, `Cycle`, `Restore`). | Selection cancellation/rejection emits no change; observer cancellation stops later observers after the transition. | TUI, RPC, print/JSON startup restoration and direct Runtime selection owners. | Observation only; unchanged selections do not emit a change. |
| `AddThinkingLevelSelectHandler` | After an effective thinking-level change; includes selected and previous levels after model-capability clamping. | Selection cancellation/rejection emits no change; observer cancellation stops later observers after the transition. | TUI, RPC, print/JSON startup restoration and direct Runtime selection owners. | Observation only; unchanged effective values do not emit a change. |

Handlers run sequentially in resolved extension registration order. An ordinary
handler exception is reported with its package source and later handlers still
run. Host cancellation uses the token owned by the run or operation and is not
reported as an ordinary extension failure. Tool observers do not replace or
block the existing mutable call/result hooks.

Lifecycle callbacks receive `PiSharpExtensionContext`; registered commands and
shortcuts receive `PiSharpExtensionCommandContext`. Both expose a callback-scoped
`Capabilities` facade alongside their existing mode, UI, and session properties.
It provides a detached session snapshot; model, thinking, active/configured tool
and command snapshots; effective settings and the calling package's flag;
context usage when known; and the current system prompt when the host can provide
it. TUI/RPC callbacks use the existing control owner for active-tool, model,
thinking, abort, shutdown and message operations. Compaction is rejected while
an agent callback is part of an active run, because waiting for that run from
inside its callback would reenter the same run. Print/JSON and direct Runtime
hosts expose read state and session-state operations; operations unavailable in
that host phase fail clearly. Retained capability objects are revoked when the
callback ends, and returned message/settings/session data are detached.

These registrations do not implement generic `pi.on(...)`, mutable turn-boundary
state, provider request hooks, or advanced renderers. Session transition and
compaction mutation hooks are documented in the following section. The broad
extension parity row remains `in_progress`.

### Session transitions, tree navigation and compaction

Native extensions can inspect or veto the host's existing session transitions and compaction plan through typed hooks. These hooks run at the authoritative mutation boundary; they do not create sessions or forks, and they do not replace PiSharp's canonical session tree or compaction coordinator.

```csharp
builder.AddSessionBeforeSwitchHandler((change, _, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    var denyResume = change.Reason == PiSharpExtensionSessionSwitchReason.Resume &&
        change.TargetSessionFile?.EndsWith("blocked.jsonl", StringComparison.OrdinalIgnoreCase) == true;
    return ValueTask.FromResult<PiSharpExtensionSessionBeforeSwitchResult?>(
        new(Cancel: denyResume));
});

builder.AddSessionBeforeForkHandler((fork, _, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    var denyForkAtRoot = fork.Position == PiSharpExtensionForkPosition.Before && fork.EntryId == "root";
    return ValueTask.FromResult<PiSharpExtensionSessionBeforeForkResult?>(new(Cancel: denyForkAtRoot));
});

builder.AddSessionBeforeTreeHandler((navigation, _, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    var denyLeaf = navigation.TargetEntryId == "protected-leaf";
    return ValueTask.FromResult<PiSharpExtensionSessionBeforeTreeResult?>(new(Cancel: denyLeaf));
});

builder.AddSessionTreeHandler((navigation, context, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    context.Ui.Notify($"Selected {navigation.NewLeafEntryId} from {navigation.OldLeafEntryId}.");
    return ValueTask.CompletedTask;
});
```

`session_before_switch` reports the outgoing session identity, `New` or `Resume`, and the destination file when known. Invalid destinations remain host validation errors. A veto occurs before the outgoing session is shut down; an allowed transition uses the existing prepare, shutdown, adopt, and start lifecycle. `session_before_fork` reports the selected entry and `Before`/`At` position. `/fork` uses `Before`; `/clone` uses `At`, matching Pi's shared fork transition and allowing either operation to be vetoed before branch creation or active-run cancellation. Fork restoration overrides such as `skipConversationRestore` are deferred.

`session_before_tree` receives detached target/current leaf IDs, their common ancestor, the entries that would be summarized, and whether the user requested a summary. This milestone does not create a branch summary. A veto leaves the selected leaf and persisted history unchanged. The `session_tree` observer runs once after a different leaf is restored and saved successfully; it does not run for a veto, failed restore/save, or no-op selection. Tree summary injection, instruction replacement, and label overrides remain unsupported.

Pre-mutation hooks run in deterministic extension registration order. A `Cancel` result stops dispatch and prevents the operation. For non-cancel results, the last non-null result wins. An ordinary handler exception is reported with its owning extension and dispatch proceeds to the next handler; host cancellation propagates. Callback contexts belong to the outgoing session and are revoked when the callback ends or its operation is cancelled. After tree navigation is durably committed, `session_tree` is best-effort: it receives a host-owned 250 ms callback deadline, so cancellation after save does not suppress the completion event and an unresponsive observer cannot hold the session gate.

The compaction hook receives a detached snapshot of the coordinator's actual plan, active branch metadata, reason (`Manual`, `Threshold`, or `Overflow`), retry state, and custom instructions when present. It can cancel or provide a summary for the existing canonical compaction operation. Physical provider overflow recovery uses this hook when the request history maps to a safe canonical boundary; provider-only in-flight projections without such a boundary retain their existing projection behavior.

```csharp
builder.AddSessionBeforeCompactHandler((request, _, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    var preparation = request.Preparation;
    var custom = new PiSharpExtensionCustomCompaction(
        Summary: "Keep the user's goals, completed changes, and remaining work.",
        FirstKeptEntryId: preparation.FirstKeptEntryId,
        TokensBefore: preparation.TokensBefore);
    return ValueTask.FromResult<PiSharpExtensionSessionBeforeCompactResult?>(
        new(Compaction: custom));
});
```

The custom result supports `Summary`, `FirstKeptEntryId`, `TokensBefore`, optional JSON `Details`, and optional supported token `Usage`. PiSharp validates the result against the captured active branch and safe compaction boundaries before canonical history is changed. The retained entry may precede the prepared boundary, but may not follow it or split a tool-call/result pair. A valid result bypasses the default summary request but still uses the coordinator's normal append, persistence, and history restoration path. `session_compact` reports `FromExtension=true` for a custom result and `false` for default summarization. A veto emits the existing aborted `session_compact_failed` event without appending a compaction entry. Invalid custom results fail the operation with an extension-attributed diagnostic and leave canonical session state unchanged. Existing cancellation deadlines and callback-context revocation apply to these hooks.

### Providers, MCP servers and virtual models

`IPiSharpExtensionBuilder` can register a provider, an MCP server, and a logical virtual model during `Configure`. These definitions are collected with the owning package and enter the ordinary provider catalogue, model selection, MCP startup and virtual-model route path.

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Extensions.Abstractions;

builder.AddProvider(new PiSharpExtensionProviderDefinition(
    "fixture_provider",
    "Fixture provider",
    [new("small", "Fixture small", ContextLength: 32000, MaxOutputTokens: 2048,
        Input: ["text"], Reasoning: true)],
    context => CreateClient(context.ModelId, context.ApiKey))
{
    Endpoint = new Uri("https://api.example.test/v1"),
    ApiKeyEnvironment = "FIXTURE_PROVIDER_KEY",
    AuthRequired = true
});

builder.AddMcpServer("fixture-tools", new PiSharpExtensionMcpServerConfiguration
{
    Type = "stdio",
    Command = "python3",
    Arguments = [Path.Combine(extensionDirectory, "mcp_server.py")],
    WorkingDirectory = extensionDirectory,
    Exposure = "direct"
});

builder.AddVirtualModel(new PiSharpExtensionVirtualModelDefinition(
    "fixture_router", "auto", "Fixture automatic model",
    (request, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var priorRoutes = request.State is { ValueKind: JsonValueKind.Object } state &&
            state.TryGetProperty("routes", out var routes) ? routes.GetInt32() : 0;
        return Task.FromResult(new PiSharpExtensionVirtualModelRoute(
            "fixture_provider", "small", request.ThinkingLevel,
            JsonSerializer.SerializeToElement(new { routes = priorRoutes + 1 })));
    })
{
    ThinkingLevels = ["off", "low", "medium", "high"],
    ContextLength = 32000,
    MaxOutputTokens = 2048,
    Input = ["text"]
});
```

The provider's model metadata describes the models shown by normal model discovery. `Input` currently supports `text` and `image`; `Reasoning` and the optional thinking-level map feed PiSharp's usual thinking controls. The provider factory receives only the selected provider/model IDs, configured endpoint, and the resolved API key. `ApiKeyEnvironment` uses the existing host credential resolver; the key is request-time data and is not added to extension metadata or session files. Keep it out of logs and dispose clients according to the `IChatClient` contract. The provider API identity is `PiSharpExtensionProviderContract.ChatApi`; provider-specific OAuth callbacks, browser/device login, and custom protocols outside `IChatClient` are not part of this contract.

MCP definitions use the existing MCP configuration fields and startup manager. Stdio is supported, as are the existing HTTP and streamable-HTTP transports, headers, auth-provider, timeout, exposure, and OAuth configuration fields. The MCP runtime owns connection lifetime and discovered tools/resources. Model-visible tool names use the server's normal `mcp__<namespace>__<tool>` form; configured-tool metadata includes the extension source path. Extension servers are not written to `mcp.json`; configured servers continue to load normally. Names that collide after MCP namespace normalization follow the same deterministic configured-server precedence and diagnostics as other extension MCP registrations.

Virtual-model callbacks receive the logical model, thinking request, route reason, copied MEAI messages, previous successful physical model/thinking level, the prior failed model/thinking level/message/error when retrying, and optional JSON state. The callback chooses a physical provider/model and effective thinking level. PiSharp validates and selects that target through the ordinary provider catalogue, then runs it through the existing routed client. Route state is saved on the active conversation branch and supplied on a later non-direct route; normal session save/resume preserves it. The host applies its existing thinking-level clamping against the selected physical model. A virtual model cannot reuse the identity of a registered physical model under the same provider ID.

Provider, MCP, and virtual-model registrations carry the extension package source that declared them. A failed `Configure` restores all registrations from that package. Reload/replacement removes the old package's catalogue entries and connection ownership before later selection; current provider streams finish on their existing client, while requests through an old removed client fail with a source-aware error. Removing an extension does not rewrite historical session/model or virtual route-state entries.

The external provider/MCP/virtual-model fixture in the test suite is built with an ordinary `.csproj` that references `PiSharp.Extensions.Abstractions`; it does not reference Runtime or CLI. It is packed, installed and exercised in fresh PiSharp processes. This verifies that registration is available through normal startup and the regular provider/tool/session paths.

Provider-specific OAuth extension hooks, provider-specific browser/device flows, arbitrary late registration, classifier/image-provider registration, and the rest of Pi's larger extension event surface remain unsupported.

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
