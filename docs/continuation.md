# Continuation — PiSharp capability parity

## Active milestone — Compaction, Context Budget, Overflow and Usage Parity — 2026-10-28

PR #27 is verified merged at `12064420179f4119bdaf37479c7cba0f5ca40717`, the
actual base of this branch. Its reviewed head `2f88019cd8840b2468b1957217ae361e0bab3ad6`
has successful exact-head push run 40617744882 and PR run 40617750121; the
published managed-package evidence records 27/27 checkpoints and 240/240 cases.
Pi is pinned to `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. The frozen
compaction contract remains 22 checkpoints / 211 cases.

C1-C8 passed 86/86 at source `843d0388`; C9 passed 9/9 on clean source
`1deb1748`. Across revisions, 95/211 cases have paired results; only C9’s
9/211 cases are currently bound to the latest source, so C1-C8 need refreshing.
The C9 focused .NET filter passes 27/27 and pinned Pi navigation UI tests pass
5/5. Compact records retain source pins and raw-artifact hashes.

C10-C13 still need implementation/evidence, followed by D0, Q0, E0, CI0, and
PUB0. The separate transient estimator formula still needs classification. The
full .NET suite must be rerun at final source. PR #28 remains open and draft.
See the
[inventory](parity/fixtures/compaction-context-management-inventory.json),
[receipt](parity/fixtures/compaction-context-management-parity-2026-10-28.md),
and [execution ledger](parity/execution-ledger.json).

## Active milestone — Complete Managed Package Lifecycle and Resource Distribution Parity — 2026-10-27

PR #26 was verified merged at `d969d3ec5c4f0bb90e9c446a6c505c0374f59dd9`;
this branch is based on that merge, and Pi remains pinned at
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. The machine-verifiable acceptance
contract contains 27 checkpoints and 240 cases. At implementation source SHA
`a760d65efdbcb2192c648336b10c4dd34cd3a6e9`, local execution passes 24/27
checkpoints and 218/240 cases with zero failures or skips. The committed-evidence
checkpoint passes 7/7. Exact-head push and PR workflows passed, and publication
verification completed all 27/27 checkpoints and 240/240 cases with zero
failures or skips. Local quality passes 3,910/3,910 .NET tests, focused package
tests 118/118, architecture tests 7/7, Python 154/154 across three suites, Node
24/24, MCP, format, warnings-as-errors build, docs, and all 18 differential rechecks. Regressions
cover Git publication recovery, origin-specific temporary Git identities,
manifest-controlled extensions, and both mixed local/managed CLI extension
orders with conflicting commands. PR #27 was merged at `12064420179f4119bdaf37479c7cba0f5ca40717`. The
resource distribution path also covers
temporary npm/Git package roots through startup and reload without persisting
invocation-only settings. See the
[receipt](parity/fixtures/managed-package-lifecycle-parity.md) and
[inventory](parity/fixtures/managed-package-lifecycle-parity/inventory.json).

## Completed prerequisite — Complete Multimodal User Input and Image Parity — 2026-10-25

PR #25 is merged at `897bc5778f87ed09ea42e48b260479ee6a6a218c`; the working
branch `milestone/multimodal-user-input-image-parity` is based on that commit.
Pi remains pinned at `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.

The prompt-image processor differential passes 13/13 strict cases and 5/5
codec-equivalent cases, covering valid PNG/JPEG/GIF/WebP pass-through and
model-limited resizing, BMP conversion, and malformed/truncated inputs. It
records one accepted safety difference: Pi accepts a WebP with an oversized RIFF
length field that PiSharp rejects. The paired positional file/initial-message corpus
passes 7/7, including 256 images, JPEG/GIF/WebP pass-through and
relative, absolute, home-expanded and symlinked image paths. Paired physical image requests pass
for OpenAI Responses, Chat
Completions and Anthropic (3/3). The file comparison exposed an extra blank line
before the prompt; PiSharp now follows pinned `buildInitialMessage` concatenation.
Pinned Pi's selected-model `before_agent_start` normalization test passes 1/1.
PiSharp normalizes accepted image prompts before canonical persistence. Pinned
Pi clipboard/print/prompt selections pass 51/51. Paired clipboard acquisition
and file-path precedence pass 4/4, and JSON-mode plus RPC prompt content passes
2/2; reports are stable across two pinned Pi runs. A saved-session restart and
provider-context differential passes 3/3 strict comparisons and is stable
across two pinned Pi runs. A separate post-restart physical provider-wire
differential passes 3/3 across the three supported APIs; it verifies the active
branch image is retained and the inactive branch image is omitted. The JSON
probe includes a truncated image followed by a valid image and matches
PiSharp's omission note and surviving image. A separate positional-file
differential follows Pi's deferred selected-model normalization path and passes
1/1 with two-run calibration, preserving the following valid image. The full
.NET suite passes 3,886/3,886; format and warning-as-error build pass. Python
parity passes 27/27, terminal Python 66/66, and terminal Node 24/24. MCP has no
new regressions against its classified baseline. Documentation paths pass for
154 documents and 672 references.
Dedicated macOS and Windows jobs verify clipboard script syntax and cleanup
contracts, not live desktop clipboard access. Push run `40196042863` and PR run
`40196046840` on head `f734c0c8` failed in the protocol image oracle because it
did not create generated catalog data in the disposable Pi checkout; platform
and MCP jobs passed. The protocol runner now uses the temporary catalog helper
and its local differential passes 2/2. Correction `bacbdc47` is published and
passes exact-head runs `40196652776` and `40196656515`. The expanded file/image
corpus and partial-file cancellation regression are published. Push run
`40200397739` and PR run `40200401449` on `0ab5007e` timed out because the
positional-file oracle decoded identical images repeatedly to report dimensions;
the pinned comparisons themselves did not mismatch, and platform/MCP/build
checks passed. The probe now caches those dimensions and retains the 256-image
ordering/count comparison. Its local differential passes 7/7 in under one
second. Commit `87d00fe1` adds the shared temporary-catalog helper to the
clipboard oracle; its local differential passes 4/4. Exact-head push and PR
runs `40201444374` and `40201446454` passed on this implementation head,
including all 3,886 .NET tests, pinned differentials, platform clipboard jobs
and MCP conformance.
Broader format, limits and resource-bound cases remain. PR #26 was subsequently
merged at `d969d3ec5c4f0bb90e9c446a6c505c0374f59dd9`; this managed-package
milestone is based on that verified merge. See the [receipt](parity/fixtures/multimodal-user-input-image-parity-2026-10-25.md)
and [inventory](parity/fixtures/multimodal-user-input-image-parity-inventory-2026-10-25.json).

## Previous milestone — Complete Model Selection, Thinking and Configuration Parity — 2026-10-24

The 93-case source-pinned behavior corpus and 35-case provider-wire corpus pass.
The implementation covers model references, initial selection, scope matching
and editing, thinking defaults/clamping, budgets and sampling, settings
precedence/persistence, and CLI/TUI/RPC integration. The Linux PTY scoped-model
journey and session/extension process regressions are included. Documented
limits include no native Windows/macOS run, unmodeled extended busy-run
interleavings, and the retained RPC/session fallback identity when no provider
is authenticated. See the [inventory](parity/fixtures/model-selection-thinking-configuration-inventory-2026-10-24.json)
and [receipt](parity/fixtures/model-selection-thinking-configuration-2026-10-24.md).

The paired reports pass 93/93 direct-Pi behavior cases and 35/35 physical
provider-wire cases, refreshed against review correction commit
`bdbf2de91a30614ca7a258f1eaaf5e13141784c7`. Format verification and the
warning-as-error build pass. The prior implementation-head full .NET suite
passed 3,780/3,780. Two local suite attempts after the correction each had one
unrelated timing/port fixture race; both affected tests passed in isolation.
Fresh correction-head exact CI results are tracked in the PR description. Node
terminal (24/24), Python terminal (66/66), parity guards (27/27), documentation
paths (152 documents / 627 references), and MCP conformance also pass.
Checkpoints A–V are complete for implementation head
`683a485e43e9f07190d0b5cead56efaafd2a1a84`. Exact-head push CI
[40106023213](https://github.com/PhilipGB/pisharp/actions/runs/40106023213) and
PR CI [40106026319](https://github.com/PhilipGB/pisharp/actions/runs/40106026319)
passed on the prior head; each full .NET suite passed 3,780/3,780 with zero
failures or skips. A review correction adds model-mapped extended reasoning and
12 direct-Pi wire cases; its focused differential passes. Two local full-suite
attempts each hit one unrelated intermittent timing/port fixture failure, and
both cases pass in isolation. Fresh correction-head CI results are recorded in
the PR description.
PR #23's unrelated Dependabot change has merged to `main` at
`f4f57ca754662222d2c0162b2758778eb2999a57`; the milestone branch includes it
through sync commit `e828fa3965e9183671863635808aff36b29a0034`. The PR description
tracks the published head and current CI evidence.

## Previous milestone — Complete Built-in Coding Tool Parity — 2026-10-22

PR #21 is merged at `ebfeb24c9dd5e03592c3b388236f22a3a3a3299a`, verified on
fetched `origin/main` before product work. Its reviewed head was
`64fc15b7317a2307b066bbba6ebb3ea630b19ee1`. The new branch
`milestone/builtin-coding-tool-parity` starts from that actual merge. Pi remains
at `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.

PR #22 is verified merged at `eefa89454a3c1cfd196894f94744b6f393c8e721`.
The current branch `milestone/model-thinking-configuration-parity` is based on
the PR #22 merge `eefa89454a3c1cfd196894f94744b6f393c8e721`, and now includes
the later PR #23 merge from current `origin/main` at
`f4f57ca754662222d2c0162b2758778eb2999a57`. Pi remains pinned at
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Implementation source is
`bacc756495b92788eb3e22f122317c16b095780e`.

The refreshed pinned-Pi behavior report passes 93/93 and the provider-wire
report passes 35/35 against this source. Local format, warning-as-error build,
full .NET suite (3,780/3,780), Node, Python, parity guard, documentation-path and
MCP checks pass. The previous exact-head push CI succeeded before the branch
synced current `main`; draft PR #24 was open. Its PR CI found a loopback port
reservation race, now fixed by using the existing bind-retry helper; the focused
case passes. Exact-head push and PR CI on implementation head
`683a485e43e9f07190d0b5cead56efaafd2a1a84` both passed with 3,780/3,780 .NET
tests and every other job green. The PR description tracks the published head
and current workflow evidence. PR #24 later merged at
`0d83418a31d84e530b099d98054739607fa3e3ad`. See the [model-selection milestone
receipt](parity/fixtures/model-selection-thinking-configuration-2026-10-24.md).

Product acceptance source is `962cfa48d97a960149e1f0cc020f25bc302c1d48`.
The final publication head and push/PR workflow IDs are maintained in the PR
description so the tested head does not change solely to record its CI result.
The [receipt](parity/fixtures/builtin-coding-tool-parity-2026-10-22.md),
[inventory](parity/fixtures/builtin-coding-tool-inventory-2026-10-22.json) and
[corpus acceptance](parity/fixtures/builtin-coding-tool-corpus-acceptance-2026-10-22.json)
record exact source revisions and scope.

The 460-case corpus has 389 exact passes, ten calibrated JPEG equivalents,
61 explicit safety exclusions and zero unresolved cases; the strict same-Pi
control is 460/460 without PiSharp. Case counts are read121/write39/edit69/
bash44/grep72/find62/ls53. Atomic/private mutations, image allocation bounds,
repository privacy and stronger shutdown behavior remain clearly excluded from
strict equivalence. Eight additional pinned owner/mode observations are facets,
not eight extra strict whole-filesystem passes: inode replacement remains
intentional.

All seven tools run through MAF, three physical provider workflows and actual
session restart. The Bash-abort workflow preserves exactly one appended byte
after restart. Concurrent tool results survive persistent compaction and RPC
history. Final validation and reliability results are in the receipt. The first
full 3,718-test run preceded the late set-ID correction and is historical; use
the later full suite, corpus, provider and repeat receipts.

CI checks out the same pinned Pi separately, installs its locked dependencies
with scripts disabled, captures actual Linux owner/group/mode results and gives
that oracle to the native tests. Both workflows retain the source JSON artifact.
The Linux gate requires an assignable alternate group; inspect this artifact and
the corresponding successful test job before closing T. Local group mapping
restrictions are recorded, without claiming that the alternate-group case ran
locally. Privileged and native Windows/macOS parity remain unverified.

The grep context review correction replaces repeated prefix scans with bounded per-file windows and byte-offset checkpoints. Seven regressions cover the measured 32 MiB case, oversized windows, UTF-8/CRLF boundaries, cancellation, read failures, eviction and backward queries. The [context receipt](parity/fixtures/builtin-coding-tool-grep-context-2026-10-22.json) preserves source expectations and application-read counters. Consult PR #22 for the corrected-head gates.

Before resuming work, fetch PR #22 metadata and verify the published head, draft
status, merge state and CI rather than repeating completed implementation. If
final-head test/MCP/ownership gates pass, the milestone is complete within its
recorded scope. Broader Pi parity remains `in_progress`.

## Previous milestone — Terminal Rendering and Interaction Parity — 2026-10-22

PR #20 merged at `5e1e5aa8ddc66a8e829a880604060b8483107284`; that exact
commit is the base of `milestone/terminal-rendering-interaction-parity`.
Pi remains pinned at `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
Current product source is `216251fd`.

The source contains terminal restoration, cell/style/wrapping, Markdown,
syntax/math, tool/image, scrolling/search, editor/mouse, and startup repairs.
Linux Ctrl+Z now restores the terminal, stops the foreground job, and resumes
without losing or replaying editor input. Its supervised normal/fullscreen
PTY comparison matches twenty-two named checkpoints; native CLI tests cover both
modes, and a Python regression verifies cleanup of a suspended child group.
The streaming stress test overlaps 200 stream/widget updates with forty resize,
theme, scroll, modal, and tool-expansion cycles while preserving the draft.

The nineteen-fixture corpus is reviewed: 45/45 calibration, normalized physical
provider requests and clipboard checks, with zero scenario errors. Its 28
differing selected states are the explicitly classified identity, resume-hint
and installation-path estimate differences. The final source additionally
matches multi-click word/line selection and bounded, independently expiring copy
feedback; all seven selected multi-click states match exactly.

Build and format checks pass; Node is 24/24, Python 66/66, and MCP retains only
its eight classified baseline failures. The final full .NET suite passes
2,711/2,711 with zero failures/skips, including the added input, color and nested math regressions.
Documentation paths pass 150 documents / 582 references. The canonical fixture workspace must
remain outside this repository: use the runner defaults under `/tmp`.
Run-specific roots beneath `artifacts/` picked up repository instructions and
invalidated earlier request comparisons; long custom roots also changed the
rendered CWD truncation. Those exploratory reports are retained, not accepted.

The implementation, local validation and classified receipt are complete.
[PR #21](https://github.com/PhilipGB/pisharp/pull/21) records the published head and exact-head
push/PR CI run IDs and conclusions. PR #21 remained draft/unmerged through review;
the following mission merged it as recorded above. Evidence is in the
[terminal parity receipt](parity/fixtures/terminal-rendering-interaction-parity-2026-10-20.md).

The independent-review correction propagates nested LaTeX failures to source
fallback and shares a 64-level recursion budget across groups, arguments and
environment rows. The new unsupported-math PTY fixture and refreshed code/math
and structural Markdown cases match all 21 action checkpoints.

## Previous milestone — Complete Native Extension Interactive UI — 2026-10-20

PR #19 was verified merged at `b4cfac1ca83b38215311d8441069286cca0dc4e6`;
`origin/main` and the active branch base match that SHA. Its reviewed head was
`c26ef860978da0117b49f491de36d05dbe9b41b6`, with successful exact-head push
CI `39346174499` and PR CI `39346177476`. The pinned Pi revision remains
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.

The PR #20 implementation was on `milestone/native-extension-interactive-ui`
from that merge and is now merged at
`5e1e5aa8ddc66a8e829a880604060b8483107284`. It adds additive public UI contracts, a
package-owned retained capability with removal/reload revocation, terminal
screen contributions, focused and overlay components, editor/autocomplete and
theme integration, decoded input interception, and a compiled-package PTY
journey. The final focused UI/input/PTY/RPC/compositor selection passes 260/260;
the shortcut-aligned external-package PTY journey passes 10/10 repeated runs.
Coverage includes listener teardown, autocomplete cancellation, screen-binding
cleanup, concurrent assistant-stream/UI updates, adjacent buffered-character
input interception, the tiny-terminal overlay fallback, and RPC transport
disposal waiting for registered event writes. The final full .NET suite passes
2,566/2,566 with zero failures and skips. The source-pinned terminal fixture
reaches all checkpoints and matches
its startup, widget, and input-update viewports, but full state/render
comparison differs in 22,411 places (14,009 render differences; raw output
differs from byte 2). The final warnings-as-errors build, format verification,
architecture, Node, Python, and MCP checks passed; documentation path
validation passed 149 files and 576 references before the merge. PR #20's
exact-head verification and merge are complete.
Evidence is recorded in the
[interactive UI receipt](parity/fixtures/native-extension-interactive-ui-2026-10-20.md).
Broad extension parity remains `in_progress`.

## Previous milestone — Native Extension Two-Stage Context Mutation — 2026-10-19

PR #19 merged at `b4cfac1ca83b38215311d8441069286cca0dc4e6`. It adds ordered
native `context` and `context_with_system` callbacks at the route-aware
provider boundary, detached message edits, host-owned tool configuration,
request-only prompt changes and the Anthropic lineage correction. See the
[context mutation receipt](parity/fixtures/native-extension-context-mutation-2026-10-19.md).

## Previous milestone — Native Extension Message Lifecycle Hooks — 2026-10-19

PR #17 is merged at `0737ee30524ebc86b845f2b1b5c19b9d481db212`. The reviewed
head was `9182227d321d1e1d7871a6273f73534549835569`; push CI `39279759725`
and PR CI `39279767767` passed on that exact revision. No additional comments,
reviews or commits appeared before the expected-head merge. See the
[message lifecycle receipt](parity/fixtures/native-extension-message-lifecycle-2026-10-19.md).

## Previous milestone — Native Extension Provider Wire Hooks — 2026-10-19

PR #16 merged the provider-wire increment at
`0f1f68b14bb636d4ed85bfd7dcce6916aa00cbff`. Its actual payload, header,
response, and raw-event boundaries preserve the pinned no-response outcomes
for Google Generative AI, Vertex, and Codex WebSocket; a host-owned bridge
supports new wire-aware extension providers, while pre-bridge `IChatClient`
providers receive no fabricated callbacks. See the
[provider-wire receipt](parity/fixtures/native-extension-provider-wire-hooks-2026-10-19.md).

The preceding Native Extension Input & User-Bash milestone is complete on
merged PR #15 (`2ea360f991b0e15c59203557a68a5a1e3c026450`); its review
correction and verification are recorded in the
[input/bash receipt](parity/fixtures/native-extension-input-bash-2026-10-18.md).

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

See the [milestone receipt](parity/fixtures/native-extension-agent-settlement-2026-10-18.md).


## Previous milestone — Native Extension Session Transitions & Compaction Hooks — 2026-10-18

Work is on `milestone/native-extension-session-hooks`, with implementation
source `967c31b2`, based on merged PR #12 and `main` at
`eef6f3f83247758d397b4e313d2c26b0088ee9ac`; configured Pi stays
pinned at `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. This bounded increment
adds typed veto and completion hooks for session replacement, fork, tree
navigation, and manual/automatic/overflow/virtual-model compaction. Existing
Runtime owners retain transition preparation, canonical persistence, compaction
planning, and history restoration. The public API, external package process
fixture, and focused RPC, TUI, Runtime tests are in place. A final audit against
the pinned `agent-session-runtime.ts` and TUI caller found that Pi's `/clone`
uses the fork hook with `position: "at"`; PiSharp now dispatches that hook in
both TUI and RPC before clone preparation. The same audit added bounded
post-commit `session_tree` delivery, cancellation-aware history restoration, and
regressions for tree rollback, observer revocation, automatic custom compaction,
and restored custom summaries. Local integrated verification passes 2,335 .NET
tests with zero skips, architecture 7/7, Node 24/24, Python 50/50, and docs
paths 142 documents / 547 references; the eight classified MCP conformance
failures remain unchanged. The final PR head SHA and exact-head CI run links are
recorded in the PR description after its checks complete.
Keep broad extension parity `in_progress`; generic `pi.on(...)`, branch-summary
injection, provider wire hooks, and other deferred fields remain open. See the [API guide](extensions.md)
and [milestone receipt](parity/fixtures/native-extension-session-hooks-2026-10-18.md).

## Previous milestone — Native Extension Events & Callback Context — 2026-10-18

This bounded increment added typed compiled-extension observers for session
metadata, compaction, agent and turn lifecycle, tool execution, model selection,
and thinking-level changes. Events reuse the current Runtime/session owners,
preserve registration order and source-aware error isolation, and expose
detached payloads with callback-scoped state access. TUI/RPC contexts reuse
existing controls; print/JSON and direct Runtime execution use read-only context
where host controls are unavailable. Exact-head push and PR Linux CI both passed
at `8f31c7ec1ef35882a8ea98a8988c18289dbaf206`, including 2,306 .NET tests. A
cancelled event token revokes the public callback context even if its handler
remains pending. See the [API guide](extensions.md) and [milestone receipt](parity/fixtures/native-extension-events-context-2026-10-17.md).

## Previous milestone — Native Extension Provider, MCP & Virtual Model Registration — 2026-10-17

Work is on `milestone/native-extension-provider-mcp-virtual-models`, based on
the PR #10 merge `632bd08b87afb10ac6669b4072d08a8e3b452687`; configured Pi stays
pinned at `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. This bounded increment
adds configuration-time public registrations for normal MEAI providers/models,
MCP servers, and routed virtual models. The host binds those source-owned
registrations to `ProviderModelRuntime`, normal MCP startup, and
`VirtualModelRegistry`; package rollback, replacement, removal and old-client
lifetime behavior are covered. A packed external package process drives the
normal provider/tool loop and resumes virtual route state across processes.

The broad extension rows remain `in_progress`. Provider-specific extension
OAuth, late registration and the full event surface remain open. See the
[API guide](extensions.md) and
[milestone receipt](parity/fixtures/native-extension-provider-mcp-virtual-models-2026-10-17.md).

## Active milestone — Native Extension Controls & Configuration — 2026-10-17

Work is on `milestone/native-extension-controls-configuration`, based on
`15b7acceb9c41a5b278b005ac5df6dadad7cda48`; the configured Pi reference stays
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Product source is
`c9d3f078fa0dba6a487bc178708667c7e562b64d`; draft PR [#10](https://github.com/PhilipGB/pisharp/pull/10)
has passing exact-head push CI
[39051662051](https://github.com/PhilipGB/pisharp/actions/runs/39051662051)
and PR merge-result CI
[39051669964](https://github.com/PhilipGB/pisharp/actions/runs/39051669964).
Local verification passes 2,268 .NET tests with zero skips, architecture 7/7,
Node 24/24, Python 50/50, and docs paths 139/536; MCP conformance reports no
regressions against its eight classified baseline failures. This bounded SDK increment adds
fresh read-only effective settings snapshots, typed extension CLI flags,
session names and entry labels, command/tool inspection and active-tool changes,
model/thinking controls, abort/compaction/graceful shutdown, and ordered,
request-local structured `before_agent_start` prompt sections. The compiled
external package acceptance covers process-level flag parsing, settings and
metadata, provider-visible control changes, reload/removal ownership, and
normal shutdown. Project extension execution stays behind the existing trust
boundary. Provider, MCP, advanced renderer, dynamic registration, and global
parity work remain out of scope; the broad extension ledger rows stay
`in_progress`.

See the [extension API guide](extensions.md) and
[milestone receipt](parity/fixtures/native-extension-controls-configuration-2026-10-17.md)
for the public contracts, pinned-source scope, acceptance, and final checks.

## Previous milestone — Native extension state and interactive controls — 2026-10-17

This bounded milestone is implemented on
`milestone/native-extension-state-interactive-controls`, based on the verified
PR #8 merge commit `d9156e53c4822e7b98226df6170b29c61bc87eb1`. The configured Pi
reference remains `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Compiled
extensions now append durable JSON entries to the canonical session tree and
read the active branch, receive session start/shutdown callbacks, use the
existing safe select/confirm/input/notify UI bridge, and register TUI shortcuts.
Product source is `2c24b4fdb9b79bb7b73c935cc4074ce6da8aa919`.
The external package process fixture covers saved state, a provider request
that excludes state markers, fresh-process resume, lifecycle transitions, and
package v1-to-v2 replacement. PTY coverage exercises the shortcut, package
reload/removal, and editor recovery. Local verification passes 2,237 .NET tests
with zero skips, architecture 7/7, Node 24/24, Python 50/50, documentation
paths (138 documents / 532 references), and MCP conformance with no new
regressions. Draft PR [#9](https://github.com/PhilipGB/pisharp/pull/9) is open;
exact-head push CI [39039746664](https://github.com/PhilipGB/pisharp/actions/runs/39039746664)
and PR merge-result CI
[39039758081](https://github.com/PhilipGB/pisharp/actions/runs/39039758081)
both pass. See the [milestone receipt](parity/fixtures/native-extension-state-interactive-controls-2026-10-17.md).

This closed only the bounded implementation increment. Keep the broad
extension API and orchestration ledger rows `in_progress`; provider/model and
advanced UI remain open. Its stop point is superseded by the active milestone
above.

## Native extension public runtime bridge — 2026-10-17

The bounded native extension runtime bridge is implemented on branch
`milestone/native-extension-runtime-bridge`, based on `main`
`5de6aac534ea1548f28f63999c81addcc8189f48`. Product source is
`6019c32cdcc65a2c17d59a8842d09f67c5f9526d`; configured Pi remains pinned at
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Draft PR #8 is open. Both exact-head
push and PR merge-result Linux CI pass, including the `test` and
`mcp-conformance` jobs ([push run 38991202559](https://github.com/PhilipGB/pisharp/actions/runs/38991202559),
[PR run 38991226901](https://github.com/PhilipGB/pisharp/actions/runs/38991226901)). See the
[milestone receipt](parity/fixtures/native-extension-runtime-bridge-2026-10-17.md).

The public compiled API now bridges turn-start discrimination and image
metadata, extension user/custom message delivery, injected custom messages,
resource discovery, provider context transforms, and tool-call/result hooks
onto the existing runtime. The PR #7 API-v1 contract fixture is packed and
loaded by the additive host. A deterministic external-package workflow checks
the hook/tool loop, resource changes, replacement, removal, persistence and
resume behavior. Acceptance hardening at `103222187a2973dbd324b899bea3ca53586c41f2`
adds automatic-retry and pending-context regressions, plus a separate-process
save/compact/resume/follow-up check that verifies a custom trigger is not
replayed. Active custom trigger messages join the owned queue during setup or
provider execution; cancellation and failure settle for a later ordinary
prompt. Full local verification passes **2,200/2,200** .NET tests with zero
skips, architecture **7/7**, Node **24/24**, Python **50/50**, and docs
validation (**137 documents, 530 references**). MCP conformance has no new
regressions; its eight classified baseline failures remain.

The acceptance source passes exact-head push CI and PR merge-result CI
([push run 38993113554](https://github.com/PhilipGB/pisharp/actions/runs/38993113554),
[PR run 38993115988](https://github.com/PhilipGB/pisharp/actions/runs/38993115988)).

Review follow-up: pinned Pi keeps `NextTurn` separate from both active-run
steering/follow-up and `triggerTurn: false` context insertion. The correction
now stages next-turn messages after the next run's starting message and defers
busy context-only messages until tool results are recorded. They are inserted
before the next provider request in a natural tool-loop continuation, without
creating a request of their own. Three regressions cover busy/idle behavior and
the tool-loop continuation; the idle `triggerTurn: false` test now uses the
same public delivery path as TUI and RPC. Focused extension tests pass 15/15,
format and warnings-as-errors build pass, the full suite passes 2,204/2,204
with zero skips, Node passes 24/24, Python passes 50/50, and docs paths verify
137 documents / 531 references. MCP conformance reports no regressions from
the classified baseline. Exact-head push and PR merge-result CI pass on
`5f71e6b0`
([push run 39023995009](https://github.com/PhilipGB/pisharp/actions/runs/39023995009),
[PR run 39023997847](https://github.com/PhilipGB/pisharp/actions/runs/39023997847));
both confirm 2,204/2,204 .NET tests and MCP conformance. See the updated [native
extension bridge receipt](parity/fixtures/native-extension-runtime-bridge-2026-10-17.md).

The occurrence/persistence review correction assigns custom messages their
canonical entry IDs, preserves those IDs through request snapshots and session
restoration, and awaits the context-only message save before provider dispatch.
Identical sends stay distinct while a cloned snapshot of the same occurrence
is reconciled once. Eight additional regressions cover repeated messages in a
real tool loop, a blocked save followed by disk recovery before run completion,
streaming/non-streaming snapshots and failed-save dispatch prevention. The
combined focused delivery/turn-start suite passes 23/23. The recovery fixture
loads the saved file into an independent runtime while the original provider
continuation is held; it does not kill the process.
Final local verification passes the complete .NET suite (2,212/2,212, zero
skipped), format verification, a zero-warning build, and documentation paths
(137 documents / 531 references).

The pending-state expansion at `a2ad5783` is superseded by a pinned-source
audit. Pi's extension `hasPendingMessages` uses `pendingMessageCount`, which
counts only steering/follow-up messages. Public callbacks and TUI/RPC commands
therefore use the ordinary prompt count again. Held `NextTurn` and busy
context-only messages retain their separate queues and do not set the public
flag. Six held-message cases now assert false, and the positive setup test
covers both steering and follow-up queues. The focused suite passes 40/40;
format, a zero-warning build and docs validation pass. Full-suite validation
uses final exact-head CI. See the milestone receipt for source references and
scope.

This completes only the bounded native runtime bridge milestone. The broad
extension API and orchestration capability remain `in_progress`; there is no
global parity promotion. Per-session state, settings, UI/keybindings, provider
registration, structured prompt-option mutation and long-term MEAI binary
compatibility remain open.

## Resources / Packages / Extensions resume — 2026-10-16

The user resumed this milestone after PR #5 merged. Work is on
`milestone/resources-packages-extensions`, based on merged `main`
`0c94c69d2f416ba0e625e353142b1d695565b75e`. The package install/remove/update/list
CLI workflow is already verified by the current ledger and
[`package-cli-real-process-2026-10-15.json`](parity/fixtures/package-cli-real-process-2026-10-15.json);
the preserved `worker/003-package-cli-*` refs are an older duplicate and would
drop newer package-output/settings behavior.

The extension increment is merged in PR #6. Its final product source is
`84d94364adb6a11e582ac917a20d3ef45eb7594b` (main merge commit
`9a1a3efbfc5a5c663e0d55eec6c08f19042e78f1`); `9d5b0a22` is the initial
implementation before review corrections. The increment adds ordered
`AddBeforeAgentStartHandler` callbacks for expanded submitted user prompts.
Handlers receive prompt text, image attachments, the current full system prompt,
and live extension context. Full prompt replacements apply only to the current
turn; handler errors emit `extension_error`, cancellation propagates, and failed
extension configuration rolls the new registrations back. `context.IsIdle` is
false during `before_agent_start` and remains false through provider work and
retries, then returns to true after the conversation run settles. The existing
provider streaming/reentrancy guard remains unchanged. The pinned Pi source is
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Idle custom-message `triggerTurn`,
custom-message injection and mutable prompt-option sections remain open.

Focused extension and recovery checks pass **5/5**, including image delivery and
the idle-state lifecycle regression. Local format verification and the
warnings-as-errors build pass. The full local suite was not run; exact-head Linux
CI passed **2,159/2,159** .NET tests and MCP conformance at the final product
source ([run 38963167005](https://github.com/PhilipGB/pisharp/actions/runs/38963167005)).
The paired process differential was not run. The extension capability remains
`in_progress`; no global capability status changed. See the
[before-agent-start receipt](parity/fixtures/extension-before-agent-start-current-pi-2026-10-16.json)
and [native extension API](extensions.md).

## Interactive Command UX consistency correction — 2026-10-16

This bounded correction is on `milestone/interactive-command-ux`, based on
`c52f02744f7ef5aa6e7d0202f617ed4e2dda3770`. The configured global Pi reference
remains `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`; this work does not promote a
global capability or claim full TUI parity. Product and acceptance-test source
was originally commit `d4e94cb7`; the review corrections are included in PR #5.

Settings now use a compact searchable in-flow list with the requested initial
ten-row order, aligned values, selected-setting description, bounded rows,
position/count, and persistence checks at 80 and 52 columns. The order test
protects the user-observed acceptance order. Pi's current selector was inspected
at `698a2d23b86a5045dd54554354a5e0cb7e466795`; its present upstream order places
Show images and Image width before Auto-resize images, so the prompt's explicit
first-ten sequence remains authoritative for this correction. Supported,
small-UX-gap and out-of-scope setting classifications are recorded in
[`interactive-command-ux-2026-10-16.md`](parity/fixtures/interactive-command-ux-2026-10-16.md).
`Clear on shrink` now tracks rendered transcript content height and requests a
full viewport redraw when that content contracts; changed rows are still erased
correctly when their text becomes narrower.

Tab and Shift+Tab cycle slash/path candidates directly in the editor buffer.
The completion metadata comes from the canonical built-in command catalog,
which merges dynamic skill, prompt and extension commands. PTY acceptance cycles
`/mod` through `/model` and `/models`, completes `/thi` to `/thinking`, and
opens the selector with one Enter. The `/thinking` selector shares supported
level order and descriptions with Shift+Tab cycling, supports direct setters,
cancel and model-specific Ctrl+S defaults, and is covered by isolated PTY tests
that verify a later provider request uses the new level. The selector marks the
implicit `medium` level as the default when no preference is stored. Terminal
progress is active during accepted agent runs and compaction, and the parent run
clears it on completion, cancellation, or error; manual compaction uses the same
OSC progress control.

The PR #5 review follow-up gives `Clear on shrink` full viewport redraw behavior
when rendered transcript content contracts, and ties terminal progress to the
agent and compaction lifecycle while retaining progress-dialog support. The
focused TUI/PTy regression set passes **115/115** with zero skips.

The correction remains bounded to settings presentation, completion, command
discovery and thinking selection. No execution-ledger or feature-matrix status
changed. Initial milestone local verification passed restore, format, and the
warnings-as-errors solution build; .NET **2,157/2,157** with zero skips;
architecture **6/6**; Node **24/24**; Python **50/50**; and documentation path
validation (**136 documents, 524 references**). The .NET suite used `TMPDIR=/tmp`
so the terminal path fixtures fit their 120-column displays.

MCP conformance passes **261** checks with the same eight classified baseline
failures in elicitation, basic CIMD and scope retry-limit behavior; there are no
new regressions. Pushed source `77d91043f3826926acaea49b6d76a0174ecc95d5`
passed both required exact-head Linux CI jobs, `test` and `mcp-conformance`
([run 38934292564](https://github.com/PhilipGB/pisharp/actions/runs/38934292564)).
Do not start Resources / Packages / Extensions from this task.

## MCP + Codemode Workflow milestone — 2026-10-16

The bounded MCP/Codemode milestone is on
`milestone/mcp-codemode-workflows`, based on main
`28f223c0c3b52ac9cd79a76c35a26c982999eee2`. Final product source is
`834927bfc984cdc003149b3f15223ecc13f24a2b`; the initial product source was
`9ca5deb4640438893c3bc26a4bf16df3dd7eada7`. The configured Pi reference stays
pinned at `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. This completes the
requested workflow milestone without claiming global parity.

Bare `/mcp` opens a selectable server list and per-server details/actions menu
with status, tool count, transport/source, scope, exposure and safe errors.
Actions include tools, exposure, sign-in/out, reconnect and enable/disable;
Escape returns from actions to the list and closes the manager to the prompt.
A Linux PTY selects and disables a configured stdio server, backs out twice,
completes a deterministic provider follow-up and verifies clean CLI/fixture
exit. The existing process PTY also covers typed status, disable and reload.
A composed agent test uses the real `PiAgent`/`ConversationRun`, calls one stdio
MCP tool, passes the result through Codemode nested read/write tools, continues
in the parent and successfully handles a follow-up.

Additional acceptance verifies that a failed MCP server does not prevent a
healthy configured server from serving tools. Parent cancellation stops a
blocked MCP call and a blocked Codemode worker, waits for settlement and then
successfully handles another prompt without replay. Direct Codemode sandbox
tests cover 64 KiB text output, 256 output items, 4 MiB per nested argument and
8 MiB aggregate argument limits.

OAuth keeps DCR as the default and supports opt-in CIMD with Pi's issuer-aware
shared or server-hash metadata document. The callback listener binds only to
the configured loopback authority; origin/path, `Host` header and remote-address
checks remain as defence in depth. Tests cover successful configured
`localhost` and `127.0.0.1` callbacks, mismatched authority/path rejection,
manual redirect retry, cancellation, and the active TUI paste flow. The pinned
MCP SDK remains responsible for PKCE, issuer/state validation, token exchange
and refresh. Codemode classifier
contexts accept 4 MiB per argument and 8 MiB total per run; a deterministic
48 KiB image context reaches the classifier fixture. The named MCP OAuth CIMD
capability is verified; broad MCP and Codemode capabilities remain in progress.

Full local verification passes restore, format, a warnings-as-errors build,
**2,130/2,130** .NET tests with zero skips, architecture checks **6/6**, Node
**24/24**, Python **50/50**, and documentation validation (**135 documents,
524 references**). MCP conformance passes **261** checks with the same eight
classified baseline failures and no new regressions. One saved-Codex and one
local llama.cpp live smoke each invoked the deterministic MCP echo fixture once,
returned its result, and shut down the fixture.

The one promoted capability leaves all 194 IDs at **13 verified, 148 in
progress and 33 not started**. Broader MCP resource/output semantics, Codemode
sandbox semantics and the established conformance failures remain open.
The initial product-source CI on `9ca5deb4640438893c3bc26a4bf16df3dd7eada7`
passed before the OAuth listener correction ([run 38911196308](https://github.com/PhilipGB/pisharp/actions/runs/38911196308)).
Final product source `834927bfc984cdc003149b3f15223ecc13f24a2b` passed both
required exact-head Linux CI jobs and **2,130/2,130** tests
([run 38917328736](https://github.com/PhilipGB/pisharp/actions/runs/38917328736)).
The milestone is complete; stop and wait for the next user goal.

## Interactive UX milestone complete — 2026-10-16

The bounded Interactive UX milestone is complete on branch
`milestone/interactive-ux`, product source
`927b9c8e8f2ff5e1e7f756925dbcc69f63565a5f`, based on main
`2f5957c808bea59292b592a26d64ca0bc6d49c3d`. The configured Pi reference
remains pinned at `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`; global Pi parity
remains incomplete.

This milestone made terminal ownership resilient through cleanup failures,
preserved raw Enter and Ctrl+J input correctly, made retry status visible,
anchored a scrolled transcript while output and geometry change, exposed
session branch heads, and added a searchable conversation-tree picker that
switches and persists the selected head. A narrow local-resource matcher
correction keeps minimatch negation consistent across relative, basename and
absolute path candidates.

Interactive evidence includes a Linux PTY multiline editor submission that
pastes a multiline prompt, edits its beginning, resizes narrow and wide, submits
the exact provider text, recalls/resubmits it from history, and completes a
file path. Model-picker evidence covers resize and cancel, an unauthenticated
provider failure that retains the working model, and successful switches whose
subsequent requests use the selected provider/model. The conversation-tree PTY
selects one of two sibling branches and verifies the chosen head persists while
the other branch remains available. A Responses-compatible Linux PTY flow
visibly renders pre-tool assistant text, working progress, extension call and
result, assistant continuation and a later follow-up; an injected command
error verifies terminal mode restoration. Retry start/success/failure status
is rendered in the transcript. Session sorting, search, rename, resume, branch
selection, settings/login dialogs, and cleanup after normal exit, cancellation,
double Ctrl+C and render failures remain covered. Compositor tests verify that
scrolling stays anchored through streaming and resize while the live view
follows the latest output. A Linux PTY acceptance pauses a 60-row stream, scrolls
back, resizes, returns to the latest row, then verifies every streamed row
appears once in follow-up history.

Final local verification on the source commit: restore and format pass;
warnings-as-errors solution build reports zero warnings/errors; .NET passes
**2,104/2,104** with zero skips; architecture checks pass **6/6**; Node passes
**24/24**; Python passes **50/50**; documentation validation finds **134
documents and 522 references**; MCP conformance reports no regressions against
its classified baseline. Tests used `TMPDIR=/tmp` because the local default temp
directory is inside `$HOME` and affected path-display assertions. At source
acceptance, live Codex and llama smoke tests were unavailable. The model-picker
provider switch used llama.cpp and an authenticated deterministic
OpenAI-compatible fixture.

Exact-head Linux CI on source `927b9c8e8f2ff5e1e7f756925dbcc69f63565a5f`
passes all required jobs: [run 38889766086](https://github.com/PhilipGB/pisharp/actions/runs/38889766086).

A follow-up live smoke on 2026-10-16 used the saved Codex and llama.cpp
credentials. PiSharp discovered each provider's models and completed one short
no-tools, no-session request with `gpt-6-luna` and the loaded
`Qwen3.8-27B-GGUF`. The run used a private temporary copy of the auth file so
model-cache writes stayed under `/tmp`; the original profile and sessions were
left untouched, and the temporary copy was removed.

The 194 global capability IDs remain at **12 verified, 148 in progress and 34
not started**. UX evidence does not claim global parity. Broader provider work,
MCP/Codemode expansion, full resource/extension parity, exhaustive terminal
differentials and broader live provider coverage remain deferred. Recommended
next bounded milestone: MCP/Codemode interactive workflow completion and
conformance, kept separate from provider breadth and the global parity audit.

## Daily Driver Core milestone complete — 2026-10-16

This checkpoint completes the bounded Codex subscription and llama.cpp daily
driver milestone. The configured Pi checkout remains pinned at
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`; global Pi parity remains a separate
roadmap. Stop this milestone here and wait for the next goal before broadening
provider or application scope.

The current PR product checkpoint is `92fccccc5604f3601a25f84796755208d147efc3`
on `parity/current-pi-completion`. Codex coverage includes manual
authorization-code/redirect-URL fallback, temporary subscription-usage retry,
account/session WebSocket reuse, cached `previous_response_id` deltas, MAF
tool-loop continuation with JSON-aware argument matching, inline PNG images,
completed usage, visible stream failures, and cancellation after output begins.
Supported Codex models now declare grammar tools as Responses custom tools;
WebSocket and SSE custom-input events map into MAF function calls, custom call
outputs replay correctly, and switching to a standard Responses provider sends
ordinary function items. Non-streaming Responses calls now aggregate the
Codex SSE/WebSocket stream for compaction summaries, preserving summary text,
response ID and usage; the SSE fallback also skips long runs of empty frames
iteratively.
Resumed requests carry the saved Codex session ID as private provider metadata
while MAF still sends the saved user and assistant history in `input`; the
focused test also checks refreshed credentials and session headers. The llama
router keeps models available when optional `/props` requests fail, bare model
names switch to exact active catalog items, and resuming a session restores its
saved llama model. Normal interactive llama fixtures now execute a local
`read` tool and continue with its result; another fixture cancels after visible
streamed text, settles the run and accepts a later prompt without replay. Core
fixtures verify steering reaches the first request, compaction retains trailing
steering after a completed tool result, retry restoration failures settle, and
RPC validates a replacement provider before cancelling the active run. Recent
session discovery skips a malformed newer session whose selected head is
missing and continues to an older valid session. The project session picker
toggles recent/name sorting with Ctrl+S and Ctrl+R renames the selected session
without losing search, selection, or persisted title. The CLI handles
`--provider llama.cpp --list-models` before startup model selection, so a
dynamic router catalog can be listed without selecting a model. A live router
rerun returned 19 selectable models, including the loaded
`NVIDIA-Nemotron-3.5-Bolt-74B-A9B-24GB-GGUF`; see the compact
[discovery receipt](parity/fixtures/llama-live-model-discovery-2026-10-16.json).
The deterministic Codex acceptance fixture now composes fake device-code
subscription login, model selection, the real MAF `read` tool loop over the
Responses WebSocket, steering during streamed output, session persistence, and
resume through a fresh runtime/client. A second Codex fixture exercises manual
compaction through Responses, then continues with the projected summary and
persists it in the session. An unauthenticated Codex model now opens the TUI
instead of trying to parse the placeholder credential as a JWT; the client
rejects any request before transport with a `/login openai-codex oauth` hint.
A Linux PTY fixture verifies the Codex browser/device choices and cancellation.
The combined Codex/llama focused acceptance filter passes 32/32 with zero skips;
format verification and the warnings-as-errors solution build pass. The
configured local router was reachable during this checkpoint: the opt-in live
Qwen3.8-27B-GGUF read-tool continuation and post-visible-text cancellation
fixtures pass 2/2. A live Codex account was not available, and live-server
manager/TUI load-switch-resume was not exercised; the goal permits deterministic
fixtures for these acceptance contracts. These live-only checks remain useful
future evidence, but they do not block this bounded milestone.
The llama Linux PTY acceptance now composes interactive `read` tool execution,
tool-result continuation, clean save/exit, fresh-process `--continue`, and a
successful follow-up using restored history. The llama manager/TUI test class
passes 11/11 with zero skips; the cross-model resume fixture waits for the
completed assistant turn before sending exit input.
Exact-head Linux CI on product source `92fccccc5604f3601a25f84796755208d147efc3`
passes restore, format, warnings-as-errors build, Node/Python/docs checks, MCP
conformance and 2,091/2,091 .NET tests with zero skips
([push run 38837170138](https://github.com/PhilipGB/pisharp/actions/runs/38837170138),
[pull-request run 38837172889](https://github.com/PhilipGB/pisharp/actions/runs/38837172889)).

All 194 capability IDs remain in the global inventory at **12 verified, 148 in
progress, and 34 not started**. These global parity counts do not measure this
milestone. Keep existing statuses and the deferred provider backlog intact.

## Daily Driver Core acceptance

The bounded acceptance scenarios are covered and pass:

- **Codex subscription:** deterministic device-code sign-in, model selection,
  streamed Responses/WebSocket output, MAF tool execution and result
  continuation, steering, cancellation/error handling, compaction, session
  persistence and continuation through a fresh runtime/client.
- **llama.cpp:** a Linux PTY fixture selects a local model, streams a response,
  uses `read`, continues from the tool result, saves and exits, resumes through
  `--continue`, and completes a follow-up using restored history. Separate
  fixtures cover cancellation, manager load/unload, model switching and
  cross-model session resume. Live Qwen inference passed tool continuation and
  cancellation against the configured local router.
- **Shared behavior:** fixtures cover queued steering, compaction with trailing
  input, durable sessions, model replacement validation and terminal cleanup
  after interactive exit.

The llama router manager/TUI class passes **11/11** with zero skips. Full
milestone verification is green on the product source above. Wider current-Pi
differentials and non-core resources/MCP/Codemode workflows remain in the global
inventory; broad secondary-provider parity stays deferred. Do not resume those
tasks under this completed milestone. Preserve `test.pdf` and unrelated files.
See the receipts in [the feature matrix](parity/feature-matrix.md) and the
execution ledger.

## Prior integrated queue checkpoint — 2026-10-15

Queues 1–12 had integrated implementation slices at the source checkpoint
above. Thinking budgets were verified. Google GenAI/Vertex matched six pinned
transcript-conversion cases. Azure Responses, Azure Foundry, and Mistral had
expanded local transport coverage; their broader provider contracts remain in
the global inventory. These results are historical evidence and do not reopen
the completed Daily Driver Core milestone above.

## Historical user-requested stop — 2026-10-15 (superseded by this resume)

This prior stop record is preserved as history. Its queue-3 unintegrated state
and stop instruction were superseded when the user resumed parity work.

Base SHA: `21d5e47e5a4b2cf084ed01b0a0ee7cca9efbc538`. The user cleared the
goal and requested **stop at the next suitable point**. Both specialists are idle;
no further task or integration is authorized by this checkpoint alone.

Queue3 is **in progress, unintegrated** on local branch
`worker/003-package-cli-a2`, committed through
`ea44cb08cbda35c2e2d152709a532192a13e82f0`. Preserve its clean external worktree
and private profile at `/tmp/pisharp-sol-orchestration-20261015/pisharp-workers/003-package-cli-a2/`.
If temporary storage disappears, recover the committed work from the branch;
do not classify this attempt as an empty preparation or discard its commits.

Luna's final bounded focused run passes **20/20, zero skips**; root inspected
the log but has **not** independently rerun it or accepted the final commit.
Package command/settings/store behavior and native process evidence are committed.
The reusable real Pi/PiSharp package CLI differential, full resource invocation
proof, compact parity receipt, final root review, integration and verification
remain unfinished. Self/model update targets remain later queue work. See
[the stop receipt](parity/fixtures/package-cli-user-stop-2026-10-15.json).

Main product source remains `a33e556b`; last full verification remains
**1,997 .NET / 24 Node / 50 Python**, six architecture checks and MCP **261/8
classified/no regressions**. Counts remain **10 verified / 146 in progress /
38 not started**; no promotion or queue3 completion. Serialized mode, both gates
not run, unchanged fingerprint, zero milestones since full verification and no
advisor escalation. Last inspected exact-head CI for pre-stop main `21d5e47e`
is successful, run **38756890943**. The checkpoint below describes the prior
startup; this stop record supersedes its active-work instructions.

## Historical Sol orchestration checkpoint — 2026-10-15

Base SHA: `a33e556b1fd88e5c935c9346ad286608f99f7384`; starting handover SHA:
`77caa3290a0300e2e02d61039b306260f127872a`. Queues **1 and 2 are completed**;
next is **3, package install/remove/update/list CLI workflows**. The package
milestone covers tasks1–3. Capability counts remain **10 verified / 146 in
progress / 38 not started**. Package CLI, configured npm commands and remaining
registry/legacy package resolution proof are required; no capability promotion.
No unresolved P0/P1 is established.

Queue2 authorship: **Luna** implemented worker `204f3f9b`; **Root** reviewed the
exact commit, squash-integrated and independently reran verification. Root made
no production/test/harness changes after integration. See [the Git receipt](parity/fixtures/managed-git-rpc-process.json).
The real CLI/RPC pair matches two provider requests, installed skill/template
expansion, native extension startup, lifecycle counts and peer omission. A direct
Pi API update probe and native PiSharp store tests establish unpinned v1-to-v2
refresh. Native tests cover unchanged pinned refs, cached startup without fetch,
project delta metadata, failed dependency/fetch recovery, and child cancellation.
This evidence does not claim a package update CLI or Git-specific reload invocation.
Queue1 remains **Luna** implementation plus documented root integration corrections;
see [the npm receipt](parity/fixtures/managed-npm-local-tarball-2026-10-15.json).

Root focused verification passes **102** tests. Four real RPC/provider regressions
match: managed Git, managed npm, deleted skill and local package. Full verification
with both specialists idle and worker processes exited passes restore, format,
zero-warning build, **1,997 .NET / 24 Node / 50 Python**, zero skips, six architecture
checks, documentation paths, clean pinned reference, and MCP **261 passing checks /
eight classified outcomes / zero regressions**. Default HOME/config/temp/session
profile is preserved; fixtures own npm caches. The receipt records the tested source
digest and base/worker SHAs. Milestones since full verification: **0**.
Raw root logs remain under `artifacts/parity/002-managed-git-root/` (Git ignored).

Concurrency stays **serialized**, both gates not run. Fingerprint: Linux x86_64;
.NET 10.0.112, Node 24.21.0, Python 3.14.4; 24 CPUs / 32 GiB memory class;
profile base `/tmp/pisharp-sol-orchestration-20261015`. Queue2 reported and removed
a Python cache created inside its worker worktree without the assigned profile;
no default-user-location write is established and no gate is certified. Queue1 and queue2 worker states were removed after publication; reconcile actual
worktrees/branches on resume. No advisor escalation.

Latest inspected exact-head CI is green at product `a33e556b`, run
[38726521422](https://github.com/PhilipGB/pisharp/actions/runs/38726521422).
Queue2 worker state is removed. The temporary filesystem and prior specialists
were refreshed after publication; no implementation was lost. The missing
read-only queue3 preparation a1 was explicitly abandoned and its Git registration
and private branch removed with guards. Queue3 now uses **worker/003-package-cli-a2**
at product `a33e556b`, with a new external worktree and full private profile under
`/tmp/pisharp-sol-orchestration-20261015/pisharp-workers/003-package-cli-a2/`.
Both required specialist probes and isolated SDK/push preflight passed again.
Toolchain fingerprint is unchanged; continuity uncertainty keeps serialized mode.
Push with per-command `GIT_SSH_COMMAND='ssh -F /dev/null'`; public GitHub REST
provides CI reads (`gh` is unauthenticated).

Configured Pi remains `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` at
`/tmp/pisharp-luna-pi`. The pinned clone, dependencies and ignored provider model
data were restored. Normal source CLI version probe now passes, and permanent
reference validation confirms unchanged tracked source. The initial missing-data
probe failure and repair are recorded in [the resumed checkpoint](parity/fixtures/sol-orchestration-resume-task3-2026-10-15.json).
Luna audited eight further commits through `5ab7fe74c400097b386662205cd93900695b8a71`;
root reviewed the provider image patch/current owners. Anthropic/Claude Bedrock
error-result image placement is required P2 for queue43. The Kimi revert/restoration
has no net protocol delta; test-only changes, Chord/Durable and the Nix wrapper
receive their bounded classifications. No P0/P1 interrupts queue3. The earlier
[startup review](parity/fixtures/sol-orchestration-upstream-startup-2026-10-15.json)
retains retry/off-thinking P2 work for tasks43/44. No pin advance; final freeze/audit
remains required. Full parity is incomplete; continue after each task and milestone.

## Sol-to-Luna handover — 2026-10-15

This checkpoint supersedes the older next-action paragraphs below. This Sol goal
ends with the verified, pushed handover; the continuing Luna goal owns full parity.
Do not spend another session extending YAML/resource permutations without a
supported observable failure. Read `sol_luna_handover` in
[the execution ledger](parity/execution-ledger.json) before implementing the queue.
It contains 45 ordered tasks, each with capability IDs, behavior, actual Pi source
and test paths, PiSharp owners, the established pattern, focused and differential
verification, and an explicit completion condition. All owner/source paths were
checked against this worktree and the configured Pi checkout.

### Repository and evidence

- Branch: `parity/current-pi-completion`; starting SHA
  `cccbf3e069a3e98f5703709df5beec1363eb1c8e`.
- Merged architecture-refactor main `22eeb2a53dbaa94e41f78a40a997e3b3156bb3ed`
  is an ancestor; no main merge/rebase was needed. Starting exact-head CI
  [38658864333](https://github.com/PhilipGB/pisharp/actions/runs/38658864333) passed.
- Configured Pi and audited cutoff:
  `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. A fresh remote read still returns
  that SHA. [The 24-commit audit](parity/fixtures/luna-handover-2026-10-15/upstream-audit.json)
  classifies nine out of scope, two no behavior change, two matched P1 repairs,
  and eleven deferred P2/P3 changes. Advancing the reference selects the target;
  it does not claim those deferred changes are implemented.
- Local validation: restore, format and zero-warning build; .NET **1,976 passed,
  zero failed/skipped**, Node **24**, Python **50**, six architecture checks;
  MCP **261 passing checks / eight classified failures / zero regressions** over
  26 scenarios. Focused native checks: 130. Pi source suites: 35 provider and
  106 resource/settings/trust tests, zero skips. See
  [the verification receipt](parity/fixtures/luna-handover-2026-10-15/verification.json).
- Verified/pushed product checkpoint: `df163a4704f3a498f40046ae6a289b870a2b8af9`. Exact-head CI
  [38664105111](https://github.com/PhilipGB/pisharp/actions/runs/38664105111) passes both jobs.
  [The publication receipt](parity/fixtures/luna-handover-2026-10-15/publication.json)
  records the thirteen-item stopping audit. Resolve the branch ref for the final
  docs-only receipt commit; product source/tests/tools are unchanged by it.
- Reviewed [contract evidence](parity/fixtures/luna-handover-2026-10-15/contracts.json)
  is committed; generated captures and TRX remain under
  `artifacts/parity/luna-handover-2026-10-15/` and are not durable backups.

### Capability closure

The required inventory remains **194 IDs: 10 verified, 146 in progress, 38 not
started**. Three implemented contracts are now deliberately closed:

- `feature-resources-instructions-and-trust`: instruction hierarchy/read recovery,
  trust before privileged project selection, declined selection, and configure-once
  pre-trust extension reuse. Fifteen current instruction cases, nine actual
  provider-request trust cases, current extension ordering and native boundary
  tests support the original contract. Full-screen trust selector styling remains
  a separate TUI P2, not a missing trust decision.
- `settings-defaultprojecttrust`: global-only default/invalid fallback,
  saved/explicit precedence and inability of project configuration to grant its
  own trust. The old pre-authentication probe was too weak; the current process
  probe requires successful HTTP plus the independently expected project prompt
  expansion in all nine cases.
- `settings-enableskillcommands`: defaults/precedence, legacy migration,
  live completion, manual invocation, persistence and reload. The current paired
  reload and same-Pi calibration match 14 rendered frames with exact normalized
  HTTP and zero scenario errors; raw byte differences remain explicit.

`feature-resources-skills-templates-themes`, packages and extension API remain in
progress for actual missing theme/package/public API behavior. Required managed
npm/git and package CLI workflows are feature work. All 38 not-started IDs still
lack required behavior; none was promoted from a plan or metadata alone. The
ledger records exact closure contracts, implementation, remaining work, evidence
and deferred priorities. A capability is not held open merely for hypothetical
parser or visual subcases.

### Major family status

| Family | Verified | In progress | Not started | Deferred |
| --- | --- | --- | --- | --- |
| Agent execution/context | 0 | 3 | 0 | None newly recorded; required feature gaps remain in the ledger. |
| Providers/models/auth/routing | 5 | 14 | 6 | P2 audited endpoint/profile/catalog/cache/model-selection/stream controls; two Google off request differences. |
| Tools/loadouts/results | 0 | 10 | 0 | None newly recorded; required feature gaps remain in the ledger. |
| CLI/commands/security | 0 | 20 | 1 | None newly recorded; required feature gaps remain in the ledger. |
| TUI/editor/input | 0 | 11 | 3 | P2 trust selector presentation and historical calibration/timing limitation; P3 read range display; P4 raw-only bytes. |
| Sessions/persistence | 0 | 2 | 0 | None newly recorded; required feature gaps remain in the ledger. |
| Resources/packages/trust | 1 | 2 | 0 | P2 empty quoted template argument tokens; P3 repository github-work asset. Required managed package and complete theme/resource behavior remain feature work. |
| Extensions | 0 | 3 | 0 | None newly recorded; required feature gaps remain in the ledger. |
| RPC | 0 | 42 | 0 | P3 legacy reload assertion diagnostic shadowing is a harness finding; future RPC argument-error serialization remains explicit feature work. |
| Settings | 3 | 37 | 27 | None newly recorded; required feature gaps remain in the ledger. |
| Codemode | 0 | 1 | 0 | None newly recorded; required feature gaps remain in the ledger. |
| MCP | 1 | 1 | 1 | No encountered P0/P1. Existing six elicitation + CIMD + scope-limit client result are classified scope differences; configured CIMD is feature work. |

### Fixed P1 boundaries and representative patterns

Completed tool arguments previously used the partial progress parser. Malformed
final JSON could execute its parsed prefix. Core `ToolArgumentJson.ParseComplete`
now requires a complete JSON object after Pi-compatible string-escape repair;
CLI `ProviderToolArgumentChatClient.Normalize` supplies empty arguments and a
parsing exception on failure. Both SDK response paths and the native Bedrock
adapter use that shape. MAF owns function rejection and continuation; tests prove
zero function invocations. RPC progress still uses `ParseStreaming`. Five SDK
fail-first rows and Bedrock regressions protect the boundary. A current Pi pair
matches 175 partial inputs and 18 completed provider calls, including malformed
objects, non-objects, repaired escapes and ordinary valid calls.

Google and Vertex now share Gemini 4+ `thinkingLevel` selection instead of sending
a rejected token budget. Corrected fail-first requests and six enabled paired
controls establish the fix. Two off-thinking controls remain an explicit **P2**
wire discrepancy; the reviewed report says `match:false` for the full set. Do not
silently generalize enabled-control evidence to all thinking behavior.

Use these existing examples:

| Surface | Pattern to repeat |
| --- | --- |
| Providers | `ProviderToolArgumentChatClient`, `ProviderToolArgumentRepairTests`, native `BedrockProviderTests`, `google-thinking-differential.py` |
| Resources | `ResourceFileDiscovery`, `ResourceFrontmatter`, `ResourceCatalog`; `resource-selection-differential.py` plus a real RPC/provider input |
| Process | `default-project-trust-process-differential.py`: successful request plus independent expected outcome, not common failure classification |
| TUI | `terminal/run.py`, paired and same-Pi calibration, `legacy-project-skills-reload.json`, semantic assertion and direct paired report inspection |
| RPC | `RpcQueueModeCommandHandler`, `RpcSessionCommandHandler`, `rpc-settlement-differential.py`, current active bindings |
| MCP | `McpClientConnection`, `McpToolPublication`, `McpResourcePublication`, pinned conformance and per-check classifications |

### Architecture decisions Luna should preserve

Production remains **CLI → Runtime → Core**, using installed **MAF 1.21.0 /
MEAI 10.10.0**. The invoked framework skill is a newer snapshot; installed
metadata and the compiled application are authoritative. No framework upgrade or
new generic infrastructure is needed for this queue.

| Family | Ownership, state, lifetime and contract |
| --- | --- |
| Agent/session | MAF owns the function loop. `ConversationRun` owns canonical appends, prompt queue and settlement. `ConversationSession`/tree is durable; MAF sessions are disposable branch projections. Preflight/save replacements before cancelling outgoing work, settle before publishing new active bindings. Preserve unknown-side-effect recovery and stale-writer refusal. |
| Current state | Read `ActiveModelRuntime`, `ActiveProjectRuntime`, `ActiveConversationRuntime` at callback invocation. Reload/model/session transitions publish explicit bindings. Do not capture stale agents, tools, project settings or stores in new callbacks. |
| Providers | CLI owns wire protocols/auth/transcript mapping; Runtime owns common execution/metadata/usage. Retry before streamed output only; malformed arguments remain errors through MAF and mapping. Keep provider endpoint identity separate from the display profile. Kimi at this cutoff uses Chat Completions `/coding/v1`, OpenCode uses inference endpoints and per-model APIs. |
| Sampling/model references | Add a pure Runtime sampling merge over descriptor defaults, exact clamped level and request overrides. A CLI chat-options wrapper handles both response paths; provider adapters map fields. Carry the exact selected level rather than reversing a lossy MEAI effort. A concrete CLI model-reference resolver collects candidates before choosing authenticated named-provider/gateway identity; shared resolver is used by CLI/scopes/auth/model commands. |
| Managed packages | Add one concrete Runtime managed package store for npm/git process/cache lifetime, cancellation and resolved roots. Feed roots into existing package resource owners. One CLI package command owner handles scopes/persistence/UI; no generic registry. Source/ref identity, lexical provenance and canonical file identity are distinct. Match Pi's supported package execution policy from source; use local tarball/git fixtures. |
| Resources/extensions | Existing discovery/frontmatter/path/package owners retain parsing/projection. User/global/CLI extensions configure once before trust, project privilege follows trust, final selection controls order/metadata. Catalog owns extension resources/disposal. Reload replaces discovered resources and refreshes active callbacks; failed registrations roll back checkpoints. |
| MCP | SDK owns protocol transport. Runtime owns naming/exposure, server+URL credential identity, publication/reconnect and startup waits. Catalog owns connections; named indirect operations wait only for the reached server. Never retry an uncertain tool side effect. Keep resource/publication lock order and callback client-identity checks. Add CIMD through actual installed SDK support or a narrowly reproduced adapter. |
| Codemode | Runtime owns nested pipeline, branch-local store, result/usage contract and Node worker admission. Request termination promptly; release admission only after actual exit. Cancel/dispose work with the owning run. UI details do not replace model-facing results. |
| TUI/settings | CLI owns settings parser/writer, terminal input/layout/modes/dialogs; Runtime owns original content/execution. Use current immutable snapshots per frame. Restore terminal modes/subscriptions on exit; initial screen renders before replies. Add settings only when a real runtime/render/input consumer exists. |
| Codex transport | One concrete connection owner keyed by account+session handles serialized socket work, bounded handshake and disposal; SSE fallback is allowed only before output. Canonical app history remains authoritative; cached response IDs never become its persistent format. |

Only changed areas were reviewed. Six architecture regression tests pass; no
broad architecture redesign was performed or is needed. The concrete strict tool
boundary and Google shared request mapping prove representative implementations;
45 queue tasks below specify the remaining shapes instead of empty scaffolding.

### Ordered mechanical queue

The complete task records are in `sol_luna_handover.queue`. Each row below names
its place in that queue; execute feature tasks before deferred cleanup. When a
family has variants, prove the difficult and ordinary case then repeat the proven
adapter/persistence/test pattern. Do not redesign project boundaries.

| Order | Work | Priority |
| --- | --- | --- |
| 1 | Install a managed npm package and expose its declared skills/prompts/themes/extensions in startup and reload. | feature |
| 2 | Resolve pinned git packages and update only sources eligible for update. | feature |
| 3 | Implement package install/remove/update/list CLI workflows and permitted configuration scope. | feature |
| 4 | Resolve extension name conflicts and replaceable builtins without discarding unrelated owners. | feature |
| 5 | Expose native event/context/UI equivalents for supported extension actions and runtime registrations. | feature |
| 6 | Resolve model defaults, clamped effective thinking-level sampling and request overrides before outbound serialization. | feature |
| 7 | Persist thinkingBudgets and send bounded budgets where the API supports token budgets. | feature |
| 8 | Complete Google/Vertex image, reasoning-signature, tools, usage and error differentials. | feature |
| 9 | Apply the completed-argument/tool-replay pattern to remaining native provider boundaries and establish provider breadth evidence. | feature |
| 10 | Add Foundry Chat Completions and finish Azure response wire contracts. | feature |
| 11 | Complete native Mistral Conversations beyond current Chat Completions-compatible slice. | feature |
| 12 | Close Radius catalog/auth and interactive setup behavior. | feature |
| 13 | Support the distinct ChatGPT sign-in route and Codex manual/device fallback without borrowing API-key credentials. | feature |
| 14 | Implement Codex WebSocket and cached continuation with pre-stream SSE fallback. | feature |
| 15 | Implement missing provider profiles using each current upstream protocol, starting with Kimi and OpenCode. | feature |
| 16 | Add Cloudflare gateway and Together catalog metadata as established profile/adaptor variants. | feature |
| 17 | Preserve Responses custom/grammar tool declarations and replay. | feature |
| 18 | Align published schema fields with runtime parsing and public discovery. | feature |
| 19 | Apply fullscreen/regular mode, exit output, scrollbar/copy and wheel preferences. | feature |
| 20 | Apply editor/output geometry and completion/cursor preferences. | feature |
| 21 | Complete terminal image, link, progress and resize controls. | feature |
| 22 | Complete tree navigation/filter and branch summary policy. | feature |
| 23 | Implement visible cache miss notices and provider cache warming policy. | feature |
| 24 | Implement remaining startup/privacy/diagnostic settings using scoped concrete owners. | feature |
| 25 | Add actual Mermaid rendering at the Markdown presentation boundary. | feature |
| 26 | Implement configured MCP Client ID Metadata Document registration with OAuth fallback. | feature |
| 27 | Complete interactive MCP management using current config and connection owners. | feature |
| 28 | Finish Codemode output envelopes, model/classifier calls and usage/termination semantics. | feature |
| 29 | Close route context budget, physical usage attribution and classifier persistence contracts. | feature |
| 30 | Close narrow RPC command contracts by comparing real supported idle/active/error paths. | feature |
| 31 | Complete protocol read/event projection for provider partial content and argument errors. | feature |
| 32 | Close persistence/replacement contracts and platform evidence. | feature |
| 33 | Close ordinary built-in tool observable contracts and live default-tool reload. | feature |
| 34 | Close agent/queue/retry lifecycle and settlement contracts. | feature |
| 35 | Close compaction and context/usage projection. | feature |
| 36 | Close CLI entry modes and flag/error/output contracts. | feature |
| 37 | Finish share/HTML export output and theme/resource rendering contracts. | feature |
| 38 | Close skills/templates/themes/settings resource family while preserving verified instruction/trust behavior. | feature |
| 39 | Close editor/keymap/input flow semantic parity. | feature |
| 40 | Finish user-visible provider login copy/cancel and supported interactive effects. | feature |
| 41 | Implement supported application install/update commands with bounded process ownership. | feature |
| 42 | Unify ambiguous bare and gateway-qualified model reference selection. | P2 |
| 43 | Import deferred provider metadata/cache/retry/reasoning deltas at audited cutoff. | P2 |
| 44 | Resolve explicit off-thinking and empty-quoted template argument discrepancies. | P2 |
| 45 | Resolve supported residual presentation and repository asset differences after features. | P3 |

### Deferred findings and verification boundaries

There were no P0 findings. Both encountered P1 defects are fixed and verified.
The upstream audit assigns every in-scope remaining delta P2/P3. P2 includes
isolated model-reference policy, cache/metadata/retry/streaming controls, missing
profile variants, empty quoted template arguments and the explicit Google off
mapping. Feature tasks that implement whole missing families precede those
isolated deltas. P3 includes read-range presentation, the repository github-work
asset and an assertion helper's local-variable shadowing. P4 raw-only terminal
bytes have no demonstrated supported visible consequence and are not scheduled
for implementation.

The legacy-skill assertion helper overwrites its local pair case with calibration
when `--calibration-report` is supplied. Its semantic comparison is correct, but
reported pair diagnostics then come from calibration. This receipt avoids that
option and independently reads both report files; fixing that P3 helper is deferred.
Earlier quiet-startup, source-only/resize calibration and timing failures remain
retained and unexplained. Current passing checks do not retrospectively resolve
them. Investigate only when a current supported failure changes priority.

MCP's eight classified failures remain six unadvertised elicitation checks,
CIMD default-registration behavior and the deliberately unsuccessful scope retry
limit client result. The limit assertion passes. No encountered MCP P0/P1 blocks
handover; configured CIMD is explicitly queued feature work. Native macOS/Windows,
paid providers, full provider breadth and complete TUI parity are not established
by this Linux run. Full final parity source audit remains a future Luna gate;
the Sol upstream delta is nevertheless completely bounded and classified.

The shared workspace required writable NuGet caches under `/tmp`; use the
recorded environment in the verification receipt if the sandbox is still active.
`git -c core.sshCommand='ssh -F /dev/null'` bypassed a host SSH include ownership
error without changing host configuration. Do not inspect or alter `test.pdf`.
Explicit staging paths exclude it and all unrelated user files.

Luna readiness: the next 45 tasks have concrete ownership, state/lifetime,
persistence/cancellation rules, source references, representative examples and
verification/completion conditions. Stop architectural exploration and execute
the queue. The product checkpoint is pushed with exact-head CI green; the publication receipt records the stopping audit. **READY FOR LUNA HANDOVER**.

---

## Current post-merge checkpoint — 2026-10-15

PiSharp merged `main` is `22eeb2a53dbaa94e41f78a40a997e3b3156bb3ed`; exact-head Linux CI [38427397999](https://github.com/PhilipGB/pisharp/actions/runs/38427397999) passes. Work continues on `parity/current-pi-completion`. The major architecture refactor and composition review are complete; only the bounded final architecture regression review remains. Production dependencies remain CLI → Runtime → Core, with MAF 1.21.0 and Microsoft.Extensions.AI 10.10.0.

Fresh local baseline: restore/format/warnings-as-errors build pass; .NET 1,390/1,390 with zero skips, Node 24/24, Python 47/47, documentation 131 documents / 457 references, MCP with no regressions against eight classified failures. All 17 existing process differential drivers ran; the classifier probe required valid boolean criteria and updated item extraction, then its billing/authority comparisons passed. This probe retains Pi output items and does not verify the open Codemode formatting contract. All 33 terminal fixtures ran at one explicitly selected case each: initial calibration 30/33 and pairs 24/33; seven repeats/corrections retain all failures. The harness teardown and Markdown input defects are fixed; Python terminal tests now pass 48/48. Product presentation and one thinking-toggle HTTP mismatch remain open.

The baseline comparisons used `ee409a85f6ad92bf8c6ab8ed735c6cd24f2e749e`. The deliberately advanced configured Pi reference is `3daa20280f6a7c7cbe0935680c9a38fdc3863005`: all 50 new commits are classified (19 out of scope, 13 no behavior change, zero needing work, 18 matched contracts). The audited delta is reconciled; resume Resources / Instructions / Trust and every remaining ledger requirement. The latest fully verified presentation checkpoint passes 1,934 tests with zero skips and a zero-warning build; eighteen named current-source contracts now match. Resources work has resumed: fifteen instruction-file loading/order/warning comparisons match; twenty-seven trusted system/append/CLI-input comparisons and four real CLI/provider selection cases now pass. Fourteen skill/template text/recovery comparisons and four CLI/provider expansion cases pass. Nineteen explicit-path comparisons and two named-pipe CLI/provider controls now pass. Ninety-eight frontmatter/typed-metadata/warning comparisons and two invalid-YAML CLI/provider recovery cases pass. Full discovery, remaining YAML edge/error cases and terminal presentation remain open; both retained CI timing failures now have focused repairs: five terminal fail-first regressions, two RPC fail-first barrier regressions, six matching fullscreen checkpoints, and ten repetitions of each original process test. Both focused repair commits pass exact-head Linux CI (38532839354 and 38533106677); five immediate persisted RPC turns also match Pi. Seventeen directory discovery comparisons match, including 1,002 skills and root declaration stopping; sixty ignore-file/order/pattern comparisons now match too. Discovery source 0eb7b114 passes exact-head CI 38535070869. Thirty-two full-loader canonical/validation/collision/source/configuration/ancestor/extension comparisons now match, with 17 discovery/60 ignore regressions retained. Ignore source 9bf391b900 passes exact-head CI 38538502785. Selection source 0672b794 passes exact-head CI 38546090541. Forty-seven skill/template invocation and error-recovery comparisons plus a real RPC/provider deletion replay now match; 49 focused native tests cover recovery, all three input commands, JSON stderr and cancellation. Invocation source 96fa481f33 passes exact-head CI 38554053384. Typed prompt hints and the repository dup prompt now match 131 loader comparisons and 15 fullscreen render frames with provider traces; 88 focused checks cover metadata, trust/discovery, reload and suggestion columns/newlines. Hint source 42ae67c471 passes exact-head CI 38556219134. Explicit YAML scalar tags now match 181 paired cases plus a real RPC/provider input; 60 focused checks pass. Scalar source 6cec6f7104 passes exact-head CI 38557804529. The expanded YAML document/key/alias/error corpus now matches all 311 cases and a real malformed-flow RPC/provider recovery; 96 focused checks pass. Newly found port retry and inherited dumb-terminal fixture failures are separately repaired at 79b2b5e4f7 with deterministic reproduction, isolated 1850-test verification and exact CI 38564664115. Invocation-error terminal presentation now matches six fullscreen frames in two pairs and same-Pi calibration, with six native rows and 50 Python checks; source-only calibration stalls and active-resize calibration remain unresolved. See [skill-error evidence](parity/fixtures/skill-invocation-error-terminal-current-pi-2026-10-14.json). Wider resources/trust/reload remain open. Remaining settings/package/ignore/YAML/error edges, remaining tag/key/error metadata and terminal/trust/reload presentation stay open. All 194 capabilities remain required: 7 verified, 149 in progress and 38 not started; schema publication now has bounded artifact evidence with runtime alignment open. Local package skill/prompt loading now matches 83 full-loader cases, including source whitespace, file URLs/error messages, separator identity and canonical/configured controls, plus URL/padded/original real RPC/provider replays; the expanded native suite passes 1,962 with zero skips. Nested extension declarations and hidden/ignore/node_modules visibility now match eleven normal CLI flow regressions and the 95 resource loader cases. Extension directory index precedence now matches one configuration rather than duplicate sibling loading, with seven normal CLI flow regressions retained. Local package extensions now match six normal CLI/bootstrap/provider comparisons across manifest/convention/file/root/disabled/no-extensions cases; package discovery/alias/error/reload/project overrides and managed/CLI operations remain open. Local package themes now match twelve availability/palette comparisons plus the 83 skill/prompt regressions and actual CLI selection; the initial fullscreen pair exposes a non-Kitty keyboard fallback/render difference. The separate keyboard/startup repair now matches two four-frame package-theme pairs and the original 15-frame Kitty hint regression; relocated-workspace context-footer differences remain retained. Retained process timing failures have separate diagnostics/scheduling mitigation; the exact quiet-startup failure remains unexplained. Complete package extension/theme metadata/errors, managed npm/git and package CLI operations remain open. Full parity and the fresh final source audit remain incomplete. Keep `test.pdf` and unrelated user files untouched.

Whitespace source `bbb0a21606` failed exact CI 38618979075 in the startup-flags process fixture (1,910 passed / one failed / zero skipped). A stalled model-catalogue body deterministically reproduces that fixture timeout. Explicit offline scope and isolated configuration preserve all original persistence/conflict assertions and the 12-second deadline; eight concurrent recovery runs pass with zero catalogue requests, and full local verification passes 1,911 with zero skips. See [startup network isolation](parity/fixtures/startup-flags-network-isolation-2026-10-14.json). Exact-head timing repair CI 38622219705 passes both jobs at 4b3837b19a (1,911 .NET tests, zero skips). The exact historical CI schedule and earlier quiet-startup/source-only calibration failures remain unproven; passing resource runs do not resolve them.

Package theme head `b1271ad986` failed exact CI 38625569590 in the unchanged six-image Codemode loop (1,926 passed / one failed / zero skipped). A controlled cleanup-pool barrier exposes native deferred termination while Pi requests termination before returning. The separate repair requests termination immediately, retains prompt settlement and release only after actual exit, and preserves the two-worker limit. Eighty-eight focused checks, all six lifecycle/admission comparisons and ten original image-test repetitions (60 images) pass. See [Codemode shutdown evidence](parity/fixtures/codemode-shutdown-order-2026-10-14.json). Exact-head shutdown repair CI 38629279578 passes both jobs at 76c7a01e68 (1,927 .NET tests, zero skips). The exact historical CI schedule remains unproven; unrelated green resource runs are not used as resolution evidence.

Extension failure source audit separates SDK recovery from CLI startup: the SDK discards partial registration and keeps valid siblings, while the normal CLI refuses startup with error diagnostics, exit 1 and a -ne hint. Configuration, missing explicit paths and a native dependency resolver each fail first; the candidate now matches thirteen CLI flows and a direct SDK comparison, with 76 focused checks and 95 resource regressions. See [extension error evidence](parity/fixtures/resource-package-extension-errors-current-pi-2026-10-15.json). Full local verification passes 1,954 tests with zero skips, a fresh zero-warning build, format and docs validation; wider conflicts/builtins/aliases/reload and managed package/CLI workflows remain open.

See [the current refresh](parity/fixtures/current-pi-refresh-2026-10-13/README.md) for source findings, plans and evidence scope.

Explicit extension file URLs and canonical aliases now match sixteen normal CLI flows, including padded-path rejection and complete diagnostics. Canonical deduplication retains the first lexical path, with package provenance for exact package-file overlap and CLI provenance for a distinct alias; three actual full-loader metadata pairs and 58 focused native checks support the distinction. The final suite passes 1,957 tests with zero failures/skips, a fresh zero-warning build, format, documentation, Node 24 and Python 50 checks. See [extension path evidence](parity/fixtures/resource-package-extension-paths-current-pi-2026-10-15.json). Configured paths, broader aliases/collisions/reload/project overrides and managed package workflows remain open; no capability is promoted.

Final extension selection after the pre-trust pass now honors trusted-project package disabling and refreshes enabled preloads to project metadata while keeping untrusted-project controls and configured instances. Two native failures before repair and one control are retained; three actual full-loader selection/metadata cases and eighteen normal CLI/provider flows now match. Focused 26 and full 1,960 native checks pass with zero failures/skips, a fresh zero-warning build and format verification. See [pre-trust selection evidence](parity/fixtures/resource-package-extension-pretrust-current-pi-2026-10-15.json). Final module ordering/conflicts, wider filters/aliases/reload and managed package workflows remain open; all 194 capabilities remain required.

Final extension ordering now puts a new project module before retained personal modules while configuring each once. A paired full-loader comparison and the real Pi ExtensionRunner expose both module and resource-discovery callback order before repair; one native failure and an untrusted control are retained. The repaired pair, prior three selection cases and eighteen normal CLI/provider regressions match. Focused 28 and full 1,962 native checks pass with zero failures/skips, a fresh zero-warning build and formatting checks. See [extension ordering evidence](parity/fixtures/resource-package-extension-order-current-pi-2026-10-15.json). Conflicts, replaceable builtins, dynamic registrations, wider filters/aliases/reload and managed package workflows remain open; whole capability statuses are unchanged.

## Historical checkpoint — 2026-10-11

Pi `origin/main` is `04566631c2b51fb688486bc1f040f60d1518c126`; PiSharp `origin/main` is `d8e5411be475a0f4bc3bf3458bc46bdbc8fd40f9`. Exact-head Linux CI [38160159991](https://github.com/PhilipGB/pisharp/actions/runs/38160159991) is green for the current PiSharp main: Node 24/24, Python 37/37, format verification, warnings-as-errors build and .NET 1,289/1,289 with zero skips. The local POSIX matcher change is also verified: 34 focused matcher tests, format, zero-warning build, Node 24/24, Python 37/37 and .NET 1,300/1,300 pass; its exact-head CI will be checked after publication. All direct Python parity scripts apply the shared two-thread/native-pool cap and CPU affinity limit, and child processes inherit the capped environment.

The latest complete llama.cpp process and terminal evidence is pinned to Pi `96abe9a5` / PiSharp `77f7dd29`. The inspected changes through Pi `04566631` do not alter llama provider, router or classifier behavior; the newer herdr image-detection change is recorded separately as a TUI delta. The twelve pinned llama flows still match normalized terminal state, 155 render frames and HTTP traces; see [the llama evidence](parity/fixtures/llama-terminal-current-pi-2026-10-11.md).

Resources / instructions and trust remains active. Pi's resource matcher uses minimatch on relative paths, basenames and absolute paths. PiSharp now covers hidden paths, brace alternatives/ranges, extglobs and POSIX escaped metacharacters; the current Pi 10.2.6 oracle and 34 focused path-rule tests support the escape slice. The five current-head skill flows (read, Bash, indirect reader, no reader, reload) pass prompt, expansion and normalized provider-request assertions with zero scenario errors and exact same-Pi calibrations. Read/Bash/indirect full terminal state and all paired raw PTY streams still differ; broader resource/trust presentation, precedence, reload and terminal differentials remain open. Evidence: parity/fixtures/resource-path-minimatch-current-pi-2026-10-11.md and parity/fixtures/skill-prompt-current-pi-2026-10-11/current-04566631-d8e5411b/README.md.

The incremental Pi audit covers 229 commits after baseline `955cc6665ee3986c6a033db52200779310d10dfd`: 85 `OUT_OF_SCOPE`, 44 `NO_BEHAVIOR_CHANGE`, 92 `NEEDS_WORK`, and 8 `MATCHED`. New unresolved in-scope deltas include delayed terminal-reply buffering, embedded compaction status coloring, herdr Kitty image detection and Codemode schema constraints; the compaction-summary thinking delta also remains open. All 194 required ledger capabilities remain in scope. Official MCP conformance, other open upstream deltas, a fresh final source audit and architecture review remain outstanding. Keep the unrelated user-owned `test.pdf` untouched.

## Historical checkpoint — 2026-10-02

Pi is at `c10bfb0d79dbbc998539a0a3e6c6a736a4e6db06`; all 58 commits after `955cc666` are classified. PiSharp `c0b012ba7192afed65138dae7d10b5d15da77b67` passes exact-head Linux CI [37019190204](https://github.com/PhilipGB/pisharp/actions/runs/37019190204): restore, format, zero-warning/error build, all 1132 tests and zero skips. Radius menu/MCP/animation is a bounded verified slice; full auth dialog/cursor/style, catalog synchronization and URL copying remain open. Fresh MCP CIMD and Cloudflare gateway catalog work are recorded as required capabilities. All 184 capabilities remain required and final parity is incomplete. Continue Anthropic inline tool definitions through shared historical system/tool transcript projection, then the full terminal harness and llama.cpp router family. See [Radius evidence](parity/fixtures/radius-interactive-onboarding-2026-10-02.md).

The checkpoint sections below retain historical source pins and counts. Current state is recorded above and in the ledger.

## Historical source and upstream audit — 2026-10-01
PiSharp `8931bd73b2e94e7831b1a054a54a8e60bb21aaf5` passed exact-head Linux CI [36904437406](https://github.com/PhilipGB/pisharp/actions/runs/36904437406): restore, format, warnings-as-errors build and 1,068/1,068 tests with zero skips. Current Pi `main` is `395315f4841ab5a090a33b8259f60b6df281c1ca`; all 41 commits after `955cc6665ee3986c6a033db52200779310d10dfd` are classified in the refreshed [upstream audit](parity/fixtures/upstream-delta-audit-2026-10-01.md). The audit has 13 `NEEDS_WORK` classifications, six resolved at published heads and seven open; the oldest open item is MCP OAuth credential isolation by server name and URL. The official MCP client conformance package/version and all three protocol modes are recorded in the execution ledger as a required deliverable. Broader parity remains incomplete.

The old source-options, Bash-output and tool-batch checkpoint paragraphs are historical evidence; their shipped history remains in Git and their implementation/evidence remains in the parity inventory.

## Historical source pointer — 2026-09-30

**Current source and upstream checkpoint:** PiSharp `40b96ada1eb6083f7ea32ac2b11667a92417d7a5` passed exact-head Linux CI [36775909169](https://github.com/PhilipGB/pisharp/actions/runs/36775909169), including 1,021/1,021 tests with zero skips. The first eight deltas are verified. Fail-first reload coverage confirms only newly added defaults join the current branch loadout, active removed tools stay active, and unchanged session-disabled tools stay off. Three items remain from the complete 26-commit classification [here](parity/fixtures/upstream-delta-audit-2026-09-30.md). Continue with MCP identifier normalization (`b29db895c5c1b30b560a39fb9e4664508f1683de`) after this docs-only checkpoint passes exact-head CI. The broader parity goal remains incomplete.

Historical physical-routing checkpoint (recorded with a commit/push/stop boundary): the branch/resume slice restored latest successful physical policy on open, branch selection, preflight and manual compaction; RPC stats use physical limits and virtual unknown limits stay zero. Ordinary physical assistant identity survives switching away from and back to a virtual model. The paired CLI replay matches clone, fork, persisted-session switch, process restart/resume and model changes; focused virtual-model/RPC tests pass 52/52 (0 skipped). Final local validation passes format verification, warnings-as-errors build (0 warnings/errors), and 896/896 tests (0 skipped). Branch/resume source checkpoint `72633674f53a43c60aef67c2077d5cf3132b1e52` passed exact-head Linux CI `36642628468` with 896/896 tests and zero skips. That checkpoint queued large-to-small routing on tool continuation and route-aware compaction, then physical summary prices and image/compatibility/TUI behavior before classifiers; the current task sequence is the upstream audit above. See [stop handoff and evidence](parity/fixtures/virtual-model-branch-context-2026-09-29.md).

The older exact-head and per-feature paragraphs below are historical evidence and their source pins are not current-state claims.

## Historical verified source — 2026-09-30

`43b33344ada902003b4a2c1911196471b0ed9c2f` passed exact-head Linux CI [36649725739](https://github.com/PhilipGB/pisharp/actions/runs/36649725739): restore, format, zero-warning/error build and 922/922 tests with zero skips. This verifies continuation/overflow/canonical-history fixes, differently priced split summaries, the nullable assertion correction and physical read-image limits. Physical TUI status is now in progress; the full goal remains active.

## Historical continuation — 2026-09-30

Resumed the existing goal from exact current `2ff42c49634374d9398f928f81b5e8d353f605be`; fetched remote main and verified exact-head CI `36646088522` remains successful. Upstream fetch still resolves to `1b347794e2a630e4359f2584f4eea388145d0ddf`. The terminal blocker is closed and was not reopened. TODO priority/next-action sections now reflect physical continuation semantics rather than MCP/foundation work.

Continuation/context slice: normal routed requests now defer all threshold checks until physical selection, overflow recovery carries forced compaction into retry routing, and post-compaction reconciliation avoids duplicate raw history. Four tool-provider-loop cases and two overflow cases cover unknown/configured/stale policies, logical identity and physical attribution. Split summaries use distinct physical pricing with correct aggregate cost. Focused tests pass 107/107; final format, zero-warning/error build and 920/920 full tests pass with zero skips. The paired current-Pi/PiSharp later-turn context-compaction process replay matches. See [evidence](parity/fixtures/virtual-model-continuation-context-2026-09-30.md). Source checkpoint `b022de907bafa179ca3e9769290d7921e01bd6f4` is pushed, but exact-head CI `36649052423` failed build because the new cost assertion used nonnullable expected decimals against nullable costs. Earlier local incremental checks missed compiling that final edit. The assertion is corrected in the current worktree; do not claim this checkpoint exact-head green.

Physical image slice ready for publication: two fail-first real-read/provider-loop cases returned 300x20 instead of the physical 150x10 limit. Successful physical responses update read-tool limits, direct summaries do not, and the existing branch resolver restores physical limits. Focused virtual-model/read-image/active-model RPC tests pass 51/51, including corrected cost tests. Format verification and warnings-as-errors build pass on these changes. First full run: 920 passed, 2 failed, zero skipped (922 total); existing SSE-byte idle and concurrent session-lease process timing tests failed while format verification was also running. All four focused timing cases and the unchanged solo full rerun passed: 922/922, zero skips. No timeouts have been changed. See [image evidence](parity/fixtures/virtual-model-image-limits-2026-09-30.md).

Physical TUI status is pushed at `e38dc1b196f2ed41eded583dcabb9e1362b6819b`; exact-head CI `36650323848` passed restore, format, zero-warning/error build and 925/925 tests with zero skips. A fail-first formatter test reproduced the absent physical route. TerminalModelStatus preserves logical selection/thinking, projects branch-local latest successful physical identity/context, and hides prior virtual dispatch under ordinary selections. Successful context status is separate from failed pending dispatch policy. Focused tests pass 49/49; final format, zero-warning/error build and 925/925 full tests pass, zero skips. See [status and bounded audit](parity/fixtures/virtual-model-status-audit-2026-09-30.md).

Historical next-action note, superseded by the current 2026-09-30 upstream delta audit above: this earlier checkpoint queued classifier contracts after polling CI `36650323848` and referenced upstream `1b347794e2a630e4359f2584f4eea388145d0ddf`. Pi classifier contracts are distinct from chat models: state object plus named choice/score/bool questions and typed answers, stop/error/aborted, optional priced usage preserved before parsing answers. System One maps bool to wire noul; TypeSafe/OpenRouter POST systemone, Cloudflare POST run with input envelope, llama.cpp is a distinct classifier protocol. Implement these as a capability-owned boundary rather than IChatClient chat emulation. The bounded virtual-model audit records active-footer presentation, extension-image normalization and cross-logical tree-navigation comparisons for their wider audits rather than delaying missing classifiers. The full goal remains active and incomplete.


## Historical Codemode checkpoint

`4a31e8aec84b5778e2950095200cf274f9447bbe` passes exact-head CI `36683707786`: restore, format, zero-warning/error build, 953/953 tests, zero skips. This includes the separate queued-reply correction `1fb2652` and Cloudflare stored account metadata. The previously failed terminal CI is superseded by this verified follow-up.

Codemode model globals are now being implemented behind ICodemodeModels and the existing process bridge. A fail-first guest test reproduced missing `models`; model lookups and identity-only classifier dispatch now work. Additional fail-first tests exposed missing nested call records and billed usage events after script failure; the owned model-call recorder fixes those and publishes usage separately from chat context. Guest data cannot supply endpoint/header authority. CLI composition now supplies the bridge across model replacement and project/session replacement. Aggregate tool-result tokens/cost, provider-loop and real CLI process persistence, four-call concurrency/cancellation, catalog metadata, credential isolation and Pi-compatible guest usage projections now pass focused coverage (95/95). The paired current-Pi process fixture matches success and later script failure, including paid nested usage. Final local validation passes the warnings-as-errors build (zero warnings/errors) and 969/969 tests (zero skips). Image model catalogs remain a material missing provider/catalog family; an empty current image catalog does not establish image capability parity. The full goal remains active and incomplete.

## Classifier authentication and shutdown follow-up

Llama classifier source `6d34ce4ab80088aa44b6555f6c161fb543ded0be` passed local format/build and 947 tests, but exact-head CI `36682520323` failed the immediate-quit PTY test: a DA1 fence remained after the shutdown wait budget. A new deterministic zero-budget input test reproduced the queued-data cutoff. The pending correction separates bounded waiting from bounded ready-byte consumption (64 KiB), without increasing the deadline or adding sleeps. Cloudflare stored account metadata now follows per-field stored/ambient precedence at request time, with key and metadata from one credential snapshot. Focused terminal/classifier/provider/auth checks pass 107/107. Format verification and the zero-warning/error build pass. Full suite passes 953/953 with zero skips after an unchanged rerun for a virtual-model fixture port collision. Publish and verify exact-head CI before claiming the follow-up green. Then continue Codemode model/classifier globals and nested usage, with remaining model headers/request observation in the classifier audit.

## Historical classifier checkpoint

`2740ece036f9e90bc9a769a255e724fa192c3d54` passes exact-head Linux CI `36651669399`: restore/format, zero-warning/error build, 934/934 tests, zero skips. The following request-options checkpoint adds: shared per-attempt HTTP deadlines/retries/headers and retry-delay caps; user cancellation versus timeout; cancellation during credential resolution returns aborted. Fail-first tests reproduced both missing retries and escaping credential cancellation. Focused checks pass 52/52, final format and zero-warning build pass; the full solution passes 939/939 tests with zero skips. Request-options source 77530840004db80c18c7ed3907acaa2897a53bde passes exact-head CI 36681140547: restore/format, zero-warning/error build and 939/939 tests with zero skips. The llama.cpp non-generative token-logprob classifier is implemented with 60 focused checks and paired Pi evidence; format/zero-warning build and full 947/947 tests pass, zero skips. Publication/CI is pending. Continue Cloudflare stored account/env metadata and paired process evidence. Codemode globals/nested billing remain after provider support. No overall parity completion.

## Classifier foundation — 2026-09-30

Typed classifier contracts, System One/Cloudflare protocol clients, separate native/custom catalogs and ProviderClassifierRuntime are implemented. Fail-first protocol/catalog evidence, seven deterministic protocol tests and a real loopback provider/auth/catalog integration are recorded in [the fixture](parity/fixtures/classifier-native-protocol-2026-09-30.md). All 12 pinned classifier entries are separate from chat selections; classifier-only providers are omitted from chat discovery and configured classifier IDs cannot masquerade as chat models. Native credential isolation applies to their environment names. Focused checks pass 47/47; final format and warnings-as-errors build have zero warnings/errors; full suite passes 934/934 with zero skips. Publication/CI for this foundation is pending. Last exact-head verified source remains e38dc1b / CI 36650323848 / 925 tests.

Exact next action: publish this foundation and verify exact-head CI, then add classifier request options/retry/timeout/header semantics using an owned shared HTTP boundary, llama.cpp's tokenize/apply-template/completion label readout, in-flight cancellation and paired process evidence. Cloudflare stored account/env resolution remains material auth work. Then Codemode model/classifier globals and nested cost aggregation through the existing isolated host bridge. The broader goal remains active; neither classifiers nor parity are complete.

## Reference state

The durable parity baseline remains `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`. Current Pi `main` is `955cc6665ee3986c6a033db52200779310d10dfd`; the full 26-commit delta from `3e9451238337071b74ba5cdd53f1ab7cf4100ae8` is classified in [the 2026-09-30 audit](parity/fixtures/upstream-delta-audit-2026-09-30.md). Older source pins below describe their original scoped comparisons, not current upstream state.

## Historical exact-head evidence

At PiSharp source head `ae05045b105bf23bee228dc6238c415d8978d4fa`, exact-head Linux CI run [36549801185](https://github.com/PhilipGB/pisharp/actions/runs/36549801185) passed restore, format, warnings-as-errors build (0 warnings/errors), and 821/821 tests (0 skipped). Current Pi is `4259686d9290c0d73ae7192b796aee3e530a9779`; the previous orchestration, MCP, Codemode, virtual-model, fullscreen-wheel and Llama/settings tests passed at their recorded `da19b63` pin, and the new OAuth/resource-resolution tests pass 7/7 at the refreshed pin.

At PiSharp source head `584a7d1eb74dfb1fc2624f00a5246806db37c548`, exact-head Linux CI run [36472207899](https://github.com/PhilipGB/pisharp/actions/runs/36472207899) passed format, warnings-as-errors build and 790/790 tests (0 skipped). This includes Google Vertex loopback coverage for API-key and ADC paths, regional endpoints, request schema and secret-safe auth status. Vertex remains in progress pending a paired current-Pi differential and broader provider semantics. Current Pi is `c90d9ea586194003211f10cded3c14f74a4f81e1`; provider implementation source is unchanged since audit pin `cd5ff1125981f0fc37a56df25eee7310286a3f89`. OpenAI Codex Responses uses its account-scoped SSE boundary and static catalog, with browser PKCE/device-code OAuth, private credential persistence and serialized refresh; its earlier exact-head CI run [36462567719](https://github.com/PhilipGB/pisharp/actions/runs/36462567719) passed 779/779. Codex WebSocket/cached transport, manual-code fallback and wider image/tool/usage/error differentials remain open. Bedrock Converse is next.

Google GenAI has a dedicated REST/SSE client, a 22-model snapshot from Pi `c90d9ea586194003211f10cded3c14f74a4f81e1`, and local HTTP, MAF tool-loop, signature-history and Gemini-version image-routing fixtures. Exact-head CI run [36472207899](https://github.com/PhilipGB/pisharp/actions/runs/36472207899) passed 790/790. Current-Pi focused Google suites passed 65 tests with 9 skipped; a paired Pi/PiSharp differential and broader provider semantics remain outstanding. Google Vertex has landed but remains in progress; Bedrock Converse and Pi Messages have since landed and are included in the current provider boundary.

At PiSharp source head `66561e1db1942624f7310bd50f68195c80ee66bf`, the Anthropic Messages history adapter applies each model's `compat.allowEmptySignature` flag during assistant reasoning replay. A fail-first real-SDK loopback established the wire difference; focused PiSharp tests pass 4/4. Current Pi tests `anthropic-empty-thinking-signature-compat.test.ts` and `qwen-token-plan-models.test.ts` pass 98/98 at Pi `11894012dd461232eb075bc890538b6866860a10`. Exact-head Linux CI run [36454550166](https://github.com/PhilipGB/pisharp/actions/runs/36454550166) passed format, warnings-as-errors build, and 768/768 tests (0 skipped). See [the provider fixture](parity/fixtures/anthropic-empty-thinking-signature-baseline.md).

Azure OpenAI Responses is implemented at source head `ec1ea0f212f2a1964c891ce4a90c286020f3bcca`, with an Azure-specific request boundary and embedded 43-model catalog. Exact-head Linux CI run [36452261244](https://github.com/PhilipGB/pisharp/actions/runs/36452261244) passed format, warnings-as-errors build, and 764/764 tests (0 skipped). Provider-wide differential coverage remains incomplete.

The terminal wrap/layout slice matches current Pi's word-boundary behavior, SGR/OSC 8 continuation rows, long-token splitting, and paired CJK/emoji vectors. `TerminalTextLayout.Create` is shared by transcript rendering, viewport scrolling, and search-row centering; nested Markdown code and links restore enclosing styles. The width-1 CJK fallback remains a deliberate PiSharp safety difference. Exact-head Linux CI run [36417080999](https://github.com/PhilipGB/pisharp/actions/runs/36417080999) passed for source `90c08f7` with 720/720 tests. See [the wrap/layout fixture](parity/fixtures/tui-wrap-layout-baseline.md).

Current Pi's generated `system` theme, full terminal-color query and appearance-refresh behavior are recorded in [the system-theme fixture](parity/fixtures/terminal-system-theme-baseline.md). The subsequent CLI slice adds repeatable `--theme`, interactive `--use-theme`, and `--no-themes`; explicit cwd-relative files/directories remain available when discovery is disabled. A Linux PTY fixture verifies the explicit theme is actually rendered. Exact-head Linux CI run [36420934092](https://github.com/PhilipGB/pisharp/actions/runs/36420934092) passed for `c68ad5b` with 729/729 tests; see [the CLI theme fixture](parity/fixtures/theme-cli-baseline.md).

Source commit `56f0659d9061c448a1837529804493c2e83d1e3e` matches Pi's 16,384 reserve and 20,000 keep-recent defaults, oversized trailing tool-result fallback, and closed recovery-omission suffix boundary. A current-main Pi RPC process made the same two summary requests and returned the aborted-assistant cut point, split summary, file details and 30/7/37 usage expected by PiSharp's process regression. PiSharp validation passed format, a zero-warning build, focused tests 91/91, and the full suite 692/692 (0 skipped). The blocked-tool model-switch process test also compacts after switching to a model-specific one-token retention override. Exact-head Linux CI run [36394452133](https://github.com/PhilipGB/pisharp/actions/runs/36394452133) passed restore, format, warnings-as-errors build, and 692/692 tests (0 skipped). Upstream compaction and settings test lanes passed 55 tests + 2 skips and 56 tests respectively. See [current compaction cut-point evidence](parity/compaction-cut-points.md).

Latest pushed source `de233c4bfe78a83f3627818cf4bebd0e92aa14f2` supports `terminal.trueColor` as a boolean or `"auto"`, applies user/trusted-project precedence to built-in and custom theme color modes, and refreshes the active theme after `/settings` saves. Exact-head Linux CI run [36356332794](https://github.com/PhilipGB/pisharp/actions/runs/36356332794), attempt 2, passed restore, format, warnings-as-errors build, and 685/685 tests (0 skipped). Attempt 1 failed the existing parallel-tool compaction request-count test; the unchanged rerun passed. Automatic `NO_COLOR`/`TERM=dumb` handling remains a documented difference. See [the setting fixture](parity/fixtures/settings-terminal-truecolor-baseline.md).

Previous pushed source `be36ea88b7d09c7a30f94f84222e773f5bc38535` supports `markdown.codeBlockIndent` in user and trusted-project settings, re-renders committed Markdown when the value changes, and exposes literal-prefix editing through `/settings`. Exact-head Linux CI run [36355353424](https://github.com/PhilipGB/pisharp/actions/runs/36355353424) passed restore, format, warnings-as-errors build, and 683/683 tests (0 skipped). The PiSharp gutter remains a documented rendered-line geometry difference.

Earlier provider-timeout source `0b2c6e19b918ad0b72953f215f542a7a73ba9598` added request and HTTP idle timeout settings to the OpenAI and Anthropic paths. Exact-head Linux CI run [36353845534](https://github.com/PhilipGB/pisharp/actions/runs/36353845534) passed restore, format, warnings-as-errors build, and 680/680 tests (0 skipped). Paired processes from pinned Pi `2b0a123de98318c2ff8069661721ce0c3794c34e` and PiSharp each made one request to a delayed local OpenAI Responses endpoint and exited on the configured timeout. At that source, Pi's dispatcher reset on response wire bytes while PiSharp's streaming idle deadline reset on parsed updates; the current partial-SSE fixture below closes that behavior gap.

Pushed resource-path source `5b690979d219dccd29a047a65d3497b198cf2eb3` adds scope-aware `extensions`, `skills`, `prompts`, and `themes` arrays, trust-gated project resolution, pattern filtering and `/reload` refresh. Exact-head Linux CI run [36401121298](https://github.com/PhilipGB/pisharp/actions/runs/36401121298) passed restore, format, warnings-as-errors build, and 696/696 tests (0 skipped). Pushed timeout source `67c283d6c90815414be8bf58446d7365f506e5ad` rearms streaming idle timeouts when the OpenAI Responses, OpenAI Completions, and Anthropic SDKs read partial SSE body bytes; the three-adapter fixture and stalled-byte checks pass, along with format, warnings-as-errors build and 699/699 tests (0 skipped). Exact-head Linux CI run [36403312164](https://github.com/PhilipGB/pisharp/actions/runs/36403312164) passed restore, format, warnings-as-errors build, and 699/699 tests (0 skipped). The fixture records .NET content-stream read cadence and buffering as the remaining difference from Pi's Undici socket-data timer.

The transport-scope documentation follow-up `9f5173a3fe21d3e17c4c23e281ae522c95e34a90` passed exact-head Linux CI run [36354259199](https://github.com/PhilipGB/pisharp/actions/runs/36354259199): format, warnings-as-errors build, and 680/680 tests (0 skipped). It records that Pi's `transport` and WebSocket connect timeout belong to `openai-codex-responses`; PiSharp now implements the Codex SSE path, while WebSocket/cached transport remains unsupported.

At that earlier checkpoint, Pi `main` resolved to `2b0a123de98318c2ff8069661721ce0c3794c34e`. Pushed source `84aa91b2da8174f1c67318f65529a56549098ec1` canonicalizes session-lock path aliases before selecting the same-process semaphore and OS lock. Exact-head Linux CI run [36349575501](https://github.com/PhilipGB/pisharp/actions/runs/36349575501) passed restore, format, warnings-as-errors build, and 674/674 tests (0 skipped). The preceding provider-history result deduplication (`72ef01995`) and user-only `httpProxy` setting (`079ffc11a`) passed exact-head run [36347187105](https://github.com/PhilipGB/pisharp/actions/runs/36347187105) with 673/673 tests.

Its predecessor, `ca92588f4b94ab5c002065baa72542df832499f8`, closes the repeated OpenAI Completions overflow loopback failure without changing recovery/history assertions. The SDK could throw during stream-enumerator creation, outside `ObservedChatClient`'s provider-error boundary, so the HTTP 400 overflow body never reached context recovery. Setup and cleanup errors now stay inside that boundary, and the Completions fixture verifies the captured `context_length_exceeded` response. Exact-head Linux CI run [36333639624](https://github.com/PhilipGB/pisharp/actions/runs/36333639624) passed all 662 tests.

Pushed source `5e38b25cd` projects Pi's generated system message into RPC `get_messages` and `get_state.messageCount`, without changing canonical C# history. Pushed source `22ff9b115` makes `get_session_stats` available during blocked provider work, projects Pi response fields, and aggregates usage from native and imported assistant/tool-result messages, compaction, branch summaries, and standalone records. Focused read/stats tests passed 6/6; format and warnings-as-errors build passed; the final full run passed 658/658. Two separate request-count failures in earlier full attempts each passed in isolation before that passing run. A paired process comparison matches counts, usage/cost fields, and context window; Pi estimates 1422 context tokens and PiSharp 817, so context-estimate parity remains open. Imported post-compaction context usage also remains open. A follow-up paired process comparison confirms both Pi and PiSharp accept active `export_html` and create the requested file; PiSharp's process test additionally verifies the stable snapshot excludes partial assistant output while streaming continues. See [the active-export fixture](parity/fixtures/rpc-export-html-active-baseline.md). PiSharp projects its actual instructions under `sections.preamble`; Pi exposes separate `preamble/tools/rules/docs/cwd` sections. See [the system-message comparison](parity/fixtures/rpc-get-messages-system-baseline.md) and [the session-stats comparison](parity/fixtures/rpc-get-session-stats-baseline.md).

At the 2026-09-27 checkpoint, upstream Pi `main` resolved to `2b0a123de98318c2ff8069661721ce0c3794c34e`. The repeated overflow failure on `000fb5dcb56d3aad84833d6338ce0336c36556fc` did not reproduce on `e1e1fd0`; current 8a CI showed a new first-attempt occurrence, then passed on failed-job rerun. Older transient failures remain recorded historically.

## Active RPC clone differential

Paired Pi 0.87.1 and PiSharp CLI processes with the same loopback provider now match active `clone` success: both clone the accepted in-flight user prompt and earlier completed history, exclude partial aborted assistant text from the new session, and emit `message_end -> turn_end -> agent_end -> agent_settled -> clone response`. The outgoing aborted assistant carries `errorMessage: "Request was aborted"`. Both also reject cloning when the source file disappears during a blocked turn, leave `get_state.isStreaming` true, and allow the turn to settle after the file is restored. `InteractiveSessionController.PrepareCloneAsync` now uses Pi's exact failure text, and the focused PiSharp success/preflight process tests pass 2/2. Format verification passes, the warnings-as-errors build reports 0 warnings/errors, and the full suite passes 656/656; exact-head CI run 36317334778 for pushed source 49e39efe2 passed 656/656 on its failed-job rerun after the first attempt hit the existing overflow loopback failure. See [the comparison fixture](parity/fixtures/rpc-clone-baseline.md). Extension veto, no-current-entry, shutdown, and other replacement failures remain open.

## Active RPC generated system-message projection

The paired active-clone processes found that Pi includes a generated leading `system` message in RPC `session.messages` and counts it in `get_state.messageCount`. PiSharp now projects a live RPC-only message from the actual MAF instruction snapshot and selected tool declarations, prepends it to `get_messages` after provider initialization, and includes it in `get_state`; fresh sessions still have no generated message. The message is not persisted in the canonical C# tree or Pi JSONL. PiSharp currently groups its real instructions under `sections.preamble`, while Pi uses richer `preamble/tools/rules/docs/cwd` sections; preserve this structural gap in parity claims. Focused RPC process filter passed 5/5; format/build pass; solo suite 656/656; the paired process comparison matches transcript, event order, count and tool metadata. Pushed source `5e38b25cd` exact-head Linux CI run `36319817145` passed all 656 tests. See [the process comparison](parity/fixtures/rpc-get-messages-system-baseline.md).

## Active RPC session-statistics slice

Current Pi accepts `get_session_stats` during a blocked provider turn. A paired Pi/PiSharp process fixture found PiSharp previously returned `Wait until the active prompt settles.`. Pushed source `22ff9b115` now returns Pi-shaped `sessionFile`, `sessionId`, message/tool counts, token/cost totals, and optional `contextUsage` without cancelling the provider run. It aggregates canonical messages and usage records across branches, plus imported Pi usage attached to assistant/tool-result messages, compaction, branch summaries, and standalone usage entries. Pi's `agent-session-stats.test.ts` covers these entry kinds and post-compaction context usage. PiSharp's blocked-stream process test verifies response fields and active state. Pi/PiSharp match availability, stable counts, token/cost totals and context window; context estimates differ (1422 in Pi versus 817 in PiSharp for this fixture), and imported post-compaction context usage remains open. Focused tests passed 6/6, format and warnings-as-errors build passed, and the full suite passed 658/658. Exact-head CI run `36323237959` passed on commit `22ff9b115ea3d36322fda8544cdde4743d833545`. The same comparison found Pi permits active `export_html`, which PiSharp now implements by taking a canonical tree snapshot before asynchronous export. See [the paired stats fixture](parity/fixtures/rpc-get-session-stats-baseline.md) and [the active-export fixture](parity/fixtures/rpc-export-html-active-baseline.md).

## Active RPC HTML export

Current Pi `2b0a123de98318c2ff8069661721ce0c3794c34e` handles `export_html` without a busy-run guard and synchronously projects session entries before writing its HTML. Paired Pi/PiSharp CLI processes both returned correlated success/path responses and created the requested file during a blocked provider stream; both processes exited cleanly. PiSharp clones `CurrentRun.Conversation` before asynchronous file writing. `RpcExportHtmlProcessTests.ExportHtmlDuringBlockedRunWritesTheAcceptedSessionSnapshot` verifies the real PiSharp process returns before provider release, exports prior history plus the accepted prompt, excludes partial assistant text, and leaves `get_state.isStreaming` true. Focused test passed 1/1. Format and warnings-as-errors build passed; serialized full suite passed 659/659. Exact-head CI run `36324847653` attempt 4 passed all 659 tests after three attempts failed on the existing overflow loopback. PiSharp HTML presentation differs from Pi's viewer; rendering, session-file prerequisites, and broader error/cancellation behavior remain open. See [the active-export process fixture](parity/fixtures/rpc-export-html-active-baseline.md).

## Active RPC idle queue slice

Current Pi `2b0a123de98318c2ff8069661721ce0c3794c34e` accepts idle `steer` and `follow_up` as queued messages, emits `queue_update` before the correlated success response, exposes the pending count in `get_state`, and returns separate steering/follow-up arrays from `clear_queue`. PiSharp now matches those observable paths. `RpcIdleQueueProcessTests` verifies event/response order, queue count and clearing, then compares the next two fake-provider request bodies against Pi for an idle steering message followed by a follow-up. `PromptDeliveryController` now owns queue contents, run-start snapshot, delivery and event publication, extracted at the boundary required by this behavior. Exact-head CI run `36299233867` passed.

## Active RPC thinking-map slice

Current Pi `2b0a123de98318c2ff8069661721ce0c3794c34e` exposes reasoning levels according to `thinkingLevelMap`: explicit `null` removes a level, while `xhigh` and `max` require explicit non-null entries. PiSharp now applies those rules to available levels, RPC set/cycle, startup and model changes; unsupported selections clamp upward then downward. Canonical map strings translate to Microsoft.Extensions.AI `ReasoningEffort` values where representable, and an OpenAI Chat Completions loopback test verifies `high: "low"` becomes `reasoning_effort: "low"`. A current Pi process fixture matches sparse levels, `xhigh` to `max`, event-before-response order, and cycling; see [the captured fixture](parity/fixtures/rpc-thinking-level-map-baseline.md). Arbitrary provider strings and API-specific request payloads remain open.

## Active RPC busy-cycle slice

A real current-Pi process at `2b0a123de98318c2ff8069661721ce0c3794c34e` accepted `cycle_model` while a Bash tool was blocked on a FIFO, returned the next model with `thinkingLevel` and `isScoped`, and sent the next provider request to that model. PiSharp's `RpcActiveModelSelectionProcessTests` exercises `set_model` followed by `cycle_model` before releasing the blocked tool, checks persisted model-change order and verifies the next request. A second current-Pi process differential confirms declared scoped order, per-model defaults, `thinking_level_changed` before both correlated responses, `isScoped: true`, and the next provider request's `reasoning_effort`; PiSharp now matches in the process test. See [the busy-cycle fixture](parity/fixtures/rpc-cycle-model-baseline.md) and [the scoped-order/thinking fixture](parity/fixtures/rpc-scoped-model-thinking-baseline.md). Explicit scoped `:thinking` suffixes and per-model editing in `/settings` remain open.

## Active RPC tool-call delta slice

The OpenAI Chat Completions adapter in Microsoft.Extensions.AI exposes the assembled `FunctionCallContent` used by MAF to execute the tool, but not its raw SSE argument fragments. PiSharp now observes `/chat/completions` SSE at its transport boundary and projects raw `toolcall_delta` events while leaving the completed MAF call authoritative. `RpcToolCallDeltaProcessTests` pauses the provider after the first argument fragment and proves PiSharp emits it before the next fragment arrives, preserves both fragment strings, emits `toolcall_end` before tool execution, and completes the real CLI tool turn. A current Pi process at `2b0a123de98318c2ff8069661721ce0c3794c34e` emitted the same start/two-delta/end sequence for the same provider chunks; see [the captured fixture](parity/fixtures/rpc-toolcall-delta-baseline.md). Exact-head CI run `36301016521` passed 636/636. OpenAI Responses, Anthropic, incomplete argument snapshot semantics and broader provider differentials remain open.

`ModelRuntimeController` now owns shared RPC model/runtime transitions. `get_available_models` and `set_model` are accepted during a blocked tool turn; process coverage verifies that the next provider request uses the newly selected model. `RpcAgentLifecycleProcessTests.RpcProcessAbortEndsPartialTurnBeforeAgentSettlement` now matches a current-Pi process for partial assistant projection, cancellation order, `willRetry: false`, and successful response shape; see [the abort fixture](parity/fixtures/rpc-abort-baseline.md). Successful shared RPC responses omit optional `error`, responses to requests without an ID omit `id`, and new-session null/empty parent paths are treated as absent; see the abort and new-session process fixtures.

## Active RPC command discovery slice

`RpcCommandDiscoveryHandler` now owns `get_commands` response projection instead of adding another command family to `RpcMode`. Resource and extension catalogs retain source metadata, and process fixtures cover explicit and auto-discovered resources, extension commands, optional descriptions, prompt fallback descriptions, source scopes and project-before-user ordering. At Pi `2b0a123de98318c2ff8069661721ce0c3794c34e`, direct process inspection showed explicit extension commands use `cli/temporary/top-level`, explicit prompt/skill resources use `local/temporary/top-level`, and auto resources use `auto` with project resources preceding user resources. See [the command-discovery fixture](parity/fixtures/rpc-get-commands-baseline.md). This is a scoped process comparison, not full command-list equivalence: provider-specific and extension-contributed commands still vary; RPC extension-command invocation now has separate process coverage, but full command-list differentials remain open.


## Active RPC extension-command invocation slice

`RpcPromptCommandHandler` now dispatches registered `/command args` prompts before resource expansion, image rejection, or active-run queueing. This follows current Pi `2b0a123de98318c2ff8069661721ce0c3794c34e`: `AgentSession.prompt` tries extension commands before input/image/streaming work, reports `handled` on success or handler failure, and emits `extension_error` for a thrown handler. `RpcExtensionCommandProcessTests` runs the real CLI against a blocked local provider and verifies active-run execution, correlated response/event ordering, the returned text event, command errors, and no queue or extra agent start. The returned string uses a PiSharp `format: "pisharp"` event because this extension API returns text rather than exposing Pi's extension UI/notification APIs. See [the scoped baseline](parity/fixtures/rpc-extension-command-baseline.md). This is source/test-based Pi evidence, not a current-Pi process differential. Cancellation/shutdown and broader extension lifecycle behavior remain open.

## Active malformed Pi JSONL recovery slice

Current Pi main `2b0a123de98318c2ff8069661721ce0c3794c34e` skips malformed physical JSONL rows, indexes later valid entries with missing parents as leaves, and resolves valid forward parent links when building the tree. A live Pi 0.87.1 process and PiSharp produced the same `get_messages` result for a malformed-parent fixture, and current Pi/PiSharp RPC processes return the same `get_tree` relationship when a valid child precedes its parent. PiSharp extracts record-to-tree materialization into `PiJsonlSessionTreeBuilder`; missing parents become canonical roots while raw Pi records keep their source parent IDs, and `ConversationTree.FromEntries` resolves valid forward references in original file order with cycle checks. A Pi session header with an id but no `cwd` also loads using the invocation directory; PiSharp now applies that only as a fallback, preserving a present header `cwd`. `PiJsonlSessionRecoveryProcessTests` verifies these CLI behaviors and preserves imported source bytes. Exact-head run `36312230706` for `7bfbe7e8b441ddafa6b7d4e801dfa4a4cce18f55` passed format, warnings-as-errors build and 649/649 tests. Forward-parent commit `000fb5dcb56d3aad84833d6338ce0336c36556fc` has a repeated single Completions overflow failure in exact-head CI run `36312799209`; the current working tree passes format/build and 655/655 locally, but exact-head CI for it has not run. The installed Pi loader appends a newline after a truncated tail, while PiSharp imports into a separate project session without modifying the source. Invalid-head recovery, other malformed record shapes and broader session-manager differentials remain open. See [the scoped baseline](parity/fixtures/session-malformed-descendant-baseline.md).

## Active session slice

Current Pi `SessionManager.forkFrom` copies a source session into the invocation project, updates the new session header `cwd` to that target, and records the source path as parent. PiSharp now loads native or Pi JSONL sources, preserves the full tree and selected head, and writes the fork into the invocation project. The CLI process test covers both formats, target CWD, parent path, transcript, inactive branch and source immutability. Focused `ConversationTreeTests` and `PiSessionStartupTargetTests` passed 10/10; exact-head CI run `36288246889` passed 616/616.

## Active RPC session replacement slice

At current Pi `2b0a123de98318c2ff8069661721ce0c3794c34e`, `AgentSessionRuntime.switchSession` constructs `SessionManager.open` and checks the stored CWD before `teardownCurrent`; malformed nonempty session files and missing project directories therefore fail before active work is aborted. PiSharp prevalidates existing targets before cancellation. Missing `.jsonl` targets now open as lazy Pi JSONL sessions in the invocation project. A Linux CLI process test verifies the switch response follows blocked-provider abort settlement, the file remains absent until the replacement receives a prompt, and that subsequent prompt is executed by and persisted through the new run. RPC prompt execution and explicit saves now use the currently adopted `ConversationRun`. A fenced `PiJsonlSessionFileStore` shares the atomic writer with native storage and refuses overwriting an existing or externally changed file. `RpcSessionReplacementProcessTests.InvalidSwitchTargetDoesNotCancelAnActiveRun` covers failure preflight. Exact-head run `36290986668` passed format, warnings-as-errors build, and 621/621 tests. No Pi process differential has been run.

Full local and exact-head CI validation for `f509750da57bece3e64a3bcbb905e3852782afa4` passed format verification, a warnings-as-errors build with 0 warnings/errors, and 617/617 tests (0 failed, 0 skipped).

## Active RPC fork slice

Current Pi includes regression test `packages/coding-agent/test/suite/regressions/8724-in-memory-fork-active-tool.test.ts`: forking during an active tool turn settles the outgoing turn and does not append its aborted messages to the replacement session. PiSharp's Linux RPC process test covers blocked provider streaming: it verifies `turn_end < agent_end < agent_settled < fork response`, retains partial aborted assistant output on the outgoing event, and confirms the new branch is empty. The first exact-head CI run `36289925328` failed 1/618 because the test raced task completion after `agent_settled`; commit `bfb37df4d9d75a7462df5c8884ee172467b3e5d2` adds an idle-state poll and CI passed 621/621. `RpcActiveToolForkProcessTests` blocks Bash inside a Linux CLI RPC process, forks, confirms the aborted assistant in `agent_end`, waits for `agent_settled` before the response, and checks the replacement transcript is empty. Exact-head CI at `1abca7f26` passed 624/624. No Pi process differential has been run.

## Active RPC clone slice

Current Pi RPC `clone` passes the current leaf to `AgentSessionRuntime.fork(position: "at")`; that runtime constructs the branch before `teardownCurrent` aborts the active run. PiSharp snapshots the current branch, removes the unfinished run's internal recovery checkpoints, validates the source session version and saves the replacement branch before cancellation. Paired Pi 0.87.1/PiSharp CLI processes match the active success response, settlement order, cloned transcript and preflight busy-state semantics. The comparison found PiSharp's missing-source sentence differed; the local controller and process test now use Pi's current wording. `RpcActiveCloneProcessTests` and `RpcClonePreflightProcessTests` pass 2/2 focused. Exact-head CI run `36317334778` passed all 656 tests on its failed-job rerun after the first attempt hit the existing overflow-loopback failure. See [the active clone fixture](parity/fixtures/rpc-clone-baseline.md). The comparison also found Pi's live generated system message in `get_messages`; see [that fixture](parity/fixtures/rpc-get-messages-system-baseline.md). Extension veto, no-current-entry, shutdown and other replacement failures remain open.

## Session crash and concurrency recovery

At current Pi `main` `6f7551516b84278eb9da1c340c8e7bc66be1a6ba`, a real CLI crash/restart replay matches the provider continuation from `CrashRecoveryProcessTests.CrashAfterToolSideEffectBeforeOutcomeCheckpointRecoversWithoutReplayingTool`: the first process persisted the user and assistant tool call but no result, was killed after a Bash side effect, and the restarted request placed Pi's synthetic `No result provided` tool result before the continuation prompt. The side effect remained exactly `x` and the restarted process exited 0. PiSharp additionally persists the unknown `bash` outcome, displays a recovery warning and never replays the side effect; the synthetic result is absent from RPC `get_messages` and durable session history. See [the session recovery differential](parity/fixtures/session-crash-recovery-baseline.md).

A two-process `SessionManager.open` probe against the same Pi JSONL file appended two valid sibling entries without a session writer lock. PiSharp serializes writes through bounded process and OS locks, then refuses a stale in-memory snapshot instead of merging it. Current Pi's loader skips malformed JSONL rows and terminates an incomplete tail with a newline; PiSharp's importer skips malformed rows but leaves the original source bytes unchanged. Upstream recovery/concurrency tests passed 40/40; PiSharp's focused recovery/lock/JSONL tests passed 7/7 plus the 1/1 stale-writer test. Same-process and path-alias lease races remain fixed. The macOS exclusive `flock` path still needs a native run. See [the current recovery/concurrency evidence](parity/fixtures/session-crash-recovery-baseline.md).
A fail-first same-process test found `FileStream.Lock` does not serialize two Linux leases in one process. `SessionFileLease` now adds a bounded, cancellation-aware per-path semaphore before the existing OS lock; same-process, cross-process, stale-store and crash/recovery coverage pass. A second fail-first Linux test showed a symlinked session-directory alias could select a different in-process gate. `ResolveLockPath` now resolves existing path links before selecting that gate and opening the OS lock, and `ConcurrentSessionLeaseAliasesWaitForTheCurrentWriter` verifies the direct and aliased paths wait for the same writer. Because .NET marks `FileStream.Lock` unsupported on macOS, that branch uses exclusive `FileShare.None`/`flock` and explicitly checks that the filesystem honors it. The macOS process fixture is enabled, but native macOS execution has not been available in this run.
The three focused lease/stale-write tests pass 3/3. Exact-head Linux CI run `36349575501` for `84aa91b2d` passed format, warnings-as-errors build with 0 warnings/errors, and 674/674 tests (0 failed, 0 skipped).

## Active RPC read snapshot slice

Current Pi RPC allows `get_messages`, `get_fork_messages`, `get_last_assistant_text`, `get_entries`, and `get_tree` during provider work. PiSharp answers from a cloned session tree and shares its active assistant projector so streamed text remains visible. `RpcSessionMessageProjector` applies compaction and context edits, preserves Pi `compactionSummary`/`custom` roles, and filters system messages before the retained boundary; provider `ContextMessages()` uses Pi's summary wrapper. A current-Pi/PiSharp process differential at Pi `2b0a123de98318c2ff8069661721ce0c3794c34e` matched `get_messages` for a compaction system message, retained boundary and context edit; it also matched `get_entries`, `get_entries` since-cursor, and `get_tree` for a branched, labelled, custom-message session. `get_last_assistant_text` matched for absent assistant text and an empty aborted tail. Linux CLI process tests cover all five reads during streaming and the compacted message response. Focused RPC/session/compaction tests pass 46/46 after one transient concurrency-sensitive compaction failure (isolated and combined reruns pass). Exact-head Linux CI run `36297279884` passed format, warnings-as-errors build and 633/633. OpenAI Chat Completions raw tool-call deltas now have process-level Pi comparison; other provider API deltas and partial argument snapshots remain open. A paired clone differential found Pi's generated live system message is included in RPC `session.messages`; PiSharp now projects it from its MAF instruction snapshot without adding it to canonical history, while section-layout parity remains open.

## Settled interrupted assistant projection

A paired RPC `compact` process comparison found Pi persists the aborted partial response as an assistant message before compaction; PiSharp previously exposed only an internal marker. `ConversationRun` now captures stream failure versus cancellation, stores provider/model/API and stop/error metadata, and rebuilds MAF history with nonempty partial text. RPC `get_messages`/`get_entries`, Pi JSONL projection and canonical provider context include the response after settlement, while retry context edits omit a failed partial before retrying. `RpcInterruptedSessionProcessTests` verifies abort snapshots, subsequent request context, persistence and clean shutdown; interchange/lifecycle tests verify projection, import/export and failure metadata. Local validation passed 662/662; exact-head CI is linked above and was still running at this checkpoint.

Do not infer full parity from any green slice. Keep architecture work tied to the next feature boundary: `Program.cs` remains over 1,100 lines, `ConversationRun.cs` around 944 lines, and `PiJsonlSessionInterchange.cs` around 740 lines. Keep `RpcMode.cs` moving toward transport/dispatch, and put new RPC capability tests in focused classes. Remaining TUI residuals include custom HTML-export theme CSS, full Markdown dialect/syntax highlighting, and broader differential coverage.

## Split-turn compaction slice and remaining scope

PiSharp routes manual and pre-prompt automatic compaction through `ConversationCompactionCoordinator`, which owns cut-point planning, split-turn and previous-summary handling, two-stage summary requests, file-detail formatting, usage aggregation, canonical persistence and context restoration. It now also uses Pi's 16,384 reserve and 20,000 keep-recent defaults, applies model overrides during queued model transitions, falls back before oversized trailing tool results, and moves past only a closed recovery-omission suffix. A nonpositive threshold for windows at or below the reserve no longer causes a pre-prompt hard rejection or an impossible in-flight budget. Internal compaction may retain a user or assistant entry; parsing/reload validates assistant boundaries. `PiAgent` uses Pi's structured history/update prompts and explicit turn-prefix checkpoint prompt, while bounded transcript excerpts preserve recent content and tool outcomes.

Deterministic tests cover normal and split turns, interrupted assistants, tool calls/results, branch restoration, custom focus, summary failure/cancellation, persistence failure, reload and pre-prompt automatic compaction. A current-main Pi RPC process returned two summary requests, focus only on the history request, the aborted-assistant boundary, combined summary and file details, and 30/7/37 usage. The PiSharp process regression checks those fields and canonical persistence. The prior paired run at Pi `2b0a123` recorded 31/41 Pi estimates and 1085/67 PiSharp estimates; the current single Pi run reported 25/41, so exact estimates on identical session state remain open. See [the compact differential](parity/fixtures/rpc-compact-baseline.md) and [current cut-point evidence](parity/compaction-cut-points.md).

## Bounded RPC residual audit

The refreshed audit is [rpc-residual-audit-2026-09-28.md](parity/rpc-residual-audit-2026-09-28.md), pinned to Pi `6f7551516b84278eb9da1c340c8e7bc66be1a6ba`. It cross-checked all 33 current command names against PiSharp dispatch/handler references and found no missing path or intervening upstream command-surface change. The bounded session recovery/concurrency slice is now also checked at that source pin; broader session-manager behavior, provider-specific streaming/error behavior and the extension lifecycle remain material. Narrow protocol-shape residuals include system-message section layout, non-string `new_session.parentSession` coercion and partial argument snapshots.

## Pinned transport audit and next settings controls

At current Pi `6f7551516b84278eb9da1c340c8e7bc66be1a6ba`, `httpIdleTimeoutMs` defaults to 300000 ms and configures Undici response-header and body idle timers; Pi also forwards the effective request timeout to provider streams, with `retry.provider.timeoutMs` taking precedence. PiSharp maps these settings through the OpenAI and Anthropic SDKs, enforces a total deadline, and now rearms the streaming idle timer on body reads in all three active HTTP adapters. The partial-SSE loopback fixture covers response bytes arriving without a parsed event, and existing stalled-stream fixtures still verify timeout after body activity stops. Local format and warnings-as-errors build pass; the full suite passes 699/699. Exact-head Linux CI run [36403312164](https://github.com/PhilipGB/pisharp/actions/runs/36403312164) passed on source `67c283d6c`. The earlier paired pinned-Pi/PiSharp process run used a 300 ms request timeout and delayed local endpoint; each CLI sent one request and exited before the response. Pi's timer observes Undici socket-data activity while PiSharp observes bytes as .NET content-stream reads, leaving buffering/read cadence as an implementation difference. See [the timeout fixture](parity/fixtures/settings-provider-timeout-baseline.md).

The pinned source audit confirms `transport` has values `auto`, `sse`, `websocket`, and `websocket-cached`; the legacy `websockets` boolean migrates to the enum. Both it and `websocketConnectTimeoutMs` reach only Pi's `openai-codex-responses` adapter. That path performs WebSocket handshake timing, session/account connection reuse, cached continuation requests, and pre-stream SSE fallback. PiSharp now has an `openai-codex-responses` SSE adapter with account-scoped headers and session affinity. WebSocket handshake/reuse, cached continuation and pre-stream SSE fallback remain explicit unsupported provider-scope residuals; these controls are not exposed as no-op settings. PiSharp now parses and applies `markdown.codeBlockIndent` at startup and through `/settings`, including a live rerender of committed Markdown; see [the setting fixture](parity/fixtures/settings-markdown-baseline.md). Its source commit passed exact-head Linux CI run 36355353424 with 683/683 tests. The pinned `markdown.mermaid` setting controls an actual Markdown transformer backed by `grok-mermaid`; it remains unsupported until that renderer boundary is implemented. The four local resource path arrays now resolve independently from user and trusted-project roots, preserve additive settings, filter configured and automatic resources with `!`/`+`/`-`, and refresh through `/reload`; see [the resource-path fixture](parity/fixtures/settings-resource-paths-baseline.md). Focused tests passed 61/61, format and warnings-as-errors build passed, the full suite passed 696/696 (0 skipped), and exact-head CI run 36401121298 passed. Provider idle timeouts now reset on body reads across all three adapters, including partial SSE events; local validation is 699/699 and exact-head CI run 36403312164 passed. Keep unsupported schema families explicit and do not claim complete settings parity. Revisit RPC only when new evidence changes a residual's impact.

No engineering is claimed between 2026-10-23 09:10:33 UTC and resumption at
2026-10-23 12:22:16 UTC; exclude that inactive interval from active duration.

Find hardening now passes 27 pinned process cases (54 shared with grep), real fd
first-output cancellation with cleanup/recovery, and a 276-test warning-as-error
selection. Preparation abort and directory-symlink privacy regressions fail
before correction and pass afterwards. Keep the named repository-local exclude
safety difference explicitly excluded from strict parity; all 22 comparison
guards pass. Find now has 62 cases (428 total), awaiting clean-head acceptance.

Clean `99016879` closes K: 61/62 strict comparisons, one explicit privacy
exclusion, zero unresolved differences and 62/62 same-Pi calibration. See the
find acceptance receipt. Stable progress is 11/20 complete, 9 remaining. L (ls)
is now in progress; earlier chronological pending notes are superseded by this
acceptance. No PR #22 has been published yet.

Clean `a68d365c` closes L with 53/53 strict ls comparisons and a 53/53
strict same-Pi control. Related checks pass 211/211 plus 40 legacy selection/
result checks. Stable progress is now 12/20 complete, 8 remaining. M (tool
integration) is in progress, followed by N physical provider workflows.

M acceptance: 15 new MAF checks and 825 related checks pass, plus 37 source
selection/prompt tests across six upstream files. ToolFailureException results
now retain actionable provider text, explicit failed metadata and durable errors.
The complete nine-call, seven-tool workflow survives native file save/reload.
Seven exclusions stay enforced after restoring an earlier all-tool loadout.
Stable progress is 13/20 complete, 7 remaining. N physical HTTP is open.

Clean `75e02e64` closes N: all three physical HTTP workflows pass supported
contract comparison, each with 11 requests, nine correlated outcomes and an
actual CLI restart; strict same-Pi controls pass 3/3. Only the previously
documented native private new-file mode remains explicitly excluded. Progress
is 14/20 complete, 6 remaining. O full corpus and P reliability remain open.

Clean `acfbe782` closes O for the defined 456-case corpus: 395 passes (385
strict plus ten named JPEG equivalents), 61 explicit failed strict safety
comparisons and zero unresolved cases. Strict same-Pi control passes 456/456.
Progress is 15/20 complete; P reliability is in progress. Any later product
correction requires affected corpus revalidation before final publication.

## Previous milestone — Model and thinking configuration

On branch `milestone/model-thinking-configuration-parity`, the previously
reported 20/22 workflow checkpoints are complete, but they do not mean the
behavioral acceptance list is complete. The pinned-Pi implementation now
normalizes empty scope to unrestricted, applies selector edits to the current
session before save, preserves those edits on Escape, supports bulk/provider/
reorder controls, and refreshes the catalog while the selector remains open.
The settings picker supports per-model thinking defaults, supported levels,
clearing, persistence, and live current-model changes. Focused Linux tests pass
for these paths.

Additional process evidence sends two model changes and a thinking-level
change while a Bash tool is still blocked, then checks the continued provider
request, RPC state/event order and session entries. A separate Linux PTY test
changes a per-model default in the settings picker and verifies the next
OpenAI Responses request carries the saved `high` effort. These transitions,
settings reliability cases, and the tracked source audit are included in the
passing local acceptance and validation. The 93-case corpus does not model every
possible busy-run interleaving; native Windows/macOS are not claimed, and the
documented unauthenticated continuation fallback remains an adaptation.

Checkpoints A–T are complete. The unrelated PR #23 Dependabot update merged to
`main`; the milestone branch was synchronized with its merge. A loopback
listener race from PR CI was repaired, and the focused case passed. Final head
`4972f6eaf8f5a0ca2c773048fa918ab6bc23e91c` passed exact-head push and PR CI,
then PR #24 merged at `0d83418a31d84e530b099d98054739607fa3e3ad`.

## Completed prerequisite — Session JSONL Persistence and Crash Recovery Parity

PR #24 is verified merged at `0d83418a31d84e530b099d98054739607fa3e3ad`.
The branch `milestone/session-jsonl-persistence-recovery-parity` starts from
that merge commit. The source checkout `/tmp/pi-pinned-session-parity` and
`tools/parity/pi-reference.txt` confirm the unchanged Pi pin
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Required pinned Pi suites pass
126/126.

Two-run exact-pin evidence covers parsing/discovery boundaries, v1-v3
migration, graph/selected-leaf behavior, canonical context, three provider
continuations, rich-content/reasoning/extension round-trips, interrupted
assistant state, RPC projections, and three process-kill boundaries. The
migration oracle includes malformed compaction-index types and unknown fields;
it exposed a PiSharp mismatch, which is now corrected and covered by local
tests. Integral numeric tokens `1.0` and `1e0` now map to the same entry as
integer `1`, matching pinned Pi. PiSharp also preserves legacy JSONL bytes
during load.

Full local validation is complete: .NET 3,843/3,843, zero failures/skips;
format verification; warning-as-error build with zero warnings/errors; terminal
Python 66/66; parity Python 27/27; terminal Node 24/24; documentation paths
153/657. MCP conformance reports no regression against its classified baseline
(three known scenarios remain classified). The dedicated macOS native lease
job is part of CI. Inventory and receipt record all accepted safety/resource
differences; unexplained mandatory differences are empty.

PR #25 is merged at `897bc5778f87ed09ea42e48b260479ee6a6a218c`; that merge is
the base of the active multimodal milestone above.
