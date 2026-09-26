# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET, using Microsoft Agent Framework and Microsoft.Extensions.AI where appropriate. Pi Packages stay excluded unless needed for a core capability.

**Stop condition:** a full current-Pi audit finds no material in-scope gaps, required behavioral/differential evidence passes, exact-head CI is green, and major architecture bottlenecks are resolved.

## Current state

- Latest pushed, exact-head CI-validated source: `49233922512294af3dd4a34381fb0d7e1e52e78d`; Linux CI run [36276515605](https://github.com/PhilipGB/pisharp/actions/runs/36276515605) passed format, warnings-as-errors build, and 604 tests (0 failed, 0 skipped).
- Current Pi `main`: `2b0a123de98318c2ff8069661721ce0c3794c34e`; durable baseline: `b3487650f6378f1b0d1643dd254445ceb4a98035`.
- RPC model commands and `get_state` now share a Pi-shaped model projector. It preserves configured/discovered model metadata, Pi input limits, and provider/model compatibility merges while keeping headers and credentials out of output. Linux process tests cover rich and sparse models.
- Major gaps remain in RPC behavior/differentials, session recovery/concurrency, settings/resources/extensions, multimodal/provider/auth breadth, coding tools, and TUI behavior. No overall parity claim is made.
- Architecture remains concentrated: `Program.cs` is 1,144 lines and `RpcMode.cs` is 517 lines. The next model/runtime work should move state transitions into a cohesive controller.

## Current priority

RPC model selection behavior and lifecycle semantics, then session interoperability; continue extracting application behavior alongside each capability.

## Exact next action

Compare current Pi's active `set_model`/`cycle_model` behavior with PiSharp, extract the shared model/runtime transition from `Program.cs`, and add deterministic coverage for switching during an active tool turn and using the selected model on the next provider request. Include `get_available_models` while busy in the same RPC compatibility pass.
