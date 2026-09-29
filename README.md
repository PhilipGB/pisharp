# PiSharp

PiSharp is a C#/.NET coding agent inspired by [Pi](https://github.com/earendil-works/pi). It uses Microsoft Agent Framework (MAF) and Microsoft.Extensions.AI (M.E.AI) for agent and provider integration.

**Status:** active parity work; experimental and incomplete. PiSharp has its own canonical session format and translates Pi-compatible data at explicit boundaries. It is not a drop-in Pi replacement. See the [parity overview](docs/parity/feature-matrix.md) for verified coverage and open gaps.

## Contents

- [Run PiSharp](#run-pisharp)
- [Providers and tools](#providers-and-tools)
- [Sessions and resources](#sessions-and-resources)
- [Safety](#safety)
- [Parity and limitations](#parity-and-limitations)
- [Build and test](#build-and-test)
- [Contributor and agent workflow](#contributor-and-agent-workflow)

## Run PiSharp

Set a provider key, then start an interactive session or send one prompt:

```sh
export OPENAI_API_KEY=...
dotnet run --project src/PiSharp.Cli
dotnet run --project src/PiSharp.Cli -- --print "Describe this repository"
```

Common options:

| Task | Example |
| --- | --- |
| Choose a provider or model | `pisharp --provider anthropic --model <model-id>` |
| Continue the latest session | `pisharp --continue` |
| Open a session or fork it | `pisharp --session <path-or-project-id>` / `pisharp --fork <path-or-project-id>` |
| Run without saving a session | `pisharp --no-session` |
| List discoverable models | `pisharp --list-models` |
| Check local credential setup | `pisharp auth check --provider <id> --local` |

Run `pisharp --help` for the full CLI. Interactive commands include `/model`, `/settings`, `/thinking`, `/sessions`, `/resume`, `/tree`, `/branch`, `/compact`, `/import` and `/export`.

Start PiSharp from the project directory its tools should use. The terminal includes a persistent transcript/editor, streaming tool feedback, model/session/settings pickers, and keyboard and mouse input; key bindings are listed in [`docs/keybindings.md`](docs/keybindings.md).

The built-in `codemode` tool requires Node.js 22.19 or newer on `PATH`. Model JavaScript runs inside a bundled QuickJS/WASM VM in a separate process; normal coding tools and provider use do not require Node.js.

For automation, `--mode json` emits PiSharp JSONL lifecycle records and `--mode rpc` accepts one JSON request per stdin line. See the [protocol contract](docs/protocol.md) for framing, commands, events and known differences.

## Providers and tools

Built-in provider profiles use separate credentials:

| Provider | API | Credential |
| --- | --- | --- |
| OpenAI | Responses | `OPENAI_API_KEY` |
| Azure OpenAI | Responses | `AZURE_OPENAI_API_KEY` |
| Anthropic | Messages | `ANTHROPIC_API_KEY` |
| xAI | Responses | `XAI_API_KEY` |
| OpenRouter | Chat Completions | `OPENROUTER_API_KEY` |
| Mistral | Chat Completions | `MISTRAL_API_KEY` |

Custom providers and static model metadata can be declared in `models.json` under `PISHARP_AGENT_DIR` (default `~/.pisharp/agent`). Provider discovery, model catalogues, reasoning and authentication are partial; OAuth login and refresh are not implemented. See the [settings inventory](docs/parity/detailed-inventory.md#settings-inventory) for the supported settings subset.

The default coding tools are `read`, `bash`, `edit` and `write`. `grep`, `find` and `ls` are opt-in. Use `--tools`, `--exclude-tools` or `--no-tools` to adjust the set. Images are supported on selected provider paths; RPC image prompts and broader multimodal parity remain open.

## Sessions and resources

Canonical sessions are stored per project under `~/.pisharp/sessions` (override with `--session-dir` or `PISHARP_SESSION_DIR`). They preserve branches and tool outcomes. Pi JSONL v1–v3 is an import/export interchange format, not PiSharp's backing store. See [session-format details](docs/session-format.md) and the [feature matrix](docs/parity/feature-matrix.md).

PiSharp can load context instructions, skills, prompt templates and themes from user and trusted project locations. These features implement subsets of Pi's resource behavior. A trusted .NET extension can add MAF tools and terminal commands; see [extension setup and limits](docs/extensions.md).

## Safety

- Built-in tools run with PiSharp's operating-system permissions. Project trust controls protected project resources; it does not sandbox tools.
- Extensions execute arbitrary .NET code. Load only assemblies you trust.
- Prompts, instructions, tool output and summaries may be sent to the selected provider. Review project resources before use.
- `--api-key` is runtime-only but may be visible in process listings and shell history. Prefer provider-specific environment variables or `/login`.
- A custom endpoint must use its own provider ID and credentials. PiSharp does not send `OPENAI_API_KEY` to a custom URL.

## Parity and limitations

PiSharp shares one MAF execution path across terminal, print, JSON and RPC modes. Its parity work prioritizes observable behavior, session recovery and maintainable C# boundaries. Major areas remain incomplete, including RPC wire compatibility, Pi session-manager recovery, provider/auth breadth, multimodal support, extensions, coding-tool edge cases and terminal rendering.

Use these documents for the current, detailed status:

- [Feature matrix](docs/parity/feature-matrix.md): capability status and evidence level.
- [Detailed inventory](docs/parity/detailed-inventory.md): behavior, differences and residuals.
- [Continuation notes](docs/continuation.md): current checkpoint, priorities and architecture risks.
- [Parity fixtures](docs/parity/fixtures): pinned Pi comparisons and process evidence.
- [Execution ledger](docs/parity/execution-ledger.json): machine-readable capability records.

Do not infer parity from a command name, passing unit test, or green CI run alone. Overall parity has not been established.

## Build and test

Requires the .NET 10 SDK.

```sh
dotnet restore PiSharp.slnx
dotnet format PiSharp.slnx --verify-no-changes
dotnet build PiSharp.slnx --warnaserror
dotnet test PiSharp.slnx
```

Tests use deterministic agent and local HTTP fixtures; no live provider key is needed for the suite.

## Contributor and agent workflow

1. Start with [`TODO.md`](TODO.md) and [`docs/continuation.md`](docs/continuation.md), then inspect the current source and worktree. Treat checkpoint prose as historical until verified.
2. Read the feature matrix, detailed inventory and execution ledger before choosing a parity gap. Compare current Pi documentation, implementation and tests for that capability.
3. Add deterministic tests and process-level or differential evidence when behavior crosses a protocol or process boundary. Record limitations honestly.
4. Extract a cohesive boundary when the next capability touches concentrated behavior; avoid broad refactors that are not needed for the slice.
5. Validate the exact source head, commit a coherent slice, push regularly, and record the exact commit and CI result in the continuation notes.
