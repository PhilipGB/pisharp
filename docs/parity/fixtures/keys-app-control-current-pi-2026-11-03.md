# Milestone Receipt: Terminal Application-Control Parity

## Status

Complete pending exact-head CI. `keys-app-control` is marked `verified` after the A01–A16
contract, paired PTY differential, focused tests, full suite, format, build and the executable
acceptance verifier all passed. The draft PR remains open and unmerged.

## Pins

| Item | Value |
| --- | --- |
| Pinned Pi | `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` |
| PiSharp implementation commit | `fcd4f53c718efa943695a0b3e542ea132bf20e14` |
| Starting `main` | `78a2b044757ea548426521e45795bc8268a84513` |
| Acceptance manifest | `docs/parity/fixtures/keys-app-control-acceptance-2026-11-03.json` |
| Pinned-Pi oracle | `docs/parity/fixtures/keys-app-control-pi-oracle-2026-11-03.json` |
| Compact paired checkpoints | `docs/parity/fixtures/keys-app-control-checkpoints-2026-11-03.json` |
| Verifier | `tools/parity/verify_keys_app_control.py` |

PR #37 was confirmed merged as the starting `main`; the milestone branch is
`milestone/keys-app-control`.

## Scope

Idle, streaming, Bash and Bash-entry Escape; completion/overlay/search precedence; Ctrl+C clear and
500 ms double-press exit; Ctrl+D empty-editor exit and forward delete; Ctrl+Z suspension and
foreground resumption including a resize while suspended; cancellation of active generation,
interactive Bash and tool process trees; configurable bindings and context precedence; shutdown and
terminal restoration.

Out of scope: complete session-tree selector rendering, broad TUI rendering parity, provider parity,
and Unix job control on Windows.

## Audit and confirmed gaps

Before changes, PiSharp cleared any draft on idle Escape, had no double-Escape handling, aborted on
Ctrl+C during a run, swallowed input while interactive Bash blocked the prompt, and could not decode
legacy ESC ESC as two interrupts. Ctrl+C clear/exit, Ctrl+D empty exit/forward delete, Ctrl+Z
suspend/resume, streaming abort plus queue restoration, and terminal-mode restoration already
existed and are retained.

Implementation:

- `TerminalEditor`: context-aware idle Escape (completion dismissal, Bash-entry clear, nonempty
  no-op, 500 ms double-Escape tree/fork), shared 500 ms Ctrl+C window, active-run Ctrl+C clear,
  interactive Bash input monitoring, cancellation and pending-submission handoff.
- `TerminalInput`: legacy ESC ESC disambiguation from ESC + CSI/OSC/SS3; Kitty ESC equivalence.
- `UserSettings`, `EditorKeymap`, `TerminalApplicationActions`: `doubleEscapeAction`
  (`fork|tree|none`, default `tree`, project overlay) and configurable `app.session.tree`.
- `TerminalConversationExecution`: interactive Bash cancellation through the existing
  `ConversationRun` token and process-group cancellation.
- `job_control.py`: resize while suspended, `SIGWINCH`, and `resized:<rows>:<columns>` evidence.

No parallel application-control state machine was added; conversation ownership, cancellation
tokens, tools and shutdown remain in the existing runtime paths.

## Pinned-Pi oracle

`keys-app-control-pi-oracle-2026-11-03.json` records seven sources with exact anchors and 16
deterministic scenarios for idle Escape, Bash-entry Escape, Ctrl+C clear/window boundaries, Ctrl+D
empty/grapheme/selection, double-Escape tree/fork/none, configurable bindings and legacy/Kitty
equivalence. `AppControlParityTests.MatchesPinnedPi` replays all 16.

## Paired real-PTY differential

Six fixtures: `terminal-app-control-tree`, `-fork`, `-none`, `-bash`, `-suspend`, and the existing
`terminal-abort-stream`. The run covers 21 cases:

- dimensions 80x24, 120x40 and 100x30;
- fullscreen and regular modes;
- doubleEscapeAction `tree`, `fork` and `none`;
- idle Escape, Ctrl+C, Ctrl+D forward delete, Bash-entry Escape, tree/fork dismissal, interactive
  Bash cancellation and a later command, streaming abort, suspend/resize/resume and five exit paths.

Same-Pi calibration reports `0` differences for all 21 cases. The paired report retains 38 named
checkpoints; every compared checkpoint matches in editor text and logical cursor, and every exit
checkpoint restores the normal screen, a visible cursor and no temporary private modes. Provider
request counts and user texts match exactly:

- tree/fork/none: one `first app control prompt` request each;
- abort-stream: one `Send a response that can be aborted.` request;
- Bash and suspension fixtures: no provider requests.

Unavoidable full-stream differences are classified as non-required: transient loader/render frames,
footer colors and normal-screen cursor rows, and the tree selector's boxed PiSharp layout with
retained tool entries versus Pi's unboxed Session Tree. Trigger, 500 ms gating, empty-editor
condition, active-session retention and dismissal handoff are verified.

## Child-process and terminal restoration

- `BashProcessLifetimeTests.CancellationKillsChildAndDetachedGrandchildBeforeSettling` verifies the
  cancellation result, child and detached grandchild exit, and later Bash execution.
- `AppControlPtyTests.EscapeCancelsInteractiveBash...` verifies no marked child survives, the prompt
  accepts input immediately, and a later `!echo` executes.
- `AppControlPtyTests.ShutdownPathsRestoreTheTerminal...` verifies `/quit`, Ctrl+D, double Ctrl+C and
  Bash-cancel-then-exit restore the captured `stty -g`, reactivate and restore bracketed paste, and
  exit zero.
- `TerminalSuspendPtyTests` verifies SIGTSTP restores canonical/echo/signal flags, a resize while
  suspended is applied, SIGCONT redraws the draft, later input is neither lost nor duplicated, and
  the final exit restores the original terminal.
- `ExtensionSessionStateTests.SessionLifecycleCallbacksAreOrderedFailureIsolatedAndScopedToTheRun`
  verifies shutdown callbacks run once and return empty on a second call.

## Verification gates

| Gate | Result |
| --- | --- |
| A01–A16 required cases | 16/16 pass |
| Pinned-Pi oracle scenarios | 16/16 |
| Paired PTY cases / checkpoints | 21 / 38 |
| PTY evidence records | 12 |
| Focused .NET tests | pending |
| Full .NET suite | pending |
| Format | `dotnet format PiSharp.slnx --verify-no-changes` pass |
| Build | `dotnet build PiSharp.slnx --warnaserror` 0 warnings/0 errors |
| Acceptance verifier + negative controls | pending |
| Exact-head CI | pending |

## Non-required differences and limitations

- Full raw terminal streams, transient loader frames, footer colors and normal-screen cursor rows
  are not byte-identical.
- PiSharp's tree selector is boxed and includes retained tool lifecycle entries; complete selector
  visual parity is outside this milestone.
- Windows reports `Suspend to background is not supported on Windows` and has no default
  `app.suspend` binding; Unix job control is not claimed there.

## Command summary

```sh
python3 tools/parity/app_control_evidence.py
python3 tools/parity/verify_keys_app_control.py --pi /path/to/pinned/pi --full-trx <full.trx>
python3 tools/parity/verify_keys_app_control.py --self-test
python3 -m unittest discover -s tools/parity/terminal -p 'test_*.py'
```
