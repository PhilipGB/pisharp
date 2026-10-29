# RPC Protocol and Command Parity — source-bound completion receipt

**Status:** 21/24 source-bound checkpoints and 231/252 required cases pass at implementation source `1481f15fb7f01217be9e41f256df94975532013b`. The 21 receipts have zero failures or skips. Final evidence and publication receipts are maintained on PR #29, which is open, draft and unmerged.

## Source pins and scope

- Base: verified PR #28 merge `646db89bd396b8089b1b9a058fa96b1489f4d0cd`.
- Pinned Pi: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
- Inventory/contract commit: `ac85aa2a0f59566115eb0109cc9022dc64f4d8fe`; the inventory covers 33 commands, 24 event families, 9 extension UI methods, 12 session fields, 5 protocol record families and 7 operations.
- Frozen contract: 24 checkpoints and 252 cases. Comparison modes: 230 strict, 22 equivalent, 0 intentional safety differences and 0 unresolved.
- Runtime: Microsoft Agent Framework 1.21.0 and .NET SDK 10.0.113.

## Current source-bound results

P0 6/6; P1 5/5; V0 6/6; V1 23/23; R1 11/11; R2 12/12; R3 10/10; R4 12/12; R5 11/11; R6 10/10; R7 13/13; R8 6/6; R9 12/12; R10 9/9; R11 8/8; R12 15/15; R13 8/8; R14 17/17; R15 13/13; D0 9/9; Q0 15/15. Every receipt is source-pinned and independently verified. [The compact evidence manifest](completion-evidence-1481f15f.json) lists all 231 cases, checkpoint hashes and quality results. Raw captures remain under ignored `artifacts/parity/rpc-protocol-command-parity/run-1481f15f/`.

Q0 passed the full .NET suite (3,998/3,998), RPC tests (178/178), runtime/session tests (595/595), compaction/tools/extensions tests (1,348/1,348), JSON event regressions (15/15), Linux process/PTY tests (684/684), Python suites (66, 23, 27, 62 and 27), Node tests (24/24), warnings-as-errors build, format, MCP conformance and documentation validation. The RPC clone process test now creates a deterministic skill fixture instead of relying on the developer machine's home skills. The pinned-Pi run passed 582 tests and skipped exactly 20 credential-gated test identities; these remain unverified. The managed-package parity corpus remains scoped to its historical milestone branch and was not rerun here. No live model endpoint was called.

## Finalization record

Final acceptance passes 24/24 checkpoints and 252/252 mandatory cases: the 21 source-bound checkpoints cover 231 cases, E0 passes 10/10, CI0 passes 5/5 and PUB0 passes 6/6. Both automatic exact-head workflows pass all five jobs. Finalization receipts are recorded in the ignored run directory and summarized in [PR #29](https://github.com/PhilipGB/pisharp/pull/29), which remains open, draft and unmerged. The Q0 `passed` marker is Boolean; publication receipts use its measured `total` of 3,998 for the test count.

The frozen [contract](../../../../tools/parity/rpc_protocol/contract.json), [source-pinned inventory](inventory.json) and [required case index](required-case-index.json) define acceptance scope.
