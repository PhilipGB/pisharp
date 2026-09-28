# Anthropic empty-thinking-signature history replay

Reference: current Pi `earendil-works/pi@11894012dd461232eb075bc890538b6866860a10`; the compatibility change originated at `c1449660c83fd00a7c71d5f7e1bd29fafd400550`.

Pi marks OpenCode and OpenCode Go `qwen3.8-flash` with `compat.allowEmptySignature`. In Pi's Anthropic Messages adapter, unsigned assistant thinking is projected as ordinary text by default. A model with `allowEmptySignature: true` keeps it as a thinking block with `signature: ""`. Signed thinking stays a thinking block, and empty unsigned thinking is dropped.

PiSharp applies that model-scoped rule in `AnthropicThinkingSignatureClient` immediately before the Anthropic SDK request. It projects both streaming and non-streaming history without mutating canonical Microsoft Agent Framework messages. The loopback fixture exercises the real Anthropic .NET SDK and verifies its serialized `/v1/messages` body for enabled/disabled compatibility, signed content, and whitespace-signature normalization.

The first loopback run failed before the fix: with compatibility disabled, the real SDK still serialized unsigned reasoning as a `thinking` block with an empty signature, where Pi sends text. After the provider-boundary projection, `AnthropicThinkingSignatureCompatibilityTests` passes 4/4. Current Pi `anthropic-empty-thinking-signature-compat.test.ts` and `qwen-token-plan-models.test.ts` pass 98/98. Exact-head Linux CI for PiSharp `66561e1db1942624f7310bd50f68195c80ee66bf` passed restore, format, warnings-as-errors build (0 warnings/errors), and 768/768 tests (0 skipped) in [run 36454550166](https://github.com/PhilipGB/pisharp/actions/runs/36454550166).

This verifies history serialization at the provider boundary. It does not verify a live OpenCode account or all compatibility flags supported by Pi.
