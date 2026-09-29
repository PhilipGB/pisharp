# Current-Pi tool-call rendering, 2026-09-29

Reference: current Pi `5257d0d5f3ab7d42550804f32c67a77b49f485d4`, commit `5257d0d`, `packages/coding-agent/src/core/tools/render-utils.ts`, `src/extensions/mcp/tools.ts`, `src/modes/interactive/components/tool-execution.ts`, and `test/tool-execution-component.test.ts`.

Fallback call headers now show serialized `key=value` arguments on the title line, capped at 100 argument characters. Expanding a call shows each `key: value` on its own line; string values are raw, continuation lines are indented, tabs are expanded and carriage returns removed. PiSharp keeps its specialized built-in summaries and invokes extension call renderers when present. If a custom renderer throws, the old safe fallback still hides arbitrary arguments.

MCP tools now register a `server/tool` call renderer and a result renderer. Collapsed results show up to five text lines plus a remaining-line count; expanding the TUI uses a separately rendered full result view. The terminal host still bounds all renderer output, strips control sequences, and retains the full expanded view for scrollback capture. `ToolCallRenderingTests` failed first for missing generic arguments and immutable call/result views; `McpRuntimeTests` failed first for absent MCP renderers. The focused rendering/MCP/transcript/screen lane passed 36/36 after implementation, and the sequential local full suite passed 852/852 with zero skipped. Exact-head CI is pending.

This is a source-matched local implementation, not a paired current-Pi terminal differential. MCP output truncation for model-facing results and complete resource/binary presentation remain open.
