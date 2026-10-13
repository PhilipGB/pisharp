# Historical extension resource reload differential — 2026-10-12

Recorded pins: Pi `04566631c2b51fb688486bc1f040f60d1518c126`; PiSharp
`8a1df81b8f33134800957d82a1978fe43bf9685a`. New verification uses the
[configured Pi reference](../../../pi-reference.md).

The reviewed [paired report](paired/report.json) and
[same-Pi calibration](calibration/report.json) record zero normalized terminal,
render and HTTP differences across 16 render frames. Neither records a scenario
error; raw terminal bytes differ.

The [behavior assertion](paired/behavior.json) records four chat requests per
product, equal expanded startup/reloaded skill and template messages, matching
collapsed/expanded skill presentation, visible theme diagnostics and reload status.

Original notes, captures and historical CI/test claims are retained unchanged in
the local ignored archive described by the
[archive inventory](../../artifact-archive-2026-10-13/README.md). This review
checked the report/assertion records, without rerunning historical processes or CI.
