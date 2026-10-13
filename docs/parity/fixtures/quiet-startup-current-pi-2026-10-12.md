# Historical quiet-startup receipt — 2026-10-12

The local receipt reports Pi `4270522473e677ddba7b093caa0064a3df51d459` and
PiSharp `ff180dac8f35731a8765b317ab37c16745d6ad39`, using the
[quiet-startup fixture](../../../tools/parity/terminal/fixtures/quiet-startup-resources.json)
in a 120x40 dark fullscreen PTY. This is historical evidence; the
[configured Pi reference](../pi-reference.md) defines subsequent verification.

The recorded same-Pi calibration matches seven normalized terminal/render frames
and HTTP traces without scenario errors. The paired run records matching HTTP
traces, compact action hints and compact/expanded Context, Skills and Prompts rows.
Full-screen presentation remains different: 64,818 normalized state/render fields,
40,086 rendered-cell fields and 440 control-boundary diagnostics across seven
frames. Product wording, clipping, transcript placement and terminal control
sequences remain limitations; raw bytes differ.

The [reviewed historical receipt](resources-terminal-current-pi-2026-10-12/current-42705224-ff180dac/quiet-startup-resources/README.md),
[calibration report](resources-terminal-current-pi-2026-10-12/current-42705224-ff180dac/quiet-startup-resources/calibration/report.json)
and [paired report](resources-terminal-current-pi-2026-10-12/current-42705224-ff180dac/quiet-startup-resources/paired/report.json)
are now committed. The original note and raw streams remain in the local ignored
archive recorded by the [archive inventory](artifact-archive-2026-10-13/README.md);
those streams are unavailable in a clean checkout. This storage review did not
rerun the scenario or establish a new full-screen parity claim. The committed
fixture and permanent reference mechanism support intentional fresh verification.
