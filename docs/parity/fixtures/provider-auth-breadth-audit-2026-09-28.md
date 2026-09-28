# Provider and authentication breadth audit

Reference: `earendil-works/pi@cd5ff1125981f0fc37a56df25eee7310286a3f89`, refreshed 2026-09-28. Since `c1449660c83fd00a7c71d5f7e1bd29fafd400550`, current `main` added durable package work under `packages/chord` and `packages/durable`; Pi Packages remain excluded. This is a bounded first audit of the provider/auth phase, not the final compatibility audit.

## Current Pi surface

`packages/ai/src/types.ts` declares 10 chat API families and 42 built-in provider IDs. The API families are `openai-completions`, `mistral-conversations`, `openai-responses`, `azure-openai-responses`, `openai-codex-responses`, `anthropic-messages`, `bedrock-converse-stream`, `google-generative-ai`, `google-vertex`, and `pi-messages`. Provider profiles also register image and classifier APIs separately; OpenRouter has an `openrouter-images` adapter and TypeSafe classifier support.

Eight built-in providers expose OAuth methods: Anthropic, GitHub Copilot, Kimi Coding, Meta, OpenAI Codex, OpenRouter, Radius, and xAI. `packages/ai/src/auth/resolve.ts` gives a stored provider credential ownership over ambient credentials. API-key profiles resolve stored keys before environment/config values. OAuth profiles refresh tokens inside the serialized credential-store mutation, normally before five minutes of remaining lifetime, then derive provider request auth. Login methods own browser/device/manual-code interaction, and some OAuth providers change model availability or request headers.

Pi's provider-specific integrations are material. Examples in current source include Codex Responses with subscription OAuth, Google GenAI and Vertex authentication, AWS Bedrock credential resolution, Mistral reasoning and tool-call conversion, and GitHub Copilot's three API paths. Current upstream commits `dc84c1ac0afd15721071507a3a06b5e3d0cad614` and `c1449660c83fd00a7c71d5f7e1bd29fafd400550` change Mistral effort mapping and OpenCode/Qwen empty thinking-signature replay respectively. Current generated Pi metadata includes 33 Mistral chat models.

## PiSharp current boundary

`BuiltinProviderProfiles` defines OpenAI, OpenRouter, Mistral, Anthropic, and xAI, plus optional local/custom endpoints. `ProviderChatClientFactory` accepts `openai-responses`, `openai-completions`, `anthropic-messages`, and `mistral-conversations`. Mistral uses `/v1/chat/completions`; its explicit adapter retains the MAF/OpenAI client loop while mapping Mistral `reasoning_effort`/`prompt_mode` and normalizing historical tool call IDs to nine alphanumeric characters with correlated tool results. Its checked-in embedded catalogue carries all 33 model records from the current Pi generator output, including reasoning maps, image modalities, image resize limits, context/output limits and pricing. Listing uses this static catalog without sending a generic `/models` request.

`ProviderModelRuntime` resolves a runtime `--api-key` override, stored user `auth.json` API key, configured `models.json` key, then provider environment key. `AuthStorage` is private and atomic on Linux. It can store an OAuth-shaped record, but OAuth is not an active provider capability: built-in profiles do not enable OAuth, resolution reports it unavailable instead of sending or refreshing the token, `/login ... oauth` rejects those profiles, and `models.json` rejects OAuth declarations. There is no project credential scope.

Most online catalog discovery uses a generic OpenAI-compatible `/models` request. Anthropic and xAI use local profile catalogs; Mistral now uses a checked-in snapshot of Pi's generated provider/model metadata. PiSharp supports image input in existing adapters, but this provider audit does not verify image, cache, usage, or error-wire equivalence across providers. Mistral session affinity and `prompt_cache_key` behavior remain open.

## Open material work

- Implement the absent provider APIs in dependency order: Azure OpenAI Responses, OpenAI Codex Responses, Google GenAI, Google Vertex, Bedrock Converse, and Pi Messages. Treat OAuth-backed APIs as dependent on their request adapter rather than exposing nonfunctional login options.
- Add working provider-auth adapters with refresh and serialized persistence, then implement and test each in-scope login flow, credential precedence, environment/config resolution, logout, and provider-specific request auth. Pi currently has eight built-in OAuth profiles; PiSharp currently has none that can authenticate a request.
- Carry current provider semantics through streaming, tool/image content, error bodies, retry headers, cache-affinity fields, usage/cache metadata, and model discovery. A generic OpenAI-compatible response is not evidence for these provider-specific contracts.
- Keep the checked-in Mistral model snapshot aligned with current Pi's generated catalog. The snapshot covers model metadata, while Mistral session affinity, `prompt_cache_key`, usage and error-wire behavior remain open.
- Implement the OpenCode/Qwen empty thinking-signature replay change from `c1449660c83fd00a7c71d5f7e1bd29fafd400550` in the relevant provider-history boundary.

## Evidence for the current Mistral slice

Current Pi `packages/ai/test/mistral-reasoning-mode.test.ts` and `mistral-http-transport.test.ts` passed 29/29 after regenerating the current checkout's ignored provider data. PiSharp `MistralProviderCompatibilityTests`, `MistralModelCatalogTests`, and the protocol-selection test pass 7/7, covering mapped `max` and `none`, Magistral `prompt_mode`, stream behavior, nine-character tool-call/result correlation, static 33-model listing, and retained catalog metadata. Exact-head CI for the catalog and parallel-history correction is pending.
