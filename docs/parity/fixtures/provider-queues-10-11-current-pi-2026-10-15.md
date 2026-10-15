# Provider queue slices 10–11 — current Pi pin, 2026-10-15

Reference Pi remains pinned at
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Queue 10 is integrated in
`b2c67611`; its Azure Responses test correction is `5ef5c30e`. Queue 11 is
integrated in `1bd72a80`. Current product code is at `1cdb57f7`.

Azure Foundry Chat Completions has four deterministic HTTP tests for its pinned
catalog model, deployment URL and `v1` path, bearer authentication, reasoning
and tool continuation, local endpoint errors, and endpoint validation. Azure
OpenAI Responses has real-SDK loopback coverage for API version, deployment
mapping, Azure key authentication, session cache key, continuation, errors, and
credential isolation. The continuation assertion checks the JSON-encoded tool
output sent on the second request. Its static catalog contains 43 models.

Mistral Conversations has nine compatibility tests for native reasoning effort,
stream updates, tool continuation, images, cached usage, finish-reason retry
before visible text, no replay after text, retry headers, terminal HTTP error
text, response-header timeout behavior, and caller cancellation after headers.

The integrated focused provider/settings suite passes 190/190; the full .NET
suite passes 2,057/2,057 with zero skips. The warnings-as-errors solution build
and format verification pass. Exact-head Linux CI run
[38789357300](https://github.com/PhilipGB/pisharp/actions/runs/38789357300)
passed on product source `1cdb57f7`.

These are bounded local transport and MAF checks. Azure's broader Pi request,
image, usage, retry, and error comparison remains open. Mistral's direct
Pi-stream comparison remains open; Pi emits tool-call start/argument deltas
while this adapter publishes the completed call. Both provider requirements
remain in progress.
