# Native extension state and interactive controls

## Scope and source pins

This bounded implementation starts from PR #8 merge commit
`d9156e53c4822e7b98226df6170b29c61bc87eb1` on `main`, on branch
`milestone/native-extension-state-interactive-controls`. The configured Pi
reference remains `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Product source
commit: `2c24b4fdb9b79bb7b73c935cc4074ce6da8aa919`.

The milestone adds durable extension-owned session entries, branch-aware public
session reads, specific session start/shutdown callbacks, read-only mode and UI
availability metadata, safe basic UI calls, and compiled-extension TUI
shortcuts. Broader extension parity remains open; no capability status is
promoted.

## Public contract and persistence

`PiSharp.Extensions.Abstractions` adds a stable session projection containing
session identity, name, head ID, and ordered custom entries on the active tree
path. Entries expose canonical IDs, custom types, JSON data, timestamps, and
source paths without exposing Runtime session types. `AppendEntryAsync` uses
the canonical session store and returns only after save; failed saves roll
back the in-memory append. Repeated identical values retain distinct IDs.
Historical entries remain part of the session after package update/removal.
Callback-scoped bridges are revoked when their owning callback ends.

`ExtensionSessionStateTests` verifies ordering and provider-context exclusion,
save rollback and retry, New-session isolation, fork/compaction branch behavior,
ordered and failure-isolated lifecycle dispatch, persistence during a callback,
and context revocation. In particular, the provider regression appends
`SECRET_STATE_MARKER`, checks the provider request does not contain it, and
recovers it from the public branch projection. Compaction retains canonical
entries without projecting them into model context.

`NativeExtensionStateProcessTests.PackedExtensionStateSurvivesFreshRpcProcessAndStaysOutOfProviderContext`
packs a normal external NuGet package, installs it through the package CLI,
appends repeated state in an RPC process, observes it in a provider turn, and
starts a fresh `--continue` process to recover it. It also verifies canonical
session JSON, headless UI results, lifecycle reasons, a New session with no
inherited entries, and that state markers do not enter provider input.

## Lifecycle, UI, and shortcuts

Session callbacks receive `Startup`, `Reload`, `New`, `Resume`, or `Fork` start
reasons and `Quit`, `Reload`, `New`, `Resume`, or `Fork` shutdown reasons where
the host transition applies. Startup dispatch occurs after session restoration
and before extension resource discovery, commands, or provider work. Reload
shuts down the old run before registration replacement and starts the new run
before rediscovery. Individual lifecycle failures are diagnosed and do not
prevent later handlers from running.

Public contexts expose stable mode and `HasUserInterface` values. The UI adapter
reuses PiSharp's existing `SelectAsync`, `ConfirmAsync`, `InputAsync`, and
`Notify` operations. Unavailable headless dialogs return the established
cancelled/false results; cancellation propagates. RPC does not open terminal
dialogs. `PiSharpExtensionUiTests` cover interactive and unavailable behavior.

`PiSharpShortcutKey` uses the existing key parser/keymap path. Reserved
application/editor keys retain built-in behavior with source-aware diagnostics;
ordinary built-in collisions are diagnosed and extension bindings take
precedence; later resolved extensions win extension-to-extension collisions.
Reload and removal release shortcut and lifecycle ownership. The catalog test
asserts replaced and removed registrations disappear and the replaced
collectible load context is collected.

The Linux PTY acceptance
`NativeExtensionShortcutPtyTests.ExternalPackageShortcutRunsAcrossReloadAndLeavesTuiEditorUsable`
loads a packed external package, invokes its shortcut in the real TUI, updates
v1 to v2 through reload, confirms the new shortcut behavior and lifecycle
ordering, removes the package, and verifies the editor still accepts input.
This uses deterministic local input and no provider credentials.

## Compatibility and verification

The existing PR #7 API-v1 compatibility fixture remains in place. Public API
additions are additive and external packages continue to reference only the
Abstractions package plus their own dependencies. PR #8 command/tool/message,
resource-discovery, context-transform, and tool-hook paths remain covered by
the existing suite.

Final local verification passed on the final product source: restore with
`NuGetAudit=false` (the sandbox's global NuGet audit cache is read-only),
format verification, warnings-as-errors build with zero warnings/errors,
**2,237/2,237** .NET tests with zero skips, architecture **7/7**, Node **24/24**,
Python **50/50**, and docs validation (**138 documents / 532 references**).
`TMPDIR=/tmp` is required for the repository's absolute temporary-path
assertions. `NativeExtensionCatalogTests`, the external state process test, and
the shortcut PTY test pass together **8/8**. MCP conformance exits successfully
with no regressions against its eight classified baseline failures.

Draft PR [#9](https://github.com/PhilipGB/pisharp/pull/9) is open. The
exact-head push CI passes both required jobs on `65b81dd20658d9c2a364b76a9e6cff3aae3c9604`
([run 39039746664](https://github.com/PhilipGB/pisharp/actions/runs/39039746664)).
The PR merge-result CI also passes both jobs
([run 39039758081](https://github.com/PhilipGB/pisharp/actions/runs/39039758081)).

Deferred work remains effective settings snapshots, CLI flags, advanced UI,
session mutation/control, provider/model registration, and broader extension
parity.
