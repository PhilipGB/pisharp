# PiSharp (early development)

.NET 10 Linux coding-agent prototype built on Microsoft Agent Framework `ChatClientAgent` and Microsoft.Extensions.AI. **Not yet Pi-compatible.** See [the parity matrix](docs/parity/feature-matrix.md) and [handoff](docs/continuation.md) before using for real work.

```sh
export OPENAI_API_KEY=... # or PISHARP_API_KEY
export PISHARP_MODEL=gpt-4o-mini
# Optional for a Chat Completions-compatible server (e.g. vLLM):
# export PISHARP_BASE_URL=http://localhost:8000/v1

dotnet run --project src/PiSharp.Cli -- --print 'Describe this repository'
dotnet run --project src/PiSharp.Cli
```

Run from the repository the agent should edit. Four default tools are `read`, `bash`, `edit`, `write`; use `--tools read,grep,find,ls` to opt into experimental search/list tools, `--exclude-tools <names>` to remove tools, or `--no-tools` to disable defaults (an explicit allowlist takes precedence). All tools operate with the local process's filesystem permissions; there is **no sandbox**. The interactive UI is a normal-screen ANSI editor with scrollback, history, multiline input (Alt+Enter), bracketed paste and Tab completion for implemented slash commands and `@` paths. On Linux it reads VT input bytes directly, restores tty settings after exit, and remains **far short of Pi's full TUI**. The canonical PiSharp-specific conversation tree checkpoints prompts, tool intentions and outcomes, and completed or interrupted turns (not Pi JSONL). A crash can leave an explicitly unknown tool outcome; recovery never automatically repeats the operation. Inspect AGENTS/CLAUDE context files before using the agent in an unfamiliar repository; extensions are not loaded. `--help` lists implemented flags. Do not use on sensitive files without reviewing model requests and generated commands.

## Local llama-server testing

With `llama-server` serving an OpenAI-compatible Chat Completions endpoint at `http://192.168.0.97:8000` and advertising the model ID `Qwen3.8-27B-GGUF`:

```sh
curl http://192.168.0.97:8000/v1/models
dotnet run --project src/PiSharp.Cli -- --local
dotnet run --project src/PiSharp.Cli -- --local --print 'Reply hello'
```

`--local` selects `http://192.168.0.97:8000/v1` and `Qwen3.8-27B-GGUF`. No API key is required for an unauthenticated server; the OpenAI SDK sends a placeholder token. If the server requires a key, set `PISHARP_API_KEY`. `PISHARP_MODEL` and `PISHARP_BASE_URL` override the local defaults (the base URL must include `/v1`). **`OPENAI_API_KEY` is never sent to a custom endpoint.** This HTTP URL is unencrypted on your LAN; do not send sensitive prompts or keys over an untrusted network. Enable llama.cpp's tool-call-compatible chat template (typically `--jinja`) to test the coding tools. This is a single-model connection, not Pi's `/llama` router management.

## Sessions (PiSharp-specific, incomplete)

Sessions are grouped by working directory under `~/.pisharp/sessions/` (override storage with `--session-dir <dir>` or `PISHARP_SESSION_DIR`). `--continue` loads the most recently saved `.session.json`; `--session <path>` loads that session or creates it if missing; `--no-session` uses an ephemeral conversation. CLI `--print` and interactive runs use the same MAF execution path. Interactive editor: bounded multi-row viewport (normal screen) with wrapped input; Enter submits; Alt+Enter inserts a newline, arrows edit/navigate history, Ctrl+C or Escape clears, Ctrl+D exits with an empty buffer. `/tree` prints entry IDs and parents, `/branch <unique-prefix>` selects one, `/fork` and `/clone` currently both copy the active branch into a separate file (the Pi-style fork picker is not implemented), `/sessions` lists this project's saved sessions, `/resume <id-prefix|exact-name>` switches to one with the current model/endpoint, `/new` creates a blank session, `/name <label>` renames it, `/model <id>` changes the Chat Completions model on the same endpoint, `/compact [instructions]` summarizes older complete turns without deleting raw history, and `/session` displays its path and selected head. Model IDs are not discovered or validated before requests; branching across a model-change entry is rejected until per-branch provider switching exists. The session is saved atomically with private permissions before a tool runs, after its outcome, and after each turn or branch change; a tool error remains marked as a failure after reload. Crash-interrupted prompts and uncertain side effects are explicitly reported on restart; partial provider output between progress checkpoints can still be lost. Concurrent writers are rejected (not merged); legacy v1 text-only canonical sessions are upgraded on next save, but legacy tool turns are refused because they did not persist failure state. Old MAF snapshot files are **not** compatible or migrated; this format is **not** upstream Pi JSONL. The JSONL fixture files under docs are archived reference evidence, not supported session files. The saved model is selected automatically on continuation unless `PISHARP_MODEL` explicitly conflicts; changing the endpoint still requires matching `PISHARP_BASE_URL` and never forwards the cloud key to a custom URL.

Manual compaction requires at least two user turns. The latest whole turn and its tools stay in the model context; a separate tool-free MAF summarizer replaces earlier turns in **model context only**, leaving the canonical transcript and branches intact. Repeated compaction carries the previous summary. Auto-compaction, context-window budgeting, split-long-turn compaction, overflow retry, token/cost accounting and branch summarization are not implemented. Summary requests may send sensitive tool output to the configured provider; no cloud key is sent to a custom endpoint.

## Context instructions (experimental)

At startup, PiSharp loads the first of `AGENTS.override.md`, `AGENTS.md`, `AGENTS.MD`, `CLAUDE.md`, `CLAUDE.MD` from `~/.pisharp/agent` (or `PISHARP_AGENT_DIR`) and each ancestor of the working directory, then includes them in the MAF system instructions. Each file is limited to 64KB; loading oversized files fails instead of silently truncating. Context files load regardless of trust, as in Pi; they are untrusted model input, not executable configuration. Project `.pi/SYSTEM.md` and `.pi/APPEND_SYSTEM.md` require project trust (a stored decision, interactive confirmation, or the one-run `--approve` flag). `--no-approve` overrides a saved decision for one run; noninteractive sessions without a decision deny protected project resources. `/trust yes|no|forget` persists a decision and rebuilds the agent, while `/reload` refreshes instructions. Global agent prompt files load as user resources. Trust does **not** sandbox tools. A first subset of user/project skills and prompt templates is discovered (project copies only after trust). Skill descriptions enter system context; `/skill:<name> [args]` loads full instructions on demand, and `/<template> [args]` expands a template before a terminal prompt. Complex Agent Skills frontmatter and configurable discovery paths are not implemented. Print, JSON and RPC prompts also resolve `/skill:<name>` and `/<template>`; RPC `get_commands` lists discovered resources. Project settings, extensions and themes are not yet loaded. Inspect context files before running in an unfamiliar directory.

## Experimental JSONL and RPC modes

`--mode json <prompt>` emits LF-framed `pisharp` session and streaming lifecycle records described in [the protocol contract](docs/protocol.md). The same authoritative events drive the terminal, JSON and RPC adapters; a failed turn never emits successful completion. `--mode rpc` reads one JSON command per line on stdin, replies with correlated `response` records, and emits the same agent events without a session header. Supported RPC commands are `prompt` (nonempty text only), `get_state`, `get_messages`, `get_entries` (with cursor), `get_tree`, `get_last_assistant_text`, `set_session_name` (idle only), `get_commands`, `compact` (idle only), and `abort`. Other commands reject explicitly; steering, follow-up, images, provider controls and Pi-compatible payloads are **not implemented**. Both modes use the same MAF agent and canonical session store as print/terminal modes. Stdout is reserved for JSONL; consume it continuously. The `format: "pisharp"` marker means these event and session payloads are not upstream Pi wire-compatible.

## Build and test

```sh
dotnet build PiSharp.slnx --warnaserror
dotnet test PiSharp.slnx
```

Deterministic tests cover MAF streaming/tool results, canonical branch persistence and a local HTTP Chat Completions fixture; no live-provider credentials are needed. Narrow edit/read/ls and archived v3/v1/v2 session fixtures were compared with pinned upstream Pi, but the archived JSONL implementation is no longer used. Full-feature parity has not been established.
