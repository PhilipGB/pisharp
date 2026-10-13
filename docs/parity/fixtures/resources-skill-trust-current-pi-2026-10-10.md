# Historical skill presentation and trust UI — 2026-10-10

Recorded pins: Pi `42a3497d03ad17e308a2299fa824727894f2c0ec`; PiSharp
`dab73a74d4fb762c78365ec4e58296370303ebc0`. These are historical capture pins,
not the [configured Pi reference](../pi-reference.md) for new verification.

The reviewed extension-resource [behavior assertion](skill-invocation-current-pi-2026-10-10/current-42a3497d-dab73a74d4/paired/behavior.json)
records four provider requests per product with equal expanded startup/reloaded
skill and template messages, arguments, collapsed/expanded skill rows, theme
diagnostics and reload status. The [paired report](skill-invocation-current-pi-2026-10-10/current-42a3497d-dab73a74d4/paired/report.json)
retains 234,929 normalized state/render differences, including 147,500 render
differences; full HTTP and raw terminal streams differ. The earlier
[same-Pi calibration](skill-invocation-current-pi-2026-10-10/current-42a3497d-9e622bcb49/calibration/report.json)
records matched terminal/render state and HTTP at PiSharp `9e622bcb498b00cb5e6c4990779816a0a334ffcc`.
That earlier calibration is retained with its own pin rather than attributed to
the later paired source.

The trust-UI [semantic assertion](project-trust-extension-ui-current-pi-2026-10-10/current-42a3497d-dab73a74d4/paired/behavior.json)
records the ordered select/confirm/input/notify interaction, protected project
skill, matching normalized system prompt and the three startup trust-dialog frames.
The [paired report](project-trust-extension-ui-current-pi-2026-10-10/current-42a3497d-dab73a74d4/paired/report.json)
retains 24,110 normalized state/render differences, including 18,921 render
differences; full HTTP and raw terminal bytes differ. The
[earlier calibration](project-trust-extension-ui-current-pi-2026-10-10/current-42a3497d-9e622bcb49/calibration-repeat/report.json)
records matched terminal/render state and HTTP at its original source pin.
The original semantic assertion treats generated documentation installation paths
as an environment difference; this does not erase full-report HTTP failures.

Original notes, raw captures and unreviewed historical CI/test/follow-up claims
are preserved in the local ignored archive described by the
[archive inventory](artifact-archive-2026-10-13/README.md). This housekeeping review
checked the selected records without rerunning historical scenarios, changing
capability status or claiming current full parity.
