# Native extension interactive UI receipt — 2026-10-20

## Scope and source pin

This receipt records the incremental native C# extension UI work on
`milestone/native-extension-interactive-ui`, based on PR #19's verified merge
`b4cfac1ca83b38215311d8441069286cca0dc4e6`. The pinned upstream Pi revision is
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` and was not advanced.

The upstream contract is `packages/coding-agent/src/core/extensions/types.ts`
at lines 149–299. Interactive behavior and lifecycle are implemented in
`packages/coding-agent/src/modes/interactive/interactive-mode.ts` at lines
2300–2690, with raw input dispatch in
`packages/tui/src/tui.ts` at lines 943–1090. The UI reset path is
`interactive-mode.ts:2452–2483`. The source checkout used for comparison is
`/tmp/pi-pinned-source` at the pin above.

PiSharp keeps terminal ownership in the existing CLI screen, compositor,
editor and terminal input parser. The new public contracts are in
`src/PiSharp.Extensions.Abstractions/PiSharpExtensionUiContracts.cs` and
`PiSharpExtensionUi.cs`. `builder.Ui` is a retained package-owned capability;
callback contexts receive a revocable UI wrapper through
`src/PiSharp.Runtime/Extensions/NativeExtensionUiCallbackScope.cs`. The CLI
host and package scope are
`src/PiSharp.Cli/Startup/NativeExtensionInteractiveUiHost.cs` and
`NativeExtensionUiPackageScope.cs`. Package removal clears registrations,
cancels package-owned modal operations, disposes components and invalidates
retained handles.

## Pinned operation matrix

"Evidence" refers to the focused CLI tests in
`tests/PiSharp.Tests/Cli/Tui/NativeExtensionUiTests.cs`, the terminal screen
and input tests, and the real process fixture in
`NativeExtensionInteractiveUiPtyTests.cs`. Repository validation and the
source-pinned full-screen comparison are summarized after the operation matrix.

| Operation | Pinned behavior and source | PiSharp implementation | Evidence and host modes | Differences and acceptance |
| --- | --- | --- | --- | --- |
| `select`, `confirm`, `input` | `types.ts:149–160`; interactive dialog routes in `interactive-mode.ts` after line 2687; RPC routes in `rpc-mode.ts:103–122` support cancellation and millisecond timeouts. | Existing dialog methods remain on `IPiSharpExtensionUi`; additive `*WithOptionsAsync` methods add timeout dismissal. TUI uses current terminal dialogs; RPC correlates `extension_ui_request` with `extension_ui_response` by request ID. | CLI dialog tests, `RpcModeTests.RpcExtensionUiRoundTripsDialogsAndEmitsPersistentPresentationEvents`, `RpcModeTests.RpcExtensionUiCorrelatesConcurrentRequestsAndHonorsTimeoutAndCancellation`, `RpcExtensionUiTransportTests.DisposeWaitsForInFlightPersistentEventWritesAndRejectsLaterEvents`, and a production CLI RPC tool turn in `RpcExtensionContextProcessTests`; unavailable operations complete immediately in JSON, print and direct Runtime contexts. | Existing methods and signatures remain. RPC clients own the visible dialog and return responses on stdin. |
| `notify` | `types.ts:159–160`; interactive notifications use Pi's UI notification path; RPC emits an `extension_ui_request` with `method: "notify"`. | TUI notifications use the interactive handler; RPC emits the matching event and buffers configuration-time notifications until the protocol loop starts; print, JSON and direct Runtime use the existing diagnostic writer. | RPC unit and production CLI process fixtures assert the notification request; TUI shortcut and fixture notification paths are exercised. | RPC events are fire-and-forget, as in pinned Pi. |
| `onTerminalInput` | `types.ts:162–163`; `interactive-mode.ts:2592–2612`; `tui.ts:943–1090`. Pi filters terminal color responses, runs raw string listeners in subscription order, then passes rewritten bytes to its key parser before focused-component and shortcut routing. | Public parsed event/result contracts; `NativeExtensionTerminalInputRouter`; `TerminalEditor.InterceptExtensionInput`; existing `TerminalInput` remains the parser and filters protocol control events. Package removal and explicit `IDisposable` unsubscribe remove listeners. Rewrites are limited to 4096 UTF-8 bytes, parsed as input events, and delivered before focused components and editor routing. | `ParsedInputListenersRewriteInRegistrationOrderAndRespectReservedKeys`, `RawInputReplacementParsesKeySequencesForRemainingListeners`, `RewrittenReservedKeyBypassesLaterExtensionListeners`, `PackageInputListenersRunBeforeFocusedCustomComponents`, `RawInputListenerReceivesOriginalEscapeAndMouseSequences`, `RawInputListenerInterceptsTerminalStreamBeforeTheEditor`, `RawInputCannotConsumeTheConfiguredExitKeyOnAnEmptyEditor`, parser protocol tests, and the PTY journey's consumed input plus rewritten Kitty keys. | Pi invokes listeners before parsing. PiSharp invokes them after parsing and protocol filtering, so callbacks cannot replace a partial sequence or inspect parser state. Parsed replacement events pass through later listeners in registration order; reserved abort/clear keys bypass later extension listeners and reach the host. The configured exit key is also host-owned while the editor draft is empty. |
| `setStatus` | `types.ts:165–166`; `interactive-mode.ts:2300–2303`; Pi renders the extension status in its footer provider; `rpc-mode.ts` emits persistent state requests. | Package-keyed status dictionary in `NativeExtensionInteractiveUiHost`; stable package order and ordinal key order feed `TerminalScreen.SetExtensionPresentation`; RPC emits status set/clear events. Component render snapshots group by stable status key and select the latest active owner value. | Shared-key and owner-removal assertions in `PackageOwnedKeysAndSingletonSlotsRestoreTheNextExtensionOnRemoval`; `DynamicComponentStatusSnapshotUsesStableStatusKeysAndLatestOwnerValue`; TUI PTY verifies `native-ui:ready`; RPC round-trip verifies set and update events. | Pi keys statuses globally. PiSharp keys them by package plus name, so equal names do not remove another extension's value. The footer retains active owner entries; component snapshots expose one stable key with the latest active owner's value. |
| `setWidget` | `types.ts:187–193`; `interactive-mode.ts:2395–2450`; Pi supports static lines and component factories above/below the editor; `rpc-mode.ts` supports static-line arrays. | Static lines or `IPiSharpExtensionUiComponent` entries enter `TerminalScreenCompositor`; RPC emits static set/clear events and disposes unsupported component registrations. | `DynamicWidgetRendersThroughTheScreenAndReplacementDisposesItsComponent`, `ReinstallingTheSameWidgetKeepsItsComponentAliveUntilRemoval`, shared-key/below-editor assertions, screen compositor tests, PTY widget redraw/update/removal and RPC persistent-event round-trip. | C# components render bounded plain lines rather than Pi TUI component trees. RPC supports only static lines. PiSharp's owner-qualified keys allow simultaneous same-name widgets from different packages. |
| `setHeader`, `setFooter` | `types.ts:195–208`; `interactive-mode.ts:2521–2590`; Pi factories receive TUI/theme and footer data. | Owner-ordered component slots render in the existing screen composition; removing the winner exposes the next contribution or built-in screen content. | `PackageOwnedKeysAndSingletonSlotsRestoreTheNextExtensionOnRemoval` and compiled-package PTY header/footer frames. | The public C# component receives detached size/theme/status data, not Pi's TUI or `ReadonlyFooterDataProvider`. |
| `setTitle` | `types.ts:210–211`; `interactive-mode.ts:2653` writes through Pi's terminal title owner; `rpc-mode.ts` emits `setTitle`. | Title is sanitized and bounded in `NativeExtensionInteractiveUiHost.SetTitle`; package removal clears the active override; TUI `TerminalScreen` clears it at shutdown; RPC emits the title request. | `PackagePresentationControlsUseTheLiveEditorAndRestoreWorkingUiOnRemoval` verifies OSC title sanitization; PTY runs with a title override; RPC unit coverage checks title events. | Pi recomputes its application title when extension UI resets. PiSharp clears the extension title to an empty terminal title because it does not otherwise own a session title. |
| `setWorkingMessage`, `setWorkingVisible`, `setWorkingIndicator` | `types.ts:168–182`; `interactive-mode.ts:2346–2379`; `WorkingStatusIndicator` is in `components/status-indicator.ts`. Null restores the default indicator, empty frames hide it, and active streaming uses the working row. | Source-owned presentation contributions feed the screen's working row. A bounded timer advances validated frames and is disposed when no visible working indicator needs it. | `PackagePresentationControlsUseTheLiveEditorAndRestoreWorkingUiOnRemoval` verifies message, visibility, static empty-frame behavior, interval validation and restoration. The PTY fixture registers overrides and executes a normal model/tool turn. | Pi uses its loader timing and accepts its own `WorkingIndicatorOptions`; PiSharp constrains frame count, frame text and intervals for bounded host refresh. Full animated streaming-frame differential remains pending. |
| `setHiddenThinkingLabel` | `types.ts:184–185`; `interactive-mode.ts:2382–2393` updates existing and streaming assistant components without changing hidden-thinking policy. | Owner-ordered label override is applied by `TerminalScreen` and `TerminalConversationPresentation`; removal restores the built-in label. | `TerminalScreenTests.HiddenThinkingLabelCanBeOverriddenAndRestoredWithoutChangingVisibility`; fixture registers a label. | Presentation only. Provider reasoning and visibility policy are unchanged. |
| `custom` | `types.ts:213–228`; interactive custom factory and overlay handling in `interactive-mode.ts` and existing TUI overlay host. Pi supports async factories and overlay handles. | `ShowCustomAsync<T>` hosts synchronous C# factories as focused or overlay components. `NativeExtensionCustomComponentRequest` provides one-shot completion, cancellation and disposal; render failures cancel the request; disposal waits for in-flight callbacks. | Focused submit, one-shot completion, Escape, app interrupt, package removal, screen-shutdown and package-removal factory races, render failure, completion during resize, mouse, undersized-terminal fallback and completion-inside-input disposal are covered by `NativeExtensionUiTests`. The real PTY opens a panel, accepts text, updates the draft, changes theme and resumes input. | Factories and render/input callbacks are synchronous C# contracts. No Pi TUI or mutable overlay handle is exposed. The 4×2 terminal fallback is tested; other size transitions and simultaneous-session stress remain pending. |
| `pasteToEditor`, `setEditorText`, `getEditorText` | `types.ts:230–237`; `interactive-mode.ts:2655–2657`; Pi paste wraps text in bracketed-paste processing; RPC falls back to `set_editor_text` and returns an empty synchronous editor read. | TUI uses the existing `TerminalEditor` draft and paste path; RPC emits `set_editor_text` for set and paste. | `PackagePresentationControlsUseTheLiveEditorAndRestoreWorkingUiOnRemoval` verifies TUI read/set/paste; PTY verifies setting the accepted draft; RPC round-trip verifies editor update events and empty editor read. | C# exposes the existing editor's current draft in TUI; it does not provide Pi's optional expanded-text editor projection. RPC cannot synchronously read client state. |
| `editor` | `types.ts:239–240`; `interactive-mode.ts:2658` uses Pi's multiline editor component; `rpc-mode.ts` emits a correlated `editor` request. | `EditAsync` reuses the current TUI editor inside the custom component host, supports prefill, submit, Escape and resize, then restores the saved draft. RPC emits `editor` and awaits a correlated response. | `MultilineEditUsesTheHostEditorAndRestoresTheOriginalDraftOnSaveOrEscape`; PTY verifies prefill, save and draft restoration; RPC unit and production process tests verify editor round-trip; headless tests verify immediate no-UI return. | RPC clients own editing and return the completed text. |
| `addAutocompleteProvider` | `types.ts:242–243`; `interactive-mode.ts:2659–2662` stacks extension wrappers over the built-in provider. | Owner-ordered provider registrations in the host merge with current editor completion; requests carry cancellation; package removal cancels/removes providers; bad providers are isolated. | `AutocompleteProvidersMergeWithNativeCompletionAndIgnoreDuplicatesAndFailures` and PTY Tab completion. | The C# provider returns typed display/replacement rows, not Pi component factories. |
| `setEditorComponent`, `getEditorComponent` | `types.ts:245–281`; `interactive-mode.ts:2663–2664`; Pi installs and returns an editor factory, with `CustomEditor` delegation for app keys. | Owner-ordered `IPiSharpExtensionEditorComponent` renders an editor-adjacent contribution or, with `ReplacesDefaultEditor`, replaces the visible editor row while retaining the host draft and submit path. Getter returns the current component. | `CustomEditorComponentOwnsInputAndRestoresPreviousRegistrationOnRemoval` checks input ownership, precedence, getter and fallback; `ExtensionEditorCanReplaceAndRestoreTheBuiltInEditorWithoutLosingItsDraft` covers visual replacement and removal. | The extension component does not replace the host's canonical `EditorBuffer`, cursor model, or all completion UI. This remains a known difference from Pi's editor factory. |
| `theme`, `getAllThemes`, `getTheme`, `setTheme` | `types.ts:283–293`; `interactive-mode.ts:2665–2681`; `ThemeController.setThemeName` changes the active theme and `SettingsManager.setTheme` persists the global setting. | `TerminalPresentation.Themes` remains authoritative. The extension receives detached records; named theme selection validates, saves to the active user's settings and updates current presentation. The selection survives package removal. | The compiled fixture enumerates and reads `light`, selects it repeatedly during the modal journey; its PTY verifies the saved user setting. Headless tests cover unavailable results. | C# selects a theme by name only, not by detached theme object. The PTY verifies selection and saved setting but does not compare every color cell against Pi. |
| `getToolsExpanded`, `setToolsExpanded` | `types.ts:295–299`; `interactive-mode.ts:2682–2683` and `4573–4589` update expandable transcript components and current header. | The host reads/writes the actual `TerminalEditor` expansion state and invalidates the live screen. | Compiled fixture reads state, expands tool output and records the resulting state; its PTY journey also performs a real tool call. | Expansion is session presentation state and is not reverted solely because its setter package unloads, matching Pi's UI reset behavior. |

## Lifecycle, security and host modes

The builder capability is created per native package and retained across
callback completion. `NativeExtensionUiCallbackScope` wraps callback operations
with callback cancellation/revocation; persistent registrations go through the
package scope instead. Configure failure rolls back the extension
registration checkpoint and disposes the just-created package scope. Package
removal/reload removes owner-qualified contributions and invalidates
subscriptions. Components serialize render/input/disposal; if a component
completes from inside an input callback, disposal waits until that callback
returns. A custom UI factory racing terminal-screen shutdown is cancelled before
it can be installed without an input surface; package removal during factory
execution disposes the newly created component and propagates cancellation
before registration. Render failures cancel and dispose the custom request after
the active render returns. UI strings are sanitized before terminal rendering,
and custom components return plain line/style data rather than terminal escape
sequences. Project extensions remain behind the existing trust decision.

Only interactive TUI mode owns a terminal screen. RPC preserves
`HasUserInterface = false` because that flag denotes an interactive terminal
surface, while `context.Ui` independently supports the remote dialog protocol and
`select`, `confirm`, `input`, `editor`, `notify`, `setStatus`, static-line
`setWidget`, `setTitle`, and `set_editor_text`. Request IDs correlate dialog
responses; cancellation and timeouts settle pending requests. RPC does not host
terminal components, custom widgets, raw input, autocomplete, editor
components, theme changes, or working/header/footer presentation overrides.
Print, JSON and direct Runtime calls return no-UI dialog/custom results without
waiting for terminal input. Noninteractive notifications use the diagnostic
writer and visual registrations have no rendered surface.

## Test evidence and remaining work

The external PTY fixture builds a separate compiled extension, packs and
installs its `.nupkg` in a temporary user extension directory, and runs the
actual CLI against a loopback OpenAI-compatible provider. It exercises the
dashboard widget/status, header/footer, an ordinary shortcut, raw-key consume,
a same-burst pass-through/consume pair, raw-key rewrite, autocomplete, a
focused panel, draft update, theme lookup/selection, tool expansion, a normal
tool turn, package reload/removal, and recovery of the built-in editor. The
same-burst case confirms text batching cannot skip listeners for later decoded
characters. The CLI is driven through Linux `/usr/bin/script`; the test
has a 90-second overall bound, a 10-second stage diagnostic, and process-tree
cleanup. No provider credentials or fabricated screen frames are used.

The compiled-package PTY fixture sends `Ctrl+Shift+Z` as its input-loop
readiness handshake before the raw key; the pinned Pi fixture registers the
same non-conflicting shortcut. The final UI/input/PTY/RPC/compositor selection
passes 260/260, and the compiled-package PTY journey passes 10/10 repeated runs
after the fixture alignment. The input-router correction separates atomic
listener deactivation from waiting for an active callback, avoiding a
router-lock wait while a callback exits. Its package-removal and
self-unsubscribe regression, autocomplete cancellation regression,
screen-binding teardown regression, tiny-terminal overlay fallback, and
concurrent stream/UI update regression all pass. Additional races verify that
custom factories cannot install after screen shutdown or package removal, and
disposing an older binding for the same screen preserves the current editor
hooks. The same-screen binding and package-removal factory regressions each
pass 20 consecutive runs (40 test executions).

The final full .NET suite passed 2,566/2,566 with zero failures and skips under
`TMPDIR=/tmp`. The final warnings-as-errors solution build passed with zero
warnings and errors, as did format verification and architecture 7/7. Python
terminal parity passed 51/51, Node terminal state tests passed 24/24, and the
MCP conformance run had no new regressions. Its classified failures remain
elicitation defaults (6), auth/basic-cimd (1), and auth/scope-retry-limit (1).
The loopback header-cancellation test uses a 3-second setup deadline so it can
reach its caller-cancellation assertion reliably; its isolated test passes.
Documentation path validation passed 149 files and 576 references; exact-head
CI results will be recorded in the draft PR description after publication.

## Source-pinned full-screen differential

The fixture `tools/parity/terminal/fixtures/native-extension-interactive-ui.json`
was run at 120x40 with the dark theme against Pi
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. PiSharp used the reviewed base
`b4cfac1ca83b38215311d8441069286cca0dc4e6`. The same-Pi calibration matched all
7 compared render frames with zero differences. The paired run completed every
scripted checkpoint with no scenario errors and exact HTTP request matches. At
`native-ui-startup`, `native-ui-widget`, and `native-ui-input-update`, trimmed
viewport text, every nonblank cell's full style state, active screen and cursor
state match. Empty terminal cells differ in their serialized representation.

The complete paired terminal comparison does not match: the report records
22,411 state differences and 14,009 render differences over 7 transitions;
raw output first differs at byte 2, and quit/restore state differs. At that
checkpoint Pi leaves its extension dashboard visible after terminal
restoration, while PiSharp clears the extension presentation. The fixture
therefore verifies the extension-owned stable viewports and the real native
package loader, but it does not establish full terminal parity. The ignored
same-Pi calibration and paired captures are under
`artifacts/parity/native-extension-interactive-ui-2026-10-20-final-aligned-calibration/`
and
`artifacts/parity/native-extension-interactive-ui-2026-10-20-final-aligned-comparison/`.

The 260-case focused selection, ten final compiled-package PTY runs, 2,566-case
full suite, warnings-as-errors build, format verification, architecture suite,
Python and Node tests, MCP conformance, and same-Pi calibration all passed.
Documentation path validation passed 149 files and 576 references. Exact-head
CI results will be recorded in the draft PR description after publication. PR #20 must remain unmerged, and global
extension parity stays `in_progress`.
