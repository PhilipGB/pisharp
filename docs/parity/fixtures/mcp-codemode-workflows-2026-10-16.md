# MCP + Codemode workflow milestone — 2026-10-16

- Branch: `milestone/mcp-codemode-workflows`
- Main base: `28f223c0c3b52ac9cd79a76c35a26c982999eee2`
- Product source: `cf2929dff3836be931a1c27ebfd60f4b65a75c6d`
- Configured Pi source: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`
- Behavioral source for CIMD: `earendil-works/pi@1499466d8581035a4dec6c869725b8712aea4bbe`

This is a bounded user-workflow checkpoint. It does not claim global Pi parity.

## Acceptance

- `McpCodemodeWorkflowTests` composes the real `PiAgent` and
  `ConversationRun`, a stdio MCP fixture, and the Codemode worker. A parent
  response invokes one MCP tool, Codemode reads a specification and writes a
  source file through nested tools, the parent incorporates the result, and a
  follow-up uses the same conversation. Assertions cover the single MCP call,
  nested read/write events, provider request/history continuity, file contents,
  and exit of the MCP fixture process.
- `InteractiveMcpProcessTests` runs the CLI in a Linux PTY. It observes a
  connected MCP server, disables it, reloads, sees the disabled state, completes
  a deterministic provider follow-up, and confirms clean CLI and MCP process
  exit. `/mcp help` and the sanitized `/mcp details <server>` workflow are
  covered separately; details omit secrets, arguments and sensitive URL data.
- MCP OAuth fixtures cover DCR as the default and CIMD opt-in, the shared
  `pi.dev` metadata document when authorization responses support `iss`, and
  the server-hash document plus callback path otherwise. A configured
  `localhost` callback retains its host and port. Browser callback tests reject
  mismatched origins and paths; the manual callback fixture rejects a mismatched
  host before accepting the valid redirect. Runtime uses the official MCP SDK
  for PKCE, discovery, issuer/state validation, token exchange and refresh. The
  TUI PTY covers URL presentation, cancel/copy, and manual redirect entry.
- `CodemodeSandbox` accepts classifier context arguments up to 4 MiB each and
  8 MiB total per run. A deterministic 48 KiB image-context case reaches the
  classifier fixture. Existing Codemode tests cover nested tools/model calls,
  usage persistence, context authority, cancellation, worker termination and
  admission release.

## Local verification

- `dotnet restore PiSharp.slnx`: passed.
- `dotnet format PiSharp.slnx --verify-no-changes --no-restore`: passed.
- `dotnet build PiSharp.slnx --no-restore --warnaserror -m:1`: passed, zero
  warnings and errors.
- Full .NET suite: **2,124 passed, zero failed, zero skipped**. Architecture
  checks: **6/6**.
- Combined `Codemode|Mcp` filter: **198/198 passed, zero skipped**. OAuth,
  lifecycle, and interactive MCP command filter: **62/62 passed, zero
  skipped**.
- Terminal Node tests: **24/24**. Terminal Python tests: **50/50**.
- Documentation path validation: **135 documents, 524 references**.
- MCP conformance: **261 passed** and the same **8 classified failures** as
  the established baseline (six elicitation cases, `auth/basic-cimd`, and
  `auth/scope-retry-limit`); no new regressions. Conformance build passed as
  part of the solution build.
- The npm install used `/tmp/pisharp-npm-cache` because the default cache under
  `/home/philip/.npm` is read-only in this environment. .NET process tests used
  `TMPDIR=/tmp` for path-display expectations.

## Bounded live smoke

Using normal saved provider credentials, one Codex conversation and one local
llama.cpp conversation each invoked a deterministic stdio MCP echo tool exactly
once, returned the expected result, and shut down the fixture process. The
Codex credential-presence check made no provider request; the smoke used the
normal auth loader and wrote no private prompt, session, or credential data into
the repository.

## Scope limits

MCP and Codemode remain `in_progress` as broad global capabilities. The
conformance baseline still has its eight classified failures, and broader MCP
resource/output semantics, cross-provider differences, and Codemode sandbox
semantics remain in the global inventory. This milestone closes the named MCP
OAuth client-metadata-document contract based on its acceptance evidence; it
does not close the broader MCP or Codemode contracts. The 194 global IDs now
stand at **13 verified, 148 in progress, and 33 not started**.

Exact-head Linux CI on branch head `bee038cc8d41e8ea964e6c197e088bef2b4aed08`
passed both required jobs and all **2,124/2,124** .NET tests
([run 38901880600](https://github.com/PhilipGB/pisharp/actions/runs/38901880600)).
