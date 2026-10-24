# Session JSONL persistence and crash recovery parity

## Final local acceptance checkpoint (2026-10-25)

PR #24 merged at `0d83418a31d84e530b099d98054739607fa3e3ad`; the new branch is
based on that commit and Pi remains pinned at
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Required pinned Pi suites pass
126/126. Current local gates pass: .NET 3,843/3,843 (zero failures/skips),
format verification, warning-as-error build (zero warnings/errors), terminal
Python 66/66, parity Python 27/27, terminal Node 24/24, and documentation
validation (153 documents / 657 references). MCP conformance has no regression
against its classified baseline; three known scenarios remain classified.

The current interchange class passes 52/52. The focused parser, migration,
file-store, and catalog group passes 73/73. The exact-pin v1 migration oracle
now covers seven stable file-backed cases, including integral floating-point
index tokens (`1.0` and `1e0`), other numeric/non-numeric invalid compaction
indices, and malformed unknown fields. That comparison found and
corrected a PiSharp mismatch: numeric invalid indices are deleted by pinned Pi,
while string, null, and boolean values remain. PiSharp's file-backed legacy
load remains read-only and is tested byte-for-byte. A regression confirms
integral floating-point tokens map to the same entry as pinned Pi.

The exact-pin crash oracle covers three real kill boundaries, repeated twice,
with stable no-replay outcomes. RPC projection evidence covers selected branch,
inactive sibling exclusion, IDs/parents, latest labels and fork messages.
Documentation and the [capability inventory](session-jsonl-persistence-recovery-inventory-2026-10-24.json)
classify all accepted durability, resource and graph-safety differences; no
unexplained mandatory difference remains. The new branch is ready for commit,
push and draft PR publication. The exact-head push/PR results, including native
macOS lease coverage, will be recorded in the PR checks and description.

## Verified prerequisite and branch

PR #24 was rechecked at head `4972f6eaf8f5a0ca2c773048fa918ab6bc23e91c`
against base `f4f57ca754662222d2c0162b2758778eb2999a57`. Its exact-head push
and PR workflows passed, each running 3,780 .NET tests with no failures or
skips and passing MCP conformance. There were no review submissions or
unresolved review threads. PR #24 was merged by merge commit
`0d83418a31d84e530b099d98054739607fa3e3ad`; that commit is on `origin/main`.

The new branch `milestone/session-jsonl-persistence-recovery-parity` was created
from that exact main commit. The Pi reference file and clean source checkout
both identify `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`; the pin was not
advanced.

## Architecture and installed framework

The repository targets .NET 10 and currently references
`Microsoft.Agents.AI` 1.21.0 and `Microsoft.Extensions.AI` 10.10.0. The
Microsoft Agent Framework session guidance was reviewed. PiSharp's canonical
state remains `ConversationSession` / `ConversationTree`, with its existing
codecs and durable store; MAF remains the agent execution foundation. This
milestone does not serialize opaque MAF `AgentSession` state as Pi JSONL or
change MAF dependencies.

## Initial inventory (first audit snapshot)

The status rows below capture the first audit pass and are historical. The current status is maintained in the machine-readable inventory and summarized in the acceptance checkpoint above. The machine-readable [capability inventory](session-jsonl-persistence-recovery-inventory-2026-10-24.json)
tracks each functional family, pinned source, PiSharp owner, existing tests,
known differences, severity, planned oracle/regression, and current status. The
audit is incomplete. Existing implementations and tests are not counted as
source-pinned parity until refreshed against this exact Pi revision.

| Family | Current observation | Status |
|---|---|---|
| Headers and JSONL parsing | Pinned oracles cover header order, BOM, CRLF, blank and malformed lines, valid unterminated records, truncated tails, and a 16 MiB+1 row. PiSharp rejects valid non-header JSON before a later header, accepts an exact 16 MiB entry and skips oversized entries while retaining later rows. It preserves input bytes while Pi appends a newline after loading an unterminated tail. | Core parser and entry-size behavior verified; discovery scan bounds, large-file boundary and file-operation-specific header rules remain open. |
| v1 migration | Legacy links and compaction index are covered locally. A deterministic ID collision revealed a bug in pinned Pi's `migrateV1ToV2`: its collision set is never populated. PiSharp now retries generated IDs to prevent a duplicate/self-parent graph. | Intentional safety divergence, strict match false. |
| v2/v3 migration | The pinned file oracle and PiSharp regressions cover v1 compaction-index conversion, v2 hook-message conversion, stable v3 records, and repeated open/export behavior. | More malformed legacy values and migration-write failure atomicity remain open. |
| Graph restoration | Pinned source captures and PiSharp regressions cover last-physical-leaf selection, forward-parent behavior, orphan continuation, and append parent. Duplicate IDs and cycles remain fail-closed safety differences. | Nested branch/native round-trips and label semantics remain open. |
| Provider context and recovery | Pinned `buildSessionContext()` matched both selected heads; pinned provider captures cover completed and orphaned tool histories on all three APIs. An oracle exposed and a focused correction now matches Anthropic's `is_error=true` flag on a synthetic missing tool result. PiSharp child-process reopen and actual kill recovery tests pass for those APIs. | Broader provider-message corpus and RPC/CLI state projection remain under audit; full exact-body equality is not claimed. |
| Interchange and native persistence | Pinned and PiSharp round-trips now cover a rich linear session with user/tool/custom-message images, reasoning signatures, correlated tool call/result, extension-only state and custom-message details. Pi opens the actual PiSharp native export and its full active branch and provider context exactly match the pinned fixture. The fail-first native-message regression also corrected generated-export `thinkingSignature` loss. | A separate two-branch pinned fixture confirms extension custom state stays branch-local and outside provider context; the matching PiSharp native save/reload test passes for both selected heads (1/1). Additional interrupted-message and failure-boundary round-trips remain open. |
| Discovery and project restoration | A b06c1f2 discovery oracle and PiSharp regressions cover configured and default JSONL listing, shared-directory CWD filtering, missing-CWD fallback, recent selection, stale listing fingerprints, JSONL-preserving saves, and ID/name case behavior. A fail-first regression found PiSharp accepted differently cased ID prefixes; the resolver now matches pinned Pi's case-sensitive prefix comparison while exact-name matching remains case-insensitive. CLI resume and fork carry the selected fingerprint through the actual file read. Process tests cover both untrusted-to-trusted and trusted-to-untrusted switches and check target resources, settings, shell prefix and relative-file access in the next provider continuation (2/2). A factory-level test verifies project provider catalog rebinding for trusted and untrusted targets (1/1). Pinned Pi source review corroborates the trust/resource/provider lifecycle, while the executed upstream cross-CWD test directly asserts CWD. | Other active-run switch scenarios and discovery scan/resource boundaries remain open. |
| Atomicity and concurrency | Atomic publication, private permissions, hash fencing and same/cross-process leases exist. A new failure-injection regression confirms a conflicting target remains intact and the temporary snapshot is removed. | Broader write/cancellation/fault boundaries and macOS lease execution remain open. |
| Crash recovery and tool outcomes | Actual child-process tests now kill after durable intent but before wrapper invocation, during a tool after side effect but before outcome checkpoint, and after the tool outcome checkpoint. Restart checks verify unknown outcomes and no replay; the child-process suite passes 9/9. | Exact-pin Pi CLI crash/orphan-tool counterpart and wider extension/RPC outcomes remain open. |
| CLI/RPC/extensions | Resume, replacement, import/export and persisted extension state have implementation and process coverage. | Complete the named flows against the pinned command/session behavior. |
| Reliability/platforms | Linux process/lock coverage exists; macOS locking has not run natively. | Run deterministic large-file, fault, contention and repeated restart cases; do not claim macOS. |

## Evidence so far

- Offline build of the exact pinned Pi checkout passed.
- The 12 required pinned Pi session-manager, invalid-session, CWD, RPC JSONL,
  and share-export suites passed **126/126**.
- PiSharp `PiJsonlSessionInterchangeTests` passed **21/21**, including both
  new collision regressions at the initial checkpoint; after header-order
  coverage was added, the class passed **24/24**.
- The initial combined PiSharp session, recovery, conversation, catalog, import,
  and RPC replacement process filter passed **114/114**. The later focused
  session, recovery, file-store, interchange, catalog, and RPC filter passed
  **59/59** after the parser correction.
- Format verification passed. The warnings-as-errors build passed with
  `-m:1 /nodeReuse:false` and zero warnings/errors. One default parallel build
  invocation exited with an internal CLR error before compiler diagnostics.
- The deterministic collision oracle is recorded in
  [session-v1-id-collision-b06-2026-10-24.json](session-v1-id-collision-b06-2026-10-24.json).
  Pinned Pi returns duplicate `deadbeef` IDs for the constructed vector;
  PiSharp returns `deadbeef` and `cafebabe`, preserving a valid parent chain.
- The exact-pin header-order oracle is recorded in
  [session-jsonl-header-order-b06-2026-10-24.json](session-jsonl-header-order-b06-2026-10-24.json).
  Two independent calls to pinned `loadEntriesFromFile()` returned no entries
  when `42`, `[]`, or `{}` preceded a valid session header. A new regression
  failed before the correction for all three inputs; the interchange class then
  passed 24/24. The acceptance outcome matches; the API reports rejection using
  an exception instead of an empty list.
- The parser source differential and same-Pi calibration are recorded in
  [session-jsonl-parser-differential-2026-10-24.json](session-jsonl-parser-differential-2026-10-24.json).
  Eight pinned file cases were repeated twice. Pi repairs unterminated tails by
  appending a newline; PiSharp's importer remains read-only. Pinned Pi accepts
  and retains a 16 MiB+1 record; PiSharp accepts an exactly 16 MiB row and skips
  the oversized row without hiding a following valid record. The interchange
  class passed 36/36 at that checkpoint, including CRLF, malformed and truncated-tail recovery,
  BOM rejection, invalid session identity and entry-size boundaries.
- The graph source differential in
  [session-jsonl-graph-differential-2026-10-24.json](session-jsonl-graph-differential-2026-10-24.json)
  repeats five normalized graph loads twice against the pinned
  `SessionManager`. Three ordinary cases match PiSharp's selected leaf, active
  path and subsequent append parent. PiSharp rejects duplicate IDs and cycles;
  pinned Pi indexes duplicates with last-entry lookup, and cycle traversal is
  not invoked because its source walk has no cycle guard. These safety
  differences are explicit rather than counted as strict matches. The
  interchange suite passed 36/36 at that checkpoint and now passes 37/37.
- The migration file differential in
  [session-jsonl-migration-differential-2026-10-24.json](session-jsonl-migration-differential-2026-10-24.json)
  repeats five open/reopen cases twice at the pinned revision. PiSharp's v1
  compaction migration, v2 hook conversion, v3 preservation and canonical
  entry-record reimport tests match. The pinned loader also accepts v4 and
  string version `"3"`; PiSharp rejects them before import as strict header
  safety checks. The interchange class passed 36/36 at that checkpoint and now
  passes 37/37.
- The reproducible [`session-context-oracle.mjs`](../../../tools/parity/session-context-oracle.mjs)
  ran against pinned Pi and the committed v3 branch fixture. Its captured output
  in [session-v3-context-b06-2026-10-24.json](session-v3-context-b06-2026-10-24.json)
  matched the expected context for both heads (`f6666666`, `daaaaaaa`) on two
  independent runs. Provider continuation after restart is covered separately
  by the direct provider captures and PiSharp process tests below.
- The exact-pin [`session-jsonl-roundtrip-oracle.mjs`](../../../tools/parity/session-jsonl-roundtrip-oracle.mjs)
  uses the committed [v3 branch input](session-v3-branch.jsonl) and pinned
  `SessionManager.open()` / `serializeSessionBranch()`. Two independent runs
  at b06c1f2 preserve the selected leaf, parent-linked active branch, provider
  context and thinking level after Pi export/reopen. The capture is recorded in
  [session-jsonl-pinned-export-reopen-b06-2026-10-24.json](session-jsonl-pinned-export-reopen-b06-2026-10-24.json).
  The matching PiSharp regression checks those semantics through PiSharp import,
  canonical native save/reload, PiSharp export, and Pi reimport. Broader
  cross-runtime round-trip cases remain open.
- The native reasoning-signature regression failed before the correction because
  generated PiSharp message records omitted `thinkingSignature`. It now passes
  through native document save/reload, Pi JSONL export, and PiSharp reimport.
  The pinned [reasoning-signature fixture](session-jsonl-reasoning-signature-b06-2026-10-24.jsonl)
  and [two-run Pi capture](session-jsonl-reasoning-signature-b06-2026-10-24.json)
  confirm b06c1f2 retains the opaque signature in provider context through its
  own export/reopen operation. This capture isolates reasoning-signature
  semantics; the richer cross-runtime cases are described below.
- The rich-content fixture exercises a complete active path containing an image
  prompt, assistant reasoning/signature and tool call, a correlated result with
  image content, extension state excluded from provider context, a custom
  message with hidden display metadata and an image, and a final assistant
  message. `RichPinnedSessionFixtureRoundTripsThroughNativePersistenceAndPiJsonl`
  verifies identities, parent links, content, model/thinking state and context
  through PiSharp import, native save/reload, export and reimport. With
  `PISHARP_SESSION_JSONL_ORACLE_EXPORT_PATH` set, the test also emits the actual
  PiSharp JSONL export for the pinned oracle. The b06c1f2 capture reports stable
  same-Pi calibration, an exact semantic match to the fixture, and a successful
  Pi export/reopen roundtrip. Reproduce it with:

  ```sh
  PISHARP_SESSION_JSONL_ORACLE_EXPORT_PATH=/tmp/pisharp-rich-content-export.jsonl \
    dotnet test tests/PiSharp.Tests/PiSharp.Tests.csproj --no-restore \
    --filter FullyQualifiedName~RichPinnedSessionFixtureRoundTripsThroughNativePersistenceAndPiJsonl
  node --import /tmp/pi-pinned-source/packages/coding-agent/src/experimental/source-resolver.ts \
    tools/parity/session-jsonl-roundtrip-oracle.mjs /tmp/pi-pinned-source \
    docs/parity/fixtures/session-jsonl-rich-content-b06-2026-10-24.jsonl \
    /tmp/session-jsonl-rich-content-pi-oracle.json /tmp/pisharp-rich-content-export.jsonl
  ```

  The comparison report is in
  [session-jsonl-rich-content-b06-2026-10-24.json](session-jsonl-rich-content-b06-2026-10-24.json).
- The direct pinned provider capture is reproducible with
  [`session-provider-continuation-oracle.mjs`](../../../tools/parity/session-provider-continuation-oracle.mjs)
  and its [case input](../../../tools/parity/fixtures/session-provider-continuation-cases-2026-10-24.json).
  Pinned Pi produced stable requests twice for each required API. The paired
  [semantic comparison](session-provider-continuation-differential-2026-10-24.json)
  records the normalized ordered history. PiSharp's reopened completed-history
  test passed 3/3, and the actual child-kill recovery suite passed 8/8 at that
  checkpoint (the refreshed suite passes 9/9), with the unknown tool outcome
  represented in each API's next provider request and the side-effect marker
  unchanged.
- The source-pinned [orphan-tool capture](session-jsonl-orphan-tool-b06-2026-10-24.json),
  produced by [`session-jsonl-orphan-tool-oracle.mjs`](../../../tools/parity/session-jsonl-orphan-tool-oracle.mjs)
  from its [case input](../../../tools/parity/fixtures/session-jsonl-orphan-tool-cases-2026-10-24.json),
  repeats three API requests twice. Pi projects an orphaned call into a
  provider-only `No result provided` result; for Anthropic it sets `is_error` to
  true. The first new PiSharp assertion failed only for Anthropic. The focused
  three-API crash/recovery theory now passes 3/3 after `ObservedChatClient` marks
  the synthetic result as a `ToolFailureException`; the durable transcript and
  RPC message projection still contain no synthetic result.
- The current focused PiSharp session/recovery/interchange/file-store filter
  passes 76/76 with zero failures or skips. This includes the reopened completed
  tool history and actual child-kill recovery cases described above.
- The pinned configured-directory discovery oracle and comparison are recorded
  in [session-jsonl-discovery-b06-2026-10-24.json](session-jsonl-discovery-b06-2026-10-24.json)
  and [session-jsonl-discovery-differential-2026-10-24.json](session-jsonl-discovery-differential-2026-10-24.json).
  Two independent pinned runs agree: project A lists and selects only its local
  JSONL session, project B sees its own session, and missing-CWD and corrupt
  headers do not become candidates. PiSharp's catalog, startup, project-runtime,
  import and fork regression selection passes 48/48. Corrupt-file diagnostics,
  stale-snapshot fencing, and 128 MiB resource bounds are classified as safety
  differences. The refreshed oracle also verifies that pinned default-directory
  list, exact-ID lookup and recent selection include a session with no persisted
  CWD. `SessionCatalogTests.DefaultDirectoryIncludesPiJsonlWithoutPersistedWorkingDirectory`
  confirms PiSharp's default catalog lists and loads that session using its
  current-store CWD fallback; `SessionCatalogTests` passes 13/13. The oracle also
  covers exact ID and prefix case sensitivity plus case-insensitive name search.
- Pinned Pi's `AgentSessionRuntime` test
  `updates the runtime session cwd on cross-cwd session replacement` passed
  twice at b06c1f2. Both runs asserted that the session manager and replacement
  runtime resolve to the destination CWD. The first invocation inherited the
  default `~/.pi` agent directory and could not create its session folder in the
  sandbox; rerunning with two isolated `PI_CODING_AGENT_DIR` roots passed both
  times. PiSharp's new `RpcSessionSwitchRebindsProjectWorkingDirectoryBeforeNextToolCall`
  process regression passes 1/1: after switching from project A to B, the next
  provider request contains the result of reading B's relative `marker.txt`
  and does not contain A's marker. The compact comparison is recorded in
  [session-cross-project-switch-b06-2026-10-24.json](session-cross-project-switch-b06-2026-10-24.json).
  The RPC process regression now passes 2/2 across both trust directions and
  checks target resources/settings, shell prefix, relative file access and no
  source-private prompt leakage. A factory-level regression passes 1/1 for
  trusted project provider-catalog registration and removal on an untrusted
  target. Pinned Pi source review corroborates trust-aware settings/resource
  reload and provider registration from the target resource loader; the executed
  pinned cross-CWD test directly asserts CWD only. Discovery scan/resource
  boundaries and other active-run switch cases remain open. The pinned
  blocked-tool replacement test and PiSharp blocked-provider RPC process test
  each pass 1/1; both prove settlement precedes a valid switch, and PiSharp
  verifies the next provider request uses destination history only. See the
  [active-switch comparison](session-active-switch-b06-2026-10-24.json).
- The exact-pin [RPC projection capture](session-rpc-projection-b06-2026-10-24.json), generated by [`session-rpc-projection-pi-oracle.mjs`](../../../tools/parity/session-rpc-projection-pi-oracle.mjs), repeats the real Pi CLI twice without provider credentials. `get_state`, `get_messages`, `get_entries`, `get_tree`, and `get_fork_messages` agree across runs for a branched session with labels and an explicit selected leaf. The output excludes the inactive sibling from active/fork messages, retains all entry identities and parents, and applies the latest label to the selected tree node. The corresponding PiSharp projection/process selection passes 62/62. The upstream `rpc.test.ts` itself is credential-gated; this direct CLI oracle uses no live provider.
- The pinned `agent-session-runtime-events.test.ts` lifecycle suite passes 4/4 at b06c1f2. PiSharp RPC read/projector, session replacement, and mode tests pass 62/62, covering new session, invalid switch, valid blocked switch, fork during a blocked run, and settlement ordering.
- The pinned extension-state oracle is stable across two observations of both branch heads and confirms custom state is branch-local and absent from provider context. `ExtensionSessionStateTests.NativeReloadRestoresOnlyExtensionStateOnTheSelectedBranch` passes 1/1 across native save/reload for both branches.
- The aborted-assistant fixture round-trips at b06c1f2 across two observations with partial text, reasoning signature, usage, abort metadata and provider context intact. `PiJsonlSessionInterchangeTests.AbortedAssistantFromPinnedPiSurvivesNativeReloadAndPiExport` passes 1/1 through native file save/reload and Pi JSONL reimport.
- `ConversationSessionTests.CancelledStoreSaveWaitingForLeaseKeepsPersistedSnapshotAndCleansNoTempFiles` passes 1/1; cancelling a save blocked on a held lease preserves the existing bytes and leaves no snapshot temporary.
- Bounded stress regressions now cover a 2,048-entry Pi JSONL chain interleaved with 2,048 malformed rows, a 2 MiB correlated tool result through native and Pi JSONL round-trip, 2,048-entry selected-branch export/import with eight native save/reload cycles, 32 concurrent reads against atomic snapshot writes, and discovery across 128 project sessions. The combined five-case focused stress selection passes 5/5.
- Linux lease verification passes 4/4 for same-process contention, symlink aliases, cancellation while waiting, and cross-process tool-outcome publication. The symlink-alias case now runs on macOS too. `.github/workflows/ci.yml` has a focused `macos-latest` job for native flock and cross-process lease behavior; its exact-head result remains pending publication.
- The current 83-case focused durability/recovery selection passes with zero
  failures or skips. It includes the new `FailedAtomicPublicationKeepsTargetAndCleansTemporarySnapshot`
  regression, bounded oversized-JSONL read rejection, same-process/cross-process stale-writer and lease cases, the
  refreshed 9-case child-process crash/recovery suite, durable intent/outcome tests,
  and three-provider continuation. This is focused coverage, not the complete
  repository gate or a claim that every filesystem failure boundary is covered.
- The exact-pin process-crash capture in [session-crash-recovery-pi-b06-2026-10-24.json](session-crash-recovery-pi-b06-2026-10-24.json) repeats each of three boundaries twice with identical normalized results. Before invocation, Pi has persisted the assistant toolCall, with no side effect or toolResult. During execution after a side effect, it has no toolResult. After the result checkpoint, the correlated toolResult is present before the next provider request. Reopen never automatically replays the tool and a follow-up provider turn continues in all three cases. The probe uses disposable worker processes against pinned Pi runtime with a faux provider/tool; empty generated model-catalog modules were added only to the disposable clone because ignored generated catalogs were absent. This is a source observation, not a claim that Pi has PiSharp's durable intent/outcome journal.
- The combined PiSharp child-process crash/recovery selections pass 12/12 with zero skips. They cover kills before tool wrapper invocation, after a tool side effect but before its durable outcome, and after the outcome checkpoint; restart verifies unknown outcomes and no repeated side effects.
- The refreshed child-process crash suite passes 9/9. Its new
  `ChildProcessKillAfterToolIntentBeforeInvocationDoesNotRunOrReplayTool` case
  pauses after the intent checkpoint is on disk but before calling the wrapped
  tool, kills the child, and verifies restart reports an unknown outcome without
  creating or replaying the write side effect.
- A sparse file just over the 128 MiB import bound is rejected before its
  contents are allocated or read through discovery, recent-session lookup, or
  explicit load. The reader also rejects file growth during a read. The new
  catalog regression is included in the 83/83 focused selection.
- The first full .NET suite passed 3,809/3,810 with one failure in
  `RpcExtensionControlAbortTests`: its second settlement wait could match the
  previous run's event. The waiter now starts after the previous line count and
  the isolated test passes. The earlier full run, including the pinned-v3
  round-trip regression, passed 3,813/3,813 with zero skips. The interchange
  class later passed 38/38 after the reasoning-signature correction and now
  passes 39/39 with the rich-content case.
- The last format and warnings-as-errors build gates passed after the reasoning
  signature correction and before the rich-content test/oracle additions.
  Documentation path validation then passed for 153 documents and 651
  references. Python terminal tests passed 66/66, full parity Python discovery
  27/27, and terminal Node tests 24/24. A full .NET run and final format/build/
  documentation gates are still required after the latest additions. MCP's
  latest recorded conformance run reported no new regression against its
  classified baseline. Exact-head CI remains open until the draft PR is
  published.
- A second fail-first regression found that PiSharp's synthetic export
  `session_info` anchor could collide with an existing entry ID, producing
  JSONL that PiSharp itself rejected on import. `PiJsonlEntryIdGenerator` now
  retries collisions for both v1 migration and export anchors. Pinned Pi's
  general entry-ID helper checks its existing ID map; the anchor remains a
  PiSharp adaptation for persisting a selected leaf/name in Pi JSONL.

These are partial audit checks, not completion evidence for the required
behavior matrix. Broader differential families, cross-project and native
session comparisons, stress/fault coverage, publication and exact-head CI
remain open.

**Status: INCOMPLETE.** Continue the source-pinned inventory and resolve every
demonstrated mandatory difference. Do not claim overall Pi parity complete.
