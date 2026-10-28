# RPC Protocol and Command Parity — frozen baseline

**Status:** inventory and acceptance contract prepared before production changes. The RPC behavior cases have not run; the milestone remains active.

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
- `PYTHONPATH=tools/parity/rpc_protocol python3 -m unittest -v test_contract` — 8/8 passed.
- `python3 tools/parity/rpc_protocol/runner.py list --checkpoint R8` — returned the six frozen usage-accounting cases.

No production files were changed to create this baseline. The runner fails closed while process adapters are absent, so unexecuted cases cannot be reported as passing.

## Known differences and limitations to revalidate

- `get_messages` generated-system-message section layout differs in the existing paired fixture: Pi exposes `preamble`, `tools`, `rules`, `docs` and `cwd` sections; PiSharp currently exposes the generated MAF instructions under `sections.preamble`.
- A previous Pi revision accepted truthy non-string `new_session.parentSession` values while PiSharp rejected them. Probe the exact pin before changing validation.
- A prior `get_session_stats` comparison reported context-token estimates of 1422 for Pi and 817 for PiSharp. Recheck equivalent persisted system state and classify any remaining difference with raw evidence.
- Explicit scoped-model `:thinking`, broader provider streaming/cancellation, extension UI lifecycle, and active session replacement require new exact-pin process cases.
- The native branch-summary usage correction from PR #28 remains a required exactly-once regression for native generation, reload, imported summaries and mixed sessions.
- The pinned Pi checkout has an unrelated untracked test file. Inventory generation reads only explicitly named tracked source files and checks their hashes.

## Next work

Commit this inventory and contract before production changes. Then complete V1's positive and negative controls, build the actual process runners, capture repeated pinned-Pi baselines, and proceed through R1–R15. Final publication is PR #29, open, draft and unmerged.
