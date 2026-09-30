# Typed model catalogs — 2026-09-30

The preceding Codemode source `a7fc6c442036cae6434d4ea3bef727779dc213bf` passes exact-head CI [36691148993](https://github.com/PhilipGB/pisharp/actions/runs/36691148993): format, zero-warning/error build, 969/969 tests, zero skips.

Fail-first configured catalog coverage reproduced an empty image catalog; a separate assertion exposed missing classifier name metadata. Image entries were parsed as chat descriptors and could silently collapse under the same provider/id during guest catalog projection. A typed ImageModelDescriptor and provider image collection now preserve chat/image/classifier identity independently. The loader validates image modalities, output, model type and endpoint ownership; chat selection excludes configured images. Guest catalogs whitelist metadata and retain names, input limits, output modalities and Pi pricing names without headers/credentials.

The embedded image catalog copies all 57 OpenRouter image models from Pi's generated IMAGE_MODELS at `1b347794e2a630e4359f2584f4eea388145d0ddf`. Upstream refreshed to `3e9451238337071b74ba5cdd53f1ab7cf4100ae8`: intervening MCP guidance/link and renderer-example commits leave the model catalog unchanged. Builtin coverage verifies all image entries, Flux preprocessing limits, unavailable credentials, authenticated provider-scoped availability and secret-free metadata. Configured coverage uses the same ID for all three model types and verifies separation and available images; malformed modality/type/endpoint cases are rejected.

Focused Codemode/classifier/provider checks pass 91/91. Format verification and warnings-as-errors build pass with zero warnings/errors. Full solution tests pass 974/974 with zero skips. Exact-head publication/CI is pending for this follow-up.

This is catalog capability, not image generation. Next material orchestration target: batch termination must occur only when every finalized root call requests it, matching current Pi in sequential and concurrent execution. Optional sandbox settings, larger Bash structured output, extension/provider/auth work and the fresh full audit remain; overall parity is incomplete.
