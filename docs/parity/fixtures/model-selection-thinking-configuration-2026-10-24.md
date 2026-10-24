# Model Selection, Thinking and Configuration Parity

## Scope and source revisions

PR #22 was verified merged at `eefa89454a3c1cfd196894f94744b6f393c8e721`.
That actual merge is the post-merge `origin/main` and PR #23 branch base. The
implementation is on `milestone/model-thinking-configuration-parity` at
`f9afa08a4ab8fc7509ad7cc961af76184906220f`. Pi remains pinned at
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.

This increment implements source-pinned model-reference selection, startup and
recovery defaults, scoped-model matching/editing, thinking-level precedence and
clamping, token budgets, sampling, settings precedence and persistence, CLI/TUI/
RPC behavior, and physical provider-request effects. The initial source inventory,
case reports and reproducibility data are linked below.

## Differential evidence

The independent direct-Pi behavior report contains 93 cases, all classified
`strict-match` and passing:

| Family | Cases |
| --- | ---: |
| Model reference resolution, ambiguity and CLI options | 25 |
| Scoped-model matching, suffixes, ordering and errors | 18 |
| Initial selection and recovery | 10 |
| Thinking defaults, supported levels and clamping | 10 |
| Budgets, output ceilings and sampling | 10 |
| Settings precedence and persistence | 10 |
| CLI, TUI and RPC integration | 10 |
| **Total** | **93** |

The separate physical provider-wire report passes 23/23 local HTTP cases: five
OpenAI Responses, five OpenAI Completions, five Anthropic Messages, four Google
Generative AI and four Google Vertex. Each case records the captured request and
paired PiSharp observation, pinned source references, synthetic credential
identity, configuration and comparison policy. No live provider keys are used.

The same-Pi control ran twice independently and produced the same normalized
result digest after removing only invocation provenance. The provider-wire
control also repeated with the same digest. See the [calibration record](model-selection-thinking-configuration-calibration-2026-10-24.json).
The direct Pi implementation is the oracle; no expected result is generated from
PiSharp. The paired reports include actual source SHAs for both implementations.

## Implementation and local verification

Corrections include provider-aware exact/fuzzy/ambiguous model resolution,
thinking suffix parsing, initial model precedence and fallback, minimatch-style
scope matching with a bounded regex cache, an interactive scoped-model editor,
per-model and per-scope thinking defaults, effective-level clamping, provider
budget/sampling projection, and coordinated settings mutation for enabled-model
scopes and thinking defaults. Provider adapters clamp or omit unsupported
thinking options at the physical request boundary.

The scope selector was exercised through Linux PTY coverage, including filter,
toggle, save and cancel while preserving unrelated settings. Process regressions
cover RPC model/thinking selection, extension selection events, session
replacement and resume. Concurrency and failure tests cover settings updates,
large model/pattern inputs and reload behavior.

After the report-recorder test helper edit, format verification and the
warning-as-error build passed, and the full .NET suite passed (3,776 passed,
zero failed, zero skipped; 6 m 12 s). The paired 116-case run also passed after
that edit. Node terminal (24/24), Python terminal (66/66), parity comparison
guards (27/27) and MCP conformance passed on the same product source; the helper
only adds .NET test-report provenance. MCP has no regression to its eight
classified failures across three scenarios. Documentation paths pass for 152
documents and 627 references.

## Scope limits and classified behavior

All local source and PTY evidence is Linux x64. Native Windows/macOS behavior is
not claimed. The 93-case corpus exercises model/TUI/RPC selection but does not
represent every possible interleaving during a busy agent run; existing process
and transition regressions remain the evidence for those paths. RPC and session
continuation retain a configured model identity when no provider is authenticated
to preserve PiSharp's existing unauthenticated continuation workflows; normal
interactive startup keeps an empty selection for an empty authenticated model
catalogue, matching the pinned behavior. This adaptation is explicit and is not
counted as a strict oracle case.

The project-wide parity status remains `in_progress`; this receipt covers only
the defined model-selection, thinking and configuration increment. Draft PR #23
and exact-head CI evidence will be recorded in the live PR description after the
final branch head is published.
