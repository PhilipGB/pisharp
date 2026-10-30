# RPC Protocol and Command Parity — completion receipt and published evidence

**Prior receipt:** Implementation source `62c650449f59be8cce7bedb2ecacbbec7770b015` passed 21/24 source-bound checkpoints and 231/252 required cases; E0 passed 10/10. That receipt predates the independent review follow-up below and is retained as historical evidence. The frozen contract remains 24 checkpoints and 252 mandatory cases.

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

## PR #29 review follow-up

Source commit `31963d3d098e8c194b1de68600cedb2d3af237f3` serializes RPC compaction admission. A deterministic CLI process regression overlaps two `compact` requests and covers normal completion, abort and EOF; it checks exactly one terminal response for each accepted request. The focused RPC process and writer tests pass 8/8, the fail-first run without the admission gate fails, and the cancellation-sensitive history-compaction test passed 10 consecutive reruns. Verifier source `f83470d7747f4e0157f8473900b49cc1ccdf8a09` parses ANSI-colored Vitest counts and skip identities. Source `51f22e9c00675cec34766e4087165fe2534cdab9` completes JSONL flush after a record write starts. Current source `e84a30b337bf09d5127a567cbaf5e4801b528d73` makes Q0 working-directory verification portable to independent checkouts and derives the source-bound case total from E0's checkpoint manifest. The RPC parity suite passes 42/42.

At head `944ec87590e9541112ad38a25a782e83029b62a3`, both standard CI workflows passed all five jobs. Its evidence bundle recorded Q0 15/15, 4,009/4,009 .NET tests and E0 10/10, but its publisher failed while computing a case count from a checkpoint record that has no `passed` field. That partial artifact remains diagnostic. The portable verifier and manifest-count corrections are included in source `e84a30b337bf09d5127a567cbaf5e4801b528d73` and are covered by the 42-test RPC parity suite. At exact head `e567e423b53120a45f0008394e084aa346b67d38`, push run 41001308274 and PR run 41001313795 each passed all five jobs. Publisher run [41001308373](https://github.com/PhilipGB/pisharp/actions/runs/41001308373) passed Q0 15/15 (full .NET 4,009/4,009) and E0 10/10, then uploaded artifact `rpc-parity-evidence-e567e423b53120a45f0008394e084aa346b67d38` with digest `sha256:db950a9f1b84d3ae17b9985843d7dd392028c7ea0b91eebc65c6a41e6dafe054`. Downloading it into a separate clean checkout and running `verify.py check` for all 21 source checkpoints and E0 passed 241/241 cases with no failures or skips. Final exact-head push/PR CI, evidence artifact, CI0 and PUB0 IDs are maintained in PR #29’s description. PR #29 remains open, draft and unmerged.

To independently verify a published run from a clean checkout, download the artifact into its head-specific evidence directory and run the check for every source checkpoint and E0:

```sh
HEAD=<final-head-sha>
RUN_ID=<rpc-parity-evidence-workflow-run-id>
gh run download "$RUN_ID" --name "rpc-parity-evidence-$HEAD" \
  --dir "artifacts/parity/rpc-protocol-command-parity/run-$HEAD"
for checkpoint in P0 P1 V0 V1 R1 R2 R3 R4 R5 R6 R7 R8 R9 R10 R11 R12 R13 R14 R15 D0 Q0 E0; do
  python3 tools/parity/rpc_protocol/verify.py check --checkpoint "$checkpoint" \
    --evidence-dir "artifacts/parity/rpc-protocol-command-parity/run-$HEAD/$checkpoint"
done
```

E0 checks the source pins and every raw capture hash. The 20 credential-dependent Pi tests remain unverified; the managed-package corpus remains scoped to its historical milestone. No live model endpoint is called.

The frozen [contract](../../../../tools/parity/rpc_protocol/contract.json), [source-pinned inventory](inventory.json) and [required case index](required-case-index.json) define the acceptance scope. Finalization results and exact-head workflow IDs are kept in the current PR description so they track the final documentation head.
