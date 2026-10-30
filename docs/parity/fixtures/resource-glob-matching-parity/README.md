# Resource path and glob matching parity

Pinned Pi source: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
Main/base: `480f058a95713fbc51b6904d4450da68bcda886c` (PR #29 merge).
Pi's lockfile pins `minimatch` `10.2.6`; the oracle uses its default options.

## Frozen contract and local results

- Glob/path matcher: **75/75**, deterministic pinned-Pi oracle run twice.
- Full resource-loader selection: **36/36**, including four frozen user/project theme cases.
- Explicit skill/prompt path cases: **19/19**.
- Frozen user/project settings process cases: **8/8** for skills, prompts, and extensions. Cases cover user-only, trusted project, untrusted project, and combined settings; selected order, source metadata, exclusions, and provider input are compared.
- Paired interactive `/reload`: settings arrays for skills, prompts, themes, and extensions change during the process. The post-reload skill and prompt inputs match; extension configuration moves to the new selected folder; blocked resources remain inactive. The theme array is included and `/reload` returns successfully; local theme identity and trust are independently verified in the full-loader cases.
- Matcher and acceptance verifier negative controls: **17/17**.
- Full .NET suite: **4,010/4,010**, zero failures or skips. Formatting and warnings-as-errors build pass.
- Focused current .NET matcher test: **76/76**. Base `480f058a95713fbc51b6904d4450da68bcda886c` focused path, selection, and theme baseline: **114/114**.

The audit found one actual difference: Pi normalizes local `file://` patterns before glob and exact matching; PiSharp did not. `LocalResourcePathRules.NormalizePattern` now handles local and `localhost` file URLs. The case-specific before/fixed-after observations are in [file-url-normalization.json](file-url-normalization.json), with a C# regression in `LocalResourcePathRulesTests`.

The combined trusted extension case has matching final project-before-user command order, metadata, exclusions, and resource selection. PiSharp's captured `Configure` trace starts with the user extension because it preloads user extensions before resolving project trust so their trust handlers are available. This startup trace is retained as evidence and is not treated as final selection order.

The paired terminal capture retains full raw/render evidence, including visual differences outside this path-selection contract. `assert_resource_glob_settings_reload.py` checks process errors, post-reload resource inputs, extension configuration traces, and exclusions. Theme rendering/style parity is explicitly outside this milestone.

## Evidence

The frozen matcher index is `tools/parity/resource_glob/cases.json` (SHA-256 `274a00c4e5d5da5b751b1babd7b5e81fe3e1872410bec09680f5f763abefb53c`). Full-loader selection, explicit paths, process cases, and theme cases are frozen in `selection_cases.json`, `path_cases.json`, `process_cases.json`, and `theme_cases.json`. `acceptance.py` verifies source hashes, expected observations, execution counts, selection order, and evidence manifests. `negative_controls.py` and `acceptance_negative_controls.py` exercise rejection paths.

Raw process, matcher, theme-loader, and terminal evidence is generated under `artifacts/parity/resource-glob/` and uploaded by automatic CI as `resource-glob-parity`. Its `manifest.json` binds file hashes, frozen indexes, pinned Pi, source SHA, PR head, and CI run ID. Run [41094555282](https://github.com/PhilipGB/pisharp/actions/runs/41094555282) passed at implementation head `3fff08ba043c0129373108c9ccb3f2600bd3a9ad`; the 77-file artifact manifest was independently verified against its source, PR head, base, Pi SHA, and run ID.

PR #30 is open, draft, and unmerged for independent review. The final documentation head receives its own automatic CI run after this ledger update.
