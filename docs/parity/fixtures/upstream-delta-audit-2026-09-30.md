# Current Pi upstream delta audit — 2026-09-30

PiSharp was clean at `e52fcc0e399545972bdec613ce01706227b73d3f`; exact-head Linux CI run [36698889067](https://github.com/PhilipGB/pisharp/actions/runs/36698889067) passed restore, format, warnings-as-errors build, and tests. The prior documented Pi source pin was `3e9451238337071b74ba5cdd53f1ab7cf4100ae8`. Current Pi `main` is `db6cc71dc7b69202dc560e71106bb9dfd454e758`. The pin is an ancestor of the current head, with 18 intervening commits, all reviewed in chronological order against their source/tests and the corresponding PiSharp implementation.

Classification totals: **8 `NEEDS_WORK`, 1 `MATCHED`, 4 `OUT_OF_SCOPE`, 5 `NO_BEHAVIOR_CHANGE`**. No implementation changes are included in this audit refresh. In-scope deltas are processed oldest first, beginning with `3dd803d7`.

## Commit classifications

### `3dd803d7e780fb04b815261b510f76bff563d46d` — `NEEDS_WORK`

- **Pi files:** `packages/ai/src/utils/overflow.ts`, `packages/ai/test/overflow.test.ts`.
- **Pi behavior:** classify a 400 response whose message says `prompt exceeds max length` as context overflow, including Z.AI CN code 1261, so the agent can take its one-shot overflow recovery path.
- **PiSharp behavior / mismatch:** `ObservedChatClient` recognizes context-window wording but not this Z.AI phrase; the same 400 currently surfaces as an ordinary provider error.
- **PiSharp files:** `src/PiSharp.Runtime/Sessions/ObservedChatClient.cs` and its focused tests.
- **Required test:** deterministic provider-error fixture with status 400 and the exact Z.AI CN wording/code; assert overflow classification and recovery occurs once.
- **Required implementation:** add the narrow phrase recognition to the existing overflow classifier without broadening status handling.
- **Local resolution evidence:** the new regression was run before the fix and failed with one request instead of the expected two. After adding the phrase check, the focused overflow theory passed 8/8; `dotnet format PiSharp.slnx --verify-no-changes` passed; `dotnet build PiSharp.slnx --warnaserror` passed with 0 warnings/errors using the writable NuGet HTTP cache; the full suite passed 1,000/1,000 with 0 skipped under `TERM=xterm-256color`. Commit `2550531a7f4813e9f10abce4241cbffc28f2ea7c` was pushed; exact-head Linux CI [36746011211](https://github.com/PhilipGB/pisharp/actions/runs/36746011211) passed restore, format, warnings-as-errors build, and 1,000/1,000 tests with zero skips.

### `d2931ad3d5bf6936fbdfa5dfc81fb32f875499b2` — `NEEDS_WORK`

- **Pi files:** `packages/codemode/src/runtime/prelude-source.ts`, `packages/codemode/test/sandbox.test.ts`, `packages/coding-agent/test/suite/agent-session-codemode.test.ts`.
- **Pi behavior:** `image()` strips base64 whitespace, validates base64 and the actual PNG/JPEG/GIF/WebP signature, and derives the emitted MIME from the bytes rather than trusting the declared data-URL MIME.
- **PiSharp behavior / mismatch:** before this slice the worker checked the declared image MIME and base64 alphabet, while host conversion decoded base64 without checking its signature. Malformed or mislabeled images could reach persisted tool output and later provider requests.
- **PiSharp files:** `src/PiSharp.Runtime/Codemode/Engine/worker.mjs`, `src/PiSharp.Runtime/Extensions/PiSharpToolResult.cs`, and Codemode/tool-result tests.
- **Required test:** cover whitespace-wrapped valid data, malformed base64, invalid signatures, all four supported signatures, and a declared MIME that differs from the detected signature; prove invalid data is rejected before persistence and output MIME follows the actual bytes.
- **Required implementation:** normalize base64 whitespace, validate base64 structure and supported image signatures at the guest boundary, and emit the signature-derived MIME.
- **Resolution evidence:** fail-first Codemode coverage showed malformed `AAAA` image data in the follow-up provider request before the fix; pre-fix checks also accepted malformed data and retained the declared MIME for mislabeled content. The worker now normalizes wrapped base64, checks its structure and supported PNG/JPEG/GIF/WebP prefixes with QuickJS-compatible predicates, and bridges only the detected MIME. `CodemodeTests` pass 10/10, including provider-loop non-persistence, all four signatures, a declared MIME mismatch, wrapped whitespace, malformed base64 and unsupported signatures. `dotnet format PiSharp.slnx --verify-no-changes` passes; `dotnet build PiSharp.slnx --warnaserror` passes with zero warnings/errors; exact-head Linux CI for `ee71fc63905044abbeaa2dd0e134a54fbd797f20`, run [36749258733](https://github.com/PhilipGB/pisharp/actions/runs/36749258733), passes restore, format, build with zero warnings/errors, and 1,003/1,003 tests with zero skips.

### `4a42f8fafaff65126b6c21632be03b605aec9ad5` — `OUT_OF_SCOPE`

Pi fixes worker packaging in Pi's Windows single-binary release pipeline. PiSharp documents Codemode as a separate Node.js process requiring Node on `PATH`; it has no equivalent bundled Windows binary or embedded worker asset pipeline in the requested coding-agent capability surface.

### `96377f5c2a980a6c2df4cf91beb8b721ab4890c5` — `OUT_OF_SCOPE`

Documentation-only structured-concurrency design for the separate `packages/durable` library. The requested scope excludes Pi Packages unless required for a core coding-agent capability; this library contract is not part of that surface.

### `03180653c624d1c079a509c61ab30322e7ff7b62` — `OUT_OF_SCOPE`

Adds task ownership, completion holds, waiting, and bottom-up abort to the separate durable execution package. These durable-library APIs are outside the requested coding-agent surface.

### `1c7e7df765a6fe542d24906756ee981bf7bb84c4` — `NEEDS_WORK`

- **Pi files:** `packages/coding-agent/src/core/mcp-servers.ts`, `packages/coding-agent/src/extensions/codemode/{execute.ts,tool.ts}`, `packages/coding-agent/src/extensions/mcp/{config.ts,index.ts,tools.ts}`, `packages/coding-agent/src/extensions/tool-search/tool.ts`, and related extension types/tests/docs.
- **Pi behavior:** MCP `codemode` exposure does not put each tool schema in the Codemode description. That description lists server namespaces and configured summaries; scripts use `searchTools(query, { namespace })` and `describeNamespace(name)` for callable tools, optional summaries and server initialize instructions. `codemode-deferred` is accepted as a legacy alias for `codemode`.
- **PiSharp behavior / mismatch:** Codemode rendered callable MCP tool samples directly in its description and exposed `searchTools`/`describeTool`, but had no `describeNamespace` path, MCP `description` configuration, or server-initialize instruction projection.
- **PiSharp files:** `src/PiSharp.Runtime/Codemode/CodemodeBuiltin.cs`, `src/PiSharp.Runtime/Codemode/CodemodeSandbox.cs`, `src/PiSharp.Runtime/Codemode/Engine/worker.mjs`, and MCP/tool namespace registration code and tests.
- **Required test:** configure two MCP namespaces with descriptions/instructions and tools; assert the model-facing Codemode description lists namespaces rather than all MCP tool schemas, and scripts can discover/describe each namespace and invoke only callable tools.
- **Required implementation:** accept configured server summaries and capture MCP initialize instructions as namespace metadata; list MCP namespaces without tool schemas or instructions in the model-facing Codemode description; add a bounded guest `describeNamespace` global, preserving callable-tool authorization. Canonicalize legacy `codemode-deferred` to `codemode`.
- **Resolution evidence:** a fail-first two-server stdio fixture failed because `mcp.json` summaries were absent from the model-facing Codemode description. The runtime now projects each server's configured `description` and MCP initialize `instructions` into its namespace. Codemode lists two namespace names/summaries without tool names, schemas, counts or instructions; `describeNamespace` returns bounded instructions and only callable tool names; namespace-scoped `searchTools` returns only the requested server's tools, nested calls reach both servers, and a hidden tool is absent and cannot be invoked. A separate configuration assertion verifies `codemode-deferred` canonicalizes to Codemode exposure. Focused Codemode/MCP/config tests pass 16/16; format and warnings-as-errors build pass; full local tests pass 1,004/1,004 with zero skips. PiSharp source `f61455a21c359ec1af704c8b093f1d209db3cb51` passes exact-head Linux CI [36753858082](https://github.com/PhilipGB/pisharp/actions/runs/36753858082): restore, format, warnings-as-errors build and 1,004/1,004 tests, zero skips.

### `7c9fe66452de9689223e3b490939f014c6e94e52` — `NEEDS_WORK`

- **Pi files:** `packages/coding-agent/src/core/mcp-servers.ts`, `packages/coding-agent/src/extensions/mcp/{cli.ts,oauth.ts,runtime.ts}`, and OAuth tests/docs.
- **Pi behavior:** accept a per-server `oauth.clientName`, validate it, and send that name as OAuth dynamic-client-registration `client_name`; the default remains `pi`. `pi mcp add --oauth-client-name` writes the same setting.
- **PiSharp behavior / mismatch:** `McpOAuthSettings` has client ID/secret, callback URL and scopes only; configuration parsing and SDK registration do not expose the custom OAuth client name.
- **PiSharp files:** `src/PiSharp.Runtime/Mcp/McpOAuth.cs`, MCP configuration/loading and OAuth runtime code, and MCP OAuth tests.
- **Required test:** loopback OAuth authorization-server fixture asserting default and custom registration names; CLI add serialization and empty-name rejection; and invalid configuration rejection with secret-safe diagnostics.
- **Required implementation:** add validated per-server client-name configuration, pass it to the SDK registration boundary, and support the equivalent `mcp add --oauth-client-name` option without changing token ownership or persistence.
- **Resolution evidence:** a fail-first loopback OAuth fixture showed that configured `oauth.clientName` was rejected and the default SDK DCR request omitted `client_name`. The implementation validates string/non-empty values, passes `clientName ?? "pi"` through `DynamicClientRegistrationOptions`, and wires `pisharp mcp add --oauth-client-name` into HTTP config while rejecting empty names and incompatible transports/auth headers. The loopback authorization server verifies custom and default names on each dynamic-registration request; config tests reject blank/non-string names without revealing a configured client secret. Focused OAuth/config/CLI tests pass 17/17; format and warnings-as-errors build pass; the full suite passes 1,006/1,006 with zero skips. Source `336be83135a0b18393bfaa41f7f826a34314f6eb` passes exact-head Linux CI [36756672893](https://github.com/PhilipGB/pisharp/actions/runs/36756672893): restore, format, warnings-as-errors build and 1,006/1,006 tests, zero skips.

### `37c9d0d200fbf609485623e49871ad6306ffbb60` — `OUT_OF_SCOPE`

Hardens a persistent-subagent example in `packages/durable`. It changes no coding-agent package behavior and the durable package remains outside this task's scope.

### `a0660b174eceded86ab93d31320249e6dc994ef8` — `NO_BEHAVIOR_CHANGE`

Pi resolves branch model selection with one catalog lookup instead of repeated lookups. PiSharp's current selection path already resolves the selected model once; this optimization has no separately observable delta. Existing branch-selection parity questions remain tracked independently.

### `c34f2d6ad593e57c5f9903e2bcd77ed3f8e3c216` — `NO_BEHAVIOR_CHANGE`

Changes remote catalog model merging from quadratic to linear work while preserving the resulting model list and order. No PiSharp behavior change is required.

### `dc83372f8fb890f37153a825860500e6830dba37` — `MATCHED`

Pi rejects extension command registrations with an empty name or missing handler. PiSharp's `ExtensionCatalog.AddCommand` already rejects blank/invalid command names and null handlers (as well as reserved and duplicate names); invalid registrations fail at the registration boundary.

### `7ef68d4f2f4a8d53fc7ad4ff31172c11918e2a23` — `NO_BEHAVIOR_CHANGE`

Fixes JavaScript `fetch` receiver binding in Pi's Streamable HTTP transport for runtimes such as Cloudflare Workers. PiSharp uses the .NET MCP SDK and `HttpClient`; this JavaScript receiver failure does not apply to its supported transport.

### `295cc72b03058ee4df1936046b1e1ec67978af4d` — `NO_BEHAVIOR_CHANGE`

Pi avoids requesting Anthropic strict tool schemas when a schema contains rejected keywords. PiSharp's Anthropic adapter does not emit strict tool-schema mode, so the triggering request shape is not currently produced. Strict-schema mode remains a separate feature question, not a regression from this delta.

### `2bbfcca437c3aa5a21af1e4ee44ae7a051f953ad` — `NEEDS_WORK`

- **Pi files:** `packages/ai/src/utils/provider-retry.ts` and provider-retry tests.
- **Pi behavior:** when `Retry-After` is present but unparseable or non-finite, use the configured exponential-backoff delay instead of retrying immediately.
- **PiSharp behavior / mismatch:** `ProviderRequestRetryPolicy.GetServerDelay` returns zero for an invalid date value, bypassing exponential backoff.
- **PiSharp files:** `src/PiSharp.Cli/ProviderRetryChatClient.cs` and provider retry policy tests.
- **Required test:** deterministic policy/loopback response with malformed `Retry-After`; assert the retry waits for the computed backoff, respects cancellation, and does not sleep for an excessive server delay.
- **Required implementation:** represent invalid server delay as absent so the existing exponential policy selects the delay; preserve valid seconds/date handling.

### `e029c3ed0add2ba55cc9c68b2bc3920e56d32888` — `NEEDS_WORK`

- **Pi files:** `packages/coding-agent/src/core/mcp-servers.ts`, `packages/coding-agent/src/extensions/{codemode/execute.ts,codemode/tool.ts,mcp/cli.ts,mcp/index.ts,tool-search/tool.ts}`, and MCP/Codemode/tool-search tests.
- **Pi behavior:** only direct MCP tools hold the first prompt for the bounded startup wait. Codemode/tool-search wait in the background for only the named/needed servers, and MCP server state is described in a dedicated context section.
- **PiSharp behavior / mismatch:** `McpRuntime.RegisterAsync` starts connections concurrently but waits up to ten seconds for all enabled servers, including indirect Codemode servers, before the first prompt.
- **PiSharp files:** `src/PiSharp.Runtime/Mcp/McpRuntime.cs`, MCP connection/registration and prompt execution boundaries, plus MCP/Codemode process tests.
- **Required test:** hold an indirect server connection open and prove the first prompt starts without waiting; prove a script naming one server waits only for that server; preserve bounded waiting for direct tools and cancellation/cleanup.
- **Required implementation:** classify direct versus indirect exposures, move indirect connection completion to operation-time waits, and attach only the required server-state context without changing callable authorization.

### `0582d9c11da78c1812d4537af2d194f1dd060d26` — `NEEDS_WORK`

- **Pi files:** `packages/coding-agent/src/core/tools/renderers/bash.ts`, `packages/coding-agent/src/extensions/{codemode/renderer.ts,mcp/tools.ts}`, `packages/coding-agent/src/modes/interactive/components/visual-truncate.ts`, and renderer tests.
- **Pi behavior:** result previews are limited by visual wrapped rows, including a single long logical line, for Bash, Codemode and MCP.
- **PiSharp behavior / mismatch:** `TerminalTranscriptBuffer.PreviewToolResult` limits logical lines, so a single very long line bypasses the intended visual preview bound.
- **PiSharp files:** `src/PiSharp.Cli/Tui/TerminalTranscriptBuffer.cs` and terminal/tool-preview tests.
- **Required test:** at a narrow terminal width, render one long unbroken line and wrapped multiline text; assert the preview stays within the visual-row limit and preserves a useful truncation marker for all three result families.
- **Required implementation:** measure preview limits in terminal display cells/rows using the existing ANSI-aware layout rules; retain the full result in the tool/session record.

### `028c0ec56ee237764af95a77522dd674e8cfe95c` — `NO_BEHAVIOR_CHANGE`

Pi filters Codemode-hidden tools out of Pi's separate system-prompt tool-snippet section when only Codemode is declared. PiSharp has no corresponding generated `<tools>` system-prompt section: nested callable tools are intentionally documented inside the Codemode tool description while remaining absent from provider declarations. The specific duplicate-snippet bug is therefore not present.

### `db6cc71dc7b69202dc560e71106bb9dfd454e758` — `NEEDS_WORK`

- **Pi files:** `packages/coding-agent/src/core/agent-session.ts`, `packages/coding-agent/src/core/sdk.ts`, and `packages/coding-agent/test/default-tools-setting.test.ts`.
- **Pi behavior:** on reload, tools newly added to `defaultTools` are activated in the current session; removed defaults do not silently deactivate active tools, and explicit tool lists/no-tools modes are preserved.
- **PiSharp behavior / mismatch:** `/reload` rebuilds the project agent using current settings but opens the existing conversation with its persisted active loadout, so newly added defaults are not merged into the current branch loadout.
- **PiSharp files:** `src/PiSharp.Cli/Program.cs` resource reload path, `src/PiSharp.Cli/UserSettings.cs`, `src/PiSharp.Runtime/Extensions/ToolRegistry.cs`/loadout restore boundary, and settings/reload process tests.
- **Required test:** change project/user `defaultTools`, reload an open session and assert newly added defaults activate while existing tools remain; verify removed defaults remain active and explicit `--tools`/`--no-tools` behavior is unchanged.
- **Required implementation:** track whether the active loadout originated from defaults versus an explicit CLI/session selection, then add only newly effective default names on reload and persist the resulting branch-local loadout.

## Processing order

The eight `NEEDS_WORK` entries are handled in upstream order unless a concrete dependency requires otherwise: Z.AI CN overflow; Codemode image validation; MCP namespaces in Codemode; MCP OAuth client name; invalid `Retry-After`; MCP startup waiting; wrapped result previews; default-tool activation on reload. The first four are implemented and verified at exact heads: Z.AI CN overflow at `2550531a7f4813e9f10abce4241cbffc28f2ea7c` / CI `36746011211`, Codemode image validation at `ee71fc63905044abbeaa2dd0e134a54fbd797f20` / CI `36749258733`, MCP namespace discovery at `f61455a21c359ec1af704c8b093f1d209db3cb51` / CI `36753858082`, and MCP OAuth client naming at `336be83135a0b18393bfaa41f7f826a34314f6eb` / CI `36756672893`. Four in-scope deltas remain; continue with malformed `Retry-After` fallback. Each slice requires fail-first evidence, focused tests, format, warnings-as-errors build, full tests, updated parity records, one coherent commit/push, and exact-head CI before proceeding.
