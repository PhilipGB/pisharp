# Continuation — PiSharp capability parity

`TODO.md` is the concise implementation handoff. The feature matrix records upstream scope and the execution ledger holds per-capability evidence. Pi Packages are excluded unless a core capability depends on them.

## Reference state

The durable parity baseline is `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`, fetched 2026-09-25. Current upstream `main` was refreshed separately to `d6af72e1857cfb10b41d8ff8e69f0d72b4cf6d31` before terminal-image work. The current `packages/tui/src/terminal-image.ts` and `test/terminal-image.test.ts` include Kitty cell-size distortion correction (#8938), with tests for thin images, width/height constraints, stable reserved rows and crop metadata. The current ref remains scoped inspection evidence; do not silently replace the durable baseline. Refresh and reconcile upstream before materially changed capability families and the final audit.

## Current implementation and validation

PiSharp uses Microsoft Agent Framework and Microsoft.Extensions.AI for the provider/tool loop, with its own canonical C# session model. Pi JSONL session interoperability and Pi-compatible RPC/JSON/SDK behavior remain major gaps. The TUI uses Markdig 1.3.2 AST parsing with separate terminal renderers, shared terminal-cell wrapping, transcript buffer/viewport/search components, `TerminalScreenCompositor`, a reusable modal picker, and focused mouse routing. The editor includes multiline history/undo, keyboard selection and copy, kill ring/yank, configurable actions, external editing, text clipboard access, and path/command completion with a searchable picker.

Exact code head `17d682c2d7206a052b8a65ca24cf9c514166e39e` passed local format verification, warnings-as-errors build and all 480 tests. Linux CI run `36166108417` passed the same checks on exact head. Representative PTY evidence covers clipboard text and image paths, external editor restoration, session/fork pickers, settings/model flows and selecting a path completion. This is not the TUI acceptance gate or a full parity claim.

## Resume

Implement terminal image presentation as a focused component: capability detection, validated image dimensions, aspect-preserving Kitty/iTerm sizing, transcript-safe reserved rows and fallback text. Use current Pi `d6af72e` renderer and tests as the scoped reference, with deterministic renderer/compositor tests and PTY evidence. Then continue the broad TUI acceptance gate through themes, extension presentation and material mouse/editor flows; proceed to Pi JSONL sessions, RPC/JSON/SDK, settings/resources/extensions, multimodal behavior, provider/auth breadth and the final current-main audit.
