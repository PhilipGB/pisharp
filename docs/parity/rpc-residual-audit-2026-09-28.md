# Bounded RPC residual audit — 2026-09-28

Reference: current Pi `6f7551516b84278eb9da1c340c8e7bc66be1a6ba` (0.87.1), verified with `git ls-remote` on 2026-09-28. Scope was the public command union in `packages/coding-agent/src/modes/rpc/rpc-types.ts`, dispatch and command lifecycle in `rpc-mode.ts`, PiSharp dispatch in `src/PiSharp.Cli/Protocols/RpcMode.cs`, and the corresponding focused handlers. This is a bounded compatibility audit, not a full process differential of every command.

The current Pi union contains 33 distinct command names, and every name has a literal dispatch/handler reference under `src/PiSharp.Cli`. The checked paths have no source diff between the prior audit pin `2b0a123de98318c2ff8069661721ce0c3794c34e` and current main, so there is no newly added command or dispatcher change. Route presence alone does not prove every command's behavior; central workflows retain their individual process or deterministic evidence.

The command inventory is:

- Prompt/queue: `prompt`, `steer`, `follow_up`, `abort`, `clear_queue`, `new_session`.
- State/model/thinking: `get_state`, `set_model`, `cycle_model`, `get_available_models`, `set_thinking_level`, `cycle_thinking_level`, `get_available_thinking_levels`.
- Queue modes and compaction: `set_steering_mode`, `set_follow_up_mode`, `compact`, `set_auto_compaction`.
- Retry and shell: `set_auto_retry`, `abort_retry`, `bash`, `abort_bash`.
- Session and discovery: `get_session_stats`, `export_html`, `switch_session`, `fork`, `clone`, `get_fork_messages`, `get_entries`, `get_tree`, `get_last_assistant_text`, `set_session_name`, `get_messages`, `get_commands`.

## Core runtime and session gaps

- Session crash recovery, truncated-tail handling, unknown tool outcomes, no-replay guarantees, and cross-process/same-process lock waits still need a paired current-Pi process audit. The Linux path-alias lock race already has a PiSharp fix; broader recovery/concurrency behavior is the next implementation slice.
- Context estimates still differ and affect automatic compaction. The current-main split-turn process replay and its residuals are recorded in [the compact fixture](fixtures/rpc-compact-baseline.md). Provider-usage projection and overflow-recovery decisions remain open.
- Provider-specific streaming, cancellation, and failure behavior is not established across OpenAI Responses, OpenAI Chat Completions, and Anthropic. The raw Chat Completions tool-argument delta fixture covers one adapter only.

## Material interoperability gaps

- Pi's extension UI and resource lifecycle is broader than PiSharp's current extension command/tool surface. Address it in the extension/resource capability pass, not by expanding the RPC dispatcher alone.
- Session transitions have substantial PiSharp process coverage, but active recovery/failure transitions and broader session-manager concurrency still need current-Pi differential evidence.

## Narrow protocol-shape residuals

- PiSharp exposes generated instructions under `sections.preamble`; Pi separates `preamble`, `tools`, `rules`, `docs`, and `cwd`.
- Pi accepts truthy non-string `new_session.parentSession` values; PiSharp enforces its string contract.
- Partial tool-argument snapshots, some optional response fields, wording, and remaining event payload differences belong in the final compatibility audit unless a process comparison shows they block a caller workflow.

No missing command route or new RPC-only fix was identified. The next action is the paired current-Pi session-recovery/concurrency slice, then settings/resources and other major capability families. Do not treat this audit or a green RPC subset as overall parity.
