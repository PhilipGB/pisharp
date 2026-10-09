# llama.cpp current-Pi process and terminal evidence — 2026-10-09

The oracle is current Pi `origin/main` at `f1b2e77f5b13b2a199b1052cb79c235451afe7d7`; the tested PiSharp `origin/main` is `be133ebfa6e17d5a4f094abe8d9a3cf01a2f6b75`. Fresh fetches confirmed both pins, and every report and capture below was generated from those clean committed heads. The Pi pin includes recent llama.cpp changes and is newer than the earlier `c10bfb0` audit. Pi's resource-loader, llama-extension, native-classifier, classifier-catalog and runtime-classifier suites pass 53/53, 15/15, 15/15, 10/10 and 1/1 respectively. PiSharp exact-head CI [37925593650](https://github.com/PhilipGB/pisharp/actions/runs/37925593650) passes the terminal harness, format verification, warnings-as-errors build and all 1,210 tests with zero skips. The [machine manifest](llama-terminal-current-pi-2026-10-09/evidence.json) records pins and per-flow results.

## Terminal comparisons

All ten paired current-Pi/PiSharp interactions match complete normalized terminal states, each compared render frame and HTTP request trace: 10/10 flows, 138 frames, zero state or render differences and zero scenario errors. The scenarios cover `/login llama.cpp`, default router URL and stored/environment/default precedence, key authentication and reload; router discovery, loaded/sleeping models, presets/autoload, `/model` selection, load progress, unload, refresh/retry, cancellation and replacement; plus Hugging Face search, gated access, quantization, token-file lookup, download progress and cancellation. Captures retain exact terminal input/output bytes and full frames under `llama-terminal-current-pi-2026-10-09/comparison-be133ebf/`.

Raw-byte equality is reported separately: none of the ten Pi/PiSharp pairs is byte-identical. Their captures retain 5,292 control-boundary diagnostics even though normalized terminal states and rendered frames match. Same-Pi calibration passes all ten terminal-state and HTTP comparisons; nine flows are byte-identical and one has a control-boundary diagnostic. Calibration captures are under `llama-terminal-current-pi-2026-10-09/calibration-be133ebf/`.

## Process differentials

Three deterministic process comparisons match at the same exact heads:

- [Native llama classifier and image rejection](llama-native-classifier-images-2026-10-09-be133ebf.json) matches baseline classification and Pi's exact image-input error; image rejection sends no router request.
- [Router decision classifier, autoload and image rejection](llama-router-classifier-images-2026-10-09-be133ebf.json) matches the routed classifier with autoload on and off, exact System One image rejection, and already-canceled behavior without an extra request.
- [Context refresh and offline reload](llama-router-context-2026-10-09-be133ebf.json) matches runtime/configured/training/fallback/decision context windows, refresh, and cached context after offline reload with no router request.

The llama.cpp family has no remaining identified behavior gap in this current-Pi audit slice. The full PiSharp parity goal remains active; the next unresolved family is Resources/instructions and trust, alongside the broader audit, MCP conformance and architecture work recorded in the ledger.
