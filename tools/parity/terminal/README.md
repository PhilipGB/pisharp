# Full terminal differential harness

The runner launches the real pinned Pi source CLI and the built PiSharp CLI in independent controlling PTYs. Both receive the same dimensions, locale, terminal colors, model, deterministic local provider, working directory and fixture actions. No credentials are inherited. The stable empty working directory is `/tmp/pisharp-terminal-fixture-workspace`; a harness marker and advisory lock prevent concurrent scenarios from sharing it. Each product receives freshly reset configuration in the harness-owned `/tmp/pisharp-terminal-fixture-agent`, so temporary session paths do not change between runs. Both fixed directories require a harness marker; foreign directories are refused.

```sh
npm ci --prefix tools/parity/terminal --ignore-scripts --no-audit --no-fund
npm test --prefix tools/parity/terminal
python3 -m unittest discover -s tools/parity/terminal -p 'test_*.py'
python3 tools/parity/terminal/run.py --pi /tmp/pisharp-current-pi \
  --calibrate --output /tmp/terminal-calibration
python3 tools/parity/terminal/run.py --pi /tmp/pisharp-current-pi \
  --output /tmp/terminal-comparison
```

Build PiSharp first. Pi must have its workspace dependencies installed. `--pisharp` selects another built DLL; `--fixture` selects a reusable JSON scenario; `--case 80x24-dark-fullscreen` selects one matrix case. Exit code 0 means all selected checkpoints match; exit code 1 means a product mismatch or an incomplete product scenario. Calibration compares two independent executions of Pi and must pass before relying on cross-product evidence. There is no option to turn product mismatches into success.

Each checkpoint captures every viewport cell's characters and width, foreground/background color modes and values, all text attributes, underline style/color and hyperlink. It also records cursor coordinates, visibility, style/blink, active screen, complete normal/alternate buffers with scrollback and wrapping, terminal/input/mouse/focus/paste/keyboard/synchronized-output modes, and default colors. The exact control-sequence trace between checkpoints remains part of the comparison, independently of PTY read boundaries. Clipboard, image and other string escape payloads remain intact in that trace and the complete base64 output. Image pixel rendering still needs dedicated image fixtures and terminal-profile evidence; captured escape payloads alone do not prove image appearance.

The emulator is pinned to `@xterm/headless@5.5.0`, the dependency used by current Pi's virtual-terminal tests. Public buffer/cell/parser APIs provide most state. The pinned cursor visibility, extended underline and hyperlink accessors are internal xterm APIs, covered by contract tests. Replies to cursor/device/color queries feed back into the PTY; the emulator supports controlled legacy and Kitty profiles. Initial interactive fixtures use Kitty to avoid an asynchronous legacy-negotiation escape-order race; that race is retained in the earlier calibration evidence, rather than normalized.

Fixtures can override per-product CLI arguments, set controlled environment values and seed files within each fresh agent directory for authentication, sessions, keybindings and resource scenarios. Fixture actions can send exact UTF-8/escape input, resize the terminal, wait for a product-specific readiness predicate, and capture process exit/restored-screen state. Predicates select a checkpoint; they never rewrite compared cells. Settling waits for output quiescence and an expected viewport condition, with a bounded timeout. Named checkpoints give deterministic intermediate frames. Long-running animated interactions require deterministic progress/clock fixtures before their sequences can be claimed as matched.

`report.json` contains the source pins, match result, exact mismatch paths and scenario failures. Each case's `.json.gz` contains both products' complete frames, controls, raw terminal output and observed fixture HTTP requests. No normalization is currently performed. Spaces, padding, borders, wrapping, truncation, colors, attributes, cursor placement, ordering and escapes remain exact. The runner supplies deterministic prose and a stable working directory rather than editing their rendered output.

The initial reusable fixtures cover startup, editor input/movement/paste, resize, regular/fullscreen intent and terminal restoration; deterministic user/assistant/Markdown transcript rendering; and model/settings selectors. These fixtures expose remaining product gaps and do not establish complete TUI parity. Add scenarios to this harness as capability families close, including tools/Bash/thinking/session/tree/fork/authentication/MCP/trust/search/selection/clipboard/images/errors/aborts/keybindings/easter eggs. The full ledger requirement remains in progress until that breadth is verified.

PiSharp currently rejects the `tuiMode: regular` configuration. For fullscreen its existing default is used; regular-mode cases preserve the requested setting and report the rejection. This is recorded as a parity failure, not normalized away.
