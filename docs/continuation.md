# Continuation — incomplete PiSharp implementation

TODO.md is the concise durable handoff. The feature matrix, detailed inventory and execution ledger hold capability scope and evidence. Pi Packages are excluded unless required for a core capability.

## Current reference

The pinned reference is `earendil-works/pi@49681e1b71c9c32cdfbf45c21f8e3cb3a8c8629d`, fetched 2026-09-25. Pi's TUI is 0.87.1 and renders Marked 18.0.11 tokens. Refresh and reconcile this pin before terminal-image/theme work, materially changed capability families, and the final audit.

## Current implementation state

PiSharp uses a canonical C# session model and Microsoft Agent Framework for its provider/tool loop. Its session and RPC formats remain PiSharp-specific. The TUI has Markdig 1.3.2 AST parsing with separate terminal renderers, split transcript buffer/viewport/search state, shared display-cell wrapping, and a persistent idle alternate-screen compositor. The current slice adds a reusable modal list host and an all/scoped model selector opened through Ctrl+L or `/model`. Provider test fixtures now dispose listeners once and create fresh listeners per bind retry. Exact code head `bb576e37c09a8fde1734f03e966e5233f62aabdd` passed format, warnings-as-errors build and all 421 tests in Linux CI run 36132336108. Markdown gaps include Pi's Unicode LaTeX layout, syntax highlighting and broader Marked dialect behavior. Session/settings pickers, editor/keybinding breadth, mouse/selection, themes, images and extension UI remain open.

## Resume

Inspect `git status --short` before staging or committing. Continue with the highest-priority unresolved capability in TODO.md. After the TUI acceptance gate, proceed through Pi JSONL session interoperability, RPC/JSON/SDK, settings/resources/extensions, multimodal behavior, provider/auth breadth and the final current-main audit.
