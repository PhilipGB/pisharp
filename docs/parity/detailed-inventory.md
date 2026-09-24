# Detailed reference interface inventory — baseline 8676a0dc

Supplement to the authoritative [feature matrix](feature-matrix.md). These are *interfaces*, not claims of implementation. Default status for every item listed here is **Not started** unless the matrix explicitly records an in-progress subset. No item is Verified by its appearance in this file. Each group requires contract/error/PTY scenarios before verification. Source references below use `packages/coding-agent/docs/` unless qualified otherwise. Installed Pi reports version `0.87.1`; pinned checkout builds the same version, but the commit hash is the reproducible target.

## Built-in interactive commands (`slash-commands.md`, `src/core/slash-commands.ts`)

| Group | Command interfaces | Expected workflow / recovery | PiSharp status and test obligation |
|---|---|---|---|
| Model/auth | `/settings`, `/model [provider/model]`, `/thinking [level]`, `/scoped-models`, `/login [provider]`, `/logout`, `/llama` | Select/filter/cycle models, clamp thinking to capabilities, persist settings/auth without leaking secrets, retry router disconnect | In progress: configured compatible provider, exact/fuzzy model lookup, thinking validation and private API-key entry/logout tested by unit, HTTP-process and Linux PTY fixtures; no browser OAuth/refresh, broad hosted-provider adapters, `/settings`, `/llama` or full selector UI |
| Session | `/new`, `/resume`, `/name [name]`, `/session`, `/tree`, `/fork`, `/clone`, `/import <path>` | Prompt history tree, project association, rename/delete picker, clone vs fork, invalid import recovery | In progress: `/sessions`, `/resume <id|name>`, basic clone, branch selection and PTY session test; no interactive picker, trash or import; metadata search and confirmation-gated permanent deletion tested under PTY; `/fork <id>` copies prior active path and seeds an editable draft with unit and PTY evidence |
| Context | `/compact [instructions]` | Summarize with cut-point, keep original branch; surface summary failure | In progress: manual whole-turn model-context compaction, raw tree preserved; tests cover persistence, repeated summaries, inactive malformed branch and failure rollback. Opt-in pre-prompt approximate auto budget has deterministic provider tests; automatic between-tool-call budget, exact token accounting, split-turn and overflow recovery remain |
| Output/share | `/copy`, `/export [path]`, `/share`, `/bug [description]` | Clipboard/export or upload, user confirms sensitive content; upload error offers export | In progress: standalone private escaped HTML `/export` tested with inactive branches, tool failures, cancellation and PTY; no JSONL export, clipboard/share/report |
| Runtime | `/trust`, `/reload`, `/hotkeys`, `/changelog`, `/quit` | Trust persistence, resource reload without stale handlers, interactive help and clean terminal exit | `/quit`, `/trust yes|no|forget`, `/reload` implemented in basic CLI; PTY tests cover only baseline interactions |
| Dynamic | template names, `/skill:name`, extension-registered commands | Loaded resource commands discoverable and completable; missing resource error | Not started; resource lifecycle tests |

## CLI (`cli.md` and `src/cli/`)

| Group | Flags/interfaces | Required behaviour / recovery | Status |
|---|---|---|---|
| Prompt/modes | `[@files...] [messages...]`, `--`, piped stdin, `-p/--print`, `--mode text/json/rpc`, `--export`, `-h/--help`, `-v/--version` | Input expansion, TTY-sensitive default mode, stdout clean in machine modes | In progress: PiSharp print/JSON/RPC/TTY paths and resource input expansion; positional @files pass UTF-8 text and PNG/JPEG/GIF/WebP DataContent through initial runs, while resize/clipboard/editor paste remain absent. Standalone `--export <PiSharp-session-file> [output.html]` produces private escaped HTML without provider initialization, with separate process fixture for duplicate/corrupt/symlink input and explicit destination. Other flags and upstream wire semantics differ or remain missing. |
| Models | `--provider`, `--model`, `--models`, `--list-models`, `--thinking`, `--api-key` | Discovery, filtering, scoped model cycling, credential precedence and capability handling | In progress: configured OpenAI-compatible `--provider` and exact/fuzzy `--model`, `--models` scope, `--thinking`, `--api-key`, `--local` and bounded `--list-models`; credentials via private auth.json or environment; exact configured IDs work without `/models`. One HTTP-process fixture validates prompt streaming and credential isolation, and one PTY fixture validates API-key entry/model switch. No broad hosted-provider settings or OAuth authorization/refresh. |
| Sessions | `-c/--continue`, `-r/--resume`, `--session`, `--session-id`, `--fork`, `--session-dir`, `--no-session`, `-n/--name` | Project grouping, exact/partial IDs and incompatibility checks | In progress: `--continue`, `--session`, `--session-dir`, `--no-session` and interactive `/resume <id>`; no CLI picker or session-id; `--session` resolves project ID prefixes and `--fork` copies a source path/project ID, both process-tested but no cross-project fork; startup `--name` and `--model` tested through CLI process, selected session metadata persists immediately; explicit conflicting saved model fails closed |
| Tools | `-t/--tools`, `-xt/--exclude-tools`, `-nbt/--no-builtin-tools`, `-nt/--no-tools` | Tool presence changes for next request; invalid names reported | Not started |
| Resources | `-e/--extension`, `-ne/--no-extensions`, `--skill`, `-ns/--no-skills`, `--prompt-template`, `-np/--no-prompt-templates`, `--theme`, `--use-theme`, `--no-themes`, `-nc/--no-context-files` | Local paths, trust, discovery and explicit override precedence | Not started |
| Runtime | `--system-prompt`, `--append-system-prompt`, `--tui-mode`, `--verbose`, `-a/--approve`, `-na/--no-approve`, `--offline` | Per-invocation overrides, trust for project resources, no accidental network activity | `--approve` and `--no-approve` gate project system prompts; remaining flags absent |
| Auth | `auth check`, `auth print-api-key`, `auth print-bearer-token` | Provider/model resolution, status/exit code, credential output only on explicit request | In progress: offline `auth check --provider <id> [--model <configured-exact-id>]` reports only local credential availability and source with explicit 0/1/2 exit codes; separate CLI-process fixture tests missing/stored credentials and rejects credential printing. No remote readiness check, token refresh or credential-output commands. |
| Pi Packages | `install`, `remove/uninstall`, `update` package targets, `list`, `config` | Package management only | Excluded: Pi Packages; model catalogue refresh independent of packages remains in scope |

## RPC and JSON events (`rpc-commands.md`, `json.md`, `src/modes/rpc/`)

The commands below are reference interfaces, not claims of implementation; the feature matrix and notes identify partial subsets, and no RPC group is Verified. JSONL uses LF framing (not JavaScript `readline`'s Unicode line separators); stdout must contain only protocol records. `prompt` success means accepted, not completed; `agent_settled` follows retries/queued work, unlike `agent_end`.

- Prompt/queue: `prompt`, `steer`, `follow_up`, `abort`, `clear_queue`, `new_session` (event-order and queue-cancel tests).
- State/model: `get_state`, `get_messages`, `set_model`, `cycle_model`, `get_available_models`, `set_thinking_level`, `cycle_thinking_level`, `get_available_thinking_levels`, `set_steering_mode`, `set_follow_up_mode` (correlated request/response tests).
- Recovery/context: `compact`, `set_auto_compaction`, `set_auto_retry`, `abort_retry` (asynchronous failure and settled-event tests).
- Shell/stats/export: `bash`, `abort_bash`, `get_session_stats` (in progress: approximate context, persisted provider token/cost accounting including cache-write counts; no Pi wire format), `export_html` (in progress: private non-overwriting HTML, no Pi wire format) (parallel command/output/cancellation tests).
- Tree/session: `switch_session`, `fork`, `clone`, `get_fork_messages`, `get_entries`, `get_tree`, `get_last_assistant_text`, `set_session_name`, `get_commands` (selected branch and naming tests).
- Events: `agent_start/end/settled`, `turn_start/end`, `message_start/update/end`, `tool_execution_start/update/end`, `queue_update`, `entry_appended`, `session_info_changed`, `thinking_level_changed`, compaction and retry events (ordered deterministic provider fixtures).

## Terminal keyboard/actions (`keybindings.md`, `packages/tui/src/keys.ts`, `src/core/keybindings.ts`)

All actions in this table are **Not started**, except the line REPL's process-level Ctrl+C cancel. Configuration via `<agent-dir>/keybindings.json` must override or disable bindings, respect widget precedence and terminal capabilities; test end-to-end in PTY.

| Context | Default keys and actions | Edge cases/tests |
|---|---|---|
| Editor movement/history | Up/Down, Left/Right, Ctrl+B/F, Alt/Ctrl+arrows, Alt+B/F, Home/End, Ctrl+A/E, PageUp/Down, Ctrl+], Ctrl+Alt+] | Unicode widths, visual lines vs history, selection |
| Editor mutation | Backspace, Delete/Ctrl+D, Ctrl+W/Alt+Backspace, Alt+D/Delete, Ctrl+U/K, Ctrl+Y, Alt+Y, Ctrl+- | Kill ring, undo, cursor/selection integrity |
| Editor input | Enter submit, Shift+Enter/Ctrl+J newline, Tab completion, Ctrl+C copy, bracketed paste, Ctrl+G external editor, Ctrl+V image | Paste multiline, file @ and slash completion, clipboard type |
| App control | Esc abort, Ctrl+C clear/exit, Ctrl+D exit empty, Ctrl+Z suspend, double Esc tree/fork | Stop tool process tree, restore raw mode on exit/suspend |
| Model/display | Ctrl+L selector, Ctrl+P/Shift+Ctrl+P cycle, Shift+Tab thinking cycle, Ctrl+T thinking visibility, Ctrl+O tools expand, Ctrl+X copy | Model change preserves context; disclosure persists |
| Queue/session | Alt+Enter follow-up, Alt+Up dequeue, session Ctrl+P/S/N/R/D/Backspace picker controls | Steering vs follow-up ordering and queue restore |
| Tree/scoped picker | Ctrl+Left/Right fold, Shift+L/T label, Ctrl+D/T/U/L/A/O filters, scoped Ctrl+A/X/P/S, Alt+Up/Down reorder | Selection/routing precedence, branch reconstruction |
| Fullscreen | PageUp/Down, Home/End, Ctrl+Shift+Up/Down, Ctrl+Shift+F search, Enter/Shift+Enter search navigation, Esc close | Resize, scrolling, focus and screen restoration |

## Settings inventory (`settings.md`, `configuration.md`, `security.md`)

User settings `<agent-dir>/settings.json`, trusted project `.pi/settings.json`, keybindings, `models.json` and `auth.json` have distinct trust/precedence rules. All settings below are **Not started** unless noted; compare their types/defaults/validation to the reference before implementation.

| Group | Settings |
|---|---|
| Model/interaction | `defaultProvider`, `defaultModel`, `defaultThinkingLevel`, `modelThinkingLevels`, `thinkingBudgets`, `enabledModels`, `hideThinkingBlock`, `showCacheMissNotices`, `cacheWarming`, `steeringMode`, `followUpMode`, `externalEditor`, `doubleEscapeAction`, `treeFilterMode`, `defaultProjectTrust` |
| Tools/context | `defaultTools`, `sessionDir`, `compaction.enabled`, `compaction.reserveTokens`, `compaction.keepRecentTokens`, `compaction.modelOverrides`, `branchSummary.reserveTokens`, `branchSummary.skipPrompt` |
| TUI/images | `theme`, `quietStartup`, `tuiMode`, `fullscreenExitOutput`, `fullscreenScrollbar`, `fullscreenCopyOnSelect`, `editorPaddingX`, `outputPad`, `autocompleteMaxVisible`, `showHardwareCursor`, `terminal.showImages`, `terminal.imageWidthCells`, `terminal.clearOnShrink`, `terminal.showTerminalProgress`, `terminal.hyperlinks`, `terminal.images`, `terminal.trueColor`, `images.autoResize`, `images.blockImages`, `markdown.codeBlockIndent`, `markdown.mermaid` |
| Network/retry/shell | `transport`, `httpProxy`, `httpIdleTimeoutMs`, `websocketConnectTimeoutMs`, `retry.enabled`, `retry.maxRetries`, `retry.baseDelayMs`, `retry.maxAgentDelayMs`, `retry.provider.timeoutMs`, `retry.provider.maxRetries`, `retry.provider.maxRetryDelayMs`, `shellPath`, `shellCommandPrefix` |
| Resources | `extensions`, `skills`, `prompts`, `themes`, `enableSkillCommands` (paths, globs, trust and reload) |
| Misc | `collapseChangelog`, `enableInstallTelemetry`, `enableAnalytics`, `warnings.anthropicExtraUsage` |
| Excluded | `packages`, `npmCommand` when only used for Pi Packages |

## Product slices still requiring source/test audit

Provider catalogue/Auth (`packages/ai/src/providers/`, `docs/providers.md`); session tree/migrations (`src/core/session-manager.ts`, `docs/session-format.md`); compaction (`src/core/compaction/`); tool edge cases (`src/core/tools/`, `test/tools.test.ts`); TUI layouts/alt-screen (`packages/tui/src/`); resource trust/skills/templates/themes/extensions (`src/core/resource-loader.ts`, `src/core/extensions/`); Linux images and clipboard. Not yet exhaustive on undocumented extension hooks, model-specific options, settings interaction and UI overlays; resolve from source before marking any corresponding row Verified.
