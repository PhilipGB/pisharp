# PiSharp

Experimental C#/.NET port of the core ideas behind [`earendil-works/pi`'s coding agent](https://github.com/earendil-works/pi/tree/main/packages/coding-agent), built on Microsoft Agent Framework.

This is an incremental port, not a line-for-line translation. See [`docs/PARITY.md`](docs/PARITY.md) for the pinned upstream parity matrix and conformance plan.

## Current milestone

Implemented and smoke-tested against llama.cpp:

- Microsoft Agent Framework `HarnessAgent` runtime
- OpenAI and OpenAI-compatible endpoints, including llama.cpp
- Pi-native compaction with deterministic token estimates, turn-aware cut points, persisted summaries, and overflow recovery; Harness compaction is disabled
- `read`, `write`, `edit`, `bash`, `ls`, `find`, and `grep` tools
- Pi-style unique exact-match edit replacements
- workspace path traversal protection for file tools
- global and hierarchical `AGENTS.md` loading
- `AGENTS.override.md` precedence and `CLAUDE.md` fallback
- optional `--context-root` to stop parent context discovery at an explicit boundary
- `/context` source reporting
- streaming terminal output
- bracketed multi-line paste handling: one terminal paste is submitted as one prompt
- one-shot and interactive modes
- durable append-only JSONL sessions with Pi v3 typed entries
- stable turn IDs and `parentId` tree semantics
- semantic user/assistant/tool-result records with MAF state as a cache bridge
- MAF `AgentSession` serialization/restoration inside durable turns
- legacy PiSharp v1 session reads
- `-c` / `--continue`
- `-r` / `--resume`
- `--session <id|path>`
- `--name <text>`
- `/session`, `/name`, `/label`, `/stats`, `/tree`, `/goto`, `/compact`, `/fork`, `/clone`, `/new`, `/resume`
- unit tests for file/path/context/session/terminal-input/resource behaviour
- thread-safe steering and follow-up queues with abort preservation
- streamed tool start/update/end rendering
- conservative fuzzy edit matching, human-readable diffs, and unified patches
- bounded deterministic tool-output truncation
- prompt history with draft-preserving previous/next navigation
- Agent Skills discovery and `/skill:name` expansion
- markdown prompt templates with Pi-compatible argument substitution
- local `package.json` Pi resource manifests
- trusted .NET extension commands, input transforms, and lifecycle hooks
- initial JSON event and JSON-RPC headless modes
- read-only and no-tools execution policies
- bounded transient-provider retries with exponential backoff
- `@file` text and image attachments for one-shot and print prompts
- PiSharp-owned project trust decisions with inherited paths and fail-closed headless startup

Remaining parity work:

- Pi-equivalent full-screen tree picker and richer TUI/editor/keybindings
- provider login/OAuth and dynamic model catalogue
- full Pi-compatible JSON/RPC event schemas and command coverage
- provider failover and model fallback
- image input in interactive/RPC prompts and image resizing/validation
- shell approval/sandbox policy and command-level permission prompts
- remote/npm/git package installation and package filtering
- loading TypeScript/JavaScript extensions (PiSharp currently loads trusted .NET DLLs)
- extension UI primitives, custom tools, themes, and provider registration
- full resource reload and live settings management
- exact JSON/RPC protocol parity (current headless modes provide a compatible initial subset)

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

### Terminal input

On an interactive terminal PiSharp enables bracketed-paste mode. A multi-line paste is collected and submitted to the agent as a **single prompt**, instead of each pasted line becoming an independent turn. Slash commands are recognized only when the submitted input is a single line, so pasted transcripts beginning with `/` are not accidentally executed as PiSharp commands.

This is deliberately a focused fix rather than the final Pi-style editor. Rich multi-line editing, keybindings, and full-screen terminal UI remain future work. While a turn is active, ordinary input is queued as steering; `/follow-up <text>` queues input for after the current run.

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
/label <entry-id> [text]
/tree
/goto <entry-id|root> [--summarize]
/compact [instructions]
/fork [turn-id]
/clone
/new
/resume
/context
/steer <text>
/follow-up <text>
```

`/label <entry-id> [text]` bookmarks an entry (omit the text to clear it); labels render in `/tree` as `[label]`. `/goto` changes the active point without deleting later turns. The next prompt branches from that turn. Prompt templates are loaded from `~/.pi/agent/prompts` and `.pi/prompts`; invoke one as `/name args`. Skills are loaded from `~/.pi/agent/skills` and `.pi/skills`; invoke one explicitly as `/skill:name args`.

## CLI

```text
pisharp [options] [@files...] [prompt...]

--model <name>              model name (or PISHARP_MODEL)
--endpoint <url>            OpenAI-compatible API base URL
--api-key <key>             API key
--cwd <path>                repository/workspace root
--context-root <path>       stop parent context discovery at this directory
--extension, -e <path>      load a trusted .NET extension DLL/directory (repeatable)
--skill <path>              load a skill file/directory (repeatable)
--prompt-template <path>    load a prompt template file/directory (repeatable)
--mode <text|json|rpc>      select text, JSON event, or JSON-RPC output
--print, -p                 run one prompt and exit
--read-only                 expose only read/search tools
--no-tools, -nt             disable built-in tools
--no-auto-retry             disable transient provider retries
--no-extensions, -ne        disable default extension discovery
--no-skills, -ns            disable default skill discovery
--no-prompt-templates, -np  disable default prompt discovery
--approve, -a              trust project-local resources for this run
--no-approve, -na          ignore project-local resources for this run
--context-tokens <n>        model context window used by Pi-native compaction
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
   +-- TerminalPromptReader
   |
   +-- OpenAI-compatible IChatClient
   |
   +-- Microsoft Agent Framework HarnessAgent
   |      |
   |      +-- function invocation loop
   |      +-- AgentSession
   |      +-- Pi-native compaction/summarisation
   |
   +-- SessionController
   |      |
   |      +-- MAF serialize / deserialize
   |      +-- branch selection and summaries
   |      +-- resume / fork / clone
   |
   +-- PiSharp.Core
          |
          +-- AgentsFileLoader
          +-- ProjectTrustStore / ProjectTrustResolver
          +-- WorkspacePathPolicy
          +-- CodingTools
          +-- SessionStore
          +-- SessionDocument
          +-- SkillCatalog / PromptTemplateCatalog / PiPackageCatalog
          +-- PiSharpExtensionHost
```

The design rule remains: Microsoft Agent Framework owns generic model/tool runtime mechanics where its semantics match Pi; PiSharp owns coding-agent product semantics, persistence, navigation, resource discovery and policy.

## Resource loading

PiSharp reads default resources without network access:

- skills: `~/.pi/agent/skills`, `~/.agents/skills`, and trusted `<workspace>/.pi/skills`/ancestor `.agents/skills`;
- prompt templates: `~/.pi/agent/prompts` and trusted `<workspace>/.pi/prompts`;
- packages: local package directories under `~/.pi/agent/packages` and `<workspace>/.pi/packages`, using the `pi` fields in `package.json`;
- extensions: trusted `.dll` files under `~/.pi/agent/extensions` and trusted `<workspace>/.pi/extensions`.

Project `.pi` resources, project `.agents/skills`, and project package contributions are discovered only after the project trust decision. User/global resources remain available while a project is untrusted. `AGENTS.md` and `CLAUDE.md` context traversal is intentionally not trust-gated. Package resources are lower precedence than user and project resources. Extension assemblies execute with the CLI process's permissions, so only load code from sources you trust.

### Project trust

PiSharp resolves project trust before `AgentFactory` discovers or loads project extensions and other instruction-bearing resources. Decisions are stored in `~/.pisharp/trust.json`, intentionally separate from Pi's TypeScript `trust.json` because PiSharp executes .NET assemblies. Decisions inherit from parent directories; a nearer decision overrides an ancestor.

Interactive startup offers trust, trust-parent, session-only, and deny choices. `--approve`/`-a` and `--no-approve`/`-na` override saved decisions for one invocation. Print, JSON, and RPC modes never prompt: `defaultProjectTrust` is read from the global `~/.pi/agent/settings.json`, and `ask` fails closed. `/trust` saves a decision for a future restart; it does not load newly enabled project resources into the current process.
