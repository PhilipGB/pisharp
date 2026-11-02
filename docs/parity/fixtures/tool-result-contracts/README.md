# Tool-result contract parity evidence

Capability: `tool-result-contracts` in `docs/parity/execution-ledger.json`.

- Pinned Pi: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
- Implementation source: `aee6cdf64aaaab56a690f91aa3a57895816b629a`.
- Acceptance manifest: `acceptance-manifest.json`.
- Executable matrix: `tests/PiSharp.Tests/Runtime/Tools/ToolResultContractMatrixTests.cs`.
- Per-case evidence: `evidence/<case>.json`.
- Pinned-Pi differential: `differential.json` produced by `tools/parity/tool_result_contracts/differential.py`.
- Verifier: `python3 tools/parity/tool_result_contracts/verify.py`.

The matrix covers 34/34 required cases. The differential covers 28 cases against pinned Pi:
27 match. E05 is a documented intentional safety difference because pinned Pi treats
`outputSchema` as declarative while PiSharp validates it and returns a visible error.
No differential remains unresolved.

Cases without an independent Pi differential are recorded explicitly in the manifest:
nested parent association (R05), canonical session reload (P02), Pi JSONL field
round-trip (P03, owned by the merged session milestone) and returned-versus-thrown
state after reload (P04, which pinned Pi cannot represent). P05 is covered by P01's
provider-continuation boundary plus its own no-replay assertion.

The committed evidence was produced at the implementation source above. Later
publication commits may touch only this evidence directory, the acceptance tooling,
the execution-ledger entry and continuation notes; the verifier enforces that policy.
