# Continuation — incomplete PiSharp implementation

TODO.md is the concise durable handoff. The feature matrix, detailed inventory and execution ledger hold capability scope and evidence. Pi Packages are excluded unless required for a core capability.

## Current reference

The pinned reference is `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`, fetched 2026-09-25. Since the prior `5fd446ca1843682e8da3fec4ceb71c42f56fbace` review, Pi changed build tooling and fixed listener snapshot dispatch, full-file read null-range display, temporary extension cache paths per pinned ref, and terminal cursor restoration after overlay close on stop. SettingsSelectorComponent and SettingsList plus their current tests were inspected; they are unchanged since the prior parity baseline. The TUI is 0.87.1 and renders Marked 18.0.11 tokens. Refresh before terminal-image/theme work, materially changed capability families, and the final audit.

## Current implementation state

PiSharp uses a canonical C# session model and Microsoft Agent Framework for its provider/tool loop. Its session and RPC formats remain PiSharp-specific. The TUI has Markdig 1.3.2 AST parsing with separate terminal renderers, split transcript buffer/viewport/search state, shared display-cell wrapping, and a persistent idle alternate-screen compositor. The shared modal list supports all/scoped model selection, a searchable project-scoped `/resume` picker, a searchable no-argument `/fork` selector with the newest active-branch user message preselected, and a bounded `/settings` editor for user or trusted-project settings. `/settings` preserves unrelated JSON with atomic replacement, applies image-block and compaction changes to the active runtime, and supports a configurable no-default `app.settings.open` action. Exact committed head `3a587ebb95e6b51a227838ecd3735c04995f3bb9` passed format verification, warnings-as-errors build and all 429 tests in Linux CI run 36139848340. The current uncommitted editor-completion slice follows Pi's path token boundaries, directory-first listing, hidden-file prefixing, quoted paths, wrapper handling and Unicode grapheme-safe common prefixes; focused tests pass 4/4 and local format/build/test passes 431/431, with exact-head CI pending. Cross-project session scope, the broader settings schema, editor selection, clipboard, external editor and fuller history remain open. Markdown gaps include Pi's Unicode LaTeX layout, syntax highlighting and broader Marked dialect behavior. Wider keybinding behavior, mouse/selection, themes, terminal images and extension UI remain open.

## Resume

Inspect `git status --short` before staging or committing. Continue with the highest-priority unresolved capability in TODO.md. After the TUI acceptance gate, proceed through Pi JSONL session interoperability, RPC/JSON/SDK, settings/resources/extensions, multimodal behavior, provider/auth breadth and the final current-main audit.
