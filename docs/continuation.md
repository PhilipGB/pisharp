# Continuation — incomplete PiSharp implementation

TODO.md is the concise durable handoff. The feature matrix, detailed inventory and execution ledger hold per-capability scope and evidence. The only planned exclusion is Pi Packages unless a core Pi capability depends on them.

## Current reference

The current upstream pin is earendil-works/pi@49681e1b71c9c32cdfbf45c21f8e3cb3a8c8629d, fetched 2026-09-25. The TUI package is 0.87.1 and renders Marked 18.0.11 tokens through terminal components. Refresh and reconcile this pin before terminal-image/theme work, materially changed capability families, and the final audit.

## Current implementation state

PiSharp uses a canonical C# session model and Microsoft Agent Framework for the provider/tool loop. Its session and RPC formats are PiSharp-specific and do not establish Pi JSONL or RPC compatibility. Coding tools have substantial local evidence with remaining gaps recorded in TODO.md and the matrix.

The active TUI currently has a normal-screen editor and a bounded alternate-screen transcript/editor/footer during model activity. Commit `5d3d7f09019967b0e3a40ab3da0a4f3ff5e385b3` adds shared terminal-cell-aware wrapping; Linux CI run 36123165224 passed on that exact commit. The Markdown renderer is still handwritten and is the next implementation slice: move it to a mature CommonMark/GFM parser plus a separate terminal renderer. Idle application surface, editor/keybinding breadth, mouse/selection, themes, images, pickers and extension UI remain incomplete.

## Resume

Start at the exact working-tree state and next action in TODO.md. Inspect git status before staging or committing. Continue beyond TUI work into session interoperability, RPC/JSON/SDK compatibility, settings/resources/extensions, multimodal behavior, provider/auth breadth and the final current-main audit.
