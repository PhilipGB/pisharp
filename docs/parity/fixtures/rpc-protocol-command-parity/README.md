# RPC Protocol and Command Parity — frozen baseline

**Status:** inventory and acceptance contract were frozen before production changes in commit `ac85aa2a0f59566115eb0109cc9022dc64f4d8fe`. R2 (12/12 at `44a360fc`), R3 (10/10 at `cfa79eeb`), R4 (12/12 at `413ebc6e`) and R5 (11/11 at `8e7e16f3`) are historical passes and stale after later source or parity-tool changes. R6 passed 10/10 at `1e1adde9b99d938620d0814e4de8ef4006c6c55c` and is the current source-bound process checkpoint. V1 and R1–R5 need final source-bound reruns after the implementation and harness are frozen. R7–R15, final quality and publication gates remain open.

## Verified base

- PR #28 is merged at `646db89bd396b8089b1b9a058fa96b1489f4d0cd`.
- The reviewed PR head `c019cd0111106b70a83d56e30d86932c987291e1` and implementation source `41e9b7e7ebdea70b878e409204cd8b3d177144e2` are ancestors of the merge.
- The milestone branch is `milestone/rpc-protocol-command-parity`, rooted at that merge commit.
- Pinned Pi is `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
- Push run [40747257587](https://github.com/PhilipGB/pisharp/actions/runs/40747257587) and PR run [40747270521](https://github.com/PhilipGB/pisharp/actions/runs/40747270521) each completed all five jobs successfully on the reviewed head. In both runs the managed-package corpus steps were explicitly skipped because their verifier is pinned to its historical milestone branch; green workflow status is not evidence that corpus reran.
- The active runtime remains Microsoft Agent Framework `1.21.0` on .NET `10.0.113`.

## Pinned inventory and contract

The inventory contains:

- 33 request command variants and 33 success response variants.
- 24 event families, including RPC-only `extension_error`.
- 9 RPC extension UI methods and 3 UI response variants.
- 12 session state fields, 5 JSONL record families and 7 protocol operations.
- Per-command source schemas, documented behavior, PiSharp owner/test references, prior fixture references, mismatch classifications and mandatory case IDs.

The acceptance contract is [tools/parity/rpc_protocol/contract.json](../../../../tools/parity/rpc_protocol/contract.json). It defines 24 mandatory checkpoints and derives **252 required case IDs** from its immutable case list. The [case index](required-case-index.json) is bound to contract SHA-256 `036c369054fbb50887629666f6b25ef76d8a9fbe856d00d4a79a2cf9609f0761`; the inventory SHA-256 is `b7c4983036690f3b929a205256aa33e44e143a8ef1ec3aab7f8bcfb6cf68051a`.

The inventory also names all **20 credential-gated upstream tests** reported pending by the pinned-Pi Q0 run, using exact source paths and test names. They remain unverified and are never counted as passing cases.

Validation performed on the baseline:

- `python3 tools/parity/rpc_protocol/verify.py contract --pi-checkout artifacts/parity/pi-ownership-oracle` — passed; Pi checkout SHA and tracked-source hashes match.
- Initial V1 run: `python3 tools/parity/rpc_protocol/runner.py run --checkpoint V1 ...` and `verify.py check --checkpoint V1 ...` passed 23/23 at source `736574cdf3755340ffa3b340325484a0a0f95025`; its log SHA-256 is `6a040fac6cf795a62914a89d2907c80e2243eab7e4eb00faa4519f9285c7f4f9`. Current-head verification now rejects this historical report as stale because parity tooling changed after that source. V1 must be rerun after implementation and tooling freeze.
- Initial R1 process run: `python3 tools/parity/rpc_protocol/runner.py run --checkpoint R1 --pi-checkout artifacts/parity/pi-ownership-oracle --evidence-dir artifacts/parity/rpc-protocol-command-parity/r1-3642c9bc`, followed by `verify.py check --checkpoint R1 ...`, passed 11/11 with zero failures/skips at source `3642c9bcbcaac05d6f7735015d8563f920847837`. The report SHA-256 is `ae4715854e5b519e2f18aadb53a076a6cd02c7808a84a4f98782e758eda650f2`. Current-head verification rejects it as stale after later parity-tool changes; R1 must also be rerun after freeze.
- Source-bound R2 process run: `python3 tools/parity/rpc_protocol/runner.py run --checkpoint R2 --pi-checkout artifacts/parity/pi-ownership-oracle --evidence-dir artifacts/parity/rpc-protocol-command-parity/r2-44a360fc`, followed by `verify.py check --checkpoint R2 ...` — **12/12 passed, zero failures/skips**. Every case matched two stable pinned-Pi process runs and a separately started PiSharp process. `evidence.json` SHA-256 is `03131c5120a57e43af54c587a6581f88be1270a26447548bd4b099fc2de90f46`; the warnings-as-errors build assembly SHA-256 is `dfb90cf3e8fd3e2e25d27d801baa63e8845b7dac1429bf499a50663e39de82c3`. It is stale after later source and harness changes and needs a final rerun.
- Source-bound R3 process run at `cfa79eeb08945295ce16c6e99c0c7ffe2a2868d9` passed **10/10**, with zero failures/skips and matching repeated Pi observations. Its evidence SHA-256 is `e371737e5490b68a919aa2eae892ce30fa2abd8b6ab79689348a7b48299f5da9`; the build assembly SHA-256 is `434e19a7e6df6f8b01ff33eefb91e710ca956f57119dccecd0bd65b5871d1224`. It is stale after R4 production and parity-tool changes and must be rerun at final source.
- Source-bound R4 process run at `413ebc6e8773996048c1dd3315594f4e00973857` passed **12/12**, with zero failures/skips and matching repeated Pi observations. The cancellation case distinguishes an explicit side-effect-free abort from crash recovery; steering queue snapshots, tool completion ordering, and persisted queue modes match Pi. `evidence.json` SHA-256 is `85c6739292fae752db99d3d2cf4c7e6268aa33578dff68f8e51f18c0a4b61d5a`; the warnings-as-errors build assembly SHA-256 is `49cd6864c3ceb1fa6fb69cf0f2c06ce2ffd8cde172736759a7f77cea8a701988`. Compact results are in [r4-case-results.json](r4-case-results.json); raw captures remain under the ignored evidence directory.
- R2 covers correlated, absent, null and non-string IDs; success/error response shapes; absent versus null arguments; invalid request shapes; unknown-command recovery; missing required fields; overlapping responses; long-command acceptance; and a command issued during active compaction. The corrections preserve explicit null IDs, Pi's invalid-request response fields, the missing-Bash-command behavior, and distinct missing/null session-name errors. Focused .NET regressions pass 4/4, Python verifier tests pass 16/16, and `dotnet format PiSharp.slnx --verify-no-changes --no-restore` passes.
- R1 covers LF/CRLF framing, UTF-8 split code points, Unicode separators in strings, incremental and incomplete records, a 1 MiB valid record, malformed-JSON recovery, blank/empty input, incomplete EOF and stdout/stderr purity.
- The process comparison found and corrected PiSharp's 1 MiB record limit and malformed-JSON response command. Exact error wording is normalized because Node and .NET JSON parsers report different text; command, id, success, data presence, and error presence/type remain checked.
- `python3 tools/parity/rpc_protocol/runner.py list --checkpoint R8` — returned the six frozen usage-accounting cases.

R5 source-bound evidence: [compact case results](r5-case-results.json) record 11/11 cases passing at `8e7e16f37e420645fc852cb8cef8bf0ba761778d`; this report is now stale after the R6 source changes. R6 source-bound evidence: [compact case results](r6-case-results.json) record 10/10 passing at `1e1adde9b99d938620d0814e4de8ef4006c6c55c`, zero failures or skips, and matching two stable pinned-Pi processes with an independent PiSharp process per case. The evidence report SHA-256 is `092f05afe6fede2ad8d561883f65717d09d47e104a7bf15a855855404266eb1c`; independent verification also passed 10/10. Focused RPC projector tests pass 9/9 and the warnings-as-errors CLI build passes. Raw captures and the full manifest remain under the ignored evidence directory. The frozen runners fail closed for unregistered cases. V1 and R1–R5 need final source-bound reruns; continue with R7–R15, then complete quality and publication gates.

## Known differences and limitations to revalidate

- Generated system-message lifecycle projection now preserves Pi's `preamble`, `tools`, `rules`, `docs` and `cwd` sections; R3 compared the idle prompt payload against pinned Pi. R3 evidence still needs a final-source rerun.
- A previous Pi revision accepted truthy non-string `new_session.parentSession` values while PiSharp rejected them. Probe the exact pin before changing validation.
- A prior `get_session_stats` comparison reported context-token estimates of 1422 for Pi and 817 for PiSharp. Recheck equivalent persisted system state and classify any remaining difference with raw evidence.
- Explicit scoped-model `:thinking`, broader provider streaming/cancellation, extension UI lifecycle, and active session replacement require new exact-pin process cases.
- The native branch-summary usage correction from PR #28 remains a required exactly-once regression for native generation, reload, imported summaries and mixed sessions.
- The pinned Pi checkout has an unrelated untracked test file. Inventory generation reads only explicitly named tracked source files and checks their hashes.

## Next work

The inventory and contract are committed before production changes. R6 passes 10/10 at source `1e1adde9`; R1–R5 and V1 have historical passes but their reports are stale. Continue with R7–R15, capture repeated pinned-Pi and PiSharp process observations, then rerun stale checkpoints and complete quality/source-bound evidence and exact-head CI. Final publication is PR #29, open, draft and unmerged.
