# llama.cpp current-Pi verification — 2026-10-11

Pi `origin/main` is `96abe9a5ad45005cae34ed1c52d42f71fc88bedc`; PiSharp `origin/main` is `77f7dd29408092834daf5fd9be1c4ea25801613c`. Pi's two commits since the previous llama audit were classified in the [current upstream delta audit](upstream-delta-audit-2026-10-11.md): one is outside the normal coding-agent surface and one changes schema publication metadata only. Current Pi's llama extension tests pass 15/15, and its skill/resource suites pass 85/85.

Exact-head PiSharp Linux CI [38148783691](https://github.com/PhilipGB/pisharp/actions/runs/38148783691) passes on `77f7dd29408092834daf5fd9be1c4ea25801613c`: format verification, warnings-as-errors build, Node 24/24, Python 37/37, and .NET 1,264/1,264 with zero skips.

Three current-head process comparisons match: native llama.cpp token-probability classifier results, request bodies and image rejection; router model/classifier projection with loaded, sleeping and unloaded preset models, autoload on/off, thinking/model metadata and native System One decisions; and context precedence through live refresh and offline cache reload, with no router requests during offline reload. Reports are in `current-96abe9a5-77f7dd29/process` (local, untracked).

All twelve paired PTY flows match normalized terminal states, all 155 synchronized render frames, and HTTP traces with zero scenario errors. They cover `/login llama.cpp`, default and environment router URLs, `LLAMA_API_KEY`, stored/environment/default precedence across restart, router-root handling when the URL includes `/v1`, `/llama` discovery and model actions, `/model` selection, retry/reconnect, load/unload, cancellation and progress, and Hugging Face search, token lookup, gated access, quantization, download and cancellation. Raw bytes match in 0/12 flows; 6,046 control-boundary differences are retained as diagnostics.

Same-Pi calibration passes all twelve flows and all 155 frames at current Pi `96abe9a5`; 11/12 raw byte captures are identical. The machine evidence manifest (local, untracked) links each process report, paired capture, calibration capture and exact source pin.

The Python harness and its child processes are constrained to two CPU cores and common thread pools are capped at two. PTY render frames are spooled and streamed through comparison and evidence writing; compressed captures preserve full raw product traces.

The current-head directories and machine manifest named above were local,
untracked evidence. They are unavailable in a clean checkout. The reported source
pins and results remain historical provenance; this summary does not replace the
missing raw reports. Subsequent verification uses the
[configured Pi reference](../pi-reference.md) and committed differential fixtures.
