# Terminal Rendering and Interaction Parity — 2026-10-20

## Source pins and scope

PR #20 was independently verified at head
`650865a69c3d96d68faf53d1061e8f412d1fd247` and merged at
`5e1e5aa8ddc66a8e829a880604060b8483107284`. GitHub reports it merged on
2026-10-20 at 11:47:25 UTC. That actual merge is the base and merge-base of
`milestone/terminal-rendering-interaction-parity`.

Pi remains pinned to `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
The complete seventeen-fixture matrix was captured at
`b2bcefeff5d3a1461ef7bd5913b6f4d0f8a4faba`. It calibrated 43/43 cases and
matched normalized physical provider requests in 43/43 cases with no scenario
errors. All 22 named suspension checkpoints match across both modes.
The final input audit added multi-click selection and stacked copy feedback at
`8002968db6b742d6c17b564cb40cabed690b8256`. Both affected mouse scenarios were
verified at that source pin, with stable controls and exact clipboard payloads.

The effective corpus is 18 fixtures / 44 cases: 44/44 calibration, normalized
physical provider request and clipboard checks, with zero scenario errors.
Across 395 action checkpoints, 366 are exact and 29 have the explicit P3 text
differences below. The selected acceptance subset is 217/245 exact; its 28
differences consist of 16 identity states, 11 resume-hint states and one context
estimate. The additional unselected action difference is the same in-flight
estimate in the new multi-click scenario. No unexplained P0/P1 or in-scope P2
checkpoint discrepancy remains in this corpus.

Reviewed [machine-readable evidence](terminal-rendering-interaction-parity-2026-10-22.json)
records each source pin, fixture checksum, dimensions, checkpoint result,
calibration/request/copy comparison, final terminal modes/cursor, state hashes,
render alignment and residual classification. The
[terminal capture](terminal-rendering-interaction-parity-2026-10-22.png) shows
decoded PTY cells for restoration, expanded tools and resumed history; its font
appearance is approximate.

This receipt covers the defined Linux PTY corpus. It does not establish global
Pi parity, other operating systems, every terminal protocol, every highlighting
grammar, or arbitrary LaTeX. PR #20's original evidence remains unchanged in
[its receipt](native-extension-interactive-ui-2026-10-20.md).

## Baseline and upstream investigation

PR #20's original seven-transition extension comparison calibrated 7/7 frames.
The paired run reached all checkpoints with exact HTTP requests and matching
startup, widget and input-update viewports, but reported 22,411 full terminal
state differences, 14,009 render differences, raw output diverging at byte 2,
and a genuine exit/restoration discrepancy. These are serialized difference
counts, not independent defect counts.

The investigation used the pinned source and tests for:

- TUI lifecycle, main/alternate screens, terminal capabilities and layout:
  `packages/tui/src/tui.ts`, `tui-main-screen.ts`, `tui-alt-screen.ts`,
  `terminal.ts`, `layout.ts`, and `layout-node.ts`.
- Input, editor, wheel/scroll, text and image components:
  `packages/tui/src/keys.ts`, `stdin-buffer.ts`, `wheel-scroll.ts`, and
  `packages/tui/src/components/`.
- Markdown, terminal images, colors and math:
  `packages/tui/src/components/markdown.ts`, `terminal-image.ts`,
  `terminal-colors.ts`, and `latex.ts`.
- Interactive composition, themes, tool panels, keybindings, footer estimates,
  suspend handling and chat viewport layout under
  `packages/coding-agent/src/modes/interactive/` and
  `packages/coding-agent/src/core/`.
- The pinned TUI render/shrink/layout/wrapping/editor/wheel/mouse/image/math
  tests and virtual-terminal implementation.

The particularly relevant suspension behavior is visible in the pinned
[interactive mode](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/modes/interactive/interactive-mode.ts),
[chat viewport](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/modes/interactive/chat-viewport.ts),
and [alternate-screen stop](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/tui/src/tui-alt-screen.ts).

## Product corrections

- Normal/fullscreen shutdown reconstructs the logical document and extension
  dashboard, preserves existing normal-buffer output, restores cursor and
  terminal modes, and removes transient overlays and image placements.
- Shared cell/layout handling preserves graphemes, CJK/emoji width, ANSI style
  continuation, clipping, tabs and safe OSC 8 links. Presentation remains
  separate from canonical conversation text.
- Markdown structures, streaming incomplete constructs, tables, list/task
  indentation, supported code highlighting and inline/display math now follow
  the pinned corpus. Dedicated syntax and math components reuse the shared
  width/layout model.
- Expanded tool results retain Pi's outer spacer/panel padding and no longer
  scroll the preceding prompt away. Muted tool text uses its theme foreground
  without an extra ANSI dim attribute. Stored tool views reflow when expanded
  or resized, and tool/image presentation retains source text for copying.
- Imported Pi usage contributes to the footer; in-flight context estimates
  include the current prompt. Startup logo colors and the deliberate bottom
  spacer are retained when restoring the normal screen.
- Page/wheel scrolling, Alt-wheel scaling, source anchoring during resize and
  streaming, search query edits, selected-match reveal and transient scrollbar
  expiry match the pinned behavior. Refining a search chooses the match at or
  after the current transcript anchor and preserves an already visible match.
- Kitty navigation/keypad keys, editor selection/copy, overlay clipping,
  model-list focus/dismissal, image fallback/Kitty sizing and terminal hyperlink
  capability handling have deterministic regressions.
- Native session files opened outside the configured store produce a resume
  command using the actual file's parent. JSONL imports intentionally become
  native sessions in PiSharp's session store; their resume commands identify
  that persisted copy.
- Linux Ctrl+Z restores terminal settings and presentation, sends SIGTSTP to
  the foreground job, ignores SIGINT while suspended, then reinstalls raw
  input and redraws after SIGCONT. Draft text is kept visible during suspend
  and survives resume. Normal mode clears/redraws as pinned Pi does.
- Fullscreen double and triple clicks select words and complete visual lines,
  and drags extend by whole words or lines. The 500 ms click window, slash/hyphen
  source-path tokens, combining characters and wide emoji have native vectors.
  Full-line selection includes its selected leading margin and trims copied
  trailing padding. Locale-specific dictionary word segmentation is not claimed.
- Repeated copy feedback stacks at the right edge and expires independently;
  copy errors retain the pinned five-second duration. One existing screen timer
  schedules expiry; retention is bounded to 64 messages and teardown clears it.

The existing CLI → Runtime → Core direction is preserved. MAF 1.21.0 remains
responsible for agent execution; presentation repairs did not replace it or
introduce a second renderer, input parser, editor buffer or lifecycle owner.

## Reproduction and controls

Use .NET 10, Python 3, the installed pinned Pi checkout at
`/tmp/pi-pinned-source`, and the pinned xterm 5.5.0 emulator. Build PiSharp and
the compiled extension fixture first. Every run records both Git revisions,
dimensions, theme/capabilities, raw bytes, decoded cells/styles/cursor/modes,
resize/input actions, process exits, clipboard payloads and provider requests.
Local deterministic HTTP responses require no live provider credentials.

Use the runner's canonical owned workspace/agent directories under `/tmp`.
A fixture lock and fresh resets keep paths identical across products.
Run-specific fixture roots inside this repository were rejected as evidence:
PiSharp discovered the repository AGENTS instructions while Pi did not.
Long external roots also changed narrow-screen CWD truncation. These exploratory
captures remain under ignored artifacts, with their limitations recorded here.

For each fixture, run `tools/parity/terminal/run.py` first with `--calibrate`,
then without it. Supply `--pi /tmp/pi-pinned-source`, the committed `--fixture`,
and distinct `--output` directories. Extension-backed fixtures also need
`--pisharp-extension` pointing to the built interactive-UI fixture assembly.
The final corpus uses the same assembly argument for every invocation so a
required extension cannot accidentally be omitted.

The comparison still reports every named-state and canonical render-stream
difference. Existing explicit handling is limited to equivalent empty-cell
glyph serialization, hidden cursor coordinates, adjacent duplicate frames,
transient loader glyphs and established in-flight Working frames. Cell width,
colors, attributes, visible cursor and terminal modes remain exact. Raw byte
and HTTP differences remain recorded. HTTP comparison accounts for generated
package-root documentation paths and structurally equal JSON tool arguments;
request identity/count/order remain checked.

The Linux job-control profile uses the existing PTY with an owned child process
group. Its supervisor waits for SIGTSTP, checks original canonical/echo/signal
flags, and resumes only after the explicit fixture action. Cleanup resumes and
kills the child group on failure. A Python regression proves reaping while
suspended; native CLI PTY regressions run in normal and fullscreen modes.

## Acceptance corpus

Fixtures are committed under `tools/parity/terminal/fixtures/`.

| Fixture | Profiles | Principal coverage |
| --- | --- | --- |
| startup-editor | 80×24, 120×40; dark/light; normal/fullscreen | Typing, cursor keys, paste, resize, startup and quit |
| startup-verbose | Same eight profiles | Banner/help, startup layout, restoration |
| transcript-markdown-resume | Same eight profiles; resize to 60×20 | Imported transcript, usage, resize, resume hint |
| terminal-streaming-markdown | 80×24, 120×40 fullscreen | Split inline constructs, graphemes, tables, code/math, commit/exit |
| terminal-code-math | 100×30 fullscreen | C#/JS/Python/JSON/Bash, wrapping, tables and math |
| terminal-tool-lifecycle | 80×24, 120×40 fullscreen | Start, in-flight output, completion, expansion, follow-up |
| terminal-image-fallback | 80×24 fullscreen | Unsupported image presentation and expanded result |
| terminal-long-transcript | 80×24, 120×40, 44×14 fullscreen | Page scroll, source anchors, search refinement/navigation, exit |
| terminal-resize-during-stream | 80×24 fullscreen with resizes | Active stream reflow, cursor and final commit |
| terminal-small-overlay | 44×14 fullscreen | Picker focus, clipping, dismissal and draft restoration |
| terminal-restoration-existing-main-buffer | 120×40 fullscreen | Existing primary buffer plus compiled extension restoration |
| terminal-mouse-selection | 80×24 fullscreen | Source selection, exact OSC 52 copy payload, wheel input |
| native-extension-interactive-ui | 120×40 fullscreen | Original widget/input/teardown and exit comparison |
| terminal-abort-stream | 100×30 fullscreen | Gated abort, delayed-chunk suppression, exit cleanup |
| terminal-image-kitty | 120×60 fullscreen | Kitty image layout/controls and fallback restoration |
| terminal-markdown-structures | 120×60 fullscreen | Nested/task lists, blockquotes, links, HTML fallback, structure stream |
| terminal-suspend-resume | 80×24 normal/fullscreen | Repeated SIGTSTP/SIGCONT, kernel terminal state, draft/history preservation |
| terminal-mouse-multiclick | 80×24 fullscreen | Exact word, line and word-drag selection styles and OSC 52 payloads |

## Difference audit

The complete `b2bcefef` matrix calibrated all 43 cases. Its 387 action checkpoints
contain 359 exact states and 28 differing states; the selected acceptance subset
contains 210 exact states out of 238. Every differing action state belongs to the
explicit product-identity, resume-hint or context-estimate classes below.
All other cells/styles, cursor/modes, copy payloads and exits agree at action
checkpoints. Full normalized state/render comparison matches 2/43; aggregate reports retain 6,909
checkpoint differences and 5,691 render differences, including identity text.
These counts are serialized field differences, not defect counts.

The unchanged original PR #20 extension fixture now has zero checkpoint
differences, exact requests and exit presentation, eight render differences,
and raw streams first diverging at byte 3. The render differences identify
shutdown cursor/control ordering and coalesced intermediate frames; they do not
represent eight incorrect settled screens. The initial receipt reported 22,411
combined differences, including 14,009 render differences, and byte-2 raw divergence.

The final requirement audit also found missing multi-click gestures, replacement
of stacked copy feedback, and omission of the selected visual line's leading
margin. Those product behaviors were corrected. The harness now waits for every
expected OSC 52 payload before a settled clipboard checkpoint; wheel checkpoints
wait for actual scrolled content instead of an arrow already present in usage.
This strengthens synchronization without changing comparison tolerances.

The final mouse reruns supersede the original mouse case and add one case.
The resulting effective totals are 6,911 checkpoint-field differences and 5,701
render differences; full normalized state/render comparison matches 2/44. The other baseline
scenarios do not activate the changed mouse-selection or nonempty copy-feedback
branches. Their captured source pin remains `b2bcefef`; they are not relabelled
as captures of the later source. The final full native suite and exact-head CI
cover the complete later tree.

| Class | Identified source and assessed impact |
| --- | --- |
| P3 product identity | Pi/PiSharp version/banner and self-description text. These are distinct products; the exact text and resulting wrapping are reported, not claimed equivalent. All other startup rows/styles/cursor must agree after the spacer correction. |
| P3 resume hint | Pi continues JSONL files in the fixture directory; PiSharp imports them into its native store and prints its own executable/store path. The hint is intentionally different. At 44 columns its extra wrapped line changes the final viewport by one row; the preceding transcript remains in normal-buffer scrollback. |
| P3 context estimate | Only the in-flight mouse-fixture percentage differs. Captured system prompts are 1,721 versus 1,679 characters because generated documentation paths differ. The shared chars/4 estimate explains 5.3% versus 5.2%; normalized provider requests agree and committed usage agrees. |
| P3/P4 redraw boundaries | Full render reports retain unmatched coalesced/intermediate frames and shutdown cursor/paste/keyboard transition timing. Stable named screens and final restoration are checked separately. Raw streams and full diff reports remain available; complete render-stream equality is not claimed. |

At shutdown the synchronized PiSharp restoration snapshot can still contain a
hidden cursor; its trailing raw control shows the cursor after that boundary.
The settled exit snapshot matches Pi, including visible cursor and reset modes.
This distinction is preserved in the report rather than normalizing cursor
visibility away. Other retained samples identify an extra intermediate editor,
extension, abort, Markdown or suspend frame, and Kitty flag transitions during
resume. Source-pinned stable checkpoints and native kernel tests separately
verify the resulting terminal state.

No visible, layout or interaction difference is accepted merely because its
serialized count is small. Source-pinned stable checkpoint checks include all
cells/styles, cursor/modes, clipboard output and exits. Any additional mismatch
requires investigation before the milestone is closed.

## Reliability and validation

The combined streaming/extension test now overlaps 200 stream updates and 200
widget/status updates with forty resize/theme/scroll/modal/tool-expansion
cycles, including a genuinely expandable 24-row tool result. It asserts the
latest frame and preserved editor draft. Existing regressions cover concurrent
editor operations, widget self-redraw coalescing/removal, package-removal races,
repeated custom-UI completion/cancellation/shutdown, timer disposal, and the
compiled package's physical resize/overlay/reload journey.

### Requirement-to-evidence coverage

The full TRX records the result of each native test below; the differential
reports independently cover physical Pi/PiSharp presentation and requests.

| Required surface | Native regression and physical evidence |
| --- | --- |
| Clean exit and subsequent input | `ExitLeavesNoTerminalRepliesAndRestoresTtyThroughLinuxPty`, including reply/cancellation variants; every corpus exit checkpoint |
| Suspension and resumption | `CtrlZSuspendRestoresTerminalSettingsAndResumePreservesTheDraft` in both modes; supervised repeated-suspend fixture, including history |
| Cell width, styles and links | `TerminalTextLayoutTests`, including CJK/emoji, width-one, regional indicators, combining characters, SGR resets and OSC 8 terminators; code/math and streaming captures |
| Markdown, syntax and math | `TerminalMarkdownTests`; streaming, code/math and Markdown-structure fixtures |
| Scroll, search and source anchoring | `TranscriptSearchControllerTests`, compositor tests and local-provider PTY regressions; all long-transcript action checkpoints at three dimensions |
| Editor, paste, mouse and copy | `EditorBufferTests`, `TerminalInputTests`, screen mouse/grapheme/copy regressions; startup/editor and mouse fixtures with exact OSC 52 payloads |
| Modal focus and resize | `TerminalSelectionListTests`, small-overlay compositor tests; small-overlay and external compiled-package journeys |
| Themes and capabilities | `TerminalThemeTests` cover dark/light, system, user/project precedence, NO_COLOR, ANSI-256 and truecolor; `CliThemeProcessTests` and live screen recoloring tests |
| Images and partial viewports | `TerminalImageRendererTests` cover Kitty/iTerm2, resize, placement reuse, partial-viewport fallback and pruning; Kitty/fallback PTY fixtures |
| Tools and progress | `ToolCallRenderingTests`, `CodemodeTimerTests` and live local-provider PTY tests; tool lifecycle and abort fixtures |
| Concurrent UI and teardown | `ExtensionPresentationUpdatesRemainResponsiveDuringAssistantStreaming`, `ConcurrentExtensionDraftPasteAndKeyboardEditsKeepEditorStateSerializable`, `RepeatedCustomUiCompletionCancellationAndShutdownReleaseEveryComponent`, `RepeatedRedrawRequestsFromRenderAreCoalescedAndStopAfterRemoval`, and package-removal races |
| External package boundary | `CompiledExtensionDashboardWorksThroughTuiAndCleansUpAfterReloadRemoval`; original extension fixture unchanged from PR #20 |
| Architecture | All seven `ArchitectureTests` cases pass; Core remains free of terminal/filesystem/process/network calls |

Transcript retention remains capped at 1,000,000 characters and 10,000 segments;
post-screen captures remain capped at 4,000,000 characters and live tool ownership
at 1,024 entries. The scheduler owns one coalesced pending render and one timer.
The milestone does not remove these bounds. Image-pruning and redraw-removal
regressions check release behavior; this evidence does not claim arbitrary
process-memory bounds for every possible input.

Current recorded gates:

- Formatting passed on the final product source `b2bcefef`.
- Warning-as-error build passed with zero warnings/errors.
- Node terminal emulator: 24 passed, zero failed/skipped.
- Python parity: 66 passed, zero failed/skipped, including the delayed second
  clipboard-write synchronization regression.
- MCP conformance: all 26 scenarios checked; no regressions from 261 passing
  checks and eight explicitly classified baseline failures.
- Focused screen/compositor/keymap checks and both native suspend PTY cases pass.
- Final selection/screen/compositor/flash selection: 126 passed with zero
  failures/skips. New coverage includes independent flash expiry, retention
  bounds, full-row margins, click expiry and line/word drag direction.
- The final full .NET run at `b2bcefef` passed 2,670/2,670 tests with zero
  failures/skips in 8.9144 minutes. Its TRX is retained under
  `artifacts/parity/terminal-rendering-interaction-parity-2026-10-22/validation-b2bcefef/`.
  This earlier source is retained separately from the final input validation.

The final `8002968d` full suite passed 2,689/2,689 with zero failures/skips after the
additional input repairs. Architecture is 7/7; documentation validation passes
150 documents / 582 references. The final TRX is retained under
`artifacts/parity/terminal-rendering-interaction-parity-2026-10-22/validation-8002968d/`.
The superseded `f601ba12` run was explicitly interrupted for the confirmed
copy-feedback correction, not reported as passing. An unconstrained build
invocation ended with internal CLR error `0x80131506`; the established
`TMPDIR=/tmp DOTNET_PROCESSOR_COUNT=2` invocation then built the complete solution
with zero warnings/errors. No timeout increase or test skip was used.

## Timing and publication

Work spans 2026-10-20 through 2026-10-22. Session event timestamps record many
active implementation/investigation turns, but an exact productive-time total
was not continuously measured. Elapsed goal time and tool waits are not
reported as productive engineering hours. No idle padding is counted.
An audit of completed session events through 2026-10-22 09:26:24 UTC finds
23 nonoverlapping active-turn intervals totaling 30.347 hours. That figure
includes reasoning, communication, API overhead and tool waits; it is recorded
as active elapsed time, not an exact productive engineering duration.

Publication and exact-head CI evidence are maintained in
[PR #21](https://github.com/PhilipGB/pisharp/pull/21), titled `Terminal Rendering and Interaction Parity`.
Its description records the final branch-head SHA, push and PR workflow run IDs,
links and conclusions. It targets `main` and must remain draft and unmerged.
The PR description is the publication ledger so no follow-up commit exists
solely to record CI for an earlier head. Do not start PR #22. Global Pi parity
remains `in_progress`.
