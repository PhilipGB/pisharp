# PiSharp capability parity — durable execution state

**Goal:** implement Pi's in-scope capabilities idiomatically in C#/.NET with behavioural and differential evidence. Pi Packages are excluded unless required for a core capability.

**Stop condition:** a full audit against current Pi main finds no material in-scope gaps and required validation/differential evidence passes.

## Current state

- PiSharp main was clean at d52a237 on entry. Linux CI run 36116615450 passed there: format, warnings-as-errors build, and 403/403 tests.
- Current upstream pin: earendil-works/pi@49681e1b71c9c32cdfbf45c21f8e3cb3a8c8629d, refreshed 2026-09-25. Current Pi TUI is 0.87.1 and uses Marked 18.0.11 tokens; its wrapping helper counts terminal cells, preserves ANSI state, and breaks long tokens by grapheme.
- The failed CI head 6473cc710e64c43551c0b49e0d610991de1664f9 was reproduced in an isolated worktree: ActiveScreenWrapsGfmTableCellsToAvailableWidth fails because a long cell causes source-Markdown fallback. Current main's follow-up table fix passes, but did not provide a reusable wrapping primitive.
- Latest known-good PiSharp main: `5d3d7f09019967b0e3a40ab3da0a4f3ff5e385b3`. Linux CI [run 36123165224](https://github.com/PhilipGB/pisharp/actions/runs/36123165224) passed format verification, the warnings-as-errors build, and the full suite. Local full suite passed 410/410; focused wrapping tests passed 9/9. The shared terminal-cell wrapper now hard-wraps long tokens and covers ANSI/OSC 8 state, combining marks, CJK, emoji/graphemes, and table borders/padding. The test-only provider HTTP listener reservation/start window is serialized to prevent parallel fixture port collisions.

## What still prevents parity

- TUI architecture and breadth: Markdown still uses a handwritten regex parser; idle full-screen application, overlays/pickers, selection/mouse, complete editor/keybindings, themes, images, and extension UI are incomplete.
- Pi JSONL/session interoperability and Pi-compatible RPC/SDK remain major gaps.
- Settings/resources/extensions, multimodal handling, provider/auth breadth, and final differential audit remain incomplete. See the feature matrix and detailed inventory for per-surface evidence and residuals.

## Priority and next action

1. Replace the Markdown regex grammar with Markdig CommonMark/GFM parsing and a separate PiSharp AST-to-terminal renderer. Preserve streaming, safe output, existing behavior, and cell-width layout.
2. Refactor screen state/layout/lifecycle and continue the TUI acceptance work, prioritizing externally visible app/picker/editor gaps.
3. Then move to sessions/interoperability and Pi RPC/JSON/SDK before remaining settings, resources, multimodal and provider breadth.
