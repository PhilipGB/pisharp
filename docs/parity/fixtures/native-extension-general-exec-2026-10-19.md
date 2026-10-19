# Native Extension General Process Execution — 2026-10-19

- Pin: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` (unchanged).
- Reviewed PR #17 head: `9182227d321d1e1d7871a6273f73534549835569`.
- Verified PR #17 push / PR CI: `39279759725` / `39279767767`, both successful.
- Actual PR #17 merge and PR #18 base main: `0737ee30524ebc86b845f2b1b5c19b9d481db212`.
- Branch: `milestone/native-extension-general-exec`, created from fetched post-merge `origin/main`.
- PR #17 was marked ready and merged with `--merge --match-head-commit` after unchanged head, base, mergeability, checks, commits and empty review/comment lists were verified. GitHub reports merged; the merge is contained in main and its tree matches the reviewed tree.

## Source and gap discovery

Inspected at the exact pin:
[exec.ts](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/exec.ts),
[types.ts](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/extensions/types.ts),
[loader.ts](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/extensions/loader.ts),
[runner.ts](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/core/extensions/runner.ts),
[extensions guide](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/docs/extensions.md), and
[waitForChildProcess](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/coding-agent/src/utils/child-process.ts).

| Area | Pinned Pi | Pre-milestone PiSharp | Implemented increment |
|---|---|---|---|
| Invocation | `spawn(command,args)`, `shell:false`, ignored stdin, inherited environment | Bash shell strings with PATH/session environment changes | Direct `ArgumentList`; inherited environment, closed stdin |
| Directory | Loader captures cwd; options override | Shell captures cwd; callback context expires | Package executor captures directory; explicit overrides validated |
| Output | Separate unlimited strings; per-chunk decoding | Merged shell presentation/truncation buffer | Separate incremental UTF-8, explicit resource failure |
| Status | Nonzero result; null signal exit becomes zero; start error becomes one | Tool failure or bash cancellation result | Nonzero result, killed zero result, explicit start exceptions |
| Cleanup | Root SIGTERM; attempted force kill; 100 ms sliding idle after exit | Process group/tree kill and bounded shell drain | Shared process launch/tree/drain helpers; deadline and caller cancellation |
| Ownership | API activity check; no running-exec registry in loader/runner | Collectible package registration ownership | Package executor invalidation and active-process cancellation on unload |

Discovery was recorded before implementation in the ignored capture's gap matrix.
`ShellExecution`, `ShellOutputBuffer`, `BashExecutionResult`, public callback
contexts/capabilities, the adapter/mapper/loader, registration checkpoint,
selection, and existing shell/extension tests were inspected. Runtime uses
MAF 1.21.0; this increment changes no MAF dependencies or provider owners.

## Implementation and boundaries

`IPiSharpExtensionBuilder.ProcessExecutor` is an additive default interface
property. Its public executor, options (`WorkingDirectory`, `TimeSpan? Timeout`)
and result (`Stdout`, `Stderr`, `ExitCode`, `Killed`) live in Abstractions.
Executors are captured during Configure and reused after callback revocation.
The package handle owns revocation, cancellation and draining on failed
configuration, replacement, removal and disposal. Unload waits at most 15 seconds;
execution cleanup failures and that deadline are isolated from catalog changes.
Pending host-owned executions retain cancellation resources, finish termination
and stream cleanup, and observe late task faults after unload returns. Retained
host handles do not retain package ALCs. No enum/delegate/assembly identity changed.

The small `ProcessLifetime` extraction supplies launch, tree termination and
pipe-drain helpers to both shell and direct execution. A controlled blocked-reader
regression exposed the need to check buffered Unix pipe bytes before treating
100 ms without a read continuation as idle. The shared helper now retains those
bytes and still releases quiet inherited handles. Shell output buffering,
normalization, environment rewriting and truncation stay in their existing
owners. Native execution bypasses model tool permission routing and preserves
project trust checks before loading package code.

Verified deviations: pre-start cancellation throws without spawning; missing
executables/directories throw rather than producing a fabricated status;
UTF-8 is decoded incrementally; output above 64 MiB combined raw bytes fails
explicitly with cleanup. Host termination returns killed/zero after cleanup; cancellation during descendant
draining preserves an already-exited root's original status.
Unix process groups use `setsid` where installed and stronger tree cleanup than
Pi's root-only kill; SIGTERM is followed by SIGKILL after five seconds if needed.
Naturally detached programs remain extension-owned. Unexpected external Unix
signals retain .NET status, typically 128 plus signal, while Pi maps null to zero.
Relative directory overrides resolve against the captured package directory
rather than Node's current process directory. Windows and hosts without `setsid`
use .NET live-tree enumeration.
If the root has already exited, that fallback cannot find surviving descendants;
they remain extension-owned. Quiet inherited pipes are closed after the idle
grace; cancellation bounds active draining, with drain failures surfaced to the
caller. `Killed` records a termination request, not proof that all descendants
exited. This narrower guarantee is consistent with pinned Pi's root-only kill.
Windows/macOS behavior is unverified; this milestone verifies Linux. No shell, PTY, sandbox,
remote execution, generic event API or deferred extension milestone was added.

## Regression evidence

Deterministic .NET child fixtures exercise literal spaces, quotes, empty and
Unicode arguments, shell metacharacters, inherited environment/closed stdin,
default/relative/absolute/invalid cwd, missing executable, independent empty,
stdout/stderr/nonzero outputs, ANSI/CR/NUL/BOM retention, simultaneous large
streams, split UTF-8, explicit capture overflow, pre-start/running cancellation,
timeouts including SIGTERM-resistant processes, natural exit and late timer
isolation, repeated calls, descendants, inherited idle/active handles, disposal,
compiled command/tool/event callbacks, retained Configure handle, trust,
configuration rollback, reload/removal and collectible ALCs. A fresh CLI process
installs the external `.nupkg`, runs its startup event and invokes its RPC command.
Historical API packages are also covered by the complete suite.

## PR #18 teardown review corrections

The review covered head `b6ea9e9c0ef2a68b0a9ea9c26d4c7887e3c543cd`.
Both CI runs on that head (`39296257742`, `39296272272`) subsequently completed
successfully; fresh CI is required for the corrections.

- `UnloadDeadlineReleasesPackageWhileOwnedExecutionContinuesStopping` loads and
  configures the external compiled package, starts a real TERM-resistant child,
  and uses an internal zero-duration unload wait to reach the deadline while
  termination is pending. Unload returns without throwing, revokes the executor,
  clears package ownership and leaves the ALC collectible before the child has
  stopped. The owned execution subsequently completes escalation and cleanup.
  This regression runs on Linux with `setsid`.
- `CleanupWithoutProcessGroupAfterRootExitClosesPipesButCannotFindDescendant`
  launches a real ungrouped root which spawns a quiet descendant inheriting both
  pipes and exits with code 7. With root exit explicitly awaited, the existing
  fallback leaves the descendant alive and finishes pipe cleanup. The fixture
  finally terminates the descendant itself. It is written for all hosts and was
  executed on Linux; no native Windows/macOS result is claimed.

The correction uses existing process ownership and documents its limit; it adds
no process subsystem. The public API and pinned source revision are unchanged.

## Local validation

| Gate | Result |
|---|---|
| Focused process/package/external RPC/historical API | 35 passed, zero failed/skipped |
| Full .NET suite | 2,484 passed, zero failed/skipped |
| Architecture | 7/7 |
| Node terminal | 24/24, zero skips |
| Python terminal parity | 50/50 |
| Documentation paths | 147 documents / 568 references; passed |
| MCP conformance | Exit 0; the same eight classified baseline failures, no new regressions |
| Formatting | Passed |
| Warnings-as-errors build | Zero warnings/errors |

Commands: `dotnet format PiSharp.slnx --verify-no-changes --no-restore`,
`dotnet build PiSharp.slnx --no-restore --warnaserror`, and
`TMPDIR=/tmp dotnet test PiSharp.slnx --no-restore`.
Local .NET commands used `DOTNET_PROCESSOR_COUNT=2` after default parallelism
aborted inside the CLR before compiler diagnostics. Restore retained audit and
used a writable NuGet HTTP cache. The documented `TMPDIR=/tmp` resource-path
workaround was used; unrelated resource assertions were untouched. SDK 10.0.112,
Node 24.21.0, Linux. The process regression suite passed three additional
consecutive stress runs while formatting was active.

A normal Debug `dotnet pack --no-build --no-restore` verifies the fixture's
manifest, runtime configuration and Abstractions-only dependency boundary.
Initial product source commit: 5d36e27deb255db5f9c3b78a2d95884f2c22ec5d.
Teardown correction commit: 4bbc133d25ae248c6246e6936d5fe714fca5f2f5.

Final branch SHA and exact-head CI IDs belong in the draft PR description and
completion report; no follow-up documentation commit is needed for those IDs.
Broad extension and orchestration parity remain `in_progress`.
