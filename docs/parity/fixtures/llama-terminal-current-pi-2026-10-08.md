# llama.cpp current-Pi process and terminal differential — 2026-10-08

Pi `origin/main` is pinned to `6fb2e7815167e6b19006fc526d1a5d0f5f998787`; PiSharp source is `a613fdd2a6ed56bd4bdbc584ab460d645737a53e` with exact-head Linux CI [37804640060](https://github.com/PhilipGB/pisharp/actions/runs/37804640060) green (1,181 tests, zero skips). The current Pi checkout passes 15 llama extension tests, 15 native classifier tests, 10 classifier catalog tests, and one model-runtime classifier test.

The current-Pi [native classifier process comparison](llama-router-classifier-2026-10-08-current.json) and [context refresh/offline reload comparison](llama-router-context-2026-10-08-current.json) both match. The latter covers runtime, configured, training and fallback context windows, refresh, offline persistence and router requests.

Ten 100×32 dark fullscreen terminal scenarios were run against Pi and PiSharp. Same-Pi calibration matches terminal state and HTTP in all ten cases; raw output is byte-exact in eight, with scheduling variance in `llama-model-picker` and `llama-retry`. All ten paired PiSharp runs complete without scenario errors and have matching HTTP traces. Exact full terminal state matches in zero of ten cases: the unnormalized reports contain 480,660 total terminal state/render differences, including 411,780 synchronized render differences, plus 7,439 control-boundary diagnostic differences. Per-case reports and raw captures are in [the machine evidence manifest](llama-terminal-current-pi-2026-10-08/evidence.json), with captures under its `calibration/` and `comparison/` directories.

The load-replacement cancellation checkpoints now show the same manager viewport text through starting, progress, cancel confirmation and restoration. Full terminal parity remains open because captures still differ in buffers/history, styles, frame scheduling, cursor/keypad mode state and control output. The remaining llama.cpp product family, including gated Hugging Face access, download/quantization and other lifecycle combinations, also remains in progress.

Reproduce a paired terminal scenario with:

```sh
python3 tools/parity/terminal/run.py --pi /tmp/pisharp-parity-current-pi --fixture tools/parity/terminal/fixtures/llama-load-replace-cancel.json --output docs/parity/fixtures/llama-terminal-current-pi-2026-10-08/comparison/llama-load-replace-cancel --case 100x32-dark-fullscreen
```

The evidence is deliberately unnormalized; comparator behavior and byte-exact calibration results are retained in each `report.json`.
