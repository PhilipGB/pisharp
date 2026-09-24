# PiSharp capability parity — durable execution state

**Goal:** provide Pi's capabilities in an idiomatic C#/.NET implementation, with behavioral and differential evidence; do not claim parity while material gaps remain. Pi Packages are the only consciously excluded ecosystem feature unless audit shows they affect core capability.

**Pinned upstream:** `earendil-works/pi@8676a0dcd8f9f6bca78835e63c8cd31493c4154d` (fetched from current `main` on 2026-05-24). Previous pin: `002fc8385268300ca91a5fc95f935c2afbbdac02`. Delta includes provider composition/catalog protocol, unified image/classifier model infrastructure, Typesafe System One, model type metadata, clipboard behavior and X11 image capability changes; these are incorporated into the gaps below.

## Current state and audit evidence

Repository `main` at `8c7dd71` is clean at start. Read `README.md`, `docs/parity/feature-matrix.md`, `docs/parity/detailed-inventory.md`, `docs/parity/execution-ledger.json`, and `docs/continuation.md`. Tracking is broadly accurate about substantial existing partial implementations, but README and matrix describe many live features while the ledger contains stale `not_started` statuses; reconcile it against code/tests before relying on statuses. There is no evidence for full feature parity. In particular the README explicitly calls out absent OAuth, wide provider semantics, Pi JSONL, full TUI, settings, keybindings, multimodal support, rich resources/extensions and Pi-compatible RPC.

## Highest-priority actionable work (continue in order, reprioritize as evidence dictates)

1. **Provider/model/auth baseline (partial):** compare current Pi `packages/ai` and coding-agent provider composition/catalog/model runtime/auth source and tests against C# implementation; implement provider adapters/capability metadata, request semantics, errors/retries/streaming, credentials and model discovery. Include current unified image/classifier model APIs, remote model catalog protocol, and Typesafe System One where reasonably applicable. Add deterministic provider fixture tests and upstream differential cases. OAuth/device/browser login, token refresh and provider-specific auth are missing.
2. **Images/multimodal:** inspect pinned image handling, classifier behavior, X11 clipboard requirement and TUI terminal-image tests; implement file/clipboard/@ attachments, validation/resize, model capability handling, tool image reads and safe fallback; differential/process tests.
3. **Configuration/settings/keybindings:** implement validated user and trusted-project settings with precedence, `/settings`, keybindings context/action map and CLI overrides; cover settings categories from detailed inventory including retry, compaction, shell, resources, TUI, theme and images. Tests include malformed and untrusted project config.
4. **Sessions interoperability/workflows:** Pi JSONL import/export and migration, picker/filter, trash/delete semantics, fork/clone/branch behavior, crash consistency, concurrent storage and recovery; differential fixtures against pinned Pi; preserve current canonical store unless interoperability requires adapters.
5. **RPC/JSON:** complete Pi-compatible commands, payloads, response correlation, event names/order, lifecycle, shell, retry, settings/model controls and session operations; diff process protocol against pinned Pi.
6. **TUI/editor/keybindings:** replace editor-only UX with maintainable transcript/rendering architecture: streaming text/reasoning, markdown/code/tools, status, overlays/pickers/settings, resize, scroll/search, mouse, clipboard/selection/external editor, normal/fullscreen restoration and image rendering. PTY snapshots/behavior tests.
7. **CLI and commands:** systematic current upstream help/options/slash-command audit; implement missing flags/workflows including prompt files/stdin, resource and theme control, trust, settings, auth, export/share/copy and session pickers; process/PTy compatibility tests.
8. **Resources/themes/extensions:** finish resource precedence/globs/reload, full skills metadata, project trust, prompt templates, themes and .NET extension capabilities (hooks/context transforms/providers/state/keybindings/RPC/UI or equivalent); deterministic isolation/order/cancellation tests.
9. **Tools:** complete differential read/write/edit/bash/grep/find/ls schemas and edge cases (encoding/BOM/CRLF/symlinks/permissions/atomicity/globs/ignore/truncation/live updates/process cancellation/image reads/result metadata); expand pinned upstream fixtures.
10. **Context/compaction:** close automatic in-tool-loop compaction, model-specific reserve/limits, overflow recovery/retry, atomic grouping, token accounting and branch summaries; preserve raw history and verify rollback.
11. **Final audit:** refresh upstream SHA, audit full source/docs/tests/interfaces (CLI, commands, settings, keybindings, tools, providers/auth, images, sessions, compaction, resources, extensions, TUI, RPC/API and recovery); run format, warnings-as-errors build, all tests, differential and representative end-to-end tests; update matrix/inventory/ledger and remove all unresolved parity items only when evidence supports it.

## Working rules

After each significant change: add/update tests and parity evidence, run focused tests, update this file and parity docs, commit coherent changes and push if established; then immediately select the next unresolved item. Never claim behavioral verification from code presence alone. Preserve exact upstream SHA for fixtures. No voluntary pause while actionable work remains.

## Completed in this continuation

- Expanded the provider model descriptor with model name, API identifier, declared input modalities, and max output tokens; load these from local catalogues (including OpenRouter `architecture.input_modalities`) and configured `models.json`, and preserve configured fallback metadata when the live endpoint omits it. Unit fixtures added. This metadata is descriptive only: native provider request adapters and image transport remain open.
- Revalidated with format verification, warning-as-error build, and all 130 tests after metadata changes.
- Added CLI positional `@file` processing for text inputs across print, JSON, and initial interactive prompts: UTF-8 with BOM stripping, XML-safe absolute file names, empty-file skipping, tilde expansion, missing-file diagnostics, and explicit binary/image rejection. Parser and behavior tests pass; upstream Pi uses true image attachments, so this is a text-only partial.
- Validation after this change: `dotnet format PiSharp.slnx --verify-no-changes`, `dotnet build PiSharp.slnx --warnaserror`, and `dotnet test PiSharp.slnx` pass (130/130).
- Refreshed upstream baseline from the prior pin to `8676a0dcd8f9f6bca78835e63c8cd31493c4154d`; delta touches model/provider and image/classifier infrastructure, catalog protocol, Typesafe System One, provider composition and clipboard/X11 behavior.
- Reconciled pinned SHA in matrix, detailed inventory, and execution ledger. The matrix still has broader stale-status problems; a full evidence-based per-item ledger reconciliation remains open.
- Fixed the HTTP provider fixture's race-prone TCP-port reservation by retrying `HttpListener.Start()` on bind collisions. The initial parallel run exposed the collision; reruns now pass in parallel.
- Validation after fix: `dotnet format PiSharp.slnx --verify-no-changes`, `dotnet build PiSharp.slnx --warnaserror`, and `dotnet test PiSharp.slnx` all pass (129/129).

## Unresolved / parity cannot be claimed

All eleven work areas above remain open; PiSharp is expressly an early-development partial implementation. No final audit or full parity evidence exists.
