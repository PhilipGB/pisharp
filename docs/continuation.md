# Continuation — incomplete PiSharp implementation

TODO.md is the concise durable handoff. The feature matrix, detailed inventory and execution ledger hold per-capability scope and evidence. The only planned exclusion is Pi Packages unless a core Pi capability depends on them.

## Current reference

The current upstream pin is earendil-works/pi@49681e1b71c9c32cdfbf45c21f8e3cb3a8c8629d, fetched 2026-09-25. The TUI package is 0.87.1 and renders Marked 18.0.11 tokens through terminal components. Refresh and reconcile this pin before terminal-image/theme work, materially changed capability families, and the final audit.

## Current implementation state

PiSharp uses a canonical C# session model and Microsoft Agent Framework for the provider/tool loop. Its session and RPC formats are PiSharp-specific and do not establish Pi JSONL or RPC compatibility. Coding tools have substantial local evidence with remaining gaps recorded in TODO.md and the matrix.

The active TUI currently has a normal-screen editor and a bounded alternate-screen transcript/editor/footer during model activity. Shared terminal-cell-aware wrapping fixed the long-token GFM table regression; latest green main is `ff4254c4168dcfee1d16868c93fb7778bc64e0dd` (Linux CI run 36123407896). The current uncommitted slice replaces handwritten Markdown grammar with Markdig AST parsing and separate terminal renderers, and separates transcript buffer, viewport and search state from `TerminalScreen`. Local format/build and 416/416 tests pass; exact-head CI is pending. Pi-level LaTeX layout, full Marked/GFM coverage and syntax highlighting remain open, as do idle application surface, editor/keybinding breadth, mouse/selection, themes, images, pickers and extension UI.

## Resume

Start at the exact working-tree state and next action in TODO.md. Inspect git status before staging or committing. Continue beyond TUI work into session interoperability, RPC/JSON/SDK compatibility, settings/resources/extensions, multimodal behavior, provider/auth breadth and the final current-main audit.
