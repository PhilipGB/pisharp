# MCP OAuth refresh coordination fixture — 2026-09-29

Reference: `earendil-works/pi@9a100c7cc4707f013438b13969c6671bc0a33c4c`, including upstream fix `1d74741e1777f7cc147d65bdfbcd08bb752972cd`. PiSharp follows the SDK-based OAuth provider design already in the runtime and coordinates refresh at its injected `HttpClient` boundary.

## Behavior

- Token grants for an MCP resource URL take a private per-server OS lock. The lock remains held while the official SDK parses and stores a successful token response.
- After acquiring the lock, a waiting client rereads the shared token file and compares it with the tokens that client observed before its refresh request. If another process has already renewed the grant, the handler supplies that cached token response to the SDK; it does not spend the old rotating refresh token again.
- Failed token requests release the lock. Successful grants release it after the SDK cache write completes. MCP connection shutdown waits for active grants and pending token-cache writes before disposing the HTTP client.
- Once a successful grant is pending persistence, the credential write ignores cancellation from the original MCP request so it cannot discard a newly rotated refresh token.
- Refresh HTTP requests have a 15-second bound; waiting for another process's lock is bounded at 25 seconds.

## Evidence

- `McpOAuthTests.ConcurrentOAuthRefreshesUseTheRotatedTokenFromTheFirstProcess` exercises two provider/cache instances, checks that only one token endpoint request occurs, and checks that the second receives the rotated access and refresh tokens.
- `McpOAuthTests.MCPRefreshLockSerializesSeparateProcesses` starts two independent `dotnet test` processes against one token file and a local rotating-token endpoint. Both children load `access-1`/`refresh-1`; the server receives one refresh request; both exit with `access-2`/`refresh-2` persisted.
- The provider-level test waits on `McpOAuthRefreshHandler.WaitForSettledAsync` and verifies it remains pending until the SDK-style token cache write completes.
- Focused OAuth tests pass 8/8. Format and warnings-as-errors build pass with 0 warnings/errors. Full local suite passes 855/855, 0 skipped. An earlier parallel full run failed the existing OpenAI Completions stream-idle timeout (854/855); that test passed 1/1 alone, and the later full run passed. Exact-head CI is pending.

## Limits

MCP file locks use native POSIX `flock` on macOS and the .NET file-lock path elsewhere. Only Linux has been exercised here; the macOS native path still needs runtime validation. MCP lazy connection, `/mcp` management, reconnect recovery and extension-registered servers remain open in the larger lifecycle slice.
