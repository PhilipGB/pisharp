# RPC `export_html` during a blocked provider run

Reference: Pi `0.87.1`, source `earendil-works/pi@2b0a123de98318c2ff8069661721ce0c3794c34e`.

Pi's RPC `export_html` handler has no busy-run restriction. `AgentSession.exportToHtml()` passes the current session manager to `exportSessionToHtml()`, which reads the entries synchronously before writing the requested HTML path. A paired Pi/PiSharp CLI probe found Pi accepts the command during blocked provider streaming and creates the file, while PiSharp previously returned `Wait until the active prompt settles.`.

PiSharp now snapshots the canonical conversation tree before starting its asynchronous HTML writer. A paired process probe was rerun against both Pi and PiSharp: both returned the correlated success response with the requested path and created the file while the provider was blocked; both processes exited cleanly. `RpcExportHtmlProcessTests.ExportHtmlDuringBlockedRunWritesTheAcceptedSessionSnapshot` separately exercises the PiSharp CLI with a loopback provider held after a partial assistant delta. It verifies the export returns before the provider is released, contains prior saved history and the accepted prompt, excludes the partial assistant delta, and leaves `get_state.isStreaming` true. Focused local test: 1/1 passed.

This establishes active-command availability, response correlation, stable persisted-history projection and preservation of the in-flight run. PiSharp's standalone private HTML presentation differs from Pi's viewer. Broader output rendering, session-file prerequisites, invalid paths, overwrite errors, cancellation and shutdown behavior remain open. The paired Pi process probe compared active availability and file creation, not byte-for-byte HTML contents.
