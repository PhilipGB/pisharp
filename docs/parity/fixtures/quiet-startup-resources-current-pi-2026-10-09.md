# Quiet startup resource details — 2026-10-09

Paired at Pi `f1b2e77f5b13b2a199b1052cb79c235451afe7d7` and PiSharp `359f14991abb3ddcc01fa1b437a0699b17a0762a` with the `quiet-startup-resources` PTY fixture. The compact startup view shows model scope and Context, Skills, and Prompts summaries; `Ctrl+O` expands the startup help and resource paths. The seven-frame scenario completes without errors and its HTTP traces match.

The full terminal capture still has 52,812 normalized state differences and 25,096 rendered-cell differences. The remaining mismatches include product branding, startup/help wording, color and full-screen layout. Raw streams and control-boundary diagnostics are retained in the comparison capture. A same-Pi calibration at the same heads reports zero state/render differences and byte-identical output, so the paired mismatch is repeatable. See the [machine manifest](quiet-startup-resources-current-pi-2026-10-09/evidence.json), [paired report and capture](quiet-startup-resources-current-pi-2026-10-09/comparison/report.json), and [same-Pi calibration](quiet-startup-resources-current-pi-2026-10-09/calibration/report.json).

This verifies the resource-summary and expansion behavior for the tested startup fixture; complete startup screen parity and broader project trust/resource workflows remain open.
