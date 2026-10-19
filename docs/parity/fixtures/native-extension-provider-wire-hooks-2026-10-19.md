# Native extension provider wire hooks — 2026-10-19

This bounded increment descends from the actual PR #15 merge commit on `main`,
`2ea360f991b0e15c59203557a68a5a1e3c026450`, which merged reviewed head
`7c93a3d06a82b6c3de55dccc8b19b08647d2f851`. The post-merge base is the same
commit. Existing provider-wire work was rebased onto that base without
conflicts; original local product commit `9ccff7546842f6462b3ca9c7c50a9edf36e20ced`
is now integrated as `b31940137201e565b15d8061055ac7b692b627e9`. Pinned Pi
remains `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Broad extension parity
remains `in_progress`.

## Review finding and correction

The protocol audit found that a shared MEAI-level wrapper cannot satisfy Pi's
provider-wire contract: several adapters finalize provider-specific payloads,
headers, response metadata, and raw events at different physical boundaries.
The correction installs hooks after built-in adapter request construction and
before transport dispatch, observes HTTP metadata before reading response
bodies, and observes parsed raw stream events before MEAI normalization.

The real Bedrock SDK pipeline test exposed one additional gap: AWS marshalled
the final request body into `IRequest.ContentStream`, while the initial hook
only checked the string content property. The Bedrock hook now reads the
marshalled body before checksum/signing, transforms it, and maps it back to
bytes. The same pipeline installs non-reserved extension headers before
SigV4. Its regression confirms the custom header appears in the signature and
that forged `authorization`, `host`, and `x-amz-date` replacements are ignored.
Those narrow reserved-header rules match pinned Pi's Bedrock adapter.

The additive `IPiSharpExtensionProviderWireBridge` is available through the
optional `PiSharpExtensionProviderClientContext.ProviderWire` property. The
host owns ordered dispatch, callback contexts, source-attributed diagnostics,
cancellation, and reload behavior. New extension providers call it at their
physical boundaries; old `IChatClient` providers are neither adapted into fake
wire events nor required to implement it.

## Verified outcome matrix

Every built-in cell marked `REQUIRED` is exercised through its actual adapter.
Codex's different response-observer outcomes are intentionally
`TRANSPORT_SPECIFIC — PASS`.

| PiSharp protocol and transport | `before_provider_request` | `before_provider_headers` | `after_provider_response` | `provider_stream_event` |
| --- | --- | --- | --- | --- |
| `openai-responses` | REQUIRED — PASS | REQUIRED — PASS | REQUIRED — PASS | REQUIRED — PASS |
| `openai-completions` | REQUIRED — PASS | REQUIRED — PASS | REQUIRED — PASS | REQUIRED — PASS |
| `anthropic-messages` | REQUIRED — PASS | REQUIRED — PASS | REQUIRED — PASS | REQUIRED — PASS |
| `mistral-conversations` | REQUIRED — PASS | REQUIRED — PASS | REQUIRED — PASS | REQUIRED — PASS |
| `azure-openai-responses` | REQUIRED — PASS | REQUIRED — PASS | REQUIRED — PASS | REQUIRED — PASS |
| `openai-codex-responses` — SSE/HTTP | REQUIRED — PASS | REQUIRED — PASS | REQUIRED — PASS | REQUIRED — PASS |
| `openai-codex-responses` — WebSocket | REQUIRED — PASS | REQUIRED — PASS | NOT_EXPOSED_BY_PINNED_ADAPTER — VERIFIED | REQUIRED — PASS |
| `google-generative-ai` | REQUIRED — PASS | REQUIRED — PASS | NOT_EXPOSED_BY_PINNED_ADAPTER — VERIFIED | REQUIRED — PASS |
| `google-vertex` | REQUIRED — PASS | REQUIRED — PASS | NOT_EXPOSED_BY_PINNED_ADAPTER — VERIFIED | REQUIRED — PASS |
| `bedrock-converse-stream` | REQUIRED — PASS | REQUIRED — PASS | REQUIRED — PASS | REQUIRED — PASS |
| `pi-messages` | REQUIRED — PASS | REQUIRED — PASS | REQUIRED — PASS | REQUIRED — PASS |
| `pisharp-extension` — new wire-aware bridge | BRIDGE_REQUIRED — PASS | BRIDGE_REQUIRED — PASS | BRIDGE_REQUIRED — PASS | BRIDGE_REQUIRED — PASS |
| `pisharp-extension` — PR #11 `IChatClient` provider | LEGACY_UNAVAILABLE — VERIFIED COMPATIBLE | LEGACY_UNAVAILABLE — VERIFIED COMPATIBLE | LEGACY_UNAVAILABLE — VERIFIED COMPATIBLE | LEGACY_UNAVAILABLE — VERIFIED COMPATIBLE |

The three negative cases are deliberate. Google Generative AI and Vertex
complete real SSE fixtures with zero response observer calls. Codex WebSocket
completes its real WebSocket fixture with zero response observer calls, while
its SSE/HTTP fallback fixture observes the response. No status 101 or
normalized-response substitute is emitted.

## Pinned Pi source evidence

All links below are pinned to `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
The shared model runtime merges effective headers and applies the header
transform before passing them to the adapter in [`models.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/models.ts#L870-L880).

- OpenAI Responses uses its Responses payload, response, and stream callbacks in [`openai-responses.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/api/openai-responses.ts#L195-L228), then reports parsed raw events before normalization in [`openai-responses-shared.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/api/openai-responses-shared.ts#L623-L627).
- Chat Completions uses its own payload/response/chunk callbacks in [`openai-completions.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/api/openai-completions.ts#L358-L384) and reports raw chunks at lines 558–560 of that file.
- Anthropic builds its final Messages request before `onPayload`, reports response metadata before event iteration, and reports each raw event in [`anthropic-messages.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/api/anthropic-messages.ts#L648-L698).
- Mistral builds its native payload before the hook, observes the HTTP response, and calls the stream hook with raw chunks in [`mistral-conversations.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/api/mistral-conversations.ts#L142-L154), [response handling](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/api/mistral-conversations.ts#L322-L334), and [chunk handling](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/api/mistral-conversations.ts#L609-L614).
- Azure applies its deployment-name transform before the payload hook in [`providers/azure.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/providers/azure.ts#L14-L24). Its adapter handles payload/response/raw Responses events and its Azure endpoint/API-version/header mapping in [`azure-openai-responses.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/api/azure-openai-responses.ts#L76-L100) and lines 154–171.
- Codex builds one effective payload and both transport header collections in [`openai-codex-responses.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/api/openai-codex-responses.ts#L277-L290). The `onResponse` call is inside the SSE/HTTP branch at lines 395–420; the WebSocket branch sends the request and forwards raw events without an `onResponse` call at lines 1509–1552.
- Google Generative AI builds parameters, supplies transformed headers to its client, and observes chunks in [`google-generative-ai.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/api/google-generative-ai.ts#L94-L110) and lines 350–363. Vertex does the same in [`google-vertex.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/api/google-vertex.ts#L99-L119) and lines 392–404. Neither adapter invokes `onResponse`.
- Bedrock transforms the ConverseStream request, injects custom headers before SigV4 while protecting reserved headers, observes raw Smithy response metadata before reading the event stream, and reports each raw event in [`bedrock-converse-stream.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/api/bedrock-converse-stream.ts#L251-L299), [header middleware](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/api/bedrock-converse-stream.ts#L459-L493), and [response middleware](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/api/bedrock-converse-stream.ts#L508-L528).
- Pi Messages constructs payload and headers, observes response headers before consuming the body, and reports each parsed event before conversion in [`pi-messages.ts`](https://github.com/earendil-works/pi/blob/b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6/packages/ai/src/api/pi-messages.ts#L385-L416).

## Regression evidence

- `ProviderStreamIdleDeadlineResetsOnSseBytesBeforeTheNextParsedUpdate` runs actual OpenAI Responses, Chat Completions, and Anthropic HTTP/SSE adapters. It verifies transformed payloads and headers, response-before-body observation, raw stream callbacks, and the selected physical provider ID.
- `AnthropicMessagesStreamsTextAndUsage`, `MistralMapsNativeReasoningEffortAndNormalizesToolCallIds`, and `AzureResponsesSendsMappedDeploymentApiKeyAndApiVersionToResponsesEndpoint` exercise each distinct request mapping. Azure asserts its deployment path, API version, API-key header, and transformed final body/header.
- `CodexResponsesUsesCachedAccountScopedWebSocketAndRealResponsesClient` verifies payload, WebSocket headers and raw events with zero response calls. `CodexResponsesFallsBackToAccountScopedSseWhenWebSocketClosesBeforeFirstEvent` verifies the HTTP response callback and raw events.
- Both cases in `GoogleAndVertexUseTheirRealSseProtocolsAndProjectMafOptionsAndUsage` verify the actual Google SDK SSE routes, transformed headers/payload, and zero response calls.
- `BedrockWireHooksTransformMarshalledRequestBeforeSigningAndObserveResponseBeforeStreamRead` uses the real AWS SDK pipeline with a local fake HTTP transport and no credentials. A gated event stream proves response observation precedes reads; it also asserts the transformed body, SigV4 coverage of the extension header, reserved-header protection, and raw event observation.
- `ProviderFactoryUsesPiMessagesHttpAndMapsStreamingResponse` exercises Pi Messages payload serialization, final headers, response observation, raw SSE JSON, and normal MEAI conversion.
- `PackedExternalWireProviderUsesHostBridgeAtRealHttpBoundaries` installs a packed external `.nupkg` in a fresh CLI process. Its wire-aware provider sends an actual HTTP request and verifies host-transformed payload/header plus callback order across all four bridge methods. `NativeExtensionProviderWireDispatcherTests.ProviderWireCallbacksPreserveOrderAndAttributeFailuresToTheirSource` verifies ordered host dispatch and source-attributed failure isolation.
- `Pr11LegacyProviderPackageLoadsAndRunsWithoutFabricatedWireCallbacks` loads an external package compiled against the PR #11 contract snapshot without shipping its Abstractions DLL, completes its provider request, and asserts all four wire observer counts stay zero.

Original local product commit: `9ccff7546842f6462b3ca9c7c50a9edf36e20ced`.
Integrated product-source commit: `b31940137201e565b15d8061055ac7b692b627e9`.
The integrated branch descends directly from post-merge `main`; the published
branch head and exact-head CI run IDs will be recorded after publication.

## Local verification

- Integrated focused provider-wire, adapter, bridge, process, and legacy compatibility batch: 67 passed, 0 failed, 0 skipped. This includes the Bedrock reserved-header/signing regression, the packed external wire-aware provider process acceptance, and the legacy PR #11 package regression.
- `dotnet format PiSharp.slnx --verify-no-changes --no-restore`: passed.
- `dotnet build PiSharp.slnx --no-restore --warnaserror`: passed with 0 warnings and 0 errors.
- `TMPDIR=/tmp dotnet test PiSharp.slnx --no-build --no-restore`: 2,434 passed, 0 failed, 0 skipped. `TMPDIR=/tmp` is the existing workaround for workspace-specific resource path assertions.
- Architecture tests: 7 passed, 0 failed, 0 skipped.
- Node terminal tests: 24 passed, 0 failed, 0 skipped.
- Python terminal parity tests: 50 passed, 0 failed, 0 skipped.
- Documentation path validation: 145 documents and 561 references verified.
- MCP conformance: no regressions; the same eight classified baseline failures remain (six elicitation checks, basic CIMD client-id behavior, and scope retry-limit behavior).
- PR #15 was merged from reviewed head `7c93a3d06a82b6c3de55dccc8b19b08647d2f851` as `2ea360f991b0e15c59203557a68a5a1e3c026450`; its exact-head push CI run `39171720211` and PR CI run `39171722992` both passed before merge.
- Provider-wire publication is in progress. Exact-head push and PR CI run IDs will be recorded after the integrated branch is pushed and its draft PR is open.

Broad extension parity remains `in_progress`.
