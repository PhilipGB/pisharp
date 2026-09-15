# PiSharp

Experimental C#/.NET port of the core ideas behind [`earendil-works/pi`'s coding agent](https://github.com/earendil-works/pi/tree/main/packages/coding-agent), built on Microsoft Agent Framework.

This is an incremental port, not a line-for-line translation.

## Current milestone

Implemented and smoke-tested against llama.cpp:

- Microsoft Agent Framework `HarnessAgent` runtime
- OpenAI and OpenAI-compatible endpoints, including llama.cpp
- MAF context compaction using configured context/output limits
- `read`, `write`, `edit`, and `bash` tools
- Pi-style unique exact-match edit replacements
- workspace path traversal protection for file tools
- global and hierarchical `AGENTS.md` loading
- `AGENTS.override.md` precedence and `CLAUDE.md` fallback
- optional `--context-root` to stop parent context discovery at an explicit boundary
- `/context` source reporting
- streaming terminal output
- one-shot and interactive modes
- durable append-only JSONL sessions
- stable turn IDs and `parentId` tree semantics
- MAF `AgentSession` serialization/restoration inside durable turns
- `-c` / `--continue`
- `-r` / `--resume`
- `--session <id|path>`
- `/session`, `/tree`, `/goto`, `/fork`, `/clone`, `/new`, `/resume`
- unit tests for file/path/context/session behaviour

Still to implement:

- Pi-equivalent full-screen tree picker and richer TUI/editor/keybindings
- queued steering/follow-up messages while a turn is running
- streamed tool-call rendering and diffs
- fuzzy edit matching and unified patches
- provider login/OAuth and dynamic model catalogue
- skills/templates/packages/extensions
- project trust
- JSON/RPC modes
- retry policy and provider failover
- image input
- shell approval/sandbox policy

## Requirements

- .NET 10 SDK
- an OpenAI-compatible model that supports tool/function calling

Pinned framework packages:

- `Microsoft.Agents.AI.Harness` 1.21.0
- `Microsoft.Extensions.AI.OpenAI` 10.10.0

## Build

```bash
dotnet restore PiSharp.slnx
dotnet build PiSharp.slnx --no-restore
dotnet test PiSharp.slnx --no-build
```

## Run with llama.cpp

```bash
export PISHARP_MODEL='Qwen3.8-27B-GGUF'
export PISHARP_ENDPOINT='http://192.168.0.97:8000/v1'

dotnet run --project src/PiSharp.Cli -- \
  "inspect this repository and explain its architecture"
```

Interactive mode:

```bash
dotnet run --project src/PiSharp.Cli
```

An API key is not required for a local endpoint; PiSharp supplies `unused` if none is configured.

## Context files

By default PiSharp follows Pi's behaviour and searches from the filesystem root down to the current workspace, selecting one of these files per directory:

1. `AGENTS.override.md`
2. `AGENTS.md`
3. `CLAUDE.md`

It also loads `~/.pi/agent/AGENTS.md` when present.

This means an unrelated context file in a parent folder is intentionally visible. To constrain discovery to the repository itself:

```bash
dotnet run --project src/PiSharp.Cli -- --context-root .
```

Inside interactive mode, use:

```text
/context
```

to see exactly which files were loaded.

## Sessions

Sessions are stored under:

```text
~/.pisharp/sessions/<workspace-key>/
```

Override the root with `--session-dir` or `PISHARP_SESSION_DIR`.

Each session is append-only JSONL. The first record is application-owned session metadata. Each completed turn records:

- stable turn ID
- parent turn ID
- timestamp
- user message
- assistant text
- opaque serialized MAF `AgentSession` state

The MAF state is an implementation detail. PiSharp's IDs, parent relationships and branch semantics remain application-owned.

Useful startup options:

```bash
pisharp --continue
pisharp --resume
pisharp --session <id-prefix>
pisharp --no-session
```

Interactive commands:

```text
/session
/tree
/goto <turn-id|root>
/fork [turn-id]
/clone
/new
/resume
/context
```

`/goto` changes the active point without deleting later turns. The next prompt branches from that turn.

## CLI

```text
pisharp [options] [prompt...]

--model <name>              model name (or PISHARP_MODEL)
--endpoint <url>            OpenAI-compatible API base URL
--api-key <key>             API key
--cwd <path>                repository/workspace root
--context-root <path>       stop parent context discovery at this directory
--context-tokens <n>        context window used by Harness compaction
--max-output-tokens <n>     maximum model output tokens
-c, --continue              continue most recent workspace session
-r, --resume                select a saved workspace session
--session <id|path>         resume a specific session
--session-dir <path>        override session storage root
--no-session                disable persistence
-h, --help                  help
```

## Architecture

```text
PiSharp.Cli
   |
   +-- OpenAI-compatible IChatClient
   |
   +-- Microsoft Agent Framework HarnessAgent
   |      |
   |      +-- function invocation loop
   |      +-- AgentSession
   |      +-- compaction
   |
   +-- SessionController
   |      |
   |      +-- MAF serialize / deserialize
   |      +-- branch selection
   |      +-- resume / fork / clone
   |
   +-- PiSharp.Core
          |
          +-- AgentsFileLoader
          +-- WorkspacePathPolicy
          +-- CodingTools
          +-- SessionStore
          +-- SessionDocument
```

The design rule remains: Microsoft Agent Framework owns generic model/tool runtime mechanics where its semantics match Pi; PiSharp owns coding-agent product semantics, persistence, navigation and policy.

## Next milestone

The next slice is live-turn behaviour:

1. steering queue: messages entered while tools are running are injected before the next model turn;
2. follow-up queue: messages held until the current agent task would otherwise finish;
3. streamed tool start/update/end events in the terminal;
4. abort semantics that preserve queued user input;
5. tests around ordering and cancellation.
