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

Run from the repository the agent should edit. Four default tools are `read`, `bash`, `edit`, `write`; use `--tools read,ls` to select the experimental `ls` tool, `--exclude-tools <names>` to remove tools, or `--no-tools` to disable defaults (an explicit allowlist takes precedence). `read`, `write`, `edit` and `bash` operate with the local process's filesystem permissions; there is **no sandbox**. The early interactive UI is a line-oriented REPL (not Pi's terminal UI). The canonical PiSharp-specific conversation tree is saved on completed and interrupted turns (not Pi JSONL). Do not load untrusted repository instructions or extensions; those features are not implemented. `--help` lists implemented flags. Do not use on sensitive files without reviewing model requests and generated commands.

## Local llama-server testing

With `llama-server` serving an OpenAI-compatible Chat Completions endpoint at `http://192.168.0.97:8000` and advertising the model ID `Qwen3.8-27B-GGUF`:

```sh
curl http://192.168.0.97:8000/v1/models
dotnet run --project src/PiSharp.Cli -- --local
dotnet run --project src/PiSharp.Cli -- --local --print 'Reply hello'
```

`--local` selects `http://192.168.0.97:8000/v1` and `Qwen3.8-27B-GGUF`. No API key is required for an unauthenticated server; the OpenAI SDK sends a placeholder token. If the server requires a key, set `PISHARP_API_KEY`. `PISHARP_MODEL` and `PISHARP_BASE_URL` override the local defaults (the base URL must include `/v1`). **`OPENAI_API_KEY` is never sent to a custom endpoint.** This HTTP URL is unencrypted on your LAN; do not send sensitive prompts or keys over an untrusted network. Enable llama.cpp's tool-call-compatible chat template (typically `--jinja`) to test the coding tools. This is a single-model connection, not Pi's `/llama` router management.

## Sessions (PiSharp-specific, incomplete)

Sessions are grouped by working directory under `~/.pisharp/sessions/`. `--continue` loads the most recently saved `.session.json`; `--session <path>` loads that session or creates it if missing; `--no-session` uses an ephemeral conversation. CLI `--print` and interactive runs use the same MAF execution path. Interactive `/tree` prints entry IDs and parents, `/branch <unique-prefix>` selects one, `/fork` starts a separate file with the active branch, `/new` creates a blank session, `/name <label>` renames it, and `/session` displays its path and selected head. The session is saved atomically with private permissions after each turn or branch change; a tool error remains marked as a failure after reload. Interrupted turns have a marker, but incomplete provider output may be lost. Concurrent writers and old snapshot migration are not supported. The old MAF snapshot files are **not** compatible; this format is **not** upstream Pi JSONL. The JSONL fixture files under docs are archived reference evidence, not supported session files. Do not switch providers/models while continuing a session.

## Build and test

```sh
dotnet build PiSharp.slnx --warnaserror
dotnet test PiSharp.slnx
```

Deterministic tests cover MAF streaming/tool results, canonical branch persistence and a local HTTP Chat Completions fixture; no live-provider credentials are needed. Narrow edit/read/ls and archived v3/v1/v2 session fixtures were compared with pinned upstream Pi, but the archived JSONL implementation is no longer used. Full-feature parity has not been established.
