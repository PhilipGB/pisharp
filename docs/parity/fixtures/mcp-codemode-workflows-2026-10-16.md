# MCP + Codemode workflow milestone — 2026-10-16

- Branch: `milestone/mcp-codemode-workflows`
- Main base: `28f223c0c3b52ac9cd79a76c35a26c982999eee2`
- Final product source: `834927bfc984cdc003149b3f15223ecc13f24a2b`
- Initial product source: `9ca5deb4640438893c3bc26a4bf16df3dd7eada7`
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
- Bare `/mcp` now opens a selectable server list and a per-server details and
  actions menu. The manager shows status, tool count, transport/source, scope,
  exposure and safe errors; it offers tool and exposure submenus, sign-in/out,
  reconnect and enable/disable. Escape returns from actions to the server list
  and closes the manager to the prompt. A Linux PTY selects and disables a
  configured stdio server, backs out twice, completes a deterministic provider
  follow-up and confirms clean CLI and fixture exit. The existing
  `InteractiveMcpProcessTests` separately covers typed status/disable/reload and
  the changed status. `/mcp help` and sanitized details omit secrets, arguments
  and sensitive URL data.
- MCP OAuth fixtures cover DCR as the default and CIMD opt-in, the shared
  `pi.dev` metadata document when authorization responses support `iss`, and
  the server-hash document plus callback path otherwise. A configured
  `localhost` callback retains its host and port. The listener binds to the
  configured loopback callback authority; origin/path, `Host` header and remote
  address checks remain as defence in depth. Tests cover configured
  `localhost` and `127.0.0.1` success, alternate-authority rejection, wrong-path
  rejection and a mismatched manual redirect before accepting the valid URL.
  Runtime uses the official MCP SDK for PKCE, discovery, issuer/state
  validation, token exchange and refresh. The TUI PTY covers URL presentation,
  cancel/copy, and manual redirect entry.
- `CodemodeSandbox` accepts classifier context arguments up to 4 MiB each and
  8 MiB total per run. A deterministic 48 KiB image-context case reaches the
  classifier fixture. Direct bounds tests cover 64 KiB text output, 256 output
  items, 4 MiB per nested argument and 8 MiB aggregate. Parent-run cancellation
  tests stop a blocked MCP call and a Codemode worker, settle the conversation,
  and complete a later prompt without replay. A two-server fixture proves a
  failed MCP server does not prevent a healthy server from serving tools.

## Local verification

- `dotnet restore PiSharp.slnx`: passed.
- `dotnet format PiSharp.slnx --verify-no-changes --no-restore`: passed.
- `dotnet build PiSharp.slnx --no-restore --warnaserror -m:1`: passed, zero
  warnings and errors.
- Full .NET suite: **2,130 passed, zero failed, zero skipped**. Architecture
  checks: **6/6**.
- Focused workflow acceptance filter: **23/23 passed, zero skipped**, including
  parent cancellation/recovery, failed-server isolation, sandbox limits,
  interactive command behavior and the selectable MCP manager PTY.
- Focused MCP OAuth and management tests: **59/59**; OAuth metadata tests:
  **24/24**; TUI OAuth tests: **7/7**.
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

The initial product-source CI on `9ca5deb4640438893c3bc26a4bf16df3dd7eada7`
passed before the OAuth listener correction ([run 38911196308](https://github.com/PhilipGB/pisharp/actions/runs/38911196308)).
Final product source `834927bfc984cdc003149b3f15223ecc13f24a2b` passed both
required exact-head Linux CI jobs and all **2,130/2,130** .NET tests
([run 38917328736](https://github.com/PhilipGB/pisharp/actions/runs/38917328736)).
