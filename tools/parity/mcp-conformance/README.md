# MCP client conformance

Run the pinned official MCP client scenarios against PiSharp's production `McpRuntime`:

```sh
dotnet restore PiSharp.slnx
dotnet build tools/McpConformance/PiSharp.McpConformance.csproj --no-restore --warnaserror
node tools/parity/mcp-conformance/run.mjs
```

The runner executes the same 26 scenarios and protocol versions as current Pi: `2025-03-26`, `2025-06-18`, and `2025-11-25`. It starts each server scenario from `@modelcontextprotocol/conformance@0.2.0-alpha.11`, drives the real PiSharp runtime and tool registrations, and uses a loopback-only simulated browser for OAuth redirects. Scenarios run sequentially. The runner and child processes cap common compute thread pools at two; the Python parity scripts use the same two-CPU limit.

`baseline.json` is derived from current Pi's committed MCP baseline, with its `pi-client` result renamed to `pisharp-client`. The oracle was fetched and rerun at Pi `4270522473e677ddba7b093caa0064a3df51d459` on 2026-10-12; all 26 scenarios matched that baseline (261 checks passed, eight known checks failed). CI fails on any additional failure, any missing passing check, or an unclassified known failure.

The eight retained failures match current Pi:

- Six elicitation checks: neither client currently advertises or handles MCP client elicitation.
- `cimd-client-id-used`: both clients use dynamic client registration instead of a Client ID Metadata Document.
- `pisharp-client` for `auth/scope-retry-limit`: the fixture never grants the requested scope; the suite confirms the client limits attempts to three, then expects the client process to report failure.

Each is classified in `failure-classifications.json`. The current Pi oracle results and baseline are checked separately from PiSharp results; update the baseline only after reviewing an upstream package change or a newly passing check. Keep known-failure classifications current when a failure is fixed or changes.

The suite exposed and now verifies server-selected MCP protocol version fallback, SSE reconnect/resume headers and retry timing, OAuth scope selection and step-up, and persistence of tool schemas through the production tool surface.
