# Current Pi upstream delta audit — refreshed 2026-10-01

The previously documented Pi pin was `955cc6665ee3986c6a033db52200779310d10dfd`. Pi `main` was refreshed first to `b56702ad345201a1de46a5f8e94542a3a59ad3bd`, then to `8ce69e9d2b171d173fe4b6b2b6256f1f4411e69d`, `70c036211de5378508ae2456f3c7ae3cf9f8bc75`, and was refreshed after the previous CI to `a4715ec9bffbfcb8a32a1a4100dcfd06c12c93e4`; the prior pin is an ancestor, with 23 intervening commits reviewed in chronological order against their source, tests, and PiSharp equivalents. PiSharp source `f72ff8ef40145c3848ff712cefc3470e08dba94b` passed exact-head Linux CI on the feature branch [36837658517](https://github.com/PhilipGB/pisharp/actions/runs/36837658517) and `main` [36838135728](https://github.com/PhilipGB/pisharp/actions/runs/36838135728), each with 1,057/1,057 tests and zero skips. The separate root worktree still has uncommitted extension context-transform work; it is not included in this source or CI evidence.

Current classifications: **7 `NEEDS_WORK`, 11 `OUT_OF_SCOPE`, 5 `NO_BEHAVIOR_CHANGE`**. The seven in-scope behavior gaps are listed in commit order. No overall parity claim is implied.

## Commit classifications

### `f3e68e8eaf3274d8111796b9df4e6b43e2f8e58a` — `OUT_OF_SCOPE`

Changes asynchronous SQLite storage in the separate experimental `packages/durable` library. This does not change the coding-agent capability surface audited by PiSharp.

### `fd3af5ee2bb27b51d24de52be77723b41834798d` — `OUT_OF_SCOPE`

Changes transaction-handle semantics in `packages/durable`'s SQLite storage API, outside the coding-agent capability surface.

### `cd0ba2fabcebd1c234d1009595cebf37a1f6c5e6` — `OUT_OF_SCOPE`

Changes SQL-text execution and statement caching in `packages/durable` storage, outside the coding-agent capability surface.

### `77ac62f22a8198608ea6d15b48547cfe2df150fe` — `OUT_OF_SCOPE`

Refactors the Node SQLite queue and bindings in `packages/durable`; it does not affect the coding-agent application surface.

### `8f89293a26a46e7c5c3213dfbf7df887a6a7dc6d` — `NO_BEHAVIOR_CHANGE`

Merge commit for the asynchronous SQLite storage work already represented by its constituent commits above; the merge commit adds no distinct tree change.

### `a0feabda7ce19ba7155c57775519640606e74a1e` — `OUT_OF_SCOPE`

Removes SQLite adapter misuse detection from `packages/durable`; the affected adapter is outside the coding-agent capability surface.

### `ed391c4f0a2db137c4b218d1d18352261cc60dfb` — `OUT_OF_SCOPE`

Hardens the async SQLite queue and close behavior in `packages/durable`, outside the coding-agent capability surface.

### `d4d74eb19be92c559f629a7f9707c5503a840edc` — `NO_BEHAVIOR_CHANGE`

Merge commit for the SQLite follow-up already represented by its constituent commit; it adds no distinct tree change.

### `b35af04f465d60c2f15d124ed074476b8986deb4` — `OUT_OF_SCOPE`

Adds a header-click-only hidden logo animation and a last-frame accessor for that animation. This is a decorative easter egg rather than a command, agent, session, provider, or protocol behavior in the audited interface inventory; PiSharp does not have a general fullscreen animation subsystem. The optional visual effect is outside the current functional parity scope.

### `65117e31f242daa8e0a17dbd2810f7bb92bf4087` — `NEEDS_WORK`

- **Pi files:** `packages/tui/src/autocomplete.ts`, `packages/tui/test/autocomplete-skill-slash.test.ts`.
- **Pi behavior:** leading spaces or tabs before a slash command do not prevent command-name completion. The original indentation remains in the editor. The same normalization lets existing command-argument completers run when the command is indented.
- **PiSharp behavior / mismatch:** `EditorCompletion` recognizes slash commands only when the slash token starts at offset zero, so `"  /mod"` falls through to path completion. PiSharp currently exposes command-name completion but has no command-argument completion contract; that pre-existing broader gap is separate from this commit's whitespace fix.
- **PiSharp files:** `src/PiSharp.Cli/Tui/EditorCompletion.cs`, `tests/PiSharp.Tests/EditorCompletionTests.cs`.
- **Required test:** cover one or more leading spaces and a tab before a slash prefix; assert matching command candidates and that applying a candidate preserves the exact leading whitespace. Keep the existing mid-sentence path-completion behavior.
- **Required implementation:** identify slash commands after leading whitespace only when the entire prompt prefix is whitespace plus the slash command, and apply completion to the command fragment without replacing the indentation.
- **Resolution evidence:** the fail-first theory returned no candidates for one space, two spaces, and a tab before `/tr`. `IsSlashCommandStart` now recognizes the slash after current-line indentation, while mid-sentence slash text remains path input. `EditorCompletionTests` passes 10/10, including the three new cases. Local restore passes with `--no-http-cache` (the default vulnerability-cache path is read-only in this environment); format verification passes and formats 0 of 409 files; warnings-as-errors build passes with 0 warnings/errors; all 1,039 tests pass with zero skips using `TERM=xterm-256color` and `xUnit.ParallelizeTestCollections=false`. Exact-head Linux CI [36795625551](https://github.com/PhilipGB/pisharp/actions/runs/36795625551) passes restore, format, warnings-as-errors build with 0 warnings/errors, and 1,039/1,039 tests with zero skips on PiSharp source `884a69b07743e57e135a50ce03e65f11c74cdd39`.

### `7a11fe1c723b942fa093edb8b00f051ddb9b6627` — `NEEDS_WORK`

- **Pi files:** `packages/ai/src/auth/oauth/anthropic.ts`, `packages/ai/test/anthropic-oauth.test.ts`.
- **Pi behavior:** Anthropic OAuth offers browser login by default and a headless copy-code option. The copy-code flow uses PKCE, Anthropic's code callback redirect URI, validates the returned state when present, exchanges the authorization code, and supports cancellation; the existing browser flow remains available.
- **PiSharp behavior / mismatch:** Anthropic supports API keys, auth tokens, and workload identity federation, but has no Anthropic OAuth adapter or refresh flow. Stored bearer-token acceptance does not implement this login behavior.
- **PiSharp files:** `src/PiSharp.Cli/ProviderOAuth.cs`, `src/PiSharp.Cli/ProviderModelRuntime.cs`, `src/PiSharp.Cli/BuiltinProviderProfiles.cs`, a new Anthropic OAuth adapter, and focused adapter/CLI authentication tests.
- **Required test:** deterministic loopback token-exchange fixtures for browser and copy-code methods; verify default method selection, exact redirect URI, PKCE/state validation, successful private credential persistence and refresh, cancellation, and rejected malformed token responses.
- **Required implementation:** register an Anthropic OAuth adapter with browser and copy-code methods, implement PKCE authorization/token exchange and refresh, preserve existing API-key/auth-token priority, and persist only through `AuthStorage`.
- **Local resolution evidence:** the fail-first provider-profile test confirmed Anthropic exposed no OAuth support. PiSharp now registers browser and copy-code methods, uses PKCE and the corresponding redirect URIs, validates supplied state before exchange, falls back to the generated state when code input omits it, stores credentials through `AuthStorage`, and refreshes rotated tokens through the shared OAuth coordinator. Manual code input is masked in the console. `AnthropicOAuthTests` passes 8/8, covering runtime registration, browser callback, copied code and full redirect URL inputs, PKCE, state rejection, refresh, cancellation and malformed response handling. Restore, format (0 of 411 files formatted), warnings-as-errors build (0 warnings/errors), and all 1,047 tests pass locally with zero skips. Exact-head Linux CI [36798041083](https://github.com/PhilipGB/pisharp/actions/runs/36798041083) passes on PiSharp source `ec7fe828d820ff6967fa569f3b93affe98177a0a`: restore, format, warnings-as-errors build and 1,047/1,047 tests with zero skips.

### `d850edee9ccc5d3b6cec79553d4839a1cbab47ed` — `NEEDS_WORK`

- **Pi files:** `packages/coding-agent/src/core/mcp-servers.ts`, `packages/coding-agent/src/extensions/mcp/{oauth.ts,runtime.ts}`, `packages/mcp/src/oauth/{discovery.ts,errors.ts,flow.ts,types.ts}`, and MCP OAuth/configuration tests.
- **Pi behavior:** an optional `oauth.authServerMetadataUrl` supplies a trusted authorization-server metadata document instead of normal authorization-server discovery; it must be HTTPS except for loopback HTTP. When metadata advertises RFC 9207 issuer responses, authorization codes are exchanged only if the callback `iss` equals that metadata's issuer. A present but mismatched `iss` is rejected as well.
- **PiSharp behavior / mismatch:** `McpOAuthSettings` does not accept a metadata URL, and the SDK performs only its normal discovery flow. RFC 9207 issuer validation is already present in the pinned `ModelContextProtocol.Core` 2.2.0: `McpOAuthLogin` returns callback `iss` in `AuthorizationResult`, and `ClientOAuthProvider.ValidateIssuerResponse` checks it against discovered metadata before code exchange. The remaining mismatch is the configured trusted metadata-document override and its URL validation.
- **PiSharp files:** `src/PiSharp.Runtime/Mcp/McpOAuth.cs`, `src/PiSharp.Runtime/Mcp/McpOAuthLogin.cs`, `src/PiSharp.Runtime/Mcp/McpOAuthMetadataHandler.cs`, `src/PiSharp.Runtime/Mcp/McpRuntime.cs`, MCP configuration parsing, and focused tests in `tests/PiSharp.Tests/McpOAuthMetadataTests.cs`.
- **Required test:** loopback fixture with an explicit metadata document; prove it replaces discovery, rejects insecure non-loopback URLs, and verifies matching/missing/wrong callback `iss` against the configured issuer before any authorization code reaches the token endpoint. Keep coverage for the SDK-provided RFC 9207 check while testing the configured-document path.
- **Required implementation:** validate and pass the configured metadata document into the OAuth flow as a trusted override that replaces normal authorization-server discovery. Preserve the SDK issuer checks and reject unsafe metadata URLs before network access.
- **Resolution evidence:** fail-first parsing rejected the new option as unknown. The implementation accepts only HTTPS or HTTP on localhost/loopback, fetches the configured metadata document in place of ordinary authorization-server discovery for login and runtime, and leaves RFC 9207 validation in `ModelContextProtocol.Core` intact. The loopback fixture covers matching, missing and wrong callback `iss`; it verifies the configured document is requested, normal discovery is not, and a rejected issuer never reaches the token endpoint. The combined MCP OAuth tests pass 21/21. Exact-head runs [36835477212](https://github.com/PhilipGB/pisharp/actions/runs/36835477212) and [36835795082](https://github.com/PhilipGB/pisharp/actions/runs/36835795082) each pass restore, format, warnings-as-errors build with zero warnings/errors, and 1,057/1,057 tests with zero skips on source `6bddf37a65e64e915969bd2519f08031b7cb3525`.

### `17f3dccbef6c56cbc8cee73dbcfe2bba4f8734f9` — `NEEDS_WORK`

- **Pi files:** `packages/tui/src/utils.ts`, `packages/tui/test/regression-slice-by-column-ansi-order.test.ts`.
- **Pi behavior:** when slicing ANSI-styled text by display columns, SGR codes before the slice and codes exactly at the boundary retain source order. This prevents a reset from being emitted before the style it resets and leaking color beyond mouse-selection or search-highlight boundaries.
- **PiSharp behavior / mismatch:** `TerminalTextLayout` has separate ANSI-aware wrapping, slicing, and selection-highlighting paths, but no fixture covers the upstream boundary ordering case. The equivalent rendered behavior is therefore not yet established.
- **PiSharp files:** `src/PiSharp.Cli/Tui/TerminalTextLayout.cs`, `src/PiSharp.Cli/Tui/TranscriptSearchController.cs`, and `tests/PiSharp.Tests/TerminalTextLayoutTests.cs`.
- **Required test:** port both upstream boundary inputs through the corresponding PiSharp cell-slice/render path and assert that style activation and reset remain ordered at the start of the visible range, with no color bleed after the selected token.
- **Required implementation:** preserve ordered pending SGR state at a column slice boundary in the shared layout/highlight path; keep plain-text clipboard slicing behavior intact.

### `4bae8677597bc4f96acae5e6d3039de1f61ecb58` — `NO_BEHAVIOR_CHANGE`

Documentation-only changes to the separate `packages/durable` design; no runtime behavior changes.

### `b56702ad345201a1de46a5f8e94542a3a59ad3bd` — `OUT_OF_SCOPE`

Adds extensions and per-conversation agent configuration to the separate experimental `packages/durable` harness. Although examples use coding agents, these APIs define a distinct durable task runtime and storage-backed harness, not the Pi coding-agent package interface being ported. PiSharp's session persistence does not make the separate harness part of this audit's scope.

### `8ce69e9d2b171d173fe4b6b2b6256f1f4411e69d` — `NEEDS_WORK`

- **Pi files:** `packages/mcp/src/client.ts`, `packages/mcp/src/oauth/{discovery.ts,flow.ts,types.ts}`, and the matching MCP client/OAuth tests.
- **Pi behavior:** optional empty/null OAuth fields are treated as absent, including empty refresh tokens and client secrets; `expires_in: null` leaves expiry unspecified; empty requested scopes fall through to the next source; invalid URLs in protected-resource metadata fall back to the MCP server origin; and empty/null pagination cursors end pagination.
- **PiSharp behavior / mismatch:** the pinned `ModelContextProtocol.Core` 2.2.0 SDK handles nullable expiry and ignores an empty dynamic-registration client secret, and its scope selection falls through for empty configured/challenge scopes. However, `McpClient.ListToolsAsync` continues while `NextCursor` is non-null, so `""` is treated as another page. OAuth token parsing stores `refresh_token: ""` rather than preserving the previously stored refresh token, and invalid `authorization_servers` metadata is rejected during authorization-server selection instead of falling back to the resource origin. `McpOAuthRefreshHandler.IsValidTokenResponseAsync` also rejects `expires_in: null` for its cross-process refresh coordination path.
- **PiSharp files:** dependency boundary `ModelContextProtocol.Core` 2.2.0 (`src/PiSharp.Runtime/PiSharp.Runtime.csproj`); PiSharp integration `src/PiSharp.Runtime/Mcp/McpRuntime.cs`; `src/PiSharp.Runtime/Mcp/McpOAuthRefreshHandler.cs`; the delegated pagination implementation in the SDK; and focused tests under `tests/PiSharp.Tests/`.
- **Required test:** deterministic streamable-HTTP pagination and OAuth fixtures for empty/null cursors, token responses with empty/null optional fields and refresh-token preservation, and invalid resource-metadata URLs; verify no repeated page request and that valid existing credentials survive refresh.
- **Required implementation:** normalize the SDK boundary to Pi's optional-field semantics, preserve an existing refresh token when the response supplies an empty value, stop pagination on an empty cursor, and use the server-origin fallback for invalid resource-metadata URLs. Keep the already-matching null-expiry and empty-scope behavior covered.

### `5b5ccddfac3abba2f3c26b558ec69dcde85000ab` — `NO_BEHAVIOR_CHANGE`

Documentation-only changes in `packages/durable/docs/` clarify lifecycle wording and the task-graph view; no runtime behavior changes.

### `49683a36476e3a3fdbb40008745073375df563b7` — `OUT_OF_SCOPE`

Adds lifecycle conformance and task-graph APIs to the separate `packages/durable` harness. These alter a durable task runtime and storage model, not the Pi coding-agent capability surface being ported.

### `5609b0d6c07cd3bf8014429123086f6da0a5e14e` — `OUT_OF_SCOPE`

Adds an explicitly experimental coding-agent TUI backed by the separate Pi Durable harness. It is an alternative durable runtime rather than a change to the default coding-agent behavior audited by PiSharp.

### `41169ba2af7f5bd5c97237633879117bc3fa9cd9` — `NEEDS_WORK`

- **Pi files:** `packages/coding-agent/src/extensions/mcp/index.ts` and its changelog.
- **Pi behavior:** `/mcp login` in TUI mode emits the long authorization URL as an OSC 8 hyperlink and puts the click instruction on a separate short linked line, so terminal wrapping does not break the target.
- **PiSharp behavior / mismatch:** `pisharp mcp login` prints a plain URL through `McpOAuthLogin`; the TUI terminal renderer understands OSC 8, but there is no equivalent interactive MCP login action or linked authorization-URL projection.
- **PiSharp files:** `src/PiSharp.Runtime/Mcp/McpOAuthLogin.cs`, `src/PiSharp.Cli/McpCommand.cs`, `src/PiSharp.Cli/Tui/TerminalTextLayout.cs`, and focused MCP/TUI tests.
- **Required test:** exercise the MCP login URL through a PTY/TUI notification with a long URL; verify the OSC 8 target survives display wrapping and the short click hint points to the same URL, while plain CLI output remains readable.
- **Required implementation:** expose the MCP login URL through the TUI's hyperlink-capable notification path and preserve the existing plain-text CLI behavior.

### `70c036211de5378508ae2456f3c7ae3cf9f8bc75` — `OUT_OF_SCOPE`

Changes the default task-panel visibility in the experimental durable TUI added by `5609b0d`; it does not change the default Pi coding-agent runtime or its audited interfaces.

### `0c453048bd4bb699b90463d9423c382d1787087f` — `NEEDS_WORK`

- **Pi files:** `packages/coding-agent/src/main.ts`, `packages/coding-agent/src/cli/args.ts`, and `packages/coding-agent/docs/cli.md`.
- **Pi behavior:** `--provider` without `--model` reports an error instead of silently ignoring the provider and running the default model from another provider. Help now describes `--provider` as a filter for `--model` and removes an outdated default-provider claim.
- **PiSharp behavior / mismatch:** `CliArguments.Parse` accepts `--provider` alone. `Program` passes it to `ProviderModelRuntime.ResolveAsync`; when there is no model reference, `ResolveSelectionAsync` selects the provider's first configured model, or settings can apply a model ID from another provider. The invocation therefore proceeds with an implicit model instead of requiring the explicit pair.
- **PiSharp files:** `src/PiSharp.Cli/CliArguments.cs`, `src/PiSharp.Cli/Program.cs`, `src/PiSharp.Cli/UserSettings.cs`, `src/PiSharp.Cli/ProviderModelRuntime.cs`, CLI argument/help text, and focused CLI tests.
- **Required test:** fail first on a normal agent invocation with `--provider` but no explicit `--model`, including when settings have a default model; assert an actionable error occurs before provider selection/request. Preserve informational/help and model-list behavior where Pi does not enter session selection.
- **Required implementation:** reject the incomplete provider/model pair before applying saved model defaults or resolving a provider model, and document that `--provider` filters an explicit `--model` lookup.

### `a4715ec9bffbfcb8a32a1a4100dcfd06c12c93e4` — `NO_BEHAVIOR_CHANGE`

Adds a pinned official MCP client conformance suite, its baseline runner, and a CI job. It changes test coverage and CI only; it does not change Pi runtime behavior, so there is no coding-agent capability delta to port.

## Processing order

Resolve the seven `NEEDS_WORK` commits in chronological order. Whitespace-aware slash completion, Anthropic OAuth copy-code login, and configured MCP authorization-server metadata are closed at exact heads `884a69b07743e57e135a50ce03e65f11c74cdd39` / CI `36795625551`, `ec7fe828d820ff6967fa569f3b93affe98177a0a` / CI `36798041083`, and `6bddf37a65e64e915969bd2519f08031b7cb3525` / CIs `36835477212` and `36835795082`. Next process ANSI slice-boundary ordering, the optional-field/pagination residuals from `8ce69e9`, the MCP login hyperlink from `41169ba`, then the explicit provider/model requirement from `0c45304`. Capture fail-first evidence, focused behavior tests, full required validation, update this fixture and the parity ledger, then commit/push and verify exact-head CI before moving on. The broader PiSharp parity backlog and extension context-transform work remain open.
