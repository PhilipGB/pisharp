# Scoped model glob parity

Pinned Pi is `earendil-works/pi@b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
In `packages/coding-agent/src/core/model-resolver.ts`, Pi tests each model's
`provider/id` and bare `id` with `minimatch(..., { nocase: true })`, preserves
scope order, resolves the selected model reference, and applies a compatible
thinking suffix.

`ModelScopeResolver` now follows that lookup order. The `ModelScopeGlob`
implementation supports the exercised minimatch behavior, including brace
alternatives and ranges, nested braces, extglobs, negation, escaped brackets,
case-insensitive matching, and hidden/model ID cases. Regex compilation uses a
bounded 256-entry cache. Scope resolution also tests order-preserving
deduplication, duplicate bare IDs, cross-provider matching, fuzzy aliases,
valid and invalid suffixes, and the no-match diagnostic.

The 18 direct source-pinned scope cases are in
[the paired behavior report](fixtures/model-selection-thinking-configuration-oracle-2026-10-24.json)
under `scoped-models`; their C# regressions are in
`tests/PiSharp.Tests/Cli/Startup/ModelScopeGlobTests.cs`. The report includes the
actual pinned oracle result beside each PiSharp result. This is scoped-model
evidence; it does not establish parity for unrelated minimatch consumers.
