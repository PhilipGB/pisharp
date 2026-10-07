# llama.cpp classifier protocol — 2026-09-30

Reference Pi remains `1b347794e2a630e4359f2584f4eea388145d0ddf` after fetch. The initial native label-readout regression failed against the existing System One client with error instead of stop. The distinct LlamaClassifierClient now uses tokenize, apply-template and completion at the server root, trimming the chat endpoint's /v1 suffix. It validates all question sizes and temperature before sending, resolves newline labels with bare-label fallback, handles integer/object token entries, rejects shared tokens and caches successful token lookups by server/model/label. Failed lookups remain retryable.

Each question receives the state, overview of all questions, repeated state and its labeled task. Thinking is disabled; templates ending in an open think block are closed before evaluation. Completion reads pre-sampling next-token log probabilities with prompt caching and escalating depths 256 (or 16 per label), 4096 and 32768. Missing and underflowed labels return errors without partial answers. Stable softmax gives typed choice distributions, expected score indices and boolean probabilities. Reported usage remains absent, matching Pi's adapter. HTTP authentication, headers, cancellation, retry and deadlines use the shared classifier transport.

Focused classifier/provider tests pass 60/60 with zero skips, including a real HTTP catalog/auth/dispatch fixture for both System One and llama.cpp. Format verification and warnings-as-errors build pass with zero warnings/errors. Full suite passes 947/947 with zero skips. Exact-head publication/CI remains pending.

The current-Pi source adapter and PiSharp native client matched bool, choice and score through the same loopback fixture, including missing-label escalation and empty thinking-block closure. Floating-point results are compared within 1e-12. Captured evidence: [paired projection](llama-classifier-process-2026-09-30.json). Reproduce with `NUGET_PACKAGES=/tmp/pisharp-parity-nuget python3 tools/parity/llama-classifier-differential.py --pi /tmp/pisharp-reference-pi --output /tmp/llama-classifier.json` (Node 24 and .NET 10).

Classifier completion remains open: Cloudflare stored account/env metadata, richer request observation/model headers, Codemode model/classifier globals and nested usage aggregation. The broader parity goal remains incomplete.

## Current Pi native router classifier — 2026-10-07

Pi `503c605528f9af993c0e37ede468cf884fb0ff5b` includes the shared System One classifier update (`ce8972a0`) and the llama.cpp decision-model integration (`f6127a1b`). The provider identifies `architecture.output_modalities: ["decisions"]` from `/models`, hides decision-only models from chat selection, and exposes them as `typesafe-system-one` classifiers at the router's `/v1` endpoint. Chat models remain `llama-cpp-classify` classifiers at the router root. Sleeping decision models are listed without a `/props` probe. The same `ce8972a0` commit also adds OpenAI Decisions classifiers and image inputs; PiSharp has no `openai-decisions` implementation yet, so that broader current-Pi capability remains queued in the classifier-runtime ledger.

The reproducible paired process fixture [llama-router-classifier-differential.py](../../../tools/parity/llama-router-classifier-differential.py) matches Pi `503c6055` and PiSharp `f64ca15e` on chat/classifier projection, context windows, thinking support, `/models` and `/props` reads, bearer auth, the `/v1/systemone` request body, bool answer and usage. The captured comparison is [llama-router-classifier-2026-10-07.json](llama-router-classifier-2026-10-07.json): the decision model returns probability `0.9`, with 42 input tokens, 0 output tokens and zero cost on both sides. Reproduce with:

```sh
python3 tools/parity/llama-router-classifier-differential.py \
  --pi /path/to/current/pi \
  --output docs/parity/fixtures/llama-router-classifier-2026-10-07.json
```

The fallback classifier has also been rerun against Pi `503c6055` and PiSharp `f64ca15e`; its current response comparison is [llama-classifier-process-2026-10-07.json](llama-classifier-process-2026-10-07.json). It covers bool, choice and score through tokenization, template application, completion-probability escalation and typed answers. The cross-provider classifier-runtime ledger remains in progress; this evidence closes only current llama router classifier discovery and dispatch.
