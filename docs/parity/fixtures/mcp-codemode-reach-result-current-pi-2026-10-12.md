# MCP and Codemode reach/result contract

The MCP contract was introduced by current Pi commit `24382bedd03c61c0994628907a017b4491f8fa76`, “refactor(codemode,coding-agent): decouple MCP from codemode.” A fresh fetch advanced Pi `origin/main` to `77332f7a34f957f495e23a1b02c70f95db85ad22`; the intervening commits do not change this MCP contract. PiSharp was based on product commit `b1cc9013fc6ac66a5947fd67347726449cc57778`; this evidence covers the working-tree implementation that follows it.

Pi declares an optional `reachesTools(input)` function as either `"all"` or the input's words. Codemode marks discovery globals (`searchTools`, `describeNamespace`, `describeTool`, and `ALL_TOOLS`) as reaching all tools; otherwise its source words identify candidate tools and namespaces. MCP waits only for the servers named by that reach. A direct MCP call waits for its owning server, while resource-list/read functions wait for all servers. MCP activates `codemode` and `tool_search` only when a registered tool of that name declares reach. Each MCP namespace aliases the configured server name.

For nested calls, Pi removes `_meta` and exposes the complete MCP result as the tool's structured value: `content`, optional `structuredContent`, and `isError`. A schema-bearing result remains available to Codemode even when `isError` is true. Each MCP output schema describes that envelope, embeds the server's structured-content schema, and relocates only local schema references under `structuredContent`.

The PiSharp process-backed checks use two real Python MCP stdio servers and the QuickJS/WASM Codemode sandbox. They verify that a script naming one server waits for its late connection without waiting for an unrelated slower server, while discovery waits for all. The same Codemode integration checks the `docs` and `mcp__docs` namespace names, scoped search, server instructions, returned success text and structured content, a returned error's `isError`/content, and that hidden tools remain unavailable. Separate assertions check `_meta` removal, false and true error flags, the complete envelope schema, and reference relocation.

The upstream Pi behavior tests use Pi's real MCP/Codemode extension code with its in-memory protocol transport for nested calls and results. Pi's MCP command tests separately exercise a real stdio child process. These test seams are not byte-for-byte transport comparisons; the paired assertions compare the observable reach, lookup, schema, and result contracts.

Verification at Pi `77332f7a34f957f495e23a1b02c70f95db85ad22`:

- `packages/coding-agent/test/suite/agent-session-mcp.test.ts`, `test/mcp-extension.test.ts`, `test/mcp-command.test.ts`, `test/suite/agent-session-codemode.test.ts`, and `test/suite/agent-session-prompt.test.ts`: 129/129 combined. This includes the new 100,000-output-item Codemode case and custom-message `before_agent_start` cases.
- `packages/codemode/test/declarations.test.ts`: 17/17.
- PiSharp `McpRuntimeTests`, `McpRuntimeLifecycleTests`, `McpCallToolResultSchemaTests`, `CodemodeTests`, and `ProjectRuntimeContextTests`: 36/36 after the result-error regression was added.

PiSharp process assertions are in [McpRuntimeTests.cs](../../../tests/PiSharp.Tests/Runtime/Mcp/McpRuntimeTests.cs), [McpRuntimeLifecycleTests.cs](../../../tests/PiSharp.Tests/Runtime/Mcp/McpRuntimeLifecycleTests.cs), and [McpCallToolResultSchemaTests.cs](../../../tests/PiSharp.Tests/Runtime/Mcp/McpCallToolResultSchemaTests.cs). Current Pi assertions are in `packages/coding-agent/test/suite/agent-session-mcp.test.ts`, `packages/coding-agent/test/mcp-extension.test.ts`, `packages/coding-agent/test/mcp-command.test.ts`, and `packages/codemode/test/declarations.test.ts`.
