# Resource path and glob matching parity

Pinned Pi source: `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
Main/base: `480f058a95713fbc51b6904d4450da68bcda886c` (PR #29 merge).
Pi's lockfile pins `minimatch` `10.2.6`; the oracle uses its default options.

## Frozen contract and local results

- Glob/path matcher: **75/75**, deterministic pinned-Pi oracle run twice.
- Malformed file URL pattern errors: **5/5**; both glob and exact override paths reject invalid percent/UTF-8 escapes, encoded `/`, and unsupported hosts with Pi-matching messages.
- Full resource-loader selection: **36/36**, including four frozen user/project theme cases.
- Explicit skill/prompt path cases: **19/19**.
- Frozen user/project settings process cases: **8/8** for skills, prompts, and extensions. Cases cover user-only, trusted project, untrusted project, and combined settings; selected order, source metadata, exclusions, and provider input are compared.
- Paired interactive `/reload`: settings arrays for skills, prompts, themes, and extensions change during the process. The post-reload skill and prompt inputs match; extension configuration moves to the new selected folder; blocked resources remain inactive. The theme array is included and `/reload` returns successfully; local theme identity and trust are independently verified in the full-loader cases.
- Total frozen matcher, error, selection, path, process, and reload outcomes: **144/144**, with zero failed or skipped cases.
- Matcher and acceptance verifier negative controls: **19/19**, including removal of mandatory reports followed by manifest regeneration.
- Full .NET suite: **4,015/4,015**, zero failures or skips. Formatting and warnings-as-errors build pass locally.
- Focused path and package file URL tests: **101/101**. Base `480f058a95713fbc51b6904d4450da68bcda886c` focused path, selection, and theme baseline: **114/114**.

The audit found one actual difference: Pi normalizes local `file://` patterns before glob and exact matching; PiSharp did not. `LocalResourcePathRules.NormalizePattern` now handles local and `localhost` file URLs and throws for malformed URL patterns instead of treating them as literal globs. The case-specific before/fixed-after observations are in [file-url-normalization.json](file-url-normalization.json), with C# regressions in `LocalResourcePathRulesTests`.

The combined trusted extension case has matching final project-before-user command order, metadata, exclusions, and resource selection. PiSharp's captured `Configure` trace starts with the user extension because it preloads user extensions before resolving project trust so their trust handlers are available. This startup trace is retained as evidence and is not treated as final selection order.

The paired terminal capture retains full raw/render evidence, including visual differences outside this path-selection contract. `assert_resource_glob_settings_reload.py` checks process errors, post-reload resource inputs, extension configuration traces, and exclusions. Theme rendering/style parity is explicitly outside this milestone.

## Evidence

The frozen matcher index is `tools/parity/resource_glob/cases.json` (SHA-256 `274a00c4e5d5da5b751b1babd7b5e81fe3e1872410bec09680f5f763abefb53c`). Malformed file URL errors are frozen in `file_url_error_cases.json` (SHA-256 `e886f081820a24fa25f4f4493010bc8ab5b06e9d0f3948e93c4b8d75f0992cd5`). Full-loader selection, explicit paths, process cases, and theme cases are frozen in `selection_cases.json`, `path_cases.json`, `process_cases.json`, and `theme_cases.json`. `acceptance.py` requires the exact 14-report inventory, validates source and artifact hashes, and replays every report against its frozen index, including process and reload assertions. `negative_controls.py` and `acceptance_negative_controls.py` exercise rejection paths.

Raw process, matcher, theme-loader, and terminal evidence is generated under `artifacts/parity/resource-glob/` and uploaded by automatic CI as `resource-glob-parity`. Its `manifest.json` binds file hashes, six frozen indexes, the exact required report inventory, pinned Pi, source SHA, PR head, and CI run ID. E0 revalidates all 144 mandatory results in those reports; a regenerated manifest with removed reports fails.

PR #30 is open, draft, and unmerged for independent review. Fresh exact-head CI and E0 receipts for the review corrections will be published in the PR after push.
