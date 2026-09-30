# Virtual-model physical image limits — 2026-09-30

Reference remains Pi `1b347794e2a630e4359f2584f4eea388145d0ddf`. AgentSession `_limitsModel()` uses the latest successful physical response under a virtual selection; tool-result image normalization uses that model's resize profile.

Two fail-first provider-loop cases read a real 300x20 PNG after a physical model requests the read tool. Both produced 300x20 instead of the physical 150x10 limit. Successful routed responses now update tool image limits, excluding direct summary calls; branch/resume restoration applies physical image limits through the existing physical-context resolver. Removing a physical branch restores selected-model image limits. Actual physical provider clients and reasoning options remain selected by ModelRuntimeController, and provider image filtering remains a non-mutating per-request projection.

Tests cover continuation routes accepting images and text-only routes filtering them. Both preserve the resized canonical image and logical selection. Focused virtual-model/read-image/active-model RPC tests pass 51/51 with zero skips, including the corrected nullable-cost assertion from the preceding checkpoint.

Prior checkpoint `b022de907bafa179ca3e9769290d7921e01bd6f4` failed exact-head CI `36649052423` at build: the new split-summary cost assertion inferred nonnullable decimal expected values against nullable recorded costs. The preceding local incremental validation did not compile that final edit. Expected cost values now explicitly use decimal?; do not claim b022de9 exact-head green. Final format verification, warnings-as-errors build (zero warnings/errors) and full suite pass 922/922 with zero skips. The first full run failed two existing timing cases; all four focused timing cases and the unchanged full rerun passed. Replacement exact-head CI remains pending.

Remaining audit: extension-injected image normalization breadth, physical compatibility options, physical TUI/status presentation, then bounded virtual-model completion review and classifiers/Codemode. The broader parity goal remains active.
