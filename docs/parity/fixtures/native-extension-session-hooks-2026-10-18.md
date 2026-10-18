# Native extension session mutation hooks — 2026-10-18

## Scope and source pins

- Base `main`: `eef6f3f83247758d397b4e313d2c26b0088ee9ac` (merged PR #12).
- Final implementation source: `967c31b2`.
- Branch: `milestone/native-extension-session-hooks`.
- Pinned Pi source: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`; unchanged for this milestone.
- Pi source inspected: `packages/coding-agent/src/core/extensions/types.ts`, `runner.ts`, `agent-session.ts`, `agent-session-runtime.ts`, `modes/interactive/interactive-mode.ts`, `session-manager.ts`, `core/compaction/`, and `docs/extensions/` at the pin. `AgentSessionRuntime.fork()` dispatches `session_before_fork` with its `position`; the pinned TUI `/clone` calls that same path with `position: "at"`, while `/fork` uses the default `"before"`. The pinned extension docs do not describe these hooks; behavior was established from the pinned types and implementation.

This is a bounded mutation-hook slice. It does not claim broad Pi extension parity. PiSharp keeps session transition ownership in the existing CLI/runtime boundaries, tree persistence in `ConversationSession`, and compaction planning/persistence/restoration in `ConversationCompactionCoordinator`.

## Pinned event contract

| Hook | Trigger and payload | Cancellation, order and precedence | Active identity and persistence | Completion/failure behavior |
| --- | --- | --- | --- | --- |
| `session_before_switch` | TUI and RPC replacement requests; outgoing session ID/file, `New` or `Resume`, and target file when known | Sequential source order; ordinary handler errors are attributed and isolated; `Cancel` stops dispatch; last non-null non-cancel result wins | Callback belongs to outgoing session. Target validation/preparation precedes commit; veto is resolved before old-session shutdown | Veto preserves outgoing session and emits no replacement start/shutdown lifecycle. Allowed transitions retain prepare-before-shutdown ownership, then adopt/start. |
| `session_before_fork` | TUI/RPC fork and clone requests; selected entry and `Before`/`At` position (`/fork` = `Before`, `/clone` = `At`) | Same ordered decision semantics; veto precedes fork side effects and active-run cancellation | Callback belongs to source session; clone uses the same pinned fork transition at `At` | Veto preserves active branch and creates no fork file. Allowed fork/clone uses existing fork/save/adoption/lifecycle path. `skipConversationRestore` remains deferred. |
| `session_before_tree` | Validated navigation target, old leaf, common ancestor, detached old-path entries to summarize, and summary-request flag | Same ordered decision semantics; cancellation prevents tree selection | The outgoing branch remains selected during the callback. Runtime selects the target, restores execution history, and saves through the existing owner | A veto, restore/save failure, or no-op emits no completion event. A successful, saved navigation emits one `session_tree` event with old and new leaf IDs. Summary injection/instruction/label overrides remain deferred. |
| `session_tree` | Completed navigation with old and new leaf IDs | Ordered observer dispatch; observer exceptions are source-attributed and isolated; host-owned 250 ms deadline applies after commit | Runs after canonical tree selection, history restoration, and persistence succeed; the deadline is independent of caller cancellation | Not emitted for veto, failed restore/save, or no-op. A committed completion event is not suppressed by cancellation after save; an unresponsive observer is cancelled and its public context revoked. This milestone does not produce a summary entry. |
| `session_before_compact` | Manual, threshold, overflow and virtual-model compaction; reason, retry flag, detached planned boundary/messages/previous summary/file operations, active branch entries and custom instructions | Same ordered decision semantics; cancellation prevents summary generation; last non-null non-cancel result supplies custom compaction | Callback belongs to the active session. Custom content is validated before canonical mutation; accepted output flows through the existing compaction append/save/history-restore path | Veto publishes the existing aborted failure event. Default summary reports `FromExtension=false`; accepted custom summary reports `true`. Invalid custom output fails without changing canonical session state and is diagnosed against its extension source. |

The pinned extension runner awaits handlers sequentially. A `cancel: true` result short-circuits; otherwise the most recently returned non-null result wins. Handler exceptions are source-reported and isolated so later handlers still run. Host cancellation remains distinct and propagates through the operation. PiSharp's callback-scoped public context is revoked when the callback completes or its owning token is cancelled, including when a handler ignores cancellation.

## Public contract and deferred fields

`PiSharp.Extensions.Abstractions` adds typed events, result records, `PiSharpExtensionSessionSwitchReason`, `PiSharpExtensionForkPosition`, immutable branch/preparation snapshots, and `PiSharpExtensionCustomCompaction`. `IPiSharpExtensionBuilder` gains additive registrations for the five hooks. Existing `Configure` and previous builder/context APIs remain unchanged.

Custom compaction supports summary, first retained entry ID, pre-compaction token count, bounded JSON details, and supported usage metadata. PiSharp validates the selected boundary against the captured active branch, checks non-empty summary and non-negative token counts, and uses the canonical compaction record. Pinned arbitrary custom metadata beyond the supported C# schema is not represented. Tree-summary injection, tree instructions/labels, fork restore override, extension-initiated session operations, and hooks outside this bounded slice remain unsupported.

## Acceptance and verification

- Session switch, fork, tree navigation, and compaction implementation: verified by the full .NET suite.
- Fork metadata: TUI and RPC fork requests dispatch `Before`; TUI and RPC clone requests dispatch `At`; deterministic tests verify clone veto before persistence/preparation.
- Tree restore cancellation: deterministic regression proves failed history restore rolls the selected head back, skips save/completion, and can be retried. A post-save cancellation plus permanently pending tree observer proves committed notification is attempted, the host deadline revokes context, and a later operation reacquires the gate.
- Cancelled terminal event observers: the dispatcher waits for callback-context revocation before cancellation escapes, while the extension task remains detached; the regression rejects subsequent session and control mutations and passed 10 consecutive focused runs.
- Manual, threshold custom, virtual overflow custom, physical overflow custom, and virtual-model compaction routes: verified by focused regression tests and the full suite. The manual custom result is restored through a new `ConversationRun` and observed in the resumed provider projection.
- External `.nupkg` built from a normal fixture project and referencing only public Abstractions: process verification covers custom compaction, RPC fork/clone veto, and TUI clone/tree veto/observation.
- Package replacement/removal, Configure rollback, callback cancellation/revocation, and collectible AssemblyLoadContext: passed in the full .NET suite.
- Compatibility with extension APIs from PRs #7–#12: passed in the full .NET suite.
- Full .NET suite: 2,335 passed, 0 skipped, with `TMPDIR=/tmp`.
- Focused session-hook/runtime/process tests: 32 passed. The cancelled observer/context-revocation regression also passed 10 consecutive runs. Architecture: 7/7. Node: 24/24. Python: 50/50. Documentation paths: 142 documents and 547 references verified. `dotnet format` and the solution warnings-as-errors build passed.
- MCP conformance: no regressions; the eight classified baseline failures remain unchanged.
- Final PR head SHA and exact-head push/PR CI run links: recorded in the PR description after both checks complete.

Broad extension parity remains `In progress`; this receipt records only the bounded hooks above.
