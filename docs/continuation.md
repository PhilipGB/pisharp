# Continuation — incomplete PiSharp implementation

TODO.md is the concise durable handoff. The feature matrix, detailed inventory and execution ledger hold capability scope and evidence. Pi Packages are excluded unless required for a core capability.

## Current reference

The pinned reference is `earendil-works/pi@49681e1b71c9c32cdfbf45c21f8e3cb3a8c8629d`, fetched 2026-09-25. Pi's TUI is 0.87.1 and renders Marked 18.0.11 tokens. Refresh and reconcile this pin before terminal-image/theme work, materially changed capability families, and the final audit.

## Current implementation state

PiSharp uses a canonical C# session model and Microsoft Agent Framework for its provider/tool loop. Its session and RPC formats remain PiSharp-specific. The TUI has Markdig 1.3.2 AST parsing with separate terminal renderers, split transcript buffer/viewport/search state, shared display-cell wrapping, and a persistent idle alternate-screen compositor. The shared modal list supports all/scoped model selection, a searchable project-scoped `/resume` picker, and a searchable no-argument `/fork` selector with the newest active-branch user message preselected; direct ID/name resume and direct `/fork <id>` remain supported. `app.session.resume` and `app.session.fork` are configurable with no default keys. Cross-project session scope and the full selector controls remain open. Latest exact-head CI checkpoint is docs commit `2582c1f74a8940d6cdf604bcfe42272bdc252d9f`, Linux CI run 36134783838; its code parent `37eff5a9c7a15c5b12e8ff948fa53714046a2fda` passed format verification, warnings-as-errors build and all 423 tests in Linux CI run 36134474053. Current fork-picker working tree: focused tests 3/3, format verification passed, warnings-as-errors build passed with 0 warnings/errors, and full solution suite passed 424/424 with 0 skipped; commit and exact-head CI are pending. Markdown gaps include Pi's Unicode LaTeX layout, syntax highlighting and broader Marked dialect behavior. Settings picker, editor/keybinding breadth, mouse/selection, themes, images and extension UI remain open.

## Resume

Inspect `git status --short` before staging or committing. Continue with the highest-priority unresolved capability in TODO.md. After the TUI acceptance gate, proceed through Pi JSONL session interoperability, RPC/JSON/SDK, settings/resources/extensions, multimodal behavior, provider/auth breadth and the final current-main audit.
