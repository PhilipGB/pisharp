# Virtual-model foundation evidence — 2026-09-29

Reference: Pi `1b347794e2a630e4359f2584f4eea388145d0ddf`, refreshed from remote HEAD. This is a focused foundation checkpoint, not completion of virtual models or full Pi parity.

`VirtualModelRoutingTests`, `VirtualModelControllerTests` and `VirtualModelProcessTests` pass 21/21 locally. Current Pi's `test/virtual-models.test.ts` and `test/suite/virtual-models.test.ts` pass 27/27. The fresh reference checkout required `npm ci --ignore-scripts`, hydration of generated model data, and `npm run build` for CLI process execution; generated catalog values reflect that hydration and were not imported into PiSharp.

The paired CLI/RPC replay is [the captured projection](virtual-model-routing-process-2026-09-29.json). Both processes retain `test-router/auto`, dispatch `physical/small`, `physical/large`, `physical/small`, preserve physical provider/model/API in assistant messages, and give routers `user`, `continuation`, `user`, state `0`, `1`, `2`, and previous model absent, `small`, `large`. Run `python3 tools/parity/virtual-model-routing-differential.py --pi /path/to/built/pi --output /tmp/virtual-routing.json` against a built PiSharp checkout including its test fixture assembly. This bounded replay does not cover retry, context sizing, branches or authentication.

Fail-first evidence exposed shared-client dispatch replacement during a state save (first client called 0 times instead of 1), absent physical identity in reconstructed streamed history, absent declared thinking-level filtering, JSONL fallback to logical identity after native serialization, and RPC rejection of `pi-virtual`. The process replay also exposed logical API leakage into live RPC assistant messages. These were fixed without adding a global routing lock.

Concurrency: ordinary `ConversationRun` and `PiAgent` runs have execution gates; `PiAgent.SummarizeAsync` uses the routed client outside that run gate. Every route now dispatches through its own validated client. Retry/completion attribution travels in a per-attempt execution object, and direct requests do not overwrite the agent loop's route lifecycle state. Raw tool-delta capture waits for the selected physical client. The process test verifies raw deltas from that client. Future classifier/Codemode calls still need their own integration evidence.

| Requirement | Current evidence / remaining work |
| --- | --- |
| Logical selection and user/continuation routing | Provider-loop tests and paired CLI/RPC replay pass |
| Retry and failed physical request | Deterministic provider-loop transient failure switches physical models; broader partial/error projection remains open |
| Direct routing and cancellation | Focused tests; direct state omitted/ignored; cancellation during router await passes; cancellation after an ignoring router still needs coverage |
| Physical provider/model/API and usage/pricing | Provider-loop, controller and process tests; RPC and native/JSONL message identity pass; direct compaction usage attribution remains open |
| Physical thinking and image capability | Deterministic boundary tests and controller clamping pass; image resize compatibility still needs physical-policy review |
| Previous successful response | Persisted metadata and failed-assistant exclusion tests; process continuation/next-turn replay passes |
| State replacement/evolution | State persisted before provider failure; process state advances three requests; fresh equal JSON versus returned existing state semantics remain open |
| Branch-local state, navigation, persistence, resume, fork/clone | Tree selection, snapshot, native parse, JSONL import/export, fork and clone-like ForkInto tested; actual resumed/forked/cloned routed process and model-changing branch transitions remain open |
| Physical context policy and compaction | Controller policy selection covered; enforcing newly routed first-request budget and retaining chosen route during compaction remain open |
| Model change away/back and TUI status | Open; no verification claim |
| Classifiers / Codemode model access | Open; begin after material virtual-model semantics and evidence |

Next implementation: add fail-first coverage for a virtual model with unknown logical limits routing to a smaller physical window on its first request; extract route-aware context projection so compaction uses the physical policy before dispatch and keeps that chosen route. Then verify branch switching/resume/fork/clone and direct compaction usage identity. Preserve the full task scope in TODO and the execution ledger.

Validation: format verification passes; serialized warnings-as-errors build passes with zero warnings/errors; full local suite passes 887/887 with 0 skipped. The initial run under inherited TERM=dumb had 886 passes and one theme PTY timeout; that isolated test also timed out under TERM=dumb, then passed with TERM=xterm-256color. The full rerun uses TERM=xterm-256color without skips or test changes. New exact-head CI is pending.
