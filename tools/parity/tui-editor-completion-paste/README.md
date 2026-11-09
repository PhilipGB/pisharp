# TUI editor differential harness

Deterministic real-process comparison for the
`feature-tui-editor-history-completion-and-paste` milestone.

- `scenarios.py` — frozen scenarios, actions and terminal sizes.
- `run.py` — runs same-Pi calibration and the Pi/PiSharp pair through
  `tools/parity/terminal/run.py`, extracts checkpoints, embeds observations and
  writes `report.json`. Exit code 0 only at 100/100.
- `inventory.py` — frozen normalisations and unresolved differences.
- `verify.py` — recomputes every comparison and validates the report; add
  `--self-test` for the negative controls. Exit code 0 at 100/100, 2 for a
  consistent report below 100, 1 for an invalid report.

The harness additions used here (`expectEditor`/`notExpectEditor`, `quiesce`,
fixture `binFiles`, `captureWorkspaceFiles`, and the action id in scenario
errors) live in `tools/parity/terminal/`.
