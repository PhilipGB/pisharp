# Keys and model display parity — pinned Pi b06c1f2c

Date: 2026-11-03

## Scope

Verified capability `keys-model-display`: Ctrl+L selector, Ctrl+P forward and
Shift+Ctrl+P backward cycling (Alt+P on Windows/WSL), Shift+Tab thinking cycle,
Ctrl+T thinking visibility, Ctrl+O tool expansion, Ctrl+X copy, model context
preservation, disclosure persistence, configurable bindings and widget
precedence.

Pinned Pi: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` (`/home/philip/Projects/pi-pinned`).

Implementation: `b8d50e6631c543551372152fa83d441aebf957de`.

## Evidence

- `docs/parity/fixtures/keys-model-display-pi-oracle-2026-11-03.json` — source
  anchors and 16 scenarios; `KeysModelDisplayParityTests.MatchesPinnedPi`
  replays all 16.
- `docs/parity/fixtures/keys-model-display-acceptance-2026-11-03.json` — M01–M16
  acceptance matrix, required comparisons and non-required findings.
- `docs/parity/fixtures/keys-model-display-checkpoints-2026-11-03.json` — four
  paired cases at 80x24 and 120x40, fullscreen and regular, 80 named
  checkpoints, provider request facts and clipboard comparison results.
- `tools/parity/terminal/fixtures/terminal-keys-model-display.json` — three
  reasoning models, request-log comparison and OSC 52 clipboard assertion.
- `tools/parity/keys_model_display_evidence.py` — compact checkpoint extractor.
- `tools/parity/verify_keys_model_display.py` — executable verifier and
  mutation-based negative controls.

## Results

- Paired terminal differential: no scenario errors in all four cases; exact
  provider HTTP and clipboard matches; matching editor text, cursor column,
  editor-relative cursor row, footer, latest status and request facts.
- Same-Pi calibration: 0 differences in all four cases.
- Provider requests: models `beta` then `alpha`, thinking `medium`; the second
  request retains `first prompt`, `fixture reply` and `second prompt` in order.
- Ctrl+T writes `hideThinkingBlock`; restart starts hidden and the first toggle
  reports visible.
- Ctrl+O reports expanded/collapsed; Ctrl+X writes the last assistant message.
- Disabled selector bindings render no stale default shortcuts; the fallback labels are used only without a configured keymap.
- Deterministic keymap/channel tests, focused PTY tests and rebinding/widget
  precedence tests pass.
- Focused suite 40/40, full suite 4,390/4,390, zero skips; `dotnet format`
  clean; warnings-as-errors build clean.

## Documented non-required differences

- Raw render streams differ in color/cursor painting and Pi-only transient
  frames; named checkpoint state and HTTP/clipboard behavior match.
- Pi keeps prior status lines in the transcript, PiSharp shows the latest; the
  latest-status and footer projections match.
- PiSharp does not echo the user prompt in the regular-mode viewport, so the
  absolute cursor row differs by one; the editor-relative row and column match.
- Windows/WSL Alt+P is covered deterministically; the PTY differential runs on
  Linux.
- Full `fullscreenCopyOnSelect` settings scope/reload acceptance remains owned
  by `settings-fullscreencopyonselect`; this milestone implements its Ctrl+X
  consumer for the parsed value.

## Commands

```sh
dotnet test PiSharp.slnx
dotnet format PiSharp.slnx --verify-no-changes
dotnet build PiSharp.slnx --warnaserror
python3 tools/parity/verify_keys_model_display.py --pi /home/philip/Projects/pi-pinned
```
