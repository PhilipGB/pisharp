# Provider retry delay and cancellation baseline

## Pinned upstream

Current Pi `main` was inspected at `earendil-works/pi@04efdfc38078f952a0436a3363e50744226ec824` on 2026-09-28. The retry helper is `packages/ai/src/utils/provider-retry.ts`; its tests are `packages/ai/test/provider-retry.test.ts` and `packages/ai/test/openai-completions-retry.test.ts`. Settings are declared and migrated in `packages/coding-agent/src/core/settings-manager.ts`, with scope-merge regression coverage in `packages/coding-agent/test/suite/regressions/7572-provider-retry-settings-merge.test.ts`.

Pi defaults `retry.provider.maxRetryDelayMs` to 60,000 ms and `maxRetries` to zero. It retries errors without a status and HTTP 408, 409, 429 or 5xx, unless `x-should-retry` explicitly says `true` or `false`. It prefers `Retry-After-Ms`, then accepts `Retry-After` as seconds or an HTTP date. A server delay above a positive configured limit fails immediately; zero removes the limit. Without a server delay, backoff starts at 500 ms, doubles up to 8 seconds and applies up to 25% jitter. The wait observes the request abort signal. Pi disables SDK retries because their backoff sleeps do not observe cancellation. Legacy `retry.maxDelayMs` migrates to the provider setting only when the nested value is absent; the nested setting takes precedence.

At this upstream SHA, isolated Vitest execution passed `provider-retry.test.ts` and `openai-completions-retry.test.ts` (8/8), plus `7572-provider-retry-settings-merge.test.ts` (1/1).

## PiSharp evidence

`ProviderRetryChatClient` is placed at the provider `IChatClient` boundary and owns retry decisions for OpenAI Responses, OpenAI Completions and Anthropic Messages. Provider SDK retry counts are set to zero. The shared `ProviderWireActivityHandler` records response status and retry headers, including for Anthropic errors whose SDK exception omits headers. Retries can restart a stream only before its first update; after any update, the failed stream is surfaced without replay. Retry waits are cancellable, including when the server-delay limit is zero.

`RetrySettings` owns parsing and scope merge separately from `UserSettings`. It validates finite, nonnegative `maxRetryDelayMs`, defaults to 60,000 ms, migrates the legacy value with nested precedence, and merges project values field-by-field over user values. `/settings` exposes the setting and refreshes the active provider runtime after changes.

Local evidence: provider/settings/PTY focused suite passed 37/37 with 0 skipped; full suite and exact-head CI are recorded in the source commit's closeout. Loopback tests cover both provider families for above-limit immediate failure and `x-should-retry: false`; a Responses SSE fixture proves pre-update retry; a fake streaming client proves there is no replay after an update; settings tests cover invalid values, default, migration, trusted project override and field-level merge; a Linux PTY test saves zero through `/settings`. This is a bounded feature slice, not full Pi retry or parity verification.
