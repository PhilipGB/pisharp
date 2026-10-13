# Historical trusted project-extension UI replay — 2026-10-12

Recorded pins: Pi `c661bc574f9e9713cc2af567fc1ac00621377a0c`; PiSharp
`bddd5005dc65aa9f7de2f15750b66c1fdb2f2ada`. These describe this historical
capture; the [configured Pi reference](../../../pi-reference.md) selects new runs.

The reviewed [paired report](paired/report.json) and
[same-Pi calibration](calibration/report.json) record zero normalized terminal,
render and HTTP differences across 14 render frames in `120x40-dark-fullscreen`.
[Semantic assertions](paired/semantic-assertions.json) record the ordered trace
`configured`, `trust-undecided:true`, `trust-decision:true`, then
`trust-ui:tui:true:Continue:true:approved`.

Both recorded products make one provider request containing the trusted project
skill. Their normalized generated system prompts match at SHA-256
`7f4100f7c700f2dcfde0929ae2fa634d7242ada53df572e8e6f782fc0da9dd6a`.
The assertions record matching startup dialogs, prompt echo/submission and
completed/exit checkpoints, with no completed-run usage shown during work.

The working indicator appears in 14 Pi renders and three PiSharp renders;
animated-frame counts depend on scheduling. Raw terminal bytes differ, and the
original provider request differs at the generated documentation path.

Original notes and raw captures are preserved in the local ignored archive
listed by the [archive inventory](../../artifact-archive-2026-10-13/README.md).
Only the reviewed reports/assertions are shipped here. This storage review did
not rerun the historical scenario or establish current full parity.
