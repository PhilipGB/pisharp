# TODO — retired

This file is a historical compatibility tombstone for frozen parity verifiers. It is not the current status document.

- Current status, project goal, and milestone history: [docs/continuation.md](docs/continuation.md)
- Open architecture risks: [docs/architecture.md](docs/architecture.md#open-architecture-risks)
- Capability status and evidence: [feature matrix](docs/parity/feature-matrix.md) and [execution ledger](docs/parity/execution-ledger.json)

## Historical frozen-checkpoint references

- Complete Managed Package Lifecycle and Resource Distribution Parity: [receipt](docs/parity/fixtures/managed-package-lifecycle-parity.md), merged by PR #27 at `12064420179f4119bdaf37479c7cba0f5ca40717`.
- Complete RPC Protocol and Command Parity: merged by PR #29 at `480f058a95713fbc51b6904d4450da68bcda886c`; base `646db89bd396b8089b1b9a058fa96b1489f4d0cd`, pinned Pi `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`, implementation source `0af1b7f8091ac2805fc7ca505333552ae8876c21`, 24/24 checkpoints and 252/252 cases. Limitations: 20 credential-dependent Pi cases remain unverified, and the managed-package corpus remains scoped to its historical milestone.
- Compaction implementation source `41e9b7e7ebdea70b878e409204cd8b3d177144e2` remains recorded in the continuation and feature matrix.

The former full status log is available from Git history.
