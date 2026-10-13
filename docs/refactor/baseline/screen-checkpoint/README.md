# Screen rendering full checkpoint

H02 separates timed render scheduling, conversation content projections/live sources
and notification/auth-origin state from screen lifetime and frame composition.
TerminalScreen retains public validation, terminal modes/writers, shared locking,
viewport/editor/panel state and explicit shutdown order. TerminalRenderScheduler owns
pending/timer/timestamp state and locks the same gate on callbacks; presentation owns
source text, thinking visibility, indentation, user/skill/tool reflow and image
registration. Notifications own captured status and sign-in URL/copy state. Current
width/theme accessors preserve original lookup timing, including pre-lock authorization
formatting; setters that intentionally work after disposal retain their guards.
The screen is 1,114 lines from 1,409; responsibility/state isolation is the resolution,
not the count. No project/interface/public API identity change or test movement.

Pre-change rendering scope: 66. Suspend/resume characterization passes 67 before
scheduling extraction; corrected scheduler/architecture scope passes 73. Live theme/
thinking reflow+commit characterization passes 68 before presentation extraction.
Authorization origin/copy eligibility characterization plus presentation scope passes
87 before notification extraction; final focused rendering/architecture scope passes
91. Mechanical rename, script guard and missing using diagnostics remain; no behavioral
expectation changed. Syntax audit retains all 73 public/internal method/constructor
signatures and all 43 distinct display/control string literals, with stated limits.

Full final-source capture passes format/build with warnings-as-errors, 1,381 .NET rows
and zero skips, 24 Node and 47 Python tests, six architecture checks and tracked docs
validation (124 documents/387 references). Every process differential/virtual variant
retains its frozen outcome; invalid paid-classifier criteria remain baseline-unrunnable.
Official MCP retains all 269 identities: 261 passes/eight classified baseline failures.
All 33 complete terminal fixtures run calibration and paired: 66 reports/136 cases,
13 applicable assertions. All 1,858 original identities are preserved; 1,893 candidate
identities add 25 .NET display groups, nine Python guards and the docs-paths suite gate.

Initial paired model-picker matches all 20 rendered states, outside Stage 1 sets.
Its first unchanged complete repeat also matches render/terminal states but reports
seven HTTP arrival-order differences around SSE/load/model polling. Both attempts
remain under run-1/outside-baseline-first-attempts and outside-baseline-second-attempts.
A freshly rebuilt untouched baseline 064bcc7b CLI reproduces the frozen 20-versus-19
render difference (37,205 differences; HTTP matches). The control receipt distinguishes
the baseline CLI revision from the current harness revision reported by run.py.
The next complete unchanged candidate repeat returns the accepted render/terminal
difference. Exact matching was also observed before H02 in the H01 checkpoint;
these captures retain timing/ordering variance without widening frozen acceptable
sets or the four baseline-flaky classifications. No source changes during capture or
repeats. Known regular-mode/presentation/malformed-Markdown limitations remain.

Source manifest/patch, commands/environment, TRX, process/MCP/terminal evidence,
controls and assertions remain. Logs/TRX/patches are losslessly compressed with
hash manifests; bulk viewport omission hashes and compact assertions remain.
All 1,748 starting user files outside the authorized AGENTS pointer are unchanged;
test.pdf retains its frozen SHA-256. The preceding committed H01 metrics and an
unchanged Core metadata-default audit remain here. A post-capture docs-only correction
clarifies that tree algorithms are deterministic over nodes, while Append generates
default IDs/timestamps. It does not redesign that public convenience behavior.
H03 draft/characterization resources are proposed scratch work, not verified code.
H03 and all Section 23 review/preservation/cleanup/fresh-checkout phases remain.
