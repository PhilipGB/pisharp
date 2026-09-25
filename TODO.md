# PiSharp capability parity

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioral and differential evidence. Pi Packages remain excluded unless a core capability requires them.

**Stop condition:** a current full audit finds no material in-scope capability gaps and required validation/differential evidence passes.

## Current state

- Latest known-good implementation head: `d3e4defdef81b5535b27a57096c5456e930ab6f4`; Linux CI run [36158324018](https://github.com/PhilipGB/pisharp/actions/runs/36158324018) passed format verification, warnings-as-errors build and all 461 tests.
- The external-editor slice is committed: Ctrl+G launches the configured editor from a private temporary directory/file, supports trusted user/project `externalEditor` settings, and suspends/restores alternate-screen and raw terminal modes. A fork-picker PTY readiness race was fixed in `33b6ef7`; exact-head Linux CI passes 438/438.
- Text clipboard paste/copy, bounded Linux/macOS/Windows helpers and remote/headless OSC 52 are committed as `d331d7d`; `b27a4b7` fixes the procfs race in the Bash process-tree test helper without weakening its assertion.
- Mouse input now routes through a focused controller: SGR wheel scrolling, transcript and prompt drag selection/copy, grapheme-safe editor cursor placement, picker option clicks, and terminal-mode restoration. `EditorViewport` maps visible cells back to source offsets, preserving wrapped and Unicode draft text. `1abedd5` adds prompt selection/cursor placement; focused viewport/screen tests pass 16/16, and exact-head Linux CI passes 455/455.
- Screen lifecycle and composition are separated: `TerminalScreenCompositor` owns frame layout, row composition, overlay placement and ANSI output; `TerminalScreen` retains lifecycle and application state. `0d73b95` passes 22 focused TUI tests, and exact-head Linux CI passes 455/455.
- Prompt history now has a 100-entry cap, consecutive duplicate suppression, saved draft cursor restoration and multiline Up/Down navigation. Undo supports grouped typing, whitespace/newline boundaries, atomic paste, deletions, platform bindings and user-configurable named actions. `d3e4def` passes 32 focused input/keymap/editor tests and exact-head Linux CI passes 461/461.
- Pi reference: `earendil-works/pi@b3487650f6378f1b0d1643dd254445ceb4a98035`, refreshed 2026-09-25. Material deltas since `5fd446ca1843682e8da3fec4ceb71c42f56fbace` are classified in `docs/continuation.md`.

## What still prevents parity

- TUI: keyboard selection, kill/yank, image clipboard paste, WSL clipboard interop, broader completion, double/triple-click and drag auto-scroll, mouse-aware extension widgets, themes, terminal images and extension UI; remaining Markdown dialect and rendering behavior. Screen frame composition is now extracted; terminal lifecycle and state remain coordinated by `TerminalScreen`.
- Pi JSONL/session interoperability and Pi-compatible RPC/JSON/SDK remain major gaps.
- Resources/extensions, multimodal behavior, provider/auth breadth and the final current-upstream audit remain incomplete. See the feature matrix and detailed inventory.

## Priority and next action

1. Add keyboard text selection with copy/highlighting, then continue editor kill/yank and completion parity plus representative PTY coverage. The Markdig AST-to-terminal-renderer boundary and screen compositor split are in place; remaining Markdown rendering gaps include syntax highlighting and Pi-style math layout. Decide whether the TUI acceptance gate is met only after its major visible workflows have representative process tests. Residual mouse work: word/line clicks, drag auto-scroll and mouse-aware extension widgets.
2. After the TUI gate, continue with session interoperability, RPC/JSON/SDK, settings/resources/extensions, multimodal and provider/auth breadth. Refresh Pi before terminal-image/theme work, materially changed capability families and the final audit.
