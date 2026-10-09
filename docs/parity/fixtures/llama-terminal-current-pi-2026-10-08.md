# llama.cpp current-Pi process and terminal differential — 2026-10-09

Fresh Pi `origin/main` is `6fb2e7815167e6b19006fc526d1a5d0f5f998787`; PiSharp `origin/main` is `ef8f364cea352de93de4bc7cc29a611b615dbe62`. Exact-head Linux CI [37867953979](https://github.com/PhilipGB/pisharp/actions/runs/37867953979) passes restore, format verification, the warnings-as-errors build, and 1,189/1,189 tests with zero skips. The terminal harness passes 24/24 Node tests and 18/18 Python tests. Current-Pi llama extension, classifier, catalog, and model-runtime-classifier tests pass 15/15, 15/15, 10/10, and 1/1.

The native llama classifier, router classifier/autoload, and context refresh/offline-reload process comparisons match at these PiSharp/Pi source pins. Machine reports are [native classifier](llama-native-classifier-2026-10-09-ef8f364c.json), [router classifier](llama-router-classifier-2026-10-09-ef8f364c.json), and [context cache](llama-router-context-2026-10-09-ef8f364c.json).

Ten paired 100×32 dark fullscreen terminal flows complete without scenario errors and with exact HTTP request traces. Exact full terminal state remains 0/10; the reports contain 443,815 state/render differences, including 383,131 synchronized-render differences and 6,311 control-boundary diagnostics. Raw terminal bytes match in 0/10. These reports retain all compared output without normalizing it. Per-flow reports and full captures are under [`comparison-ef8f364cea-full/`](llama-terminal-current-pi-2026-10-08/comparison-ef8f364cea-full/), summarized in the [machine evidence manifest](llama-terminal-current-pi-2026-10-08/evidence.json).

The four named `/llama` manager checkpoints—startup, opening the manager, closing it, and quitting—match cell-for-cell under the harness comparison rules. The loading panel also matches, including the title, full-width blank spacing, and border rows. The full synchronized render series remains different: intermediate command-input frames, repaint count/order, terminal modes, styles, cursor/control output and screen restoration still need reconciliation. A fresh complete manager report has 14,370 differences while its named checkpoints have zero state differences; its HTTP trace matches.

Reproduce the manager case with:

```sh
python3 tools/parity/terminal/run.py --pi /tmp/pisharp-parity-current-pi \
  --fixture tools/parity/terminal/fixtures/llama-manager.json \
  --output docs/parity/fixtures/llama-terminal-current-pi-2026-10-08/comparison-ef8f364cea-full/llama-manager \
  --case 100x32-dark-fullscreen
```

The runner exits nonzero while the full terminal comparison differs; it still writes the exact checkpoint, render, request, control, and raw-byte evidence. The llama.cpp family and the overall parity goal remain open.
