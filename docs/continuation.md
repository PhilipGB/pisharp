# Continuation handoff — milestone 0/early milestone 1

## State

- Greenfield repository initialized with .NET 10 solution (`PiSharp.slnx`), CLI and xUnit test project. Reference checkout is `/tmp/pisharp-upstream` pinned to `002fc8385268300ca91a5fc95f935c2afbbdac02`; re-clone there at the exact hash if the temporary checkout is gone. Do not substitute a newer HEAD.
- `docs/parity/feature-matrix.md` lists the initial feature families and explicit differences. **Inventory is not yet exhaustive** at the subfeature/shortcut/edge-case level; expand before claiming parity. The pinned upstream tool suite ran successfully (84/84) after installing/building in the temporary checkout; no **cross-implementation** fixture comparison has been executed. No row is Verified.
- `PiAgent` is a `ChatClientAgent` with four local `AIFunction` tools; `OpenAIClient.AsIChatClient()` supplies OpenAI or Chat Completions-compatible model streams. Same agent drives line-oriented terminal and `--print`, using a per-process `AgentSession`. The session is NOT persisted. `CodingTools` has basic text read, single-block edit, write, and bash. Function result messages and streamed text reach the caller. Tool tests plus a scripted model integration test run without credentials.
- UI is a basic REPL, **not** Pi's TUI. Only `/quit` is recognized; no slash-command framework, keyboard editor or project trust exists. No images, session JSONL, JSON/RPC, provider discovery/auth flows, compaction, resources or extensions. There is no live-provider test. The command runner does not implement upstream live-output or full-output retention. Do not treat this implementation as safe for untrusted projects; tools use process privileges.

## Decisions and adaptation

- MAF 1.21.0 / MEAI.OpenAI 10.10.0 pinned in project; use `ChatClientAgent`'s built-in function-invoking wrapper and MAF `AgentSession` for the initial model/tool loop. Pi's tree-shaped JSONL history will need an application-owned canonical log and context reconstruction instead of assuming MAF session serialization alone models branches; avoid writing an incompatible persistence format.
- OpenAI SDK Chat Completions used for broad endpoint compatibility. Provider capability negotiation, authentication and thinking settings still required. No TS extension source compatibility required; C# plugin API should preserve observable capabilities.

## Next concrete task

+ Read the pinned `packages/coding-agent/src/core/tools/{edit,edit-diff,file-mutation-queue,truncate,read,bash,output-accumulator}.ts` and corresponding `test/*` thoroughly. Use the now-runnable upstream tool test suite to capture exact deterministic tool-output fixtures and add an upstream conformance test project.
+ Change `edit` from `(path,oldText,newText)` to upstream `edits[]` (validate each unique/nonoverlapping region against original snapshot; BOM/CRLF/diff; queue same-file mutations). Adjust tool schemas and test ambiguous/missing/overlap/no-write/CRLF cases against upstream. Then complete read and bash truncation/cancellation to reference contracts. Never mark Verified on local tests alone.
+ Implement project-independent session JSONL tree before broadening interfaces; inspect `docs/session-format.md` and `src/core/session-manager.ts` and capture versioned fixtures. For TUI, inspect `packages/tui/src/{tui,terminal,editor-component}.ts` and make an explicit library/VT decision, then add PTY tests. Keep `PiAgent` the one runtime for all modes.

## How to reproduce

```sh
git status --short
git -C /tmp/pisharp-upstream rev-parse HEAD
dotnet format PiSharp.slnx --verify-no-changes --no-restore
dotnet build PiSharp.slnx --no-restore --warnaserror
dotnet test PiSharp.slnx --no-build --no-restore
OPENAI_API_KEY=... dotnet run --project src/PiSharp.Cli -- --print 'Read README.md'
```

Conformance evidence: upstream pinned commit, source/docs and passing upstream tool suite (84 tests), **no differential runtime results**. Integration test validates MAF tool invocation/continuation against scripted provider, not Pi.
