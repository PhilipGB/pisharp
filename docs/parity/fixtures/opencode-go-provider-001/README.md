# OpenCode Go provider and API-key login parity

Bounded milestone on `milestone/opencode-go-provider-parity`.

| Pin | Revision |
| --- | --- |
| PiSharp base | `d432ca40eed1bb68d8e3dc37c2cf22c08fad7799` (PR #49 merge) |
| Pinned Pi oracle | `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` |
| Implementation | Recorded in `receipt.json` at the final exact head. |

The pinned Pi sources used as the oracle are
`packages/ai/src/providers/opencode-go.ts`, `opencode-go.models.ts`,
`opencode.ts`, `opencode.models.ts`, `opencode-headers.ts` and the checked-in
`data/opencode-go.json` and `data/opencode.json` catalogues.

## API-key login root cause

`TerminalProviderLogin` wrote `API key for <provider>:` with `Console.Error.Write`
and read the value with `Console.ReadKey(intercept: true)` instead of using the
active TUI editor prompt. In fullscreen raw mode the prompt bypassed the TUI
render path, produced no masked feedback, and had no deterministic dialog
lifecycle. The repair routes every API-key provider through
`TerminalEditor.PromptSequenceAsync` with a masked prompt: Enter submits,
Backspace edits, Escape cancels, the panel renders bullets instead of the
secret, and status lines are appended through the TUI. The special
`llama.cpp` URL/key flow and OAuth flows are unchanged.

PTY regressions:

- `OpenCodeApiKeyLoginPtyTests.InteractiveApiKeyLoginIsMaskedEditableCancellableAndLeavesTheTuiUsable`
  selects `/login` → API key → fixture provider through the real TUI menus,
  types a secret, verifies six bullets, Backspace to five, edits again, cancels
  with Escape, verifies nothing is stored, verifies the secret is absent from the
  transcript/stderr, then opens and closes `/model`.
- `OpenCodeApiKeyLoginPtyTests.OpenCodeGoLoginUsesTheTuiSelectsModelsLogsOutAndPersistsAcrossRestart`
  performs `/login` → API key → OpenCode Go, `/model opencode-go/claude-haiku-5-5`,
  opens `/model` and filters OpenCode Go models, runs `/logout opencode-go`,
  then uses `/login opencode-go api-key`, and verifies the stored credential
  survives a new process through `auth check --provider opencode-go`.

Fail-first at base `d432ca40` (same test files, no production changes): both PTY
tests fail because the TUI prompt/title never exists and no masked input is
rendered. See `fail-first.txt`.

## Provider contract

Both identities are built in, require API-key authentication, read
`OPENCODE_API_KEY`, and keep separate IDs, names, endpoints and catalogues.

| Provider | Display name | Endpoint | Chat models | Protocols |
| --- | --- | --- | --- | --- |
| `opencode` | `OpenCode Zen` | `https://opencode.ai/inference` | 79 (+ 2 System One classifiers already present) | anthropic-messages 17, google-generative-ai 8, openai-completions 24, openai-responses 30 |
| `opencode-go` | `OpenCode Go` | `https://opencode.ai/inference/go` | 30 | anthropic-messages 5, google-generative-ai 1, openai-completions 19, openai-responses 5 |

Catalogue files are copied from the pinned Pi JSON with `contextWindow` →
`contextLength`, `maxTokens` → `maxOutputTokens`, `cost.cacheRead` →
`pricing.cachedInput`, `cost.cacheWrite` → `pricing.cachedWrite`, `compat` →
`compatibility`, and per-model `baseUrl` preserved:

- `opencode-models.json` SHA-256
  `d2f019ba761c3fbfaaacd11ce39461b9ad6371ce393aec1cf385895f670756f9`
- `opencode-go-models.json` SHA-256
  `ab4c670ab165b759a513ea6c5cc256a5cfb58f1eaa9590b56695383b3fb8f3ad`

The default models match Pi's resolver pin: `opencode` → `kimi-k2.6`,
`opencode-go` → `kimi-k3`. `PinnedProviderModelDefaults` already contained both
identities, so initial model composition now reaches a real catalogue.

## Protocol routing

`ProviderChatClientFactory` resolves each model's `api` instead of collapsing
the provider to OpenAI compatibility. The Google guard now admits the two
OpenCode identities; every other protocol keeps its existing native adapter.
Model-specific `baseUrl` values drive the connection endpoint, so the pinned
paths are:

| Protocol | Example model | Path |
| --- | --- | --- |
| `anthropic-messages` | `claude-haiku-5-5` | `/inference/go/v1/messages` |
| `google-generative-ai` | `gemini-4-argon` | `/inference/go/v1/models/gemini-4-argon:streamGenerateContent?alt=sse` |
| `openai-completions` | `kimi-k3` | `/inference/go/v1/chat/completions` |
| `openai-responses` | `gpt-6-luna` | `/inference/go/v1/responses` |

Focused deterministic HTTP fixtures cover all four protocols for exact
path/query, auth header shape (`x-api-key`, `x-goog-api-key`, and bearer),
model ID, system/user content, tool declarations, a two-request tool-call round
trip with the tool result in the continuation, streamed usage, thinking
controls, cancellation, 401 propagation and secret redaction. No fixture uses
the live OpenCode service.

## Session header

Pi adds `x-opencode-session` only when a session ID is present and does not
replace a caller-supplied value. PiSharp reads the existing
`ProviderSessionMetadata.ConversationSessionIdProperty`, carries it through
`SamplingParametersRequestContext`, and lets `ProviderWireHookHandler` add the
header for `opencode`/`opencode-go` before wire hooks run. Deterministic tests
cover session present, absent, explicit caller value, a second conversation and
no leakage to Anthropic.

## Credential precedence and isolation

- Stored `/login` credential wins over `OPENCODE_API_KEY`, matching
  `envApiKeyAuth` in pinned Pi.
- Both OpenCode identities share the environment variable but not storage keys,
  endpoints, catalogues or request paths.
- `/logout opencode-go` removes only `opencode-go` from `auth.json`; a stored
  `opencode` credential and ambient keys remain. The regression also checks
  that an unrelated provider stays unauthenticated when only
  `OPENCODE_API_KEY` exists.
- No fixture observes the OpenCode key on a non-OpenCode request; the session
  header is also withheld from other providers.

## Verification

- Focused OpenCode tests: 22 passed.
- Full .NET suite: **4545/4545 passed** in 9 m 19 s on exact head
  `978e15efc55b9ada68b5f42a865a2de8f2571fc1`.
- `dotnet build PiSharp.slnx --warnaserror`: pass, 0 warnings.
- `dotnet format PiSharp.slnx --verify-no-changes`: pass.
- Exact-head Linux CI: [run 42707954824](https://github.com/PhilipGB/pisharp/actions/runs/42707954824)
  passed all jobs on `978e15ef`.
- Draft PR: [#50](https://github.com/PhilipGB/pisharp/pull/50).

## Live smoke

A stored OpenCode Go credential was available for final acceptance. The live
smoke used the access-enabled free model `beam-free` (`openai-completions`):

- minimal text prompt: success (`OK`);
- bash tool round trip: success (`opencode-tool-ok`);
- second turn in the same session: success (`opencode-tool-ok`);
- provider stayed selected and the session header routed the requests.

Paid models tried during model selection returned HTTP 403
`permission_error: Model access is disabled` for that account; this is an
account entitlement result, not a transport failure. No key value was printed,
committed or written to evidence.

The live run exposed one integration gap before the final fix: interactive
`ConversationRun` only supplied its conversation ID as provider session
metadata for `openai-codex-responses`, so OpenCode requests failed with
`MissingSessionID`. `ConversationRun` now supplies the existing conversation ID
for `opencode` and `opencode-go` too; a deterministic ConversationRun regression
asserts the resulting `x-opencode-session` header.

Record only provider, model, protocol, success/failure, HTTP status when
useful, and redacted evidence. Never record the key.

## Limitations

- The live OpenCode catalogue may have moved since the pinned Pi revision. This
  milestone intentionally keeps the pinned Pi catalogue as the parity oracle
  and does not silently add live-only models.
- OpenCode Console management, subscriptions and MCP remain out of scope.
- The `opencode` classifier models are the pre-existing System One catalogue
  entries; this milestone adds its chat catalogue and does not redesign
  classifiers.
