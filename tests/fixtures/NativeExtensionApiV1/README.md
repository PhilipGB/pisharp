# PR #7 API v1 compatibility fixture

This fixture compiles against the original public `IPiSharpExtension.cs` from
the PR #7 merge (`5de6aac534ea1548f28f63999c81addcc8189f48`). The snapshot
preserves the original `PiSharp.Extensions.Abstractions` assembly identity and
references the same shared Microsoft.Extensions.AI 10.10.0 contract.

The extension is packed as an ordinary `.nupkg` with `apiVersion: 1`. The
acceptance test installs it through the CLI process, then loads it in a fresh
runtime and executes its original command, tool, and before-agent-start
registration callbacks against the additive host.
