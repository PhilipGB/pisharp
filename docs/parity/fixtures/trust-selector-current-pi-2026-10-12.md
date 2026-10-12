# Bare `/trust` selector rendering — current Pi, 2026-10-12

## Startup spacing follow-up — Pi `ee409a85` / PiSharp `387ae5b3`

The startup compositor now follows Pi's spacer rows: row 0 is blank, the logo starts at row 1, help occupies rows 2–3, the built-in onboarding message starts at row 5, and the trust notice starts at row 7 after a blank row. The `TerminalScreenCompositorTests` regression covers both the leading spacer and the blank row between onboarding and startup details.

Same-Pi calibration matches all ten render frames and HTTP traces. The paired flow has matching HTTP traces and no scenario errors; the full terminal remains different by 48,590 state/render fields, including 33,458 rendered-cell fields, and raw PTY bytes differ. Product-specific logo/version and onboarding/restart copy remain visible differences. Full captures and reports are in [the paired run](trust-selector-current-pi-2026-10-12/startup-spacing-387ae5b3/paired/) and [same-Pi calibration](trust-selector-current-pi-2026-10-12/startup-spacing-387ae5b3/calibration/).

Oracle: Pi `4270522473e677ddba7b093caa0064a3df51d459`. Product: PiSharp `cf5f427f4990d7404f80f8a378a64d6731a7a5fd`.

The selector's title, path, saved-decision and current-session rows, option text, current check, and selected arrow/label now match the Pi terminal cells exactly in both `open-trust-selector` and `verify-session-stays-untrusted`. Pi styles selector details with `muted`, selected text and markers with `accent`, and unselected labels with `text`. A deterministic renderer regression covers those roles, and the Linux PTY test still verifies saving a parent decision leaves current-session trust unchanged.

Same-Pi calibration has zero state/render differences across ten frames and matching HTTP traces. The paired flow has matching HTTP traces and no scenario errors, but the complete terminal still differs: 73,022 total state/render fields, including 45,566 render fields. The remaining full-screen difference includes surrounding startup, notification, and control-screen output; the selector rows listed above are exact. Raw PTY bytes also differ. The full paired and calibration captures and machine-readable row assertions are in [paired](trust-selector-current-pi-2026-10-12/paired/), [calibration](trust-selector-current-pi-2026-10-12/calibration/), and [dialog render assertions](trust-selector-current-pi-2026-10-12/dialog-render-assertions.json).
