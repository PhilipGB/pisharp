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

Run from the repository the agent should edit. `read`, `write`, `edit` and `bash` operate with the local process's filesystem permissions; there is **no sandbox**. The early interactive UI is a line-oriented REPL (not Pi's terminal UI). A preliminary MAF snapshot of the linear conversation is saved after successful turns (not Pi's JSONL session tree). Do not load untrusted repository instructions or extensions; those features are not implemented. `--help` lists implemented flags. Do not use on sensitive files without reviewing model requests and generated commands.

## Local llama-server testing

With `llama-server` serving an OpenAI-compatible Chat Completions endpoint at `http://192.168.0.97:8000` and advertising the model ID `Qwen3.8-27B-GGUF`:

```sh
curl http://192.168.0.97:8000/v1/models
dotnet run --project src/PiSharp.Cli -- --local
dotnet run --project src/PiSharp.Cli -- --local --print 'Reply hello'
```

`--local` selects `http://192.168.0.97:8000/v1` and `Qwen3.8-27B-GGUF`. No API key is required for an unauthenticated server; the OpenAI SDK sends a placeholder token. If the server requires a key, set `PISHARP_API_KEY`. `PISHARP_MODEL` and `PISHARP_BASE_URL` override the local defaults (the base URL must include `/v1`). **`OPENAI_API_KEY` is never sent to a custom endpoint.** This HTTP URL is unencrypted on your LAN; do not send sensitive prompts or keys over an untrusted network. Enable llama.cpp's tool-call-compatible chat template (typically `--jinja`) to test the coding tools. This is a single-model connection, not Pi's `/llama` router management.

## Experimental sessions

Sessions are grouped by working directory under `~/.pisharp/sessions/`. Use `--continue` to reopen the latest snapshot in that directory, `--session /absolute/path/to/existing.json` to reopen a specific snapshot, or `--no-session` to disable saving. Supplying `--session` with a nonexistent file currently errors; a new session is created automatically by default. Snapshot files are user-private and atomically replaced on successful turns. They are **not compatible** with upstream Pi JSONL, cannot branch/replay, and do not preserve partial interrupted turns. Do not switch providers or models while continuing a snapshot. A separate experimental Pi v3 JSONL codec and branch projection exists in Core with a private file adapter, but **the CLI does not use it yet**.

## Build and test

```sh
dotnet build PiSharp.slnx --warnaserror
dotnet test PiSharp.slnx
```

Integration tests exercise a scripted tool call and a local HTTP Chat Completions streaming fixture; no live-provider credentials are needed for tests. Narrow edit-planner, v3 branch-projection and v1/v2 migration fixtures have been compared with pinned upstream Pi; full-feature parity has not been established.
