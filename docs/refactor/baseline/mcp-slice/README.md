# MCP connection/publication slice

H10 separates SDK connection/discovery/auth lifetime, MCP tool publication/result
projection and shared resource publication. Runtime coordinates server selection,
startup and live publication. Existing per-server/resource lock ordering, protocol
negotiation, authentication, reconnect and no-uncertain-tool-replay behavior remain.
No SDK/package/project/assembly/public API or test/invocation movement changed.

Pre-change existing checks: 53. Failed child startup diagnostic characterization:
four runtime tests passed before edits. Focused MCP: 83 tests. Full .NET passed
1,364 rows, zero skips; all original 1,353 groups/1,356 rows preserved, with eight
added characterization groups. Strict formatting and warnings-as-errors build pass.

Official conformance ran all three protocol modes. The preserved parser compares
all 269 original MCP identities and three suite outcomes (conformance, credential,
Radius MCP differential): 272/272 accepted. The eight classified baseline failures
are unchanged; no expectations or frozen outcome sets changed. The conformance
project built with warnings-as-errors before the run.

`comparison-scope.json` records the preserved parser hash and exact bounded scope.
Temporary parser-context links provided prior Node/Python logs only because the
full capture reader requires them; they were excluded from the MCP comparison and
are not claimed as new runs. Full .NET comparison uses its actual new TRX.

Verification commands/source hashes and raw TRX/conformance/differential logs and
reports remain. Whitespace-bearing raw files are losslessly gzip-compressed with
receipts. Permanent capability documentation is `docs/capabilities/mcp.md`.
