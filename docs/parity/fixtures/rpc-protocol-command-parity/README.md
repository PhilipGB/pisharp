# RPC Protocol and Command Parity — corrected source-bound completion receipt

**Status:** Corrected implementation source `62c650449f59be8cce7bedb2ecacbbec7770b015` passes 21/24 source-bound checkpoints and 231/252 required cases, with zero failures or skips. The frozen contract remains 24 checkpoints and 252 mandatory cases. E0 independently passes 10/10. Exact-head CI0 and PUB0 receipts and current workflow IDs are recorded in PR [#29](https://github.com/PhilipGB/pisharp/pull/29), which remains open, draft and unmerged.

## Source pins and scope

- Base: verified PR #28 merge `646db89bd396b8089b1b9a058fa96b1489f4d0cd`.
- Pinned Pi: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
- Implementation source: `62c650449f59be8cce7bedb2ecacbbec7770b015`.
- Inventory/contract commit: `ac85aa2a0f59566115eb0109cc9022dc64f4d8fe`; the inventory covers 33 commands, 24 event families, 9 extension UI methods, 12 session fields, 5 protocol record families and 7 operations.
- Frozen contract: 24 checkpoints and 252 cases: 230 strict, 22 equivalent, 0 intentional safety differences and 0 unresolved.
- Runtime: Microsoft Agent Framework 1.21.0 and .NET SDK 10.0.113.

## Source-bound results

P0 6/6; P1 5/5; V0 6/6; V1 23/23; R1 11/11; R2 12/12; R3 10/10; R4 12/12; R5 11/11; R6 10/10; R7 13/13; R8 6/6; R9 12/12; R10 9/9; R11 8/8; R12 15/15; R13 8/8; R14 17/17; R15 13/13; D0 9/9; Q0 15/15. The [completion evidence manifest](completion-evidence-62c65044.json) records the 231 case results, source pins, checkpoint hashes and quality results. The [remediation report](remediation-evidence-62c65044.json) records fail-first reproductions, fixes, transition behavior, regressions and authoritative Q0 evidence. Raw captures remain under ignored `artifacts/parity/rpc-protocol-command-parity/run-62c65044/`.

Q0 passed the full .NET suite (4,005/4,005), RPC tests (185/185), runtime/session tests (602/602), compaction/tools/extensions tests (1,355/1,355), JSON event regressions (15/15), Linux process/PTY tests (691/691), Python suites (66, 32, 27, 62 and 27), Node tests (24/24), warnings-as-errors build, format, MCP conformance and documentation validation (157 documents, 701 references). `NuGetAudit=false` was used because the managed workspace's NuGet vulnerability cache is read-only; the test command and suite were unchanged. The pinned-Pi run passed 582 tests and skipped exactly 20 credential-gated identities, which remain unverified. The managed-package parity corpus remains scoped to its historical milestone branch and was not rerun here. No live model endpoint was called.

## Independent-review corrections

- **Bash ownership:** each accepted operation retains its originating run, session identity, execution environment, session file and persistence callback. Pinned Pi process comparisons show a blocked Bash returns a cancelled result before the response for `switch_session`, `new_session`, `fork` and `clone`; its result persists only in the original session. EOF cancels and persists without writing a terminal response after stdout closes.
- **Q0 counts:** `summary.passed` remains a Boolean. Numeric full-suite counts are emitted in `summary.testCounts`, checked against the captured raw `dotnet test PiSharp.slnx` output and its hash, and consumed by CI0/PUB0 only from that schema. The remediation evidence includes fail-first evidence and negative controls.

The frozen [contract](../../../../tools/parity/rpc_protocol/contract.json), [source-pinned inventory](inventory.json) and [required case index](required-case-index.json) define the acceptance scope. Finalization results and exact-head workflow IDs are kept in the current PR description so they track the final documentation head.
