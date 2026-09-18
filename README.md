# PiSharp

Experimental C#/.NET port of the core ideas behind [`earendil-works/pi`'s coding agent](https://github.com/earendil-works/pi/tree/main/packages/coding-agent), built on Microsoft Agent Framework.

This is an incremental port, not a line-for-line translation. See [`docs/PARITY.md`](docs/PARITY.md) for the pinned upstream parity matrix and conformance plan.

## Current milestone

Implemented and smoke-tested against llama.cpp:

- Microsoft Agent Framework `HarnessAgent` runtime
- OpenAI and OpenAI-compatible endpoints, including llama.cpp
- Pi-native compaction owned by PiSharp end to end: deterministic token estimates, turn-aware cut points, pre-prompt compaction at provider-request boundaries, forced provider-overflow recovery that retries only the failed request (never the prompt or already-run tools), persisted summaries, and branch summaries; Harness compaction is disabled
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
- `/session`, `/name`, `/label`, `/stats`, `/tree`, `/goto`, `/compact`, `/fork`, `/clone`, `/new`, `/resume`, `/settings`, `/reload`
- Pi-compatible settings system: global `~/.pi/agent/settings.json` plus trusted-project `.pi/settings.json`, deep merge with project precedence, legacy-format migrations, malformed-file diagnostics, `defaultProjectTrust`, settings-backed compaction budgets (with per-model overrides), retry budgets, session directory, and keybinding configuration
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
- provider/model runtime: full pinned built-in provider set (including the dynamic `llama.cpp` router provider with `/llama` load/unload/Hugging Face download) plus `models.json` file providers and the remote catalog, deterministic startup model resolution, live `/model` (exact reference, cycling, scope) and `/thinking` with persisted `model_change`/`thinking_level_change` entries, dynamic context-window and max-output tracking of the current model
- provider credentials: `auth.json` storage, `/login`/`/logout`, `pisharp auth check|print-api-key|print-bearer-token` with pinned exit codes, runtime API-key overrides, offline model-catalog cache
- pinned retry semantics: provider-layer retries (Retry-After, backoff, streaming before-content-only) with a turn-level classifier (billing/quota errors never retried), turn restarts composing fresh provider budgets, and HTTP idle timeout
- per-response usage and tier-aware cost persisted in every durable assistant entry
- `@file` text and image attachments for one-shot and print prompts
- PiSharp-owned project trust decisions with inherited paths and fail-closed headless startup

Remaining parity work (tracked per capability in [`docs/PARITY.md`](docs/PARITY.md)):

- provider/model gaps: live OAuth round-trips, `enabledModels`/`/scoped-models`, the account-scoped `radius` gateway, and provider-specific credential paths (AWS profiles, GCP ADC, Cloudflare account ids)
- consuming the remaining settings values: queue modes (`steeringMode`/`followUpMode`), `defaultTools`, resource paths, terminal/image/TUI options (the values are already parsed and exposed by the settings manager)
- settings-backed keybindings in the terminal UI
- full session product surface: interactive picker, delete, import, JSONL/HTML export, statistics with usage/cost totals, v1 migration
- tool parity: Pi's exact default tool selection, schemas, partial tool output, serialized file mutations, PowerShell on Windows
- resource/package/extension lifecycle: npm/git/local package sources, install/remove/list/update, full Pi extension API surface, themes
- Pi-equivalent terminal UI: multiline editor, selectors, tree picker, status footer, configurable keybindings
- interactive shell commands (`!command`, `!!command`) and interactive image input
- exact JSON event and RPC protocol parity plus a typed .NET RPC client
- an embeddable .NET SDK exposing the runtime

Pi does not have arbitrary provider failover, per-command shell approval, or sandboxing, so none of those are planned as "parity".

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

The `llama.cpp` provider also supports the router management flow: point `LLAMA_BASE_URL` (or `/login llama.cpp`) at a `llama-server` with the router enabled (`--models`, `--autoload`), then use `/llama` to list, load, unload, or download models (Hugging Face `owner/repo[:quant]`) and `/model` to switch to a loaded one. The server URL comes from the login-stored credential or the ambient `LLAMA_BASE_URL`; when neither is set the provider has no server (its models are unavailable) — `http://127.0.0.1:8080` is only the login prompt's fallback. Ctrl+C during a `/llama` load or download stops just that operation (the server-side stop plus restore of any replaced models) and returns to the menu; a second Ctrl+C exits.

### Terminal input

On an interactive terminal PiSharp enables bracketed-paste mode. A multi-line paste is collected and submitted to the agent as a **single prompt**, instead of each pasted line becoming an independent turn. Slash commands are recognized only when the submitted input is a single line, so pasted transcripts beginning with `/` are not accidentally executed as PiSharp commands.

This is deliberately a focused fix rather than the final Pi-style editor. Rich multi-line editing, keybindings, and full-screen terminal UI remain future work. While a turn is active, ordinary input is queued as steering; `/follow-up <text>` queues input for after the current run.

## Context files

By default PiSharp follows Pi's behaviour and searches from the filesystem root down to the current workspace, selecting one of these files per directory:

1. `AGENTS.override.md`
2. `AGENTS.md`
3. `CLAUDE.md`

It also loads `~/.pi/agent/AGENTS.md` when present.

## Settings

PiSharp reads the same two-scope configuration as Pi:

```text
~/.pi/agent/settings.json   global settings (override with PI_CODING_AGENT_DIR)
<workspace>/.pi/settings.json  project settings (read only while the project is trusted)
```

Project values win per key; nested objects merge recursively while arrays and scalars are replaced. Legacy Pi formats are migrated on load (`queueMode` → `steeringMode`, `websockets` → `transport`, the object-form `skills` → array, `retry.maxDelayMs` → `retry.provider.maxRetryDelayMs`). A malformed settings file never aborts startup: the scope falls back to defaults and a warning is printed.

Values the runtime already consumes from settings:

- `defaultProjectTrust` — global-only default for the project trust decision (invalid values fall back to `ask`)
- `compaction.*` — reserve/keep-recent budgets, enable flag, and per-model `modelOverrides` keyed by `provider/modelId`
- `retry.*` — retry enable flag, attempt count, backoff delays (still overridable by `--no-auto-retry`)
- `sessionDir` — session storage root (CLI `--session-dir` and `PI_CODING_AGENT_SESSION_DIR` take precedence)

`~/.pi/agent/keybindings.json` is also loaded, migrated, and re-read by `/reload`; the terminal UI will consume it when it lands.

In interactive mode:

```text
/settings                  show file locations and effective values
/settings <key> <value>    set a global value ("unset" clears it back to the Pi default)
/reload                    re-read settings and keybindings
```

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

Override the root with `--session-dir`, `PI_CODING_AGENT_SESSION_DIR`, or the `sessionDir` setting (in that order of precedence). The default location moves to Pi's `~/.pi/agent/sessions/<encoded-cwd>/` layout when the session product surface lands.

Each session is append-only JSONL in the Pi v3 session format. The first line is the session header (version 3, session id, timestamp, cwd, and — for forks — the source session path in `parentSession`). Every durable event is then appended as its own typed entry linked through a stable `id`/`parentId` chain:

- `message` entries — each user prompt, each steering/follow-up message, each completed assistant message (with api/provider/model identity, `responseModel` when the provider reports a different one, stop reason, and usage with tier-aware cost), and each tool call/result record
- `compaction` entries — the persisted summary, first kept entry, token estimate, details and usage
- `branch_summary` entries — summaries of abandoned work created when navigating the tree
- `model_change` / `thinking_level_change` entries — model and thinking-level switches (appended at startup and by `/model`, `/thinking`, and post-login selection)
- `label` entries — user bookmarks on arbitrary entries
- `session_info` entries — session display name changes
- `pisharp.agent-state` entries — serialized MAF `AgentSession` state

The typed Pi transcript is authoritative; serialized MAF `AgentSession` state is only an implementation cache. PiSharp rebuilds the effective context from the typed entries after the latest compaction or branch boundary and restores a cached MAF session only when no boundary post-dates it; compaction and navigation always start a fresh MAF session. Session ids, parent links, and branch semantics are application-owned.

Legacy PiSharp v1 turn-based sessions remain readable so existing workspaces keep working; they are not rewritten.

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
/llama
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
/model [provider/model|next|prev] [--persist]
/thinking [level|next] [--persist]
/login [provider]
/logout [provider]
```

`/name [text]` shows or sets the session display name. `/label <entry-id> [text]` bookmarks an entry (omit the text to clear it); labels render in `/tree` as `[label]`. `/goto` changes the active point without deleting later turns. The next prompt branches from that turn. Prompt templates are loaded from `~/.pi/agent/prompts` and `.pi/prompts`; invoke one as `/name args`. Skills are loaded from `~/.pi/agent/skills` and `.pi/skills`; invoke one explicitly as `/skill:name args`.

## CLI

```text
pisharp [options] [@files...] [prompt...]
pisharp auth <check|print-api-key|print-bearer-token> [options]

--model <name>              model name (or PISHARP_MODEL), e.g. openai/gpt-4o or local/my-model with --endpoint
--provider <id>             provider id (or PISHARP_PROVIDER) with the model's default model
--endpoint <url>            OpenAI-compatible API base URL (or PISHARP_ENDPOINT)
--api-key <key>             API key (or PISHARP_API_KEY / OPENAI_API_KEY)
--models <refs>             comma-separated provider/model scope for cycling and /model listing
--thinking <level>          thinking level: off, minimal, low, medium, high, xhigh, max (or PISHARP_THINKING)
--list-models               list available models (optionally --list-models <provider>) and exit
--offline                   no network for model catalogue refresh
--cwd <path>                repository/workspace root
--context-root <path>       stop parent context discovery at this directory
--extension, -e <path>      load a trusted .NET extension DLL/directory (repeatable)
--skill <path>              load a skill file/directory (repeatable)
--prompt-template <path>    load a prompt template file/directory (repeatable)
--mode <text|json|rpc>      select text, JSON event, or JSON-RPC output
--print, -p                 run one prompt and exit
--read-only                 expose only read/search tools
--no-tools, -nt             disable built-in tools
--no-auto-retry             disable transient provider retries (settings still define the budget)
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
--session-dir <path>        override session storage root (also PI_CODING_AGENT_SESSION_DIR / sessionDir setting)
--no-session                disable persistence
-h, --help                  help
```

## Architecture

```text
PiSharp runtime (hosts: interactive, print, JSON, RPC)
   |
   +-- application-owned session/tree/history
   |      |
   |      +-- SessionController / SessionStore / SessionDocument
   |      +-- typed Pi v3 JSONL entries (user, assistant, tool, compaction,
   |      |   branch summary, label, name); MAF state as a secondary cache
   |
   +-- compaction / branch-summary authority (PiSharp-owned)
   |
   +-- model runtime (ModelRuntime: providers, credentials, startup resolution)
   |      +-- ModelSessionState (current model/thinking, scope, session overrides)
   |
   +-- IChatClient middleware
   |      |
   |      +-- provider-boundary compaction (CompactionChatClient)
   |      +-- steering injection (SteeringChatClient)
   |      +-- provider client (ModelRuntimeChatClient bridge: retry, idle timeout,
   |      |   per-request model/max-output/thinking, usage capture)
   |
   +-- MAF HarnessAgent
          |
          +-- generic tool invocation loop (Harness compaction disabled)

PiSharp.Core: deterministic algorithms (session trees, edit engine, compaction
planner, path policy, truncation, trust, prompt history) without MAF/ASP.NET
dependencies; resources (AGENTS context, skills, prompts, packages, trusted
.NET extensions) are discovered and trust-filtered before the agent starts.
```

The design rule remains: Microsoft Agent Framework owns generic model/tool runtime mechanics where its semantics match Pi; PiSharp owns coding-agent product semantics, persistence, navigation, resource discovery and policy. Harness never performs PiSharp compaction — it runs the tool loop against whatever history the middleware hands it.

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
