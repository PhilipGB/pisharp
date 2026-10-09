# Incremental current-Pi source audit — 2026-10-09

Pi `origin/main` was refreshed to `f1b2e77f5b13b2a199b1052cb79c235451afe7d7`. The previous incremental audit covered 194 commits from the full audit pin `955cc6665ee3986c6a033db52200779310d10dfd` through `6fb2e7815167e6b19006fc526d1a5d0f5f998787`. This refresh adds one commit, bringing the classified range to 195 commits: 65 `OUT_OF_SCOPE`, 36 `NO_BEHAVIOR_CHANGE`, 87 `NEEDS_WORK` and 7 `MATCHED`.

## Current-head delta

| Pi commit | Classification | Changed behavior / evidence | PiSharp ledger mapping |
|---|---|---|---|
| `f1b2e77f5b13b2a199b1052cb79c235451afe7d7` `fix(coding-agent): load symlinked AGENTS.md in nested git worktrees` | `NEEDS_WORK` | `resource-loader.ts` detects a nested linked worktree and skips the main worktree's duplicate context file only when the nested worktree has the same logical file. This now handles a worktree `AGENTS.md` symlink that resolves to the main repo file and avoids double-applying shared instructions. Pi adds two regression cases in `resource-loader.test.ts`. The PiSharp context loader initially reproduced the defect: both fail-first symlink cases included the same instructions twice. Commit `78bc0ffc9523cc28aa1f8bc05777ff4812b3f1e4` implements the linked-worktree scope rule and passes nine focused context tests, including same-name override, inheritance, filename, bare-layout and sibling-worktree guards. Its exact-head Linux CI run [37902383991](https://github.com/PhilipGB/pisharp/actions/runs/37902383991) passes 1,201/1,201 tests with zero skips. | `feature-resources-instructions-and-trust` |

The changed Pi paths are `packages/coding-agent/CHANGELOG.md`, `packages/coding-agent/src/core/resource-loader.ts`, and `packages/coding-agent/test/resource-loader.test.ts`. The focused Pi `resource-loader.test.ts` suite passes 53/53 at `f1b2e77f`. The prior llama.cpp terminal/process reports remain valid as a source-path audit: this Pi delta changes no llama implementation or tests, and the corresponding focused llama/classifier suites pass 41/41 at the refreshed head. See [current llama evidence](llama-terminal-current-pi-2026-10-09.md) and its [manifest](llama-terminal-current-pi-2026-10-09/evidence.json).

## Audit state

This is an incremental audit of the new source delta, not the final complete source audit required by the parity goal. The previous 194 classifications remain in [the 2026-10-08 audit](upstream-delta-audit-2026-10-08.md); together this record covers the full 195-commit range from `955cc6665ee3986c6a033db52200779310d10dfd`. The resource trust/settings acceptance, remaining capability ledger, official MCP conformance and final audit remain open.
