# Incremental current-Pi source audit — 2026-10-09

Pi `origin/main` is now `42a3497d03ad17e308a2299fa824727894f2c0ec`. The prior incremental audit covered 195 commits from the full audit pin `955cc6665ee3986c6a033db52200779310d10dfd` through `f1b2e77f5b13b2a199b1052cb79c235451afe7d7`. This refresh classifies three later commits, bringing the range to 198 commits: 68 `OUT_OF_SCOPE`, 36 `NO_BEHAVIOR_CHANGE`, 87 `NEEDS_WORK` and 7 `MATCHED`.

## Current-head delta

| Pi commit | Classification | Changed behavior / evidence | PiSharp ledger mapping |
|---|---|---|---|
| `5a10492690efa146791bb17f3b82cf9cc49f9e13` `feat: add local issue triage tool in scripts/issues` | `OUT_OF_SCOPE` | Adds a repository-maintainer issue sync, triage agent and local review UI under top-level `scripts/issues/`. It is not part of the normal coding-agent executable or a user-facing Pi capability. | None; repository-maintainer tooling is outside the product surface. |
| `eba849739511223c51a62bbd7e3f1c00f99fb1d0` `feat(durable): nested tool calls, structured output, callers; storage errors end the Session` | `OUT_OF_SCOPE` | The commit changes the separate `@earendil-works/pi-durable` runtime, its tests/examples, issue-triage tooling, and one experimental vacation example that consumes the durable package. The normal coding-agent package does not depend on or export that alternate runtime: its published exports are the package root, client, and experimental plugin, and `dist/experimental` is excluded. The normal CLI entry graph does not reach the vacation example or durable package. No normal CLI, llama.cpp, classifier, provider, settings, resource, session, RPC, or TUI path changed. | None; the ledger excludes experimental alternate runtimes unreachable from the normal executable. |
| `42a3497d03ad17e308a2299fa824727894f2c0ec` `feat(durable): read returns images, optional Photon image processor` | `OUT_OF_SCOPE` | All product/runtime changes are under `packages/durable`; the remaining changed file is the entry-graph checker. The normal coding-agent CLI does not import or export this package, and no normal CLI, llama.cpp, classifier, provider, settings, resources, session, RPC or TUI path changed. The source paths and entry graph do not expose the alternate durable image/read tools to the regular coding-agent product. | None; the durable runtime remains outside the in-scope CLI product surface. |

## Previous incremental delta

| Pi commit | Classification | Changed behavior / evidence | PiSharp ledger mapping |
|---|---|---|---|
| `f1b2e77f5b13b2a199b1052cb79c235451afe7d7` `fix(coding-agent): load symlinked AGENTS.md in nested git worktrees` | `NEEDS_WORK` | `resource-loader.ts` detects a nested linked worktree and skips the main worktree's duplicate context file only when the nested worktree has the same logical file. This now handles a worktree `AGENTS.md` symlink that resolves to the main repo file and avoids double-applying shared instructions. Pi adds two regression cases in `resource-loader.test.ts`. The PiSharp context loader initially reproduced the defect: both fail-first symlink cases included the same instructions twice. Commit `78bc0ffc9523cc28aa1f8bc05777ff4812b3f1e4` implements the linked-worktree scope rule and passes nine focused context tests, including same-name override, inheritance, filename, bare-layout and sibling-worktree guards. Its exact-head Linux CI run [37902383991](https://github.com/PhilipGB/pisharp/actions/runs/37902383991) passes 1,201/1,201 tests with zero skips. | `feature-resources-instructions-and-trust` |

The `f1b2e77f` changed paths are `packages/coding-agent/CHANGELOG.md`, `packages/coding-agent/src/core/resource-loader.ts`, and `packages/coding-agent/test/resource-loader.test.ts`. The three later commits are outside the normal product implementation graph. Current Pi `42a3497d` resource/skills tests pass 85/85 and llama extension tests pass 15/15. The llama process reports remain pinned at `eba84973` / `bab03219`; eleven paired terminal flows remain pinned at `42a3497d` / `368c3bcf`. See [current Pi llama verification](llama-terminal-current-pi-2026-10-10.md).

## Audit state

This is an incremental audit of the three commits after `f1b2e77f`, not the final complete source audit required by the parity goal. The previous 195 classifications remain in prior audit records; together this record covers the 198-commit range from `955cc6665ee3986c6a033db52200779310d10dfd`. Resource trust/settings acceptance, the remaining capability ledger, official MCP conformance and the final audit remain open.
