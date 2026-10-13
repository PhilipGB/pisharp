# Historical quiet-startup help comparison — 2026-10-12

Recorded pins: Pi `4270522473e677ddba7b093caa0064a3df51d459`; PiSharp
`ff180dac8f35731a8765b317ab37c16745d6ad39`, using
[quiet-startup resources](../../../../../../tools/parity/terminal/fixtures/quiet-startup-resources.json)
in a 120x40 dark fullscreen terminal. New runs use the
[configured Pi reference](../../../../pi-reference.md).

The reviewed [same-Pi calibration](calibration/report.json) records seven matched
normalized terminal/render frames and HTTP traces without scenario errors.
The [paired report](paired/report.json) also records no scenario errors and exact
HTTP traces, while full-screen parity remains unequal: 64,818 normalized
state/render differences, including 40,086 render differences and 440 diagnostic
control-boundary differences. Raw terminal bytes differ.

The original receipt states that compact configured-key action hints and resource
projections matched. That narrower statement is preserved as a receipt claim;
the report retains the full-screen failures and does not establish full parity.

Original notes and complete captures are preserved in the local ignored archive
listed by the [archive inventory](../../../artifact-archive-2026-10-13/README.md).
This review checked existing records without rerunning the historical scenario.
