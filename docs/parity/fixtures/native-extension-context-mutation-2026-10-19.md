# Native extension two-stage context mutation — 2026-10-19

This receipt tracks PR #19 on `milestone/native-extension-context-mutation`,
based on the verified PR #18 merge `3be359066d6d3bef815a2acefcc0a117bac12666`.
Pi remains pinned at `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. This is a
bounded native extension API increment; `extension-orchestration-api` remains
`in_progress` and no broad parity claim is made.

## Pinned contract

The pinned `packages/coding-agent/src/core/extensions/runner.ts` implements
`context` first and `context_with_system` after it, in extension registration
order. `context` receives the messages with every system-role entry hidden. If
the visible message list changes, Pi restores a current system message at the
front, combining prompt text, section edits and active tool declarations. A
callback failure is source-reported and later handlers continue. The full
context stage can replace the provider-bound list and warns if it removes an
existing leading system message. The public event types are in
`packages/coding-agent/src/core/extensions/types.ts`; system prompt and tool
state replay is in `packages/ai/src/utils/transcript.ts`.

`packages/agent/src/agent-loop.ts` invokes context transformation once before
the provider stream for each logical model request. A normal provider retry
does not repeat it; a follow-up/tool-loop request does, and overflow recovery
starts a new request after compaction. The extension runner receives the
provider-bound request from `packages/coding-agent/src/core/sdk.ts`.

## PiSharp behavior

The public builder now offers `AddContextHandler` and
`AddContextWithSystemHandler`. The first stage sees detached messages with
system-role entries hidden. PiSharp keeps `ChatOptions.Instructions` and
executable `ChatOptions.Tools` outside that event. The second stage receives
the full PiSharp message projection, mutable system instructions, and a
read-only tool-name snapshot. A null system-instructions value removes that
prompt. Current tool functions remain host-owned.

The stages run after existing legacy transforms and route-specific context
preparation, immediately before model image filtering and the physical provider
client. Changed requests update the request-local Anthropic transcript while
preserving its system and tool revision history. Prompt edits update the initial
revision; message boundaries are rebased only for structural message edits,
using retained message identity rather than serialized content. New, replaced,
or ambiguous duplicate message objects have no source lineage. The persistent
tool transcript and canonical conversation history are not mutated. The
request-local dispatcher result is reused for equivalent physical retries and
recalculated for changed context after overflow compaction. Package source
ownership participates in extension ordering, configuration rollback,
replacement and removal.

## Review correction — 2026-10-20

The original projection collapsed the Anthropic system/tool transcript whenever
the native callback changed the message list. That erased earlier tool
declaration revisions even when the callback only edited instructions or
retained message content. The dispatcher now carries each retained message's
original request index across both callback stages, including the hidden-system
stage. Structural edits rebase each existing revision to the position after the
last retained message from its original prefix; ordinary content and
instruction edits leave revision positions intact. The Anthropic adapter inserts
system/tool updates at those message boundaries and keeps updates after matching
tool results and before the next assistant turn.

The physical-boundary regression starts with tool A, removes A and adds B at a
mid-conversation revision, then edits instructions and removes a message while
editing two retained messages. It verifies the actual Anthropic request keeps
the A-to-B update between the retained users, uses the fresh prompt, and leaves
the caller's messages and executable tool options unchanged. The physical full
system-removal regression also verifies cleared prompt text is absent from the
Anthropic request while the tool revision remains projected. Dispatcher tests
cover lineage through stage-one removal/content edits, duplicate message text,
insertions, reordering and replacement objects.

Each callback receives a fresh detached message snapshot. Opaque content and
metadata that cannot be cloned safely invalidate that handler's result rather
than exposing shared mutable objects. Invalid outputs and exceptions emit
source-attributed `extension_error` events and later handlers continue. Dropping
an existing leading system message emits a nonfatal `extension_warning` while
honoring the requested output. Cancellation revokes the public callback
context, detaches non-cooperative extension work and prevents the provider
request from continuing.

## Evidence

`NativeExtensionContextMutationTests` cover stage order, hidden-system
restoration, tool replay, malformed output rollback, source diagnostics,
leading-system warning, detached mutable metadata, cancellation revocation,
route-preparation order, retry reuse, initial and tool-loop requests, prompt
transcript synchronization, and canonical history isolation. The
`NativeExtensionApiV10` package is compiled against the current abstractions,
installed and loaded, then executed through `ExtensionCatalog` and `PiAgent`.
The historical compiled API fixtures remain part of the same focused run.

The original focused extension suite passed 16/16 tests, including the current
compiled-package fixture and historical API compatibility checks. The review
correction selection passes 23/23 dispatcher, transcript, and physical
Anthropic tests; the Anthropic provider test group passes 51/51. Local
verification passes the full .NET suite (2,505/2,505, zero skips), the
warnings-as-errors build, formatting, architecture (7/7), Node (24/24), Python
(50/50), and documentation path validation (148 documents, 568 references).
MCP conformance reports no regressions; the same eight classified baseline
failures remain. Fresh exact-head push and PR Linux CI results are tracked in
the PR description. Restore passed with `NuGetAudit=false` because the shared
home NuGet HTTP cache is read-only.

## Scope and limitations

The test providers are deterministic local clients; no paid or live provider was
used. Provider image filtering is re-applied after native callbacks on direct
requests and uses the selected model policy on routed requests. Unsupported
opaque content is rejected from the extension callback snapshot and reported;
provider-specific content reconstruction beyond safe serialization remains
outside this milestone. The broader extension capability stays
`in_progress`.
