# Continuation — incomplete PiSharp implementation

TODO.md is the concise durable handoff. The feature matrix, detailed inventory and execution ledger hold per-capability scope and evidence. The only planned exclusion is Pi Packages unless a core Pi capability depends on them.

## Current reference

The current upstream pin is earendil-works/pi@49681e1b71c9c32cdfbf45c21f8e3cb3a8c8629d, fetched 2026-09-25. The TUI package is 0.87.1 and renders Marked 18.0.11 tokens through terminal components. Refresh and reconcile this pin before terminal-image/theme work, materially changed capability families, and the final audit.

## Current implementation state

PiSharp uses a canonical C# session model and Microsoft Agent Framework for the provider/tool loop. Its session and RPC formats are PiSharp-specific and do not establish Pi JSONL or RPC compatibility. Coding tools have substantial local evidence with remaining gaps recorded in TODO.md and the matrix.

The active TUI currently has a normal-screen editor and a bounded alternate-screen transcript/editor/footer during model activity. Shared terminal-cell-aware wrapping fixed the long-token GFM table regression. Main uses Markdig 1.3.2 AST parsing with separate terminal renderers, and separates transcript buffer, viewport and search state from `TerminalScreen`. The first parser commit CI run exposed a disposed-listener retry bug in an unrelated provider test; the fixture now creates a fresh listener per attempt. Latest green main is `b8c3a21af5ee5b4cedeb9c8feb8a0911d5d01acd` (Linux CI run 36126888086 passed format, warnings-as-errors build and all tests). Pi-level LaTeX layout, full Marked/GFM coverage and syntax highlighting remain open, as do idle application surface, editor/keybinding breadth, mouse/selection, themes, images, pickers and extension UI.

## Resume

Start at the exact working-tree state and next action in TODO.md. Inspect git status before staging or committing. Continue beyond TUI work into session interoperability, RPC/JSON/SDK compatibility, settings/resources/extensions, multimodal behavior, provider/auth breadth and the final current-main audit.
