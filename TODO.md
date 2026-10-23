# PiSharp capability parity

## Active milestone — Complete Built-in Coding Tool Parity — 2026-10-22

PR #21 merged at `ebfeb24c9dd5e03592c3b388236f22a3a3a3299a` after its
reviewed head and both exact-head workflows were verified. That actual merge
is present on `origin/main` and is the base of
`milestone/builtin-coding-tool-parity`. Pi remains pinned at
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.

Stable mission checkpoints: **9 complete / 11 remaining** (A–T). A is the
verified merge prerequisite; B is the fresh branch. C is the completed initial inventory of all seven
tool definitions and implementations before product changes. D establishes the independent oracle.
E completes common declarations/validation, truncation and read/Bash result metadata.
F verifies the supported read contract (119/121, two explicit safety exclusions).
G verifies write corrections with 20 strict passes and nineteen retained safety exclusions.
H verifies edit behavior with 31 strict passes and thirty-eight explicit atomicity exclusions.
I verifies supported Bash streams, metadata, process cleanup and current session environment.
J is in progress; J–T cover searches, integration, safety, validation,
documentation, draft PR #22 and final-head CI. An inventory is not a parity claim.

Bash now has 44 independent cases, with 2/44 strict matches before the update,
decoding, stdin and spawn corrections. The related selection passes 157 tests,
including deterministic 100 ms coalescing and detached-grandchild cleanup.
Clean `b46aff69` now passes 44/44 with a 44/44 same-Pi control.
The expanded environment/shutdown/extension selection passes 196 checks.
Fresh final Bash evidence and shared integration acceptance remain pending.

See the [tool milestone receipt](docs/parity/fixtures/builtin-coding-tool-parity-2026-10-22.md)
and [initial acceptance inventory](docs/parity/fixtures/builtin-coding-tool-inventory-2026-10-22.json).
Keep the new PR draft and unmerged. Do not start PR #23. Global parity remains
`in_progress`.

## Previous milestone — Terminal Rendering and Interaction Parity — 2026-10-22

PR #20 is verified merged at `5e1e5aa8ddc66a8e829a880604060b8483107284`.
The implementation branch is `milestone/terminal-rendering-interaction-parity`,
based on that merge; Pi remains pinned at
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Product source is `216251fd`.
Nineteen deterministic real-PTY fixtures now cover rendering, lifecycle,
Markdown/code/math, tools, images, scrolling/search, resize, overlays,
keyboard/paste, mouse selection/copy, native extension UI, and Linux job-control
suspension in normal and fullscreen modes. Corrections include terminal
restoration, styled wrapping, tool spacing, search anchoring, startup spacing,
and draft-preserving Ctrl+Z/SIGCONT handling. The new suspend scenario matches
all twenty-two named checkpoints across both modes.

The reviewed corpus has 45/45 calibration, physical provider-request and
clipboard matches, with zero scenario errors. The 28 differing selected states
are explicitly classified identity, resume-hint and installation-path estimates.
Build, formatting, Node 24/24, Python 66/66 and MCP baseline checks pass. The final
.NET suite passes 2,711/2,711 with zero failures/skips, including multi-click/copy-feedback,
color and nested math regressions. Documentation paths pass 150 documents / 582 references.
Publication and exact-head push/PR CI evidence are recorded in
[PR #21](https://github.com/PhilipGB/pisharp/pull/21).
PR #21 was kept draft/unmerged through review and is now merged as recorded
above. Broad Pi parity remains
`in_progress`. See the
[terminal parity receipt](docs/parity/fixtures/terminal-rendering-interaction-parity-2026-10-20.md).

The independent-review correction propagates nested LaTeX failures to source
fallback and shares a 64-level recursion budget across groups, arguments and
environment rows. The new unsupported-math PTY fixture and refreshed code/math
and structural Markdown cases match all 21 action checkpoints.

## Previous milestone — Complete Native Extension Interactive UI — 2026-10-20

PR #19 is verified merged at `b4cfac1ca83b38215311d8441069286cca0dc4e6`;
the reviewed head `c26ef860978da0117b49f491de36d05dbe9b41b6` passed push CI
`39346174499` and PR CI `39346177476`. PR #20 was on
`milestone/native-extension-interactive-ui`, based on that merge, and is now
merged at `5e1e5aa8ddc66a8e829a880604060b8483107284`. The pinned Pi
revision is unchanged at `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.

The implementation adds package-owned interactive UI registrations and
terminal hosting for widgets, statuses, components, editor input, autocomplete,
themes, working presentation, and parsed terminal input. The final full .NET
suite passes 2,566/2,566, the focused UI/input/PTY/RPC/compositor selection
passes 260/260, and the shortcut-aligned compiled-package PTY journey passes
10/10. New regressions cover concurrent streaming/UI updates and the tiny
overlay fallback; listener teardown passed 20/20 repetitions. The source-pinned
differential reaches every checkpoint and matches stable startup, widget, and
input-update viewports, while full terminal parity remains incomplete: 22,411
state differences, 14,009 render differences, raw output diverging at byte 2,
and a quit/restore presentation difference. Architecture 7/7, Node 24/24,
Python 51/51, warnings-as-errors build, format verification, and MCP
conformance pass; MCP retains only its classified baseline failures.
Documentation path validation passed 149 files/576 references before PR #20's
merge. Its exact-head CI and merge are complete. Global extension parity stays
`in_progress`.

### Progress log (UTC)

- 05:03 — `TMPDIR=/tmp dotnet test PiSharp.slnx --no-build --no-restore`
  passed 2,553/2,553 with zero skips. The default home TMPDIR had caused two
  resource-display expectation failures; the isolated temp root matches the
  tests' intended absolute-path environment. A pending listener lock-order fix
  was found in final review and is being validated separately before the last
  full run.
- 05:22 — The input-router deactivation/wait split now passes its deterministic
  concurrent package-removal regression 1/1. The pinned terminal comparison
  reaches each scripted checkpoint without scenario errors; its named startup,
  widget, and input-update viewports match, but full terminal state/render and
  raw output comparisons do not. Final build, formatting, focused/full tests,
  and documentation checks will be rerun on the reviewed source.
- 05:48 — Aligned the pinned Pi and compiled C# fixture on `Ctrl+Shift+Z` after
  finding that the earlier shortcut conflicted with a built-in search key.
  Same-Pi calibration passed all seven frames; the paired report now records
  22,411 state and 14,009 render differences, exact HTTP matches, and the
  remaining quit/restore screen difference.
- 06:18 — Added and passed deterministic tests for concurrent assistant stream
  and extension presentation updates plus the 4×2 overlay fallback. The
  expanded 164-test focused selection and final format check pass; the
  full-suite rerun has not yet returned its summary.
- 06:22 — The final full .NET suite passed 2,557/2,557 with no skips or
  failures. The compiled-package PTY journey passed ten times on the aligned
  shortcut fixture; final docs-path validation and publication remain.
- 06:23 — Documentation path validation passed 149 files and 576 references
  after the receipt and execution ledger were updated; exact-head CI and PR
  publication remain outstanding.
- 06:54 — The RPC transport now registers event writes under a shutdown gate,
  tracks them with a completion placeholder, avoids invoking output under the
  gate, and makes concurrent disposal callers await the same flush. Its
  blocking-writer regression passes. The focused selection passes 165/165.
  A later full run exposed the existing Mistral cancellation fixture's 30 ms
  header deadline expiring before the loopback server received the request;
  the timeout was raised to 3 s without changing the caller-cancellation
  assertion. The isolated test passes and the final full suite passes
  2,558/2,558. Format and warnings-as-errors build pass on final source.
- 07:24 — The final architecture selection passes 7/7. Documentation path
  validation passes 149 documents / 576 references after the publication-note
  edits; the JSON ledger parses and `git diff --check` is clean. Reviewed the
  public contracts, package revocation, component lifecycle, input ordering and
  RPC shutdown path, and drafted the PR description with the exact-head CI
  section reserved for the final workflow IDs.
- 08:23 — The latest full .NET suite passed 2,566/2,566 with zero skips. This
  includes the factory/screen-shutdown, factory/package-removal, and current
  editor-binding lifecycle regressions. The focused UI/input/PTY/RPC/compositor
  selection is now 260/260. Python terminal tests pass 51/51; MCP conformance
  has no new regressions against the classified baseline. Refreshed docs paths
  validate 149 files / 576 references, the ledger parses, and `git diff --check`
  is clean.
- 08:43 — Final review found that the editor could batch decoded characters
  after the first input event, bypassing extension listeners for later
  characters. The editor now processes individual events whenever the extension
  input hook is bound. The compiled-package PTY fixture proves an adjacent
  pass-through/consume pair; its focused run passes after keeping the new y
  consumer scoped to that pair. Final full-suite verification is running.
- 08:53 — The final full .NET suite passed 2,566/2,566 with zero failures or
  skips after the buffered-character routing fix. The compiled-package PTY
  journey passed 10/10 repetitions with the new adjacent-character case.
  Format verification and the warnings-as-errors build also pass on that source.
- 08:55 — Node terminal tests passed 24/24 and Python terminal tests passed
  51/51 on the final input-routing source. MCP conformance has no new baseline
  regressions; the exact CI MCP build passes with warnings as errors. Refreshed
  documentation and ledger checks are being repeated before staging.

## Previous milestone — Native Extension Two-Stage Context Mutation — 2026-10-19

PR #19 merged at `b4cfac1ca83b38215311d8441069286cca0dc4e6`; its implementation
and review correction are recorded in the [context mutation receipt](docs/parity/fixtures/native-extension-context-mutation-2026-10-19.md).

## Previous milestone — Native Extension Message Lifecycle Hooks — 2026-10-19

PR #17 is merged at `0737ee30524ebc86b845f2b1b5c19b9d481db212`. The reviewed
head was `9182227d321d1e1d7871a6273f73534549835569`; push CI `39279759725`
and PR CI `39279767767` passed on that exact revision. No additional comments,
reviews or commits appeared before the expected-head merge. See the
[message lifecycle receipt](docs/parity/fixtures/native-extension-message-lifecycle-2026-10-19.md).

## Previous milestone — Native Extension Provider Wire Hooks — 2026-10-19

PR #16 merged provider wire hooks at the pinned physical payload, header,
response, and raw-event boundaries; see the
[provider-wire receipt](docs/parity/fixtures/native-extension-provider-wire-hooks-2026-10-19.md).

## Previous milestone — Native Extension Input & User-Bash Interception — 2026-10-18

PR #15 was merged from `milestone/native-extension-input-bash` as
`2ea360f991b0e15c59203557a68a5a1e3c026450`. Its completed input and user-bash
correction is recorded in the [milestone receipt](docs/parity/fixtures/native-extension-input-bash-2026-10-18.md).

## Previous milestone — Native Extension Agent Settlement & Mutable Turn Boundaries — 2026-10-18

Branch `milestone/native-extension-agent-settlement` starts from PR #13's actual
merge on `main`, `ca47d554bd48887147100ab11195c8086361963d`. PR #13 was merged
only after its expected head and both exact-head CI runs passed verification.
Pi remains pinned at `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.

The additive SDK exposes four canonical boundary draft kinds, detached previews,
ordered mutable turn-end and pre-settlement handlers, and a typed settled
observer. `ConversationRun` retains prompt, retry, compaction and settlement
ownership; MAF tool continuation reaches an awaited boundary before the next
provider call. Existing turn observers keep their delegate and run before the
mutable chain. Draft preview and commit share one canonical mapper; restoration
and save failures roll back provisional entries and suppress continuation.

Focused Runtime and external `.nupkg`/RPC acceptance cover continuation,
provider-visible context, retries, tools, abort, save failure, compaction,
package replacement/removal, collection of old load contexts, and fresh-process
resume. Final integrated verification and publication are recorded in the
receipt. Broad extension parity remains `in_progress`; provider wire/input
hooks, advanced UI, TypeScript compatibility and the global audit stay deferred.

See the [milestone receipt](docs/parity/fixtures/native-extension-agent-settlement-2026-10-18.md).


## Previous milestone — Native Extension Session Transitions & Compaction Hooks — 2026-10-18

Work is on `milestone/native-extension-session-hooks`, based on the PR #12
merge `eef6f3f83247758d397b4e313d2c26b0088ee9ac`, with Pi pinned at
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Add ordered typed hooks for
session replacement, fork/clone, tree navigation and canonical compaction while
preserving existing transition, persistence and compaction ownership. The
current audit covers clone's pinned `At` position, transactional tree restore,
post-commit observer cancellation, and custom summaries across supported
manual, automatic and virtual-model paths. Keep broad extension parity
`in_progress`; see [the continuation](docs/continuation.md), [API guide](docs/extensions.md),
and [milestone receipt](docs/parity/fixtures/native-extension-session-hooks-2026-10-18.md).

## Previous milestone — Native Extension Events & Callback Context — 2026-10-18

Final source `8f31c7ec1ef35882a8ea98a8988c18289dbaf206`, based on PR #11 merge
`1a0165c3858d973948c9ac8eb698e75ed0fcc535`, added typed session, compaction,
agent, turn, tool, model and thinking observers with detached callback snapshots
and safe host-owned capabilities. Exact-head push and PR CI both passed,
including 2,306 .NET tests. Broad extension parity remains `in_progress`; see
[its receipt](docs/parity/fixtures/native-extension-events-context-2026-10-17.md).

## Previous milestone — Native Extension Provider, MCP & Virtual Model Registration — 2026-10-17

Work is on `milestone/native-extension-provider-mcp-virtual-models`, based on
`632bd08b87afb10ac6669b4072d08a8e3b452687`, the PR #10 merge. Keep the
configured Pi reference at `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. This
bounded increment lets ordinary compiled extensions register MEAI providers and
models, MCP server configurations, and virtual models through PiSharp's normal
provider, MCP, selection, and routing owners. External `.nupkg` process coverage
exercises normal model discovery/selection, provider tool execution, persisted
virtual routing, package replacement/removal, and failed-Configure rollback.

See [the continuation](docs/continuation.md), [extension API guide](docs/extensions.md),
and [milestone receipt](docs/parity/fixtures/native-extension-provider-mcp-virtual-models-2026-10-17.md).
The broad extension capability remains `in_progress`; provider OAuth, the full
event surface, advanced renderers, and the global parity audit remain deferred.

## Previous milestone — Native Extension State & Interactive Controls — 2026-10-17

Work is on `milestone/native-extension-state-interactive-controls`, based on
the verified PR #8 merge commit `d9156e53c4822e7b98226df6170b29c61bc87eb1`.
The configured Pi reference remains `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
The implementation adds canonical session-tree state entries, branch-aware
read access, start/shutdown callbacks, the four safe UI operations, and
extension-owned TUI shortcuts. External-package process and PTY acceptance
covers persistence, provider-context exclusion, resume, package replacement,
removal, and editor recovery. Product source commit is `2c24b4fd`; the local
suite passes 2,237 .NET tests with zero skips, architecture 7/7, Node 24/24,
Python 50/50, docs 138/532, and MCP conformance has no new regressions. This
milestone does not promote broad extension parity; provider/model/settings
registration and advanced UI remain deferred. Draft PR #9 is open; exact-head
push CI run 39039746664 and PR merge-result run 39039758081 both pass.

See [the previous milestone receipt](docs/parity/fixtures/native-extension-state-interactive-controls-2026-10-17.md)
for its source, acceptance, verification, and CI results. Its stopping point is
superseded by the active increment above; broad parity scope remains unchanged.

## Active resumed milestone — Resources / Packages / Extensions — 2026-10-16

Work resumed on `milestone/resources-packages-extensions`, based on merged
`main` `0c94c69d2f416ba0e625e353142b1d695565b75e`. Managed package CLI workflows
are already verified in the ledger and package process receipt; do not duplicate
the stale worker branch. Extension conflict/replaceable-builtin and typed
context/UI receipts are already present. PR #6 is merged with final product
source `84d94364` (initial implementation `9d5b0a22`). It adds ordered
`before_agent_start` handlers for submitted user prompts and reports
`context.IsIdle == false` during the hook, returning to idle after the run
settles. Five focused extension/recovery checks pass; exact-head CI passes
2,159 .NET tests and MCP conformance. Idle custom `triggerTurn` messages remain
unsupported, and the broad .NET extension API capability remains open.

See [the continuation](docs/continuation.md) and the
[before-agent-start receipt](docs/parity/fixtures/extension-before-agent-start-current-pi-2026-10-16.json).

## Current integration checkpoint — 2026-10-16

Current PR product source is `29d4a993` on `parity/current-pi-completion`, based
on PR head `4883258303c2f631bcfa9c1607940ec060716282`; Pi remains pinned at
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Current execution prioritizes
Codex subscription and llama.cpp usability, then core agent/session behavior
and the interactive application. Codex grammar/custom tools have local
WebSocket/SSE fallback, replay and provider-switch fixtures; non-streaming
summary calls now aggregate the Responses stream, and empty SSE frames are
skipped iteratively. Session discovery skips malformed candidates whose head
entry is missing. The project session picker toggles recent/name sorting with
Ctrl+S and preserves the current selection, including while a search is active.
Linux terminal mode disables XON/XOFF while active and restores it on exit.
The llama UI has local tool-use and stream-cancellation evidence. A dynamic
router model-list regression now covers `--provider llama.cpp --list-models`
without prior model selection, and a live router rerun returned 19 selectable
models; the full local inference and interactive acceptance remains open.
Ctrl+R now renames the session selected in the picker while retaining search,
selection and the saved title. Broad secondary-provider parity is deferred for
this goal; queue order remains inventory.

Format verification and a warnings-as-errors build pass; the focused discovery
and Ctrl+R tests pass 6/6. The full local suite had 2,086 passes and one
ephemeral-port bind failure (2,087 total, zero skipped); the affected
classifier test passed 2/2 when rerun alone. Exact-head Linux CI at PR
checkpoint `cc4557a5` passed both test and MCP conformance jobs; CI passed
2,087/2,087 .NET tests with zero skips ([run 38826952362](https://github.com/PhilipGB/pisharp/actions/runs/38826952362)).
Counts are **12 verified, 148 in progress, 34 not started** across the 194
required capabilities. Full parity and the final source audit remain incomplete.

See the [thinking budget](docs/parity/fixtures/thinking-budgets-current-pi-2026-10-15.md),
[Google/Vertex](docs/parity/fixtures/google-genai-vertex-current-pi-2026-10-15.json),
[queues 10–11 provider](docs/parity/fixtures/provider-queues-10-11-current-pi-2026-10-15.md),
[Git](docs/parity/fixtures/managed-git-rpc-process.json), and
[npm](docs/parity/fixtures/managed-npm-local-tarball-2026-10-15.json) receipts.

## Historical Sol-to-Luna checkpoint — 2026-10-15 (superseded)

The configured/audited Pi cutoff is `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
All 194 required capability IDs remain: **10 verified, 146 in progress, 38 not
started**. Instruction/trust behavior, global defaultProjectTrust and live
skill-command settings are now verified against their defined observable
contracts. The two encountered P1 repairs are strict completed tool arguments
and Gemini 4 thinking-level requests. Full Pi parity remains incomplete.
The 45-task ordered Luna queue, architecture decisions, exact verification scope
and deferred P2/P3/P4 findings supersede the older next-action prose below.
See [the handover](docs/continuation.md) and the execution ledger for the current plan.


## Historical post-merge checkpoint — 2026-10-15 (superseded)

PiSharp merged `main` is `22eeb2a53dbaa94e41f78a40a997e3b3156bb3ed`; exact-head Linux CI [38427397999](https://github.com/PhilipGB/pisharp/actions/runs/38427397999) passes. Work continues on `parity/current-pi-completion`. The major architecture refactor and composition review are complete; only the bounded final architecture regression review remains. Production dependencies remain CLI → Runtime → Core, with MAF 1.21.0 and Microsoft.Extensions.AI 10.10.0.

Fresh local baseline: restore/format/warnings-as-errors build pass; .NET 1,390/1,390 with zero skips, Node 24/24, Python 47/47, documentation 131 documents / 457 references, MCP with no regressions against eight classified failures. All 17 existing process differential drivers ran; the classifier probe required valid boolean criteria and updated item extraction, then its billing/authority comparisons passed. This probe retains Pi output items and does not verify the open Codemode formatting contract. All 33 terminal fixtures ran at one explicitly selected case each: initial calibration 30/33 and pairs 24/33; seven repeats/corrections retain all failures. The harness teardown and Markdown input defects are fixed; Python terminal tests now pass 48/48. Product presentation and one thinking-toggle HTTP mismatch remain open.

The baseline comparisons used `ee409a85f6ad92bf8c6ab8ed735c6cd24f2e749e`. The deliberately advanced configured Pi reference is `3daa20280f6a7c7cbe0935680c9a38fdc3863005`: all 50 new commits are classified (19 out of scope, 13 no behavior change, zero needing work, 18 matched contracts). The audited delta is reconciled; resume Resources / Instructions / Trust and every remaining ledger requirement. The latest fully verified presentation checkpoint passes 1,934 tests with zero skips and a zero-warning build; eighteen named current-source contracts now match. Resources work has resumed: fifteen instruction-file loading/order/warning comparisons match; twenty-seven trusted system/append/CLI-input comparisons and four real CLI/provider selection cases now pass. Fourteen skill/template text/recovery comparisons and four CLI/provider expansion cases pass. Nineteen explicit-path comparisons and two named-pipe CLI/provider controls now pass. Ninety-eight frontmatter/typed-metadata/warning comparisons and two invalid-YAML CLI/provider recovery cases pass. Full discovery, remaining YAML edge/error cases and terminal presentation remain open; both retained CI timing failures now have focused repairs: five terminal fail-first regressions, two RPC fail-first barrier regressions, six matching fullscreen checkpoints, and ten repetitions of each original process test. Both focused repair commits pass exact-head Linux CI (38532839354 and 38533106677); five immediate persisted RPC turns also match Pi. Seventeen directory discovery comparisons match, including 1,002 skills and root declaration stopping; sixty ignore-file/order/pattern comparisons now match too. Discovery source 0eb7b114 passes exact-head CI 38535070869. Thirty-two full-loader canonical/validation/collision/source/configuration/ancestor/extension comparisons now match, with 17 discovery/60 ignore regressions retained. Ignore source 9bf391b900 passes exact-head CI 38538502785. Selection source 0672b794 passes exact-head CI 38546090541. Forty-seven skill/template invocation and error-recovery comparisons plus a real RPC/provider deletion replay now match; 49 focused native tests cover recovery, all three input commands, JSON stderr and cancellation. Invocation source 96fa481f33 passes exact-head CI 38554053384. Typed prompt hints and the repository dup prompt now match 131 loader comparisons and 15 fullscreen render frames with provider traces; 88 focused checks cover metadata, trust/discovery, reload and suggestion columns/newlines. Hint source 42ae67c471 passes exact-head CI 38556219134. Explicit YAML scalar tags now match 181 paired cases plus a real RPC/provider input; 60 focused checks pass. Scalar source 6cec6f7104 passes exact-head CI 38557804529. The expanded YAML document/key/alias/error corpus now matches all 311 cases and a real malformed-flow RPC/provider recovery; 96 focused checks pass. Newly found port retry and inherited dumb-terminal fixture failures are separately repaired at 79b2b5e4f7 with deterministic reproduction, isolated 1850-test verification and exact CI 38564664115. Invocation-error terminal presentation now matches six fullscreen frames in two pairs and same-Pi calibration, with six native rows and 50 Python checks; source-only calibration stalls and active-resize calibration remain unresolved. See [skill-error evidence](docs/parity/fixtures/skill-invocation-error-terminal-current-pi-2026-10-14.json). Wider resources/trust/reload remain open. Remaining settings/package/ignore/YAML/error edges, remaining tag/key/error metadata and terminal/trust/reload presentation stay open. All 194 capabilities remain required: 7 verified, 149 in progress and 38 not started; schema publication now has bounded artifact evidence with runtime alignment open. Local package skill/prompt loading now matches 83 full-loader cases, including source whitespace, file URLs/error messages, separator identity and canonical/configured controls, plus URL/padded/original real RPC/provider replays; the expanded native suite passes 1,962 with zero skips. Nested extension declarations and hidden/ignore/node_modules visibility now match eleven normal CLI flow regressions and the 95 resource loader cases. Extension directory index precedence now matches one configuration rather than duplicate sibling loading, with seven normal CLI flow regressions retained. Local package extensions now match six normal CLI/bootstrap/provider comparisons across manifest/convention/file/root/disabled/no-extensions cases; package discovery/alias/error/reload/project overrides and managed/CLI operations remain open. Local package themes now match twelve availability/palette comparisons plus the 83 skill/prompt regressions and actual CLI selection; the initial fullscreen pair exposes a non-Kitty keyboard fallback/render difference. The separate keyboard/startup repair now matches two four-frame package-theme pairs and the original 15-frame Kitty hint regression; relocated-workspace context-footer differences remain retained. Retained process timing failures have separate diagnostics/scheduling mitigation; the exact quiet-startup failure remains unexplained. Complete package extension/theme metadata/errors, managed npm/git and package CLI operations remain open. Full parity and the fresh final source audit remain incomplete. Keep `test.pdf` and unrelated user files untouched.

Whitespace source `bbb0a21606` failed exact CI 38618979075 in the startup-flags process fixture (1,910 passed / one failed / zero skipped). A stalled model-catalogue body deterministically reproduces that fixture timeout. Explicit offline scope and isolated configuration preserve all original persistence/conflict assertions and the 12-second deadline; eight concurrent recovery runs pass with zero catalogue requests, and full local verification passes 1,911 with zero skips. See [startup network isolation](docs/parity/fixtures/startup-flags-network-isolation-2026-10-14.json). Exact-head timing repair CI 38622219705 passes both jobs at 4b3837b19a (1,911 .NET tests, zero skips). The exact historical CI schedule and earlier quiet-startup/source-only calibration failures remain unproven; passing resource runs do not resolve them.

Package theme head `b1271ad986` failed exact CI 38625569590 in the unchanged six-image Codemode loop (1,926 passed / one failed / zero skipped). A controlled cleanup-pool barrier exposes native deferred termination while Pi requests termination before returning. The separate repair requests termination immediately, retains prompt settlement and release only after actual exit, and preserves the two-worker limit. Eighty-eight focused checks, all six lifecycle/admission comparisons and ten original image-test repetitions (60 images) pass. See [Codemode shutdown evidence](docs/parity/fixtures/codemode-shutdown-order-2026-10-14.json). Exact-head shutdown repair CI 38629279578 passes both jobs at 76c7a01e68 (1,927 .NET tests, zero skips). The exact historical CI schedule remains unproven; unrelated green resource runs are not used as resolution evidence.

Extension failure source audit separates SDK recovery from CLI startup: the SDK discards partial registration and keeps valid siblings, while the normal CLI refuses startup with error diagnostics, exit 1 and a -ne hint. Configuration, missing explicit paths and a native dependency resolver each fail first; the candidate now matches thirteen CLI flows and a direct SDK comparison, with 76 focused checks and 95 resource regressions. See [extension error evidence](docs/parity/fixtures/resource-package-extension-errors-current-pi-2026-10-15.json). Full local verification passes 1,954 tests with zero skips, a fresh zero-warning build, format and docs validation; wider conflicts/builtins/aliases/reload and managed package/CLI workflows remain open.

See [the current refresh](docs/parity/fixtures/current-pi-refresh-2026-10-13/README.md) for source findings, plans and evidence scope.

Explicit extension file URLs and canonical aliases now match sixteen normal CLI flows, including padded-path rejection and complete diagnostics. Canonical deduplication retains the first lexical path, with package provenance for exact package-file overlap and CLI provenance for a distinct alias; three actual full-loader metadata pairs and 58 focused native checks support the distinction. The final suite passes 1,957 tests with zero failures/skips, a fresh zero-warning build, format, documentation, Node 24 and Python 50 checks. See [extension path evidence](docs/parity/fixtures/resource-package-extension-paths-current-pi-2026-10-15.json). Configured paths, broader aliases/collisions/reload/project overrides and managed package workflows remain open; no capability is promoted.

Final extension selection after the pre-trust pass now honors trusted-project package disabling and refreshes enabled preloads to project metadata while keeping untrusted-project controls and configured instances. Two native failures before repair and one control are retained; three actual full-loader selection/metadata cases and eighteen normal CLI/provider flows now match. Focused 26 and full 1,960 native checks pass with zero failures/skips, a fresh zero-warning build and format verification. See [pre-trust selection evidence](docs/parity/fixtures/resource-package-extension-pretrust-current-pi-2026-10-15.json). Final module ordering/conflicts, wider filters/aliases/reload and managed package workflows remain open; all 194 capabilities remain required.

Final extension ordering now puts a new project module before retained personal modules while configuring each once. A paired full-loader comparison and the real Pi ExtensionRunner expose both module and resource-discovery callback order before repair; one native failure and an untrusted control are retained. The repaired pair, prior three selection cases and eighteen normal CLI/provider regressions match. Focused 28 and full 1,962 native checks pass with zero failures/skips, a fresh zero-warning build and formatting checks. See [extension ordering evidence](docs/parity/fixtures/resource-package-extension-order-current-pi-2026-10-15.json). Conflicts, replaceable builtins, dynamic registrations, wider filters/aliases/reload and managed package workflows remain open; whole capability statuses are unchanged.

## Historical checkpoint — 2026-10-12

Pi `origin/main` is `ee409a85f6ad92bf8c6ab8ed735c6cd24f2e749e`; PiSharp product commit is `387ae5b300f187eec28c5b50b51ab77489732f31`. Exact-head Linux CI [38228305151](https://github.com/PhilipGB/pisharp/actions/runs/38228305151) passes both jobs: terminal 24/24, Python 38/38, format, warnings-as-errors build, .NET 1,356/1,356 with zero skips, and official MCP conformance with no baseline regressions. The fresh Pi OpenRouter catalog suites pass 56/56. Python test/parity runners cap CPU affinity and common native worker pools at two; the child-process inheritance test passes. The MCP reach, aliases and `CallToolResult` slice is implemented and pushed. Current Pi's trusted-project extension UI replay at `ee409a85` / `387ae5b3` matches all 14 normalized render frames, terminal state, HTTP traces, and provider semantics. The startup spacer correction places the logo, help, onboarding and trust notice on Pi's rows; the full bare `/trust` terminal still differs by 48,590 state/render fields, including 33,458 render fields. Resources / Instructions / Trust remains the active family; remaining selector, reload and behavioral differences stay open. The new key-specific OpenRouter model catalog behavior is audited as a separate open provider gap. The llama.cpp, matcher, help, keybinding, and skill flows remain tracked with their exact Pi pins; provider sign-in URL copy/cancel controls are implemented, with paired auth-screen and callback cases still open.

The llama.cpp family was refreshed at Pi `42705224` / PiSharp `8a1df81`. The two later Pi commits through `c661bc57` do not change llama source or tests. Current Pi llama-related tests pass 16/16; PiSharp router/classifier tests pass 36/36 and Hugging Face client tests 3/3; all three process differentials match. Twelve PTY flows have matching HTTP traces and zero scenario errors; ten match all paired terminal renders, while two retain one Pi-only intermediate model-list frame during the progress-overlay handoff. Same-Pi calibration passes for those two current-head flows; the paired captures directly establish exact terminal matches in the other ten. The two visible handoff frames remain recorded as a presentation difference. See [the current llama evidence](docs/parity/fixtures/llama-terminal-current-pi-2026-10-12.md).

Resources / Instructions / Trust remains open. Pi's resource matcher uses minimatch on relative paths, basenames and absolute paths. PiSharp's matcher covers minimatch comments/negation, POSIX classes, class negation, signed/zero brace steps, hidden paths, braces, extglobs and escapes; direct matcher/catalog tests pass 75/75. At Pi 42705224 / PiSharp ff180dac, compact startup Context/Skills/Prompts rows and action hints match; the older full-screen pair differs in 64,818 state/render fields and 40,086 rendered-cell fields over seven frames. Current Pi/PiSharp pairs match resource precedence, extension resource discovery/reload, legacy-skill settings reload, read/Bash/indirect skill-prompt flows, and the trusted-project extension UI. The refreshed trust-extension replay at Pi ee409a85 / PiSharp 387ae5b3 has zero normalized terminal, render, provider, or HTTP differences across 14 frames; same-Pi calibration also passes. Raw PTY bytes retain documented terminal-protocol differences. The `Ctrl+T` thinking toggle works for retained and streamed reasoning and persists across restart. See [resource precedence and reload evidence](docs/parity/fixtures/resources-terminal-current-pi-2026-10-12/current-42705224-3043c4b185/README.md), [skill-prompt evidence](docs/parity/fixtures/skill-prompt-current-pi-2026-10-12/current-42705224-0461f41de6/README.md), [thinking-toggle evidence](docs/parity/fixtures/thinking-toggle-current-pi-2026-10-12/current-42705224-65d045c5/README.md), [minimatch evidence](docs/parity/fixtures/resource-path-minimatch-current-pi-2026-10-12.md), [trust-selector evidence](docs/parity/fixtures/trust-selector-current-pi-2026-10-12.md), and [current trust-extension evidence](docs/parity/fixtures/project-trust-extension-ui-current-pi-2026-10-12/current-ee409a85-387ae5b3/README.md).

The incremental source audit covers 237 commits after baseline `955cc6665ee3986c6a033db52200779310d10dfd` through Pi `ee409a85`: 88 `OUT_OF_SCOPE`, 44 `NO_BEHAVIOR_CHANGE`, 95 `NEEDS_WORK`, and 10 `MATCHED`. The durable-only tool-search expansion remains outside the normal CLI; Pi's MCP reach/result-schema refactor is matched at PiSharp `e92d592b`. The latest OpenRouter commit adds key-specific accessible chat models and limits/pricing; PiSharp currently uses generic `/models` discovery and does not fetch `/models/user`, so this remains an open provider delta after the active Resources / Instructions / Trust family. Next, resolve the remaining resource, startup, trust-selector, precedence and reload differences, then investigate delayed terminal-reply buffering, compaction-status coloring, herdr Kitty image detection, Codemode schema constraints, and compaction-summary thinking behavior. All 194 required capabilities remain in scope; a fresh final source audit and architecture review remain outstanding. Keep the unrelated user-owned `test.pdf` untouched.

## Historical progress and evidence

Fresh fetch on 2026-10-02 confirms Pi `main` at `c10bfb0d79dbbc998539a0a3e6c6a736a4e6db06`, seventeen commits after the prior audit head `395315f4841ab5a090a33b8259f60b6df281c1ca` and 58 after `955cc6665ee3986c6a033db52200779310d10dfd`. PiSharp fetched main is `c0b012ba7192afed65138dae7d10b5d15da77b67`. Every intervening commit is classified below. Historical validation records retain their original pins; they do not prove current full parity.

Current classifications: 14 `OUT_OF_SCOPE`, 12 `NO_BEHAVIOR_CHANGE`, 26 `NEEDS_WORK`, 6 `MATCHED`. User-reachable visual effects, OAuth wording/callback presentation and package-resource CLI flows are in scope. Earlier exclusions for those surfaces are corrected. Fresh Anthropic inline tool definitions, grammar/custom-call replay, auth URL copying, current 3D interactions and Together metadata join the existing open queue.

MCP credential isolation is implemented: four fail-first regressions are fixed, six current-Pi credential sequences match, 34 focused tests pass, and format/zero-warning build/full 1076 tests pass with zero skips. Exact-head Linux CI [36986049950](https://github.com/PhilipGB/pisharp/actions/runs/36986049950) passes on `930bc2d020`; deferred MCP tool restoration now matches eight current-Pi scenarios, including ordered provider declarations and persisted loadouts. Format verification, zero-warning build and all 1087 tests pass with zero skips. Exact-head Linux CI [36989476624](https://github.com/PhilipGB/pisharp/actions/runs/36989476624) passes on `2521200471`. System-theme pastel chroma and 256-color gray ties now match 1008 current-Pi ANSI-prefix comparisons; the color slice passes format verification, zero-warning build and all 1091 tests with zero skips. Exact-head Linux CI [36991960991](https://github.com/PhilipGB/pisharp/actions/runs/36991960991) passes on `099c63cea6`. The fresh upstream capacity-retry slice matches six current-Pi lifecycle scenarios and passes 17 focused checks; format verification, zero-warning build and all 1098 tests pass with zero skips. Exact-head Linux CI 36994405262 passes on source 095c893a15f5fcdbea356d6c1cfbc57c8c4c5329, including all 1098 tests with zero skips. Quiet-startup header policy now matches 28 current-Pi settings/gating scenarios and 24 focused tests; Format verification passes; warnings-as-errors build has zero warnings/errors; the full serial suite passes 1114/1114 with zero skips in 4m29s. Exact-head Linux CI 36997235947 passes on source 4595bc9f4f2db98e45adf98b8ece2e00bbcfc0d1, including all 1114 tests with zero skips. Startup resource notices and exact presentation remain open.

Radius interactive onboarding is implemented as a bounded slice: 59 focused checks pass, ten current-Pi MCP configuration outcomes match, and 26 exact shimmer ANSI frames match. Loopback OAuth/PTY coverage proves menu/direct cancellation and that only accepted MCP setup reloads the runtime. Full dialog/cursor/style parity, catalog synchronization and URL copying remain open; Format verification and the zero-warning/error build pass; the full serial suite passes 1132/1132 with zero skips. Exact-head Linux CI [37019190204](https://github.com/PhilipGB/pisharp/actions/runs/37019190204) passes on `c0b012ba7192afed65138dae7d10b5d15da77b67`, including all 1132 tests and zero skips. MCP CIMD and Cloudflare gateway deltas remain recorded; the active Anthropic slice is followed by the full terminal harness and llama.cpp family. See [Radius evidence](docs/parity/fixtures/radius-interactive-onboarding-2026-10-02.md).


**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET, using Microsoft Agent Framework and Microsoft.Extensions.AI where appropriate. Normal CLI package-resource workflows and user-reachable visual interactions are in scope.

**Stop condition:** a full current-Pi audit finds no material in-scope gaps, required behavioral/differential evidence passes, exact-head CI is green, and major architecture bottlenecks are resolved.

## Historical progress at earlier checkpoints

The detailed entries in this section preserve earlier source/test evidence only; use the 2026-10-09 checkpoint above for current heads, family, CI, and next action.

- **Historical source checkpoint, 2026-10-01:** `8931bd73b2e94e7831b1a054a54a8e60bb21aaf5` passed exact-head Linux CI [36904437406](https://github.com/PhilipGB/pisharp/actions/runs/36904437406), including 1,068/1,068 tests and zero skips. Current Pi is `395315f4841ab5a090a33b8259f60b6df281c1ca`; all 41 commits after `955cc6665ee3986c6a033db52200779310d10dfd` are classified, with seven open upstream deltas beginning with per-server MCP OAuth credential isolation. The full parity goal remains incomplete.

- Historical branch/resume slice (from an earlier commit/push/stop checkpoint): latest successful physical policy is restored on open, branch selection, preflight and manual compaction; RPC stats use physical limits and virtual unknown limits stay zero. Ordinary physical assistant identity survives switching away from and back to a virtual model. The paired CLI replay matches clone, fork, persisted-session switch, process restart/resume and model changes; focused virtual-model/RPC tests pass 52/52 (0 skipped). Final local validation passes format verification, warnings-as-errors build (0 warnings/errors), and 896/896 tests (0 skipped). Branch/resume source checkpoint `72633674f53a43c60aef67c2077d5cf3132b1e52` passed exact-head Linux CI `36642628468` with 896/896 tests and zero skips. Exact next action when resumed: test large-to-small routing on tool continuation and reconcile InFlightContextBudget with route-aware compaction, then split physical summary prices and physical image/compatibility/TUI behavior before classifiers. See [stop handoff and evidence](docs/parity/fixtures/virtual-model-branch-context-2026-09-29.md).

- Bedrock Converse, Pi Messages, Radius/OAuth, and Anthropic browser/copy-code OAuth are implemented locally. Five other current-Pi OAuth flows and provider-specific wire semantics/differentials remain; see [the provider/auth reconciliation](docs/parity/fixtures/provider-auth-breadth-audit-2026-09-28.md).
- Shared root/nested execution has `PiSharpToolResult` with model text/images, UI details, structured JSON, returned error state, usage and termination. JSON Schema validates successful structured content. `tool_search` activates ranked deferred tools through the persisted loadout. Codemode runs model JavaScript in a separate Node process inside bounded QuickJS/WASM, uses the shared nested pipeline and persists branch-local store. Multi-call termination, optional model/classifier globals and usage accounting, broad Pi differentials and virtual models remain open. MCP now refreshes live tools/resources, follows `builtin:mcp` enable/explicit-load rules, supports live server management and load-time extension registrations, and starts enabled connections concurrently with a 10-second startup wait. Continue with virtual models, Codemode classifier/usage, extension lifecycle and the wider audit with dedicated routing evidence. See [tool orchestration and virtual models](docs/parity/fixtures/tool-orchestration-virtual-model-audit-2026-09-29.md) and [MCP refresh evidence](docs/parity/fixtures/mcp-oauth-refresh-2026-09-29.md).
- Bounded provider/auth reconciliation found no wholly absent major chat API family. Six OAuth profiles remain (Anthropic, GitHub Copilot, Kimi Coding, Meta, OpenRouter and xAI), alongside OpenAI Sign in with ChatGPT support. Pi `1ff5b6f` raises Codemode Bash output to 1 MiB. The current `12c416e` GPT-6.1 Sol catalog/default change remains to import during provider/catalog work. Provider-specific wire semantics and paired differentials remain behind MCP and virtual models in priority; see [the provider/auth reconciliation](docs/parity/fixtures/provider-auth-breadth-audit-2026-09-28.md).
- The older bullets below are historical slice records. This checkpoint and the current upstream audit supersede their older source pins and next-action claims.
- Latest pushed source head `584a7d1eb74dfb1fc2624f00a5246806db37c548` adds Google Vertex request/auth coverage and fixes use of its templated catalog endpoint in model connection construction. Vertex has a 14-model current-Pi snapshot, API-key and ADC routes, regional endpoint selection, and deterministic loopback tests for request paths, auth headers, request body, and secret-safe auth status. Exact-head Linux CI run [36472207899](https://github.com/PhilipGB/pisharp/actions/runs/36472207899) passed format, warnings-as-errors build, and 790/790 tests (0 skipped). Google Vertex remains `In Progress`: a paired Pi/PiSharp differential, metadata-server ADC status, and broader tool/image/reasoning/usage/error behavior remain open.
- Google GenAI adds a dedicated REST/SSE boundary and embedded 22-model snapshot from Pi `c90d9ea586194003211f10cded3c14f74a4f81e1`. Its current provider adapter uses the `x-goog-api-key` boundary and covers MAF tool continuation, signatures, Gemini-version image routing and usage locally; exact-head CI run [36472207899](https://github.com/PhilipGB/pisharp/actions/runs/36472207899) passed 790/790. Current-Pi focused Google suites passed 65 tests with 9 skipped, but a paired Pi/PiSharp differential remains outstanding.
- OpenAI Codex Responses and OAuth landed at `531392ad4`/`eecb324e7`; browser PKCE/device-code login, private credential persistence, serialized refresh and request-time account authentication are covered. Codex WebSocket/cached transport, manual-code fallback and broader provider image/tool/usage/error differentials remain open.
- Source checkpoint `66561e1db1942624f7310bd50f68195c80ee66bf` implements model-scoped Anthropic unsigned-reasoning replay from `compat.allowEmptySignature`. Current Pi upstream targeted tests pass 98/98, PiSharp SDK loopback tests pass 4/4, and exact-head Linux CI run [36454550166](https://github.com/PhilipGB/pisharp/actions/runs/36454550166) passes 768/768 tests with 0 skipped. See [the fixture](docs/parity/fixtures/anthropic-empty-thinking-signature-baseline.md).
- Azure OpenAI Responses source `ec1ea0f212f2a1964c891ce4a90c286020f3bcca` adds a provider-specific request boundary and embedded 43-model catalog; exact-head Linux CI run [36452261244](https://github.com/PhilipGB/pisharp/actions/runs/36452261244) passed 764/764 tests (0 skipped). Broader image/tool/usage/error evidence remains incomplete.
- Pushed source `84ba4c6d1d761421f55a462a716e4cdd9d84f0aa` adds the Mistral request boundary for mapped reasoning and nine-character tool IDs; exact-head Linux CI run [36441630920](https://github.com/PhilipGB/pisharp/actions/runs/36441630920) passed restore, format, warnings-as-errors build, and 752/752 tests (0 skipped). Current work imports Pi `cd5ff1125981f0fc37a56df25eee7310286a3f89`'s 33-model Mistral catalog and uses static listing. Two full local runs exposed a duplicate `read:third` call during the parallel-tool compaction test; turn-result collection is now synchronized with provider-turn closure. Targeted parallel-history/reconciler tests pass 10/10, Mistral tests pass 7/7, local format/build pass, and the post-fix full suite passes 754/754; exact-head Linux CI run [36446757583](https://github.com/PhilipGB/pisharp/actions/runs/36446757583) passed restore, format, warnings-as-errors build, and 754/754 tests (0 skipped).
- Latest pushed source `c68ad5b8f51d9184ef4c69bedf858d2617291d43` adds repeatable `--theme`, interactive `--use-theme`, and `--no-themes`; explicit file/directory paths remain loadable when discovery is disabled. Parser/catalog tests and a Linux PTY test cover the CLI behavior. Exact-head Linux CI run [36420934092](https://github.com/PhilipGB/pisharp/actions/runs/36420934092) passed restore, format, warnings-as-errors build, and 729/729 tests (0 skipped). See [the theme CLI fixture](docs/parity/fixtures/theme-cli-baseline.md).
- Pushed source `8d065991b13580a14e30a4c7f4fd9adafe5da58a` implements `retry.provider.maxRetryDelayMs` at the provider `IChatClient` boundary, disables nested SDK retries, captures retry headers for all three adapters, and extracts retry settings parsing and scope merge. Exact-head Linux CI run [36428055518](https://github.com/PhilipGB/pisharp/actions/runs/36428055518) passed format, warnings-as-errors build, and 736/736 tests (0 skipped). Pushed source `92281f3912ca3c9f68ec95e27c4a29e06ba02dcb` implements `shellCommandPrefix` through the shared shell execution boundary, including interactive `!` and `!!`; focused settings, trust, coding-tool, RPC, parser, and Linux PTY tests pass 14/14. Exact-head Linux CI run [36435658026](https://github.com/PhilipGB/pisharp/actions/runs/36435658026) passed restore, format, warnings-as-errors build, and 748/748 tests (0 skipped). Pi `main` was refreshed to `c1449660c83fd00a7c71d5f7e1bd29fafd400550`; its settings-manager tests pass 3/3. See the [retry-delay fixture](docs/parity/fixtures/settings-provider-retry-delay-baseline.md) and [shell-prefix fixture](docs/parity/fixtures/settings-shell-command-prefix-baseline.md). The shell-prefix source passed exact-head Linux CI run [36435658026](https://github.com/PhilipGB/pisharp/actions/runs/36435658026).
- Source `90c08f7ed94c64b425e2accf1b6c3b8e3d674264` adds Pi-like whitespace wrapping, shared ANSI-aware visual-row mapping, nested Markdown style restoration, and the generated system-theme path. Exact-head Linux CI run [36417080999](https://github.com/PhilipGB/pisharp/actions/runs/36417080999) passed restore, format, warnings-as-errors build, and 720/720 tests (0 skipped). See [the wrap/layout fixture](docs/parity/fixtures/tui-wrap-layout-baseline.md) and [the system-theme fixture](docs/parity/fixtures/terminal-system-theme-baseline.md).
- Current-Pi compaction alignment is implemented in source commit `56f0659d9061c448a1837529804493c2e83d1e3e`: 16,384 reserve and 20,000 keep-recent defaults, active model-switch propagation, oversized trailing tool-result fallback, and the closed recovery-omission suffix rule. Current Pi main `6f7551516b84278eb9da1c340c8e7bc66be1a6ba` passed its relevant compaction/settings tests (111 passed, 2 skipped) and a current-main RPC process replay. PiSharp passed focused tests 91/91 and full tests 692/692 (0 skipped); exact-head Linux CI run [36394452133](https://github.com/PhilipGB/pisharp/actions/runs/36394452133) passed restore, format, warnings-as-errors build, and 692/692 tests (0 skipped). The active-model RPC process test switches models during a blocked tool turn, then compacts with the selected model's one-token override. See [cut-point evidence](docs/parity/compaction-cut-points.md) and [the compact differential](docs/parity/fixtures/rpc-compact-baseline.md).
- Latest pushed source `f45f0f720ffd48399f386c0cbcb9ccf4798bd08e` adds `enableSkillCommands` with boolean default-on behavior, trusted-project precedence, `/settings` persistence, and live completion updates while preserving manual `/skill:name` expansion. Exact-head Linux CI run [36357426552](https://github.com/PhilipGB/pisharp/actions/runs/36357426552) passed restore, format, warnings-as-errors build, and 687/687 tests (0 skipped). See [the pinned setting fixture](docs/parity/fixtures/settings-enable-skill-commands-baseline.md).
- Latest pushed source `de233c4bfe78a83f3627818cf4bebd0e92aa14f2` supports `terminal.trueColor` (`boolean | "auto"`) with user/trusted-project precedence, `/settings` editing, and active theme refresh. Exact-head Linux CI run [36356332794](https://github.com/PhilipGB/pisharp/actions/runs/36356332794), attempt 2, passed restore, format, warnings-as-errors build, and 685/685 tests (0 skipped). Attempt 1 hit the existing intermittent parallel-tool compaction request-count failure; the unchanged failed-job rerun passed. PiSharp's automatic `NO_COLOR`/`TERM=dumb` mode remains a documented color-mode difference.
- Previous pushed source `be36ea88b7d09c7a30f94f84222e773f5bc38535` adds user/trusted-project `markdown.codeBlockIndent`, updates live and committed terminal Markdown after changes, and exposes literal-prefix editing through `/settings`. Exact-head Linux CI run [36355353424](https://github.com/PhilipGB/pisharp/actions/runs/36355353424) passed restore, format, warnings-as-errors build, and 683/683 tests (0 skipped). PiSharp retains its vertical code gutter, so full code-block line geometry still differs from Pi.
- Latest pushed source `5b690979d219dccd29a047a65d3497b198cf2eb3` adds scope-aware `extensions`, `skills`, `prompts`, and `themes` settings and refreshes resource catalogs through `/reload`. Exact-head Linux CI run [36401121298](https://github.com/PhilipGB/pisharp/actions/runs/36401121298) passed restore, format, warnings-as-errors build, and 696/696 tests (0 skipped). Full `minimatch` syntax and process differential evidence remain open.
- Pushed timeout source `67c283d6c90815414be8bf58446d7365f506e5ad` rearms the stream-idle deadline on response-body reads across OpenAI Responses, OpenAI Completions, and Anthropic Messages. A partial-SSE loopback fixture sends body bytes every 80 ms without completing an event; the stream completes under a 250 ms idle deadline, while the existing stalled-byte fixtures still expire. Local focused wire-byte tests passed 3/3, format and warnings-as-errors build passed, and the full suite passed 699/699 (0 skipped). Exact-head Linux CI run [36403312164](https://github.com/PhilipGB/pisharp/actions/runs/36403312164) passed restore, format, warnings-as-errors build, and 699/699 tests (0 skipped). Pi's Undici socket-data timing and .NET content-stream read cadence remain different implementation boundaries.
- The pinned `transport`/WebSocket scope audit is recorded in docs commit `9f5173a3fe21d3e17c4c23e281ae522c95e34a90`; exact-head Linux CI run [36354259199](https://github.com/PhilipGB/pisharp/actions/runs/36354259199) passed format, warnings-as-errors build, and 680/680 tests (0 skipped). The audit keeps `transport` and `websocketConnectTimeoutMs` as explicit Codex Responses protocol residuals because PiSharp has no corresponding adapter.
- Latest pushed source `84aa91b2da8174f1c67318f65529a56549098ec1` closes a same-process session-lock alias race: Linux `FileStream.Lock` does not serialize handles in one process, and a symlinked session directory previously selected a second in-process gate. `SessionFileLease` resolves existing path links before choosing the gate and OS lock. Exact-head Linux CI run [36349575501](https://github.com/PhilipGB/pisharp/actions/runs/36349575501) passed restore, format, warnings-as-errors build, and 674/674 tests (0 skipped). The preceding provider-history and user-wide HTTP proxy changes are at `079ffc11a91b3cc3bfd5eb25a6040ef1c0fcfcc4`; exact-head run [36347187105](https://github.com/PhilipGB/pisharp/actions/runs/36347187105) passed 673/673.
- Pushed session-recovery source `7e5d9563323b976a028f5b7922fbd2b47b10c6b3` passed exact-head Linux CI run [36341167560](https://github.com/PhilipGB/pisharp/actions/runs/36341167560), attempt 3: restore, format, warnings-as-errors build and 666/666 tests. Attempts 1 and 2 each failed the same OpenAI Completions overflow loopback theory (665/666); the focused two-API theory passed 2/2 and local full runs passed 666/666. This remains a CI reliability residual; provider-turn failure events now retain an internal exception for test diagnostics if it recurs.
- Settings source `a156f30fa065a808797dc267f0b56227adc8a7c8` added `/settings` controls for both prompt-delivery modes, aligned the fallback reasoning level to Pi's `medium`, and mapped `retry.provider.maxRetries` to OpenAI and Anthropic policies with an explicit default of zero. Its exact-head CI run [36344227089](https://github.com/PhilipGB/pisharp/actions/runs/36344227089) failed the OpenAI Completions overflow theory; the follow-up trace pinpointed capture-iterator disposal as the masking error, fixed in `718efcd2e2b046cb35b0cd6a40f1bddd74575018`.
- The settings fixtures verify steering changes reach the active run, reasoning defaults are sent, each provider makes one request by default and exactly one retry when configured, and inherited `HTTP_PROXY`/`HTTPS_PROXY` values take precedence over Pi's user-wide proxy setting. Project settings reject `httpProxy`, and `/settings` redacts its saved value. See `docs/parity/fixtures/settings-prompt-delivery-baseline.md`.
- A full run reproduced the parallel-tool compaction failure as three canonical results for two tool lifecycle events. Provider-history reconciliation now advances exact matches before filtering already-recorded tool call IDs. During this fix, an OpenAI Responses image replay test exposed a duplicate attached image; the ordering correction preserves that attachment. The reconciler, compaction and both image-protocol tests passed together (11/11), and the formerly failing parallel-tool test passed 10 consecutive runs. The pushed full-suite run passed 673/673.
- Its predecessor, `ca92588f4b94ab5c002065baa72542df832499f8`, closes the repeated OpenAI Completions overflow loopback failure without weakening recovery/history assertions. The SDK could throw while creating its stream enumerator, outside `ObservedChatClient`'s provider-error boundary, so the HTTP 400 overflow body never reached context recovery. The client captures setup and cleanup failures and preserves the original `context_length_exceeded` response; exact-head CI run [36333639624](https://github.com/PhilipGB/pisharp/actions/runs/36333639624) passed all 662 tests.
- RPC read commands `get_messages`, `get_fork_messages`, `get_last_assistant_text`, `get_entries`, and `get_tree` answer during blocked provider work from one cloned tree snapshot. `RpcSessionMessageProjector` follows Pi's `session.messages` semantics: compaction summaries keep the `compactionSummary` role, context edits apply to retained messages, custom-message fields survive, accepted prompts and completed run messages are visible, and partial assistant content stays in lifecycle events until `message_end`. `ConversationSession.ContextMessages()` also uses Pi's compaction wrapper and removes system entries before the retained boundary. Process coverage and direct current-Pi differentials cover live reads, compacted/context-edited messages, `get_entries` cursors, branched/labelled `get_tree`, active-stream count/snapshot behavior, and last-assistant results. Pinned Pi `2b0a123de98318c2ff8069661721ce0c3794c34e` and PiSharp matched the named fixtures. Idle RPC queue behavior, raw OpenAI Chat Completions deltas, sparse thinking maps, and busy model selection/cycling also have process comparisons; provider API breadth, arbitrary thinking-map values, partial argument snapshots and wider RPC differentials remain open. The generated system message is now projected into RPC `get_messages` and counted by `get_state`, without changing canonical or Pi JSONL history. PiSharp puts its real MAF instructions under `sections.preamble`; Pi exposes separate `preamble/tools/rules/docs/cwd` sections.
- The earlier `35be9dc8f8614dcdc66ceaab3136c8ad4301f406` run [36282903215](https://github.com/PhilipGB/pisharp/actions/runs/36282903215) failed 4/607 tests after format/build passed. Provider history reconciliation in `b71ed3119b3827a525515aa50d952f7b99d5a4f1` fixed the regressions; exact-head run [36286403870](https://github.com/PhilipGB/pisharp/actions/runs/36286403870) passed 612/612. `138820263` adds process-level abort lifecycle coverage and passed 613/613.
- Current Pi `main` was refreshed on 2026-09-28 at `6f7551516b84278eb9da1c340c8e7bc66be1a6ba`; durable parity baseline: `b3487650f6378f1b0d1643dd254445ceb4a98035`. Feature-specific records preserve their historical source SHAs.
- RPC model selection now uses the extracted `ModelRuntimeController`; model listing, `set_model`, and `cycle_model` work during a blocked tool turn. Current Pi process comparison and PiSharp CLI coverage match declared scoped model order, per-model thinking defaults, `thinking_level_changed` before correlated model-command responses, `isScoped`, persisted model-change order, and the next provider request. Scoped `:thinking` suffixes remain open.
- RPC abort process coverage now matches current Pi's partial assistant output, `message_end < turn_end < agent_end < agent_settled < abort response`, `willRetry: false`, consistent snapshots, and success response fields with no `data` or `error`; see `docs/parity/fixtures/rpc-abort-baseline.md`.
- Compaction remains in progress. `ConversationCompactionCoordinator` owns planning, two-stage history/turn-prefix summaries, persistence, usage and context restoration; the working slice adds current Pi defaults and recent-token/recovery cut-point behavior. Deterministic and process tests cover assistant boundaries, oversized trailing tool results, reload, branches, failure rollback and pre-prompt automatic compaction. A current Pi process at `6f7551516b84278eb9da1c340c8e7bc66be1a6ba` matches the split summary, interrupted-assistant boundary, details and usage; exact token estimates and in-flight/overflow behavior remain open. The historical paired process fixture is `docs/parity/fixtures/rpc-compact-baseline.md`.
- `--fork <path>` across project boundaries is implemented: PiSharp loads native or Pi JSONL sources, preserves every branch and the selected head, and writes the child into the invocation project. Focused tree and cross-project fork tests passed 10/10; exact-head CI run `36288246889` passed all 616 tests.
- RPC `switch_session` prevalidates existing targets before cancelling active work and now matches Pi's missing-path behavior: an absent `.jsonl` path becomes a lazy Pi JSONL session in the invocation project. A blocked-provider process test verifies abort settlement before the switch response, no file creation until the next prompt, and subsequent prompt persistence to the replacement session. Prompts and explicit RPC saves now use the current replacement `ConversationRun`; previously they could keep running/saving through the original run. `PiJsonlSessionFileStore` uses a fenced atomic writer shared with `ConversationStore` and refuses external replacement. Exact-head CI run `36290986668` passed all 621 tests.
- `RpcSessionReplacementProcessTests.ForkDuringBlockedRunSettlesBeforeTheResponseAndOmitsTheAbortedTurn` verifies provider-stream fork settlement and branch isolation. `RpcActiveToolForkProcessTests` now blocks a real Bash tool during a CLI RPC prompt, forks, verifies the aborted `agent_end` and settlement-before-response order, and confirms the replacement transcript excludes the turn. Current Pi regression `8724-in-memory-fork-active-tool` was inspected; this is PiSharp process evidence, not a Pi process differential.
- RPC fork captures the selected branch and prompt text before cancelling active work, then creates and adopts the replacement from that snapshot. RPC clone snapshots the current branch and stages its session file before cancelling the active run, matching Pi's `fork(currentLeaf, { position: "at" })` order. `ConversationSession.ForkForSessionReplacement` keeps accepted conversation messages while omitting the unfinished run's internal recovery checkpoints. Blocked-provider clone tests verify branch contents and settlement order; a second verifies a missing source returns Pi's error while the active run continues. Exact-head CI at `1abca7f26` passed 624/624.
- No overall parity claim. Current-Pi crash recovery, malformed-tail loading and writer concurrency now have bounded process/source evidence; broader session-manager behavior and native macOS lock execution remain open, alongside settings/resources/extensions, multimodal/provider/auth breadth, coding tools and TUI behavior.
- Architecture remains concentrated: `Program.cs` is about 1,160 lines, `ConversationRun.cs` about 944 lines, and `RpcMode.cs` 517 lines. Extract cohesive responsibilities when the next capability touches them.

## Current priority

Resources / instructions and trust remains open at Pi `4270522473e677ddba7b093caa0064a3df51d459` / PiSharp product code `65d045c5ff99e8c289b2cddb75cf6309a5879fdd`. Exact-head Linux CI [38205629517](https://github.com/PhilipGB/pisharp/actions/runs/38205629517) is green on the current evidence commit. Precedence/reload, current skill prompts and thinking visibility now have paired current-Pi evidence; trust callback/editor echo, submitted user-message and usage presentation, settings reload and broader startup differences remain open. The current Pi audit classifies 231 commits through `42705224`; the Codemode schema-error description delta is matched.

## Exact next action

Refresh current-Pi same-head calibration and paired PTY evidence for the trusted project-extension UI at Pi `4270522473e677ddba7b093caa0064a3df51d459` / PiSharp `3043c4b185e594b989aab73ff7eff915f7beed8d`. Compare trust callback order, editor echo, submitted user message, provider requests and terminal states; fix the earliest confirmed product mismatch. Continue unresolved startup/TUI, other ledger capabilities, MCP conformance, upstream deltas and the final audit; the final parity gate remains false.

## Architecture risks to reconcile before completion

- Keep `Program.cs` a composition root; extract behavior at the boundary required by the next capability.
- `ConversationRun.cs` remains a concentration risk; prompt queue state/delivery has moved into `PromptDeliveryController`, while retry, compaction and provider-turn boundaries remain candidates only when the next capability needs them.
- `PiJsonlSessionInterchange.cs` remains large; separate import/migration, mapping, export or I/O only when session interoperability requires it.
- Keep RPC capability tests in focused classes rather than growing `RpcModeTests.cs` further.
- Preserve remaining TUI gaps: custom HTML-export theme CSS, full Markdown dialect/syntax highlighting, and broader TUI differential coverage. System-theme acquisition/update, word-aware wrapping, shared ANSI-aware visual-row layout, and nested Markdown style restoration now have pinned fixtures and exact-head CI evidence.
- Before final parity, reread the feature matrix and ledger, audit current Pi `main` after this checkpoint, collect required behavioral/differential evidence, and confirm exact-head CI.
