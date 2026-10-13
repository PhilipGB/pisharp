# Current Pi refresh — 2026-10-13

Starting merged main: `22eeb2a53dbaa94e41f78a40a997e3b3156bb3ed`.
Branch: `parity/current-pi-completion`. PR #1 is merged; exact-main [Linux CI 38427397999](https://github.com/PhilipGB/pisharp/actions/runs/38427397999) passes both jobs. The architecture refactor and Program composition review are complete. Final parity work still requires a bounded architecture regression review.

The starting reference was `ee409a85f6ad92bf8c6ab8ed735c6cd24f2e749e`; all baseline comparisons below used that SHA. The completely audited new configured reference is `3daa20280f6a7c7cbe0935680c9a38fdc3863005`. [The source audit](upstream-audit.json) classifies every one of the 50 commits between them: 19 `OUT_OF_SCOPE`, 13 `NO_BEHAVIOR_CHANGE`, 13 `NEEDS_WORK`, 5 `MATCHED`. Each in-scope entry records its observable change, exact changed upstream paths, affected existing ledger ID, current PiSharp source, implementation plan and differential plan. `NEEDS_WORK` includes missing proof where existing code may already match; it does not presume all remaining items need product edits.

## Baseline evidence

Fresh local verification on unchanged product source at the merged-main SHA:

- .NET SDK 10.0.112 / runtime 10.0.12; Ubuntu 24.04 x64; Node 24.21.0; Python 3.12.3.
- Restore, format verification and warnings-as-errors build pass (zero warnings/errors).
- .NET: 1,390 passed, zero failures, zero skips.
- Node terminal contracts: 24 passed, zero failures/skips.
- Python terminal harness contracts: 47 passed, zero failures.
- Documentation paths: 131 documents / 457 references pass.
- Official MCP: no baseline regression; eight previously classified checks remain to reconcile with the new reference.
- All 17 existing process differential drivers were executed at the configured reference. Sixteen passed initially; the classifier probe failed on obsolete input/output assumptions. After correcting boolean criteria and reading ordered output values by line, the classifier billing/authority slice passes. Raw Pi output items remain recorded; Codemode formatting remains open.
- All 33 terminal fixtures were run with same-Pi calibration and one explicitly selected matrix case per fixture. Initial outcomes were 30/33 calibrations and 24/33 pairs passing; every failure is retained. Repeats separately validate the model-picker and trust calibrations. The Markdown fixture was repaired to supply its intended reply; its fresh calibration passes and its pair still differs in 200 fields. Broader matrices remain required before full completion.

Generated logs/captures live under `artifacts/parity/current-pi-baseline-2026-10-13/` (ignored). [The completed baseline manifest](baseline.json) records all 66 original reports, seven repetitions/corrections and source/log hashes. No product behavior has changed in this refresh.

## Classification inventory

| Commit | Classification | Finding |
| --- | --- | --- |
| `eb2fce43` | `OUT_OF_SCOPE` | Changes only independent durable runtime/source/tests and associated metadata. No normal coding-agent source, imported behavior or .NET equivalent surface changes. |
| `148622bc` | `NEEDS_WORK` | MCP login help describes a whole-sign-in timeout; PiSharp help lists the flag without its scope or default. |
| `2ee5c1e5` | `OUT_OF_SCOPE` | Changes only independent durable runtime/source/tests and associated metadata. No normal coding-agent source, imported behavior or .NET equivalent surface changes. |
| `7472cda0` | `OUT_OF_SCOPE` | Removes the separately excluded experimental server/client/protocol/plugin stack. Reviewed distribution changes remove those exports/dependencies; normal src/cli.ts and RPC entry remain. No equivalent C# capability is being removed. |
| `0104fef1` | `OUT_OF_SCOPE` | Replaces the independent Chord host/facet/service runtime and its tests. Root browser/entry-graph tooling follows that package; normal coding-agent runtime remains separate. |
| `066ebe4c` | `NO_BEHAVIOR_CHANGE` | Changelog and Chord package metadata record the already classified runtime replacement; no normal CLI source changes. |
| `6db4070f` | `NO_BEHAVIOR_CHANGE` | Documentation and Chord tests remove obsolete architecture descriptions; PI_RADIUS_GATEWAY still serves /bug, and removed relay wording describes the deleted experimental stack. |
| `6a51a39d` | `MATCHED` | PiSharp already expands the home directory in glob exclusions and exact +/- overrides. Four new capability tests verify glob exclusion, exact reinclusion and final exact exclusion for extension, prompt, theme and skill paths; the current Pi package-manager suite includes its tilde regression and passes 128/128. No product edit was required. |
| `f4cca2dc` | `MATCHED` | MCP connections and resource-change notifications no longer enumerate resources/templates. Capability status reports resources without counts; shared resource tools list only on demand. The fail-first startup log now shows no listings until requested, and the notification scenario retains tool refresh plus fresh resource/template results. Seven current-Pi real-stdio scenarios match connection/on-demand request counts; CLI status capability checks pass. |
| `67996ab7` | `NEEDS_WORK` | structuredContent accepts arrays, null, strings, numbers and booleans; absent-schema Codemode type is unknown. PiSharp schema requires object and does not render structured-only results as model text. |
| `11f99114` | `NEEDS_WORK` | Unhandled wheel input scrolls the transcript below a focused overlay. PiSharp routes mouse input through concrete dialogs and selection lists; no broad current overlay differential proves all paths. |
| `014873d7` | `NEEDS_WORK` | Codemode emits an initial progress update and shows Elapsed while running and recorded Took on completion/reload. PiSharp lacks the timer contract. |
| `da0eadf0` | `NEEDS_WORK` | Calls cancelled or timed out during shared OAuth recovery never dispatch after refresh; surviving requests still proceed. PiSharp forwards tokens through SDK handlers but lacks this race proof. |
| `b0ac3968` | `OUT_OF_SCOPE` | Private Android Pi Mobile APK workflow and apps/p2 pre-commit checks; neither is a .NET coding-agent surface. |
| `39badde8` | `NO_BEHAVIOR_CHANGE` | Developer pi-test.sh now starts the normal source CLI; product CLI source/behavior is unchanged. Verification commands must use src/cli.ts. |
| `441f5767` | `NEEDS_WORK` | Text print mode preserves partial stdout but warns on stderr and exits 1 for a final length stop. PiSharp print execution does not handle output-token-limit finish reason. |
| `e6fb2581` | `NEEDS_WORK` | Windows delete-pending EPERM during auth-lock acquisition is retried briefly; persistent permission errors remain bounded. PiSharp uses different file/refresh locks and has no equivalent Windows evidence. |
| `19cb534c` | `OUT_OF_SCOPE` | Changes only independent chord runtime/source/tests and associated metadata. No normal coding-agent source, imported behavior or .NET equivalent surface changes. |
| `d4d53612` | `NEEDS_WORK` | Known strict grammar capacity HTTP 400 rejections downgrade only prefer constraints once, retain require and original schemas/hooks, and never replay a started stream. PiSharp does not yet encode constrainedSampling strict policies. |
| `abfe6292` | `OUT_OF_SCOPE` | Changes only independent durable runtime/source/tests and associated metadata. No normal coding-agent source, imported behavior or .NET equivalent surface changes. |
| `aa499c24` | `OUT_OF_SCOPE` | Changes only independent env runtime/source/tests and associated metadata. No normal coding-agent source, imported behavior or .NET equivalent surface changes. |
| `b6fda579` | `OUT_OF_SCOPE` | Changes only independent env runtime/source/tests and associated metadata. No normal coding-agent source, imported behavior or .NET equivalent surface changes. |
| `55df12bf` | `OUT_OF_SCOPE` | Changes only independent durable, env runtime/source/tests and associated metadata. No normal coding-agent source, imported behavior or .NET equivalent surface changes. |
| `b6d505cd` | `MATCHED` | Misplaced // @options: lines now fail before worker launch with the exact line-number error, including blank/indented/CRLF/repeated directives. The source parser preserves valid optionsx/string/standalone-CR controls. Eight real current-Pi source comparisons match; 20 focused source tests pass (four fail-first cases plus controls). |
| `3e477eab` | `MATCHED` | PI_CLIPBOARD_OSC52=1 now emits the exact UTF-8 OSC52 payload after a successful native copy on macOS, Windows and Linux. Other values do not force emission. Three cases failed before the fix; the clipboard/retry focused run passes 30/30 and twelve real-Pi comparisons match native-write/terminal ordering with injected platform writers. Native OS clipboard availability remains separately unverified. |
| `7bd01fb9` | `MATCHED` | The transient retry classifier now accepts provider returned an error while retaining quota/context exclusions and retry budgets. The new wording failed first, then focused tests passed. Twelve current-Pi agent/session comparisons match request contexts/counts, ordered recovery/exhaustion events, disabled/zero budgets, billing exclusions and backoff cancellation for both capacity and gateway messages. |
| `0d79b74d` | `NO_BEHAVIOR_CHANGE` | Changelog-only inventory of changes individually classified in this range; no runtime change. |
| `06a46693` | `NO_BEHAVIOR_CHANGE` | Release version/changelog and internal dependency-version bump to 1.2.0; reviewed lockfile changes contain workspace version changes, not a new normal coding-agent behavior. Product branding/version output remains PiSharp-owned. |
| `d5479491` | `NO_BEHAVIOR_CHANGE` | Only adds empty Unreleased changelog headings. |
| `7f022043` | `OUT_OF_SCOPE` | Changes only independent chord runtime/source/tests and associated metadata. No normal coding-agent source, imported behavior or .NET equivalent surface changes. |
| `235e8886` | `NO_BEHAVIOR_CHANGE` | Removes a mistakenly committed Chord node_modules symlink and broadens its ignore rule; no runtime contract changes. |
| `cd6b2a55` | `OUT_OF_SCOPE` | Changes only independent chord runtime/source/tests and associated metadata. No normal coding-agent source, imported behavior or .NET equivalent surface changes. |
| `0a8cfff4` | `NO_BEHAVIOR_CHANGE` | Nix packaging drops the deleted Chord dependency and esbuild smoke check; C#/.NET product packaging does not use Nix upstream workspace tarballs. |
| `de022efd` | `OUT_OF_SCOPE` | Changes only independent env runtime/source/tests and associated metadata. No normal coding-agent source, imported behavior or .NET equivalent surface changes. |
| `d6370b72` | `OUT_OF_SCOPE` | Changes only independent env runtime/source/tests and associated metadata. No normal coding-agent source, imported behavior or .NET equivalent surface changes. |
| `7d75a0e3` | `OUT_OF_SCOPE` | Changes only independent chord runtime/source/tests and associated metadata. No normal coding-agent source, imported behavior or .NET equivalent surface changes. |
| `e6220e13` | `NO_BEHAVIOR_CHANGE` | Developer test launcher preserves custom CA certificates for network tests; normal product requests are unchanged. |
| `90f17f59` | `OUT_OF_SCOPE` | Changes only independent chord runtime/source/tests and associated metadata. No normal coding-agent source, imported behavior or .NET equivalent surface changes. |
| `018c87dd` | `OUT_OF_SCOPE` | Changes only independent chord runtime/source/tests and associated metadata. No normal coding-agent source, imported behavior or .NET equivalent surface changes. |
| `a4d040d3` | `OUT_OF_SCOPE` | Changes only independent chord runtime/source/tests and associated metadata. No normal coding-agent source, imported behavior or .NET equivalent surface changes. |
| `db6497c3` | `OUT_OF_SCOPE` | Changes only independent chord runtime/source/tests and associated metadata. No normal coding-agent source, imported behavior or .NET equivalent surface changes. |
| `a3a2ee8e` | `NO_BEHAVIOR_CHANGE` | GitHub contributor allowlist only. |
| `f13c00fb` | `NO_BEHAVIOR_CHANGE` | Pins existing internal workspace versions exactly and updates release/check scripts and lockfiles; no source-level inference/CLI contract change at the audited checkout. |
| `746d995b` | `NO_BEHAVIOR_CHANGE` | Adds platform setup documentation for an external NVIDIA RTX llama.cpp installation; no Pi provider/router code changes. |
| `4b994d5e` | `NEEDS_WORK` | Malformed Unicode escapes such as \usepackage become literal text while valid/incomplete Unicode escapes retain streaming semantics. PiSharp provider tool argument parsing lacks evidenced repair. |
| `cea25743` | `NEEDS_WORK` | sessionShareDestination selects auto/gist/radius; gist bypasses Radius, forced radius reports missing auth without gist fallback; selector persists the choice. PiSharp lacks this setting and broader Radius share parity. |
| `34a8bb38` | `NEEDS_WORK` | Anthropic error tool results replace each image with the exact omission note; successful tool images remain intact. PiSharp forwards tool-result images through the SDK. |
| `6f2f164a` | `NEEDS_WORK` | Default-tool resumes restore transcript loadouts with +/- modifiers; explicit tools and exclusions override restoration. PiSharp restores saved loadouts unconditionally, so explicit/modifier behavior needs reconciliation. |
| `b9ab2a62` | `NO_BEHAVIOR_CHANGE` | Reorders prerequisites in that external NVIDIA setup guide; no product code change. |
| `3daa2028` | `NEEDS_WORK` | Abort/timeout/close settle promptly without waiting on worker exit, retain partial output and account two shared worker slots until actual exit. PiSharp kills a separate Node process then awaits exit without a bound, has no global admission limit and discards cancelled output. |

The 194 ledger statuses are unchanged: 7 verified, 147 in progress, 40 not started. No final source audit or full parity completion is claimed. `test.pdf` and unrelated files were not opened or changed.

Tilde-pattern contract evidence: [four new resource-type vectors and upstream regression results](home-pattern-evidence.json). Production code already matched; the focused matcher suite passes 69/69 and current Pi package-manager passes 128/128.

Current-cutoff source verification at `3daa2028`: dependencies installed, model data hydrated and offline build passed with a clean tracked checkout. The 17 applicable Vitest suites pass 327 tests with three explicit Bun process-lifetime skips (Bun unavailable); the TUI source suite uses `node:test` and passes 66/66 separately. Running the TUI file through Vitest first produced a runner/setup failure, corrected by using its native runner. The repeat of package-manager alone passes 128/128; those cases are already included in the 327 count. These are real upstream checks, distinct from comparisons against the still-configured old reference.

Current-cutoff official Pi MCP conformance also has no regression: 26 scenarios, 261 passing checks and the same eight failures. Thus none of those historical limitations is assumed from the old source; all were rerun at the audited cutoff. Their final scope/implementation resolution remains required. All five optional virtual-model process replays at the old configured reference pass (context compaction, session transitions, state identity, minimal thinking and max thinking).

The terminal harness shutdown regression first times out with an unread real emulator snapshot larger than its pipe. Draining teardown output in bounded chunks fixes it; all 48 Python terminal tests pass. Comparison expectations and captured state are unchanged. The Markdown fixture now explicitly configures the fake-provider Markdown reply its checkpoint already required. Retained-log trust replays preserve native extension side effects under the owned artifact directory; both semantic assertions pass. Resource precedence, extension reload, legacy skills/reload, read/Bash/indirect/no-reader skill prompts and trusted settings reload assertions also pass.

The pin advancement is intentional: `ee409a85f6ad92bf8c6ab8ed735c6cd24f2e749e` → `3daa20280f6a7c7cbe0935680c9a38fdc3863005`, with all 50 intervening commits classified before advancement. The new checkout is clean, dependencies are installed, model data is hydrated and its offline build passes. Its current MCP source is freshly checked; historical conformance provenance is preserved. The pin advancement does not resolve the remaining 17 in-scope delta items or the open capability ledger.
