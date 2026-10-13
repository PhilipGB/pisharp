# Historical quiet-startup resource listing — 2026-10-12

Recorded pins: Pi `4270522473e677ddba7b093caa0064a3df51d459`; PiSharp
`ccc346725d5d39c74eca96c088a8fa7c66e6118a`. The fixture was
[quiet-startup resources](../../../../../../tools/parity/terminal/fixtures/quiet-startup-resources.json),
in a 120x40 dark fullscreen terminal. New runs use the
[configured Pi reference](../../../../pi-reference.md).

The reviewed [same-Pi calibration](calibration/report.json) records seven matched
render frames, terminal state, HTTP and raw bytes. The
[paired report](paired/report.json) records matching HTTP and raw HTTP traces
with no scenario errors. Full-screen parity remains unequal: 73,685 normalized
state/render differences, including 47,613 render differences. Raw bytes differ.

The [resource projection](resource-projection.json) records matching compact
startup and expanded Context/Skills/Prompts rows, including scoped user paths,
with the untrusted project skill hidden. That scoped projection does not establish
full-screen parity; product wording and help/layout residuals remain in the report.

Original notes and complete captures are in the local ignored archive listed by
the [archive inventory](../../../artifact-archive-2026-10-13/README.md). Historical
CI/test and broader follow-up claims in the original note were not reverified by
this storage review. The selected records are historical evidence, not a new run.
