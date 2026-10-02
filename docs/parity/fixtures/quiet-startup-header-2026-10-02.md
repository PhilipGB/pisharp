# Quiet startup header policy — 2026-10-02

Oracle: current Pi `3874b3e98983c70fa05fa193b675d42cfcb8b9f8`, including `f29ea3deb298280b417892c6229ce478ad8c4d2f`. Inspected `SettingsManager.getQuietStartup`, `InteractiveMode.shouldShowStartupHeader/Details`, the startup builder, loaded-resource gating, the settings selector and upstream startup tests.

Pi accepts true, false and `"header"`. True hides startup presentation; header keeps the header and suppresses details; false shows both. Missing or unrecognized raw values fall back to false. `--verbose` shows both. Trusted project settings override user settings, including an explicit invalid/null value resolving to false.

PiSharp now uses a nullable three-state setting, exact boolean/string persistence and Pi's supported value order/description in the settings picker. `TerminalStartupPresentation` extracts output and both visibility gates from `Program.cs`. The existing metadata appears only when details are enabled; version and command help remain in header mode.

Eight fail-first cases demonstrate previously rejected header/invalid values, failed header persistence and an interactive header-mode launch ending with exit 2. After implementation, 24 focused settings tests pass with zero skips. Six fixed 120-column by 40-row terminal launches exercise every mode with and without verbose; a seventh PTY interaction selects and persists header mode through `/settings`.

Twenty-eight paired current-Pi/PiSharp scenarios compare exact resolved scalars and header/details booleans, including trusted and untrusted project overrides. Three writes compare complete preserved JSON; selector evidence compares exact label/description and ordered supported values. Current Pi's targeted startup tests pass 5/5; the explicit name filter deselects 30 unrelated tests.

This evidence proves the setting and visibility policy. It does not compare header text, ANSI, cell matrices, cursor modes, logo/help expansion, resource sections or forced quiet diagnostics. Those startup surfaces remain in progress in the ledger. The PiSharp inherit choice/scope UI is outside the supported-value comparison and remains part of broader selector parity. This upstream delta is not yet marked fully resolved.

Reproduce the paired policy fixture:

```sh
python3 tools/parity/quiet-startup-differential.py --pi /path/to/current/pi --output /tmp/quiet-startup.json
```

The Pi checkout needs dependencies and generated provider metadata. [The JSON fixture](quiet-startup-header-2026-10-02.json) preserves all inputs/results and the oracle SHA. Format verification passes; warnings-as-errors build has zero warnings/errors; the full serial suite passes 1114/1114 with zero skips in 4m29s. Exact-head Linux CI 36997235947 passes on source 4595bc9f4f2db98e45adf98b8ece2e00bbcfc0d1, including all 1114 tests with zero skips.
