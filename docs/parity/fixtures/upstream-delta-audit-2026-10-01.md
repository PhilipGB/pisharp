# Current Pi upstream delta audit — 2026-10-01

The prior recorded Pi pin was `955cc6665ee3986c6a033db52200779310d10dfd`. Fresh fetch confirms Pi `main` at `395315f4841ab5a090a33b8259f60b6df281c1ca`, 41 commits after that pin. Every commit in the complete range is classified below in chronological order. PiSharp source `8931bd73b2e94e7831b1a054a54a8e60bb21aaf5` is the current published head; exact-head Linux CI [36904437406](https://github.com/PhilipGB/pisharp/actions/runs/36904437406) passed restore, format, warnings-as-errors build, and 1,068/1,068 tests with zero skips. The provider-context-transform extension slice is included in this head.

The complete range has **13 `NEEDS_WORK`, 4 `MATCHED`, 15 `OUT_OF_SCOPE`, and 9 `NO_BEHAVIOR_CHANGE`** commits. Six earlier `NEEDS_WORK` changes are resolved at published PiSharp heads (recorded on their entries); seven open items remain. The oldest open delta is per-server MCP OAuth credential isolation (`5806068c`). The official MCP conformance commit is classified as test infrastructure with no runtime behavior change, while its required equivalent PiSharp conformance evidence is tracked separately in the execution ledger. Overall parity remains incomplete.

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
- **Resolution evidence:** fail-first parsing rejected the new option as unknown. The implementation accepts only HTTPS or HTTP on localhost/loopback, fetches the configured metadata document in place of ordinary authorization-server discovery for login and runtime, and leaves RFC 9207 validation in `ModelContextProtocol.Core` intact. The loopback fixture covers matching, missing and wrong callback `iss`; it verifies the configured document is requested, normal discovery is not, and a rejected issuer never reaches the token endpoint. The combined MCP OAuth tests pass 21/21. Exact-head runs [36835477212](https://github.com/PhilipGB/pisharp/actions/runs/36835477212) and [36835795082](https://github.com/PhilipGB/pisharp/actions/runs/36835795082) each pass restore, format, warnings-as-errors build with zero warnings/errors, and 1,058/1,058 tests with zero skips on source `6bddf37a65e64e915969bd2519f08031b7cb3525`.

### `17f3dccbef6c56cbc8cee73dbcfe2bba4f8734f9` — `MATCHED`

- **Pi files:** `packages/tui/src/utils.ts`, `packages/tui/test/regression-slice-by-column-ansi-order.test.ts`.
- **Pi behavior:** when slicing ANSI-styled text by display columns, SGR codes before the slice and codes exactly at the boundary retain source order. This prevents a reset from being emitted before the style it resets and leaking color beyond mouse-selection or search-highlight boundaries.
- **PiSharp behavior:** mouse selection renders the complete ANSI-preserving row through `TerminalTextLayout.HighlightCells`; terminal wrapping carries active SGR sequences in source order. Clipboard selection deliberately uses `SliceCells` to return plain text.
- **PiSharp files:** `src/PiSharp.Cli/Tui/TerminalTextLayout.cs`, `src/PiSharp.Cli/Tui/TranscriptSearchController.cs`, and `tests/PiSharp.Tests/TerminalTextLayoutTests.cs`.
- **Resolution evidence:** both upstream input vectors now run through the wrapped-row and selection-highlight paths. The resulting visible-range prefix keeps the preceding style before the boundary `39` reset, the reset precedes selection highlighting, and later styled text remains intact. The focused regression test passes 1/1; local restore, format verification, warnings-as-errors build (0 warnings/errors), and the full serial suite pass with 1,058/1,058 tests and zero skips. Existing behavior already matches, so no runtime code change was needed. Exact-head Linux CI [36840078307](https://github.com/PhilipGB/pisharp/actions/runs/36840078307) and [36840344261](https://github.com/PhilipGB/pisharp/actions/runs/36840344261) pass on PiSharp source `bc2b1be94254fa08beb483098730e7eb00700b31`, each with 1,058/1,058 tests and zero skips.

### `4bae8677597bc4f96acae5e6d3039de1f61ecb58` — `NO_BEHAVIOR_CHANGE`

Documentation-only changes to the separate `packages/durable` design; no runtime behavior changes.

### `b56702ad345201a1de46a5f8e94542a3a59ad3bd` — `OUT_OF_SCOPE`

Adds extensions and per-conversation agent configuration to the separate experimental `packages/durable` harness. Although examples use coding agents, these APIs define a distinct durable task runtime and storage-backed harness, not the Pi coding-agent package interface being ported. PiSharp's session persistence does not make the separate harness part of this audit's scope.

### `8ce69e9d2b171d173fe4b6b2b6256f1f4411e69d` — `NEEDS_WORK` (resolved at exact head `9f33151c7`)

- **Pi files:** `packages/mcp/src/client.ts`, `packages/mcp/src/oauth/{discovery.ts,flow.ts,types.ts}`, and the matching MCP client/OAuth tests.
- **Pi behavior:** optional empty/null OAuth fields are treated as absent, including empty refresh tokens and client secrets; `expires_in: null` leaves expiry unspecified; empty requested scopes fall through to the next source; invalid URLs in protected-resource metadata fall back to the MCP server origin; and empty/null pagination cursors end pagination.
- **PiSharp behavior / mismatch:** the pinned `ModelContextProtocol.Core` 2.2.0 SDK handles nullable expiry and ignores an empty dynamic-registration client secret, and its scope selection falls through for empty configured/challenge scopes. However, `McpClient.ListToolsAsync` continues while `NextCursor` is non-null, so `""` is treated as another page. OAuth token parsing stores `refresh_token: ""` rather than preserving the previously stored refresh token, and invalid `authorization_servers` metadata is rejected during authorization-server selection instead of falling back to the resource origin. `McpOAuthRefreshHandler.IsValidTokenResponseAsync` also rejects `expires_in: null` for its cross-process refresh coordination path.
- **PiSharp files:** dependency boundary `ModelContextProtocol.Core` 2.2.0 (`src/PiSharp.Runtime/PiSharp.Runtime.csproj`); `src/PiSharp.Runtime/Mcp/McpProtocolCompatibilityHandler.cs`, `McpRuntime.cs`, `McpOAuthLogin.cs`, and `McpOAuthRefreshHandler.cs`; and focused tests under `tests/PiSharp.Tests/`.
- **Implementation:** an outer HTTP compatibility handler removes empty/null pagination cursors from JSON and event-stream responses and repairs invalid protected-resource metadata URLs with the MCP server/resource origin. OAuth token responses treat empty/null optional fields as absent, retain the refresh token used for refresh when the response omits it, and keep nullable or empty expiry responses under the persistence lease. Already-matching SDK behavior for empty scope candidates remains unchanged.
- **Fail-first and resolution evidence:** the loopback pagination fixture reproduced a second request for `nextCursor: ""`; refresh fixtures reproduced loss of `refresh-1` and a released persistence lease for `expires_in: null`; OAuth login reproduced SDK rejection of `authorization_servers: ["not a URL"]`. The corrected empty/null cursor, token-field, and metadata fixtures pass. Local restore, full format verification, warnings-as-errors build, and serial tests pass at 1,064/1,064 with zero skips. Exact-head feature CI [36845344087](https://github.com/PhilipGB/pisharp/actions/runs/36845344087) and main CI [36845868682](https://github.com/PhilipGB/pisharp/actions/runs/36845868682) pass those same required lanes on PiSharp source `9f33151c7f0417c9a4d46b5f9bcae2d6e0e63181`.

### `5b5ccddfac3abba2f3c26b558ec69dcde85000ab` — `NO_BEHAVIOR_CHANGE`

Documentation-only changes in `packages/durable/docs/` clarify lifecycle wording and the task-graph view; no runtime behavior changes.

### `49683a36476e3a3fdbb40008745073375df563b7` — `OUT_OF_SCOPE`

Adds lifecycle conformance and task-graph APIs to the separate `packages/durable` harness. These alter a durable task runtime and storage model, not the Pi coding-agent capability surface being ported.

### `5609b0d6c07cd3bf8014429123086f6da0a5e14e` — `OUT_OF_SCOPE`

Adds an explicitly experimental coding-agent TUI backed by the separate Pi Durable harness. It is an alternative durable runtime rather than a change to the default coding-agent behavior audited by PiSharp.

### `41169ba2af7f5bd5c97237633879117bc3fa9cd9` — `NEEDS_WORK` (resolved at exact head `6d8c594258`)

- **Pi files:** `packages/coding-agent/src/extensions/mcp/index.ts` and its changelog.
- **Pi behavior:** `/mcp login` in TUI mode emits the long authorization URL as an OSC 8 hyperlink and puts the click instruction on a separate short linked line, so terminal wrapping does not break the target.
- **PiSharp behavior / mismatch:** before this slice, interactive `/mcp login` passed the URL through generic console text normalization, which strips terminal control sequences. The standalone `pisharp mcp login` path prints through `McpOAuthLogin` and must remain plain text.
- **PiSharp files:** `src/PiSharp.Runtime/Mcp/McpOAuthLogin.cs`, `src/PiSharp.Runtime/Mcp/McpRuntimeManager.cs`, `src/PiSharp.Cli/Sessions/ProjectRuntimeContext.cs`, `src/PiSharp.Cli/Program.cs`, `src/PiSharp.Cli/Tui/TerminalScreen.cs`, and focused MCP/TUI tests.
- **Required test:** exercise the MCP login URL through a PTY/TUI notification with a long URL; verify the OSC 8 target survives display wrapping and the short click hint points to the same URL, while plain CLI output remains readable.
- **Required implementation:** expose the MCP login URL through the TUI's hyperlink-capable notification path and preserve the existing plain-text CLI behavior.

- **Resolution evidence:** the fail-first Linux PTY test reached `/mcp login` and confirmed the authorization URL had no OSC 8 target. Interactive MCP login now passes the authorization URL to an explicit TUI presenter that validates HTTP(S), inserts trusted OSC 8 links directly into the terminal transcript, and links both the wrapped URL and short platform-specific click hint to the same target. Project reload and session adoption rebind the presenter. Standalone OAuth output remains plain text and is asserted to contain the URL with no OSC 8 sequences. The PTY fixture at 48 columns verifies the wrapped URL reopens its link target on every visible row and the Ctrl+click hint shares that target. Focused MCP tests pass 12/12; format verification and warnings-as-errors build pass with zero diagnostics; the full suite passes 1,065/1,065 with zero skips. A local full run without `TERM=xterm-256color` reproduced the existing theme PTY timeout; the same test also failed on baseline `811487f`, and passed with the established terminal setting. Exact-head Linux CI [36850176163](https://github.com/PhilipGB/pisharp/actions/runs/36850176163) and [36850579208](https://github.com/PhilipGB/pisharp/actions/runs/36850579208) pass restore, format, warnings-as-errors build with zero warnings/errors, and 1,065/1,065 tests with zero skips on PiSharp source `6d8c594258589faca4d8f4fd1e84f0c6cbb3bc4c`.

### `70c036211de5378508ae2456f3c7ae3cf9f8bc75` — `OUT_OF_SCOPE`

Changes the default task-panel visibility in the experimental durable TUI added by `5609b0d`; it does not change the default Pi coding-agent runtime or its audited interfaces.

### `0c453048bd4bb699b90463d9423c382d1787087f` — `NEEDS_WORK` (resolved at exact head `e50e74dbbd`)

- **Pi files:** `packages/coding-agent/src/main.ts`, `packages/coding-agent/src/cli/args.ts`, and `packages/coding-agent/docs/cli.md`.
- **Pi behavior:** `--provider` without `--model` reports an error instead of silently ignoring the provider and running the default model from another provider. Help now describes `--provider` as a filter for `--model` and removes an outdated default-provider claim.
- **PiSharp behavior / mismatch:** `CliArguments.Parse` accepts `--provider` alone. `Program` passes it to `ProviderModelRuntime.ResolveAsync`; when there is no model reference, `ResolveSelectionAsync` selects the provider's first configured model, or settings can apply a model ID from another provider. The invocation therefore proceeds with an implicit model instead of requiring the explicit pair.
- **PiSharp files:** `src/PiSharp.Cli/CliArguments.cs`, `src/PiSharp.Cli/Program.cs`, `src/PiSharp.Cli/UserSettings.cs`, `src/PiSharp.Cli/ProviderModelRuntime.cs`, CLI argument/help text, and focused CLI tests.
- **Required test:** fail first on a normal agent invocation with `--provider` but no explicit `--model`, including when settings have a default model; assert an actionable error occurs before provider selection/request. Preserve informational/help and model-list behavior where Pi does not enter session selection.
- **Required implementation:** reject the incomplete provider/model pair before applying saved model defaults or resolving a provider model, and document that `--provider` filters an explicit `--model` lookup.
- **Resolution evidence:** PiSharp commit `e50e74dbbd` now rejects a normal `--provider` invocation without an explicit `--model` before model resolution while preserving help and model-list paths. Focused CLI regression coverage and exact-head CI at the published range include this behavior.

### `e529a82c98fad679c6e20c03ffbb0bc7896fecb9` — `MATCHED`

- **Pi behavior:** MCP OAuth preserves previously granted scopes when an `insufficient_scope` challenge requests additional scopes; a challenge that names only the missing scope does not discard the earlier grant.
- **PiSharp evidence:** PiSharp pins `ModelContextProtocol.Core` 2.2.0. Its installed SDK contract documents `ClientOAuthProvider.GetCurrentOperationScopes`, `ChallengeIntroducesNewScopes`, the challenge/metadata/configured-scope precedence, accumulated scope tracking, and `TokenContainer.Scope`. PiSharp uses that provider and persists the complete `TokenContainer` in its private token cache, so the challenged-scope union is owned by the already-used SDK rather than app-side refresh code. A focused integrated regression test is still required before MCP OAuth can be considered fully verified; the SDK API evidence is sufficient to classify this delta as already matched.

### `a4715ec9bffbfcb8a32a1a4100dcfd06c12c93e4` — `NO_BEHAVIOR_CHANGE`

Adds a pinned official MCP client conformance suite, its baseline runner, and a CI job. It changes test coverage and CI only; it does not change Pi runtime behavior, so there is no coding-agent capability delta to port.

### `5806068c26e55feefd1f5875bf05c0419ec5d912` — `NEEDS_WORK`

- **Pi files:** `packages/coding-agent/src/extensions/mcp/{oauth.ts,runtime.ts,cli.ts}` and `packages/coding-agent/test/mcp-oauth-store.test.ts`.
- **Pi behavior:** OAuth credentials are isolated by normalized MCP server name and canonical URL, so two configured server names at the same URL can use different accounts. A legacy URL-only entry migrates to the first server that loads it; logout removes only that server's key (or its legacy entry).
- **PiSharp mismatch:** `McpTokenCache` keys credentials, refresh locks and logout by URL alone. `McpRuntime`, explicit login and `/mcp logout` pass the URL but not `McpServerConfiguration.Name`; same-URL aliases therefore share tokens.
- **PiSharp files:** `src/PiSharp.Runtime/Mcp/McpOAuth.cs`, `McpOAuthLogin.cs`, `McpRuntime.cs`, `McpRuntimeManager.cs`, `McpOAuthRefreshHandler.cs`, and `tests/PiSharp.Tests/McpOAuthTests.cs`.
- **Fail-first evidence:** create two named servers with the same URL and prove they can persist, refresh, read and remove distinct tokens; test legacy URL-only migration, normalized-name collisions and concurrent refresh locking under the new composite key.
- **Required implementation:** key storage and refresh coordination by normalized server name plus canonical URL, pass server identity through runtime/login/logout, and migrate legacy URL-only state deterministically without exposing credentials.
- **Validation/dependencies:** full solution validation and exact-head CI; no runtime dependency. This is the oldest open upstream delta.

### `54c19a252997ee6607e2379b4c168a0adc93338e` — `NO_BEHAVIOR_CHANGE`

Reduces retained memory in Pi TUI render caches by flattening strings and weakly holding parsed Markdown tokens. It does not change rendered content or the coding-agent interaction contract.

### `e792ba131ed0495f3ff58a0eb13f20540e344d5c` — `NO_BEHAVIOR_CHANGE`

Changes the interactive user-message component to avoid retaining a duplicate full-width rendered copy of each line. The commit states and implements identical rendered output; this is an internal memory optimization with no PiSharp capability delta.

### `0f8740bb65638180403a225ad7ec4d0cc1f8dedf` — `NO_BEHAVIOR_CHANGE`

Changes the OAuth selector's empty-status label from “unconfigured” to “not configured” and updates its wording assertions. Authentication state, selection, and login behavior are unchanged, so the commit adds no capability delta to the PiSharp audit.

### `c662ec7e374563bd549dc35f47bac52dcc4bed88` — `NEEDS_WORK`

- **Pi files:** `packages/coding-agent/src/core/agent-session.ts` and `packages/coding-agent/test/suite/agent-session-mcp.test.ts`.
- **Pi behavior:** tool names restored from a saved session remain pending while MCP servers register; explicit deactivation clears pending names, and pending names that miss the next prompt do not reactivate unexpectedly. `/reload` carries the prior MCP tool loadout through the registry replacement.
- **PiSharp mismatch:** `ToolRegistry.CreateSnapshot` drops unknown active names and `RefreshForRegistryChange` reconstructs only currently available names, so an MCP tool loaded by `tool_search` can be lost when restoring before server registration or during reload.
- **PiSharp files:** `src/PiSharp.Runtime/Extensions/ToolRegistry.cs`, `PiAgent.cs`, MCP lifecycle/session restoration, and focused tool-loadout/search tests.
- **Fail-first evidence:** delay MCP tool registration across session restore; cover `/reload`, explicit clear, and a pending tool absent by the next prompt boundary.
- **Required implementation:** preserve unresolved selected names with explicit lifecycle semantics until registry registration or the defined clearing boundary.
- **Validation/dependencies:** full solution validation and exact-head CI; independent of other queued deltas.

### `409e808f5834dc4f2f37b54abf24d3a2f6ecad75` — `NEEDS_WORK`

- **Pi files:** `packages/coding-agent/src/modes/interactive/theme/system-theme.ts` and `packages/coding-agent/test/system-theme.test.ts`.
- **Pi behavior:** generated anchored palette colors are capped at the source color's chroma, keeping pastel source palettes pastel as lightness changes.
- **PiSharp mismatch:** `TerminalSystemTheme.Anchored` preserves OKHSL saturation without a source-chroma ceiling; lightness shifts can increase actual chroma and oversaturate a pastel palette.
- **PiSharp files:** `src/PiSharp.Cli/Tui/TerminalSystemTheme.cs`, `TerminalColorSpace.cs`, and a focused `TerminalThemeTests` fixture.
- **Fail-first evidence:** port the Catppuccin Frappe pastel-pink sample and compare generated OKLCH chroma at the relevant palette steps.
- **Required implementation:** clamp generated chroma to the source chroma with the same palette falloff while retaining current contrast/lightness rules.
- **Validation/dependencies:** focused color fixtures, full solution validation and exact-head CI.

### `f29ea3deb298280b417892c6229ce478ad8c4d2f` — `NEEDS_WORK`

- **Pi files:** `packages/coding-agent/src/core/settings-manager.ts`, `src/index.ts`, `src/modes/interactive/{components/settings-selector.ts,interactive-mode.ts}`, settings docs and interactive-mode tests.
- **Pi behavior:** `quietStartup` accepts `true`, `false` or `"header"`. Header mode keeps the startup header/version/key hints while suppressing the model-scope line and resource notices; `true` hides the full startup presentation.
- **PiSharp mismatch:** `UserSettings.QuietStartup` is boolean-only and `Program` either hides or shows a combined banner/metadata string.
- **PiSharp files:** `src/PiSharp.Cli/UserSettings.cs`, `UserSettingsWriter.cs`, `Tui/TerminalSettingsPicker.cs`, `Program.cs`, and `tests/PiSharp.Tests/QuietStartupTests.cs` plus a CLI/PTY fixture.
- **Fail-first evidence:** parse/write and render all three values; assert exact header, model metadata and resource-notice visibility at startup.
- **Required implementation:** model the three-value setting through precedence, persistence and settings UI, and render the header separately from the gated metadata/notices.
- **Validation/dependencies:** full solution validation and exact-head CI.

### `7fd478a2e888ebc28869566f33a186303d372838` — `OUT_OF_SCOPE`

Removes the experimental agent harness from the separately distributed `pi-agent-core` package. Pi Packages and their experimental harness surfaces are excluded by this parity objective; the change adds no coding-agent capability requirement for PiSharp.

### `48dd1e2f0f9dc7a767d7e5ee693bc85a4d6db38c` — `OUT_OF_SCOPE`

Ports an experimental client/server and durable harness into the separate `pi-durable` package. This is an experimental package/runtime, not the in-scope coding-agent interface; Pi Packages remain excluded.

### `233f174401c1fbf112046b0020bcfb7ea5cd8467` — `OUT_OF_SCOPE`

Changes the color treatment of the Pi logo on the browser OAuth callback page. It changes branding presentation only, adds no authentication behavior, and is specific to Pi's brand assets rather than a reusable coding-agent capability.

### `ed8b3bcc194c8263ec8bec3f337053ae73866da1` — `NEEDS_WORK`

- **Pi files:** `packages/coding-agent/src/core/radius.ts`, OAuth and Radius login selectors, `interactive-mode.ts`, and interactive-mode tests.
- **Pi behavior:** `/login` offers Radius sign-in, then offers to configure the Radius MCP server in global `mcp.json` with provider-backed authentication; accept persists and reloads configuration, cancel returns to the parent flow, and subscription/account status is presented distinctly.
- **PiSharp mismatch:** Radius OAuth provider support and MCP provider-auth configuration exist, but `/login` does not connect them into the same onboarding/configuration flow.
- **PiSharp files:** `src/PiSharp.Cli/ProviderOAuth.cs`, login command/selector services, `src/PiSharp.Runtime/Mcp/McpConfigurationEditor.cs`, manager/reload flow, and focused auth/PTY process tests.
- **Fail-first evidence:** run `/login` through a PTY with Radius available; assert sign-in, configure, cancel, persisted provider-auth configuration and live server reload paths.
- **Required implementation:** compose the existing OAuth and MCP services into the interactive onboarding flow without embedding behavior in `Program.cs` or exposing credentials.
- **Validation/dependencies:** full solution validation and exact-head CI. Radius capabilities exist in PiSharp; only the connected `/login` path is queued.

### `aab34df655e3c5974fc40618f75e9eafdb031293` — `NEEDS_WORK`

- **Pi files:** `packages/coding-agent/src/core/model-registry.ts`, `src/extensions/codemode/{execute.ts,tool.ts}`, model/Codemode docs and Codemode session tests.
- **Pi behavior:** Codemode exposes `models.generateImages(model, context)`, resolves identity against the configured image-model catalog (never trusting script-supplied credentials/base URL), returns text/image output plus usage and stop/error status, and records nested call/cost evidence.
- **PiSharp mismatch:** `ICodemodeModels` and `ProviderCodemodeModels` expose catalog and classifier operations but no image-generation method or guest global; image catalog metadata exists separately.
- **PiSharp files:** `src/PiSharp.Runtime/Codemode/ICodemodeModels.cs`, model globals/call accounting, `src/PiSharp.Cli/ProviderCodemodeModels.cs`, process worker/output projection and focused Codemode image tests.
- **Fail-first evidence:** guest-process fixture covers valid image generation, identity-only model resolution, image/text blocks, usage, error/aborted results and image emission.
- **Required implementation:** add configured-provider image generation through the existing model-owned boundary and Codemode nested-call accounting, with no credential flow from script data.
- **Validation/dependencies:** full solution validation and exact-head CI; the diagnostic overlap with the following Codemode prompt/error commit should be covered without merging unrelated behavior.

### `ca9c925117b8d172d42e516a4175be4db0e51d79` — `MATCHED`

Pi uses a text wordmark in Apple Terminal where its graphical logo is unsuitable. PiSharp's interactive header already uses a plain text `PiSharp` wordmark across terminals and does not emit the half-block logo, so the behavior is present without platform-specific branching. Evidence: `src/PiSharp.Cli/Program.cs` startup/header composition and `TerminalScreen` PTY fixtures.

### `c2f65d8f13f6205c2efa963f7689ebb08a87fd33` — `NO_BEHAVIOR_CHANGE`

Documentation-only provider-page and documentation-navigation edits; they add no runtime capability or observable coding-agent behavior.

### `88ff80b986e34d4fbd1fa94a4df65c60ae964516` — `MATCHED`

For this commit's default-mode change, PiSharp already starts its interactive TUI in the alternate screen and its normal interactive view, matching Pi's new default fullscreen presentation. Evidence: `src/PiSharp.Cli/Tui/TerminalScreen.cs` enters `?1049h`; `TerminalScreenTests` and Linux PTY tests verify alternate-screen lifecycle. PiSharp's separate `tuiMode` setting and normal-mode option remain open ledger work; this classification only covers the upstream commit's new default.

### `6f1072cc081f06b86a673bd142f03720d17afe15` — `NEEDS_WORK`

- **Pi files:** `packages/codemode/src/{declarations.ts,index.ts,runtime/prelude-source.ts}`, Codemode sandbox tests, coding-agent Codemode prompt/tool files and `docs/codemode.md`.
- **Pi behavior:** reduces Codemode prompt size, documents globals one line each, reports close matches/available names for unknown tool or model members, and gives actionable model-catalog, image-output and store-capacity guidance from runtime errors.
- **PiSharp mismatch:** the built-in Codemode description is large and inline; the guest bridge does not provide equivalent namespace/member diagnostics and errors for unknown globals, bad model selections and store overflow are generic.
- **PiSharp files:** `src/PiSharp.Runtime/Codemode/CodemodeBuiltin.cs`, `CodemodeModelGlobals.cs`, `src/PiSharp.Runtime/Codemode` worker bridge and sandbox, plus dedicated prompt/runtime tests.
- **Fail-first evidence:** assert bounded prompt text and guest errors for close matches, available members, wrong catalog type, output guidance and per-value/total store limits.
- **Required implementation:** update model-facing declarations and worker-side error projection to produce equivalent useful guidance while preserving the existing isolated sandbox and output limits.
- **Validation/dependencies:** implement after `models.generateImages` so image catalog errors include that operation; full solution validation and exact-head CI.

### `395315f4841ab5a090a33b8259f60b6df281c1ca` — `OUT_OF_SCOPE`

Adds an experimental vacation-planner demo under `packages/coding-agent/src/experimental/vacation/`, implemented on the separate Pi Durable harness. It is a demo and alternate durable runtime rather than a coding-agent capability surface; Pi Packages and experimental durable harness work are excluded by the objective.

## Processing order

Resolve the currently open deltas in chronological order: MCP OAuth credentials keyed by server name and URL (`5806068c`), restored MCP tool loadouts (`c662ec7`), pastel system-theme chroma (`409e808`), header-only quiet startup (`f29ea3d`), Radius `/login` onboarding (`ed8b3bc`), Codemode image generation (`aab34df`), and Codemode prompt/error guidance (`6f1072c`). Before treating MCP as complete, run and baseline the official client conformance scenarios listed in the execution ledger. For each behavior change, add fail-first evidence, implement only that mismatch, run full validation, update parity records, commit/push, verify exact-head CI, refresh Pi, and continue.
