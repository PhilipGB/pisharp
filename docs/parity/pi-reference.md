# Pi parity reference

[`tools/parity/pi-reference.txt`](../../tools/parity/pi-reference.txt) is the single
current Pi commit used by live parity verification. Its full SHA was selected from
committed execution-ledger and startup/trust evidence. Historical fixture pins
and the MCP baseline's original oracle SHA identify the evidence that produced
those artifacts; they do not select a different live checkout.

Obtain a new checkout at the configured commit:

```sh
python3 tools/parity/pi_reference.py checkout --destination /tmp/pisharp-reference-pi
cd /tmp/pisharp-reference-pi
npm ci --ignore-scripts --no-audit --no-fund
npm run hydrate:model-data
npm run build:offline
```

Hydration uses Pi's generator to fetch provider model values and validates them
against the model declarations at the pinned source. These public catalog values
can change independently of the source SHA; record that reproducibility limitation
and the generated manifest with verification. The offline build checks the data
before compiling. Pi's committed npm lockfile fixes workspace dependencies.
Python 3, Git and Node.js 22.19+ are required; PiSharp CI
uses Node 24. Dependency-install warnings from third-party packages are retained
in verification output.

Verify an existing checkout:

```sh
python3 tools/parity/pi_reference.py verify --pi /tmp/pisharp-reference-pi
```

The verifier rejects a different HEAD, tracked working/index changes, an invalid
reference, and a path below the repository root. Temporary untracked oracle tests
and fixture directories are allowed. Verification succeeds silently when called
inside drivers; the explicit command prints the verified SHA.

Every Python process differential, the terminal runner, and the standalone Node
probes verify their checkout before loading Pi or creating comparison fixtures.
Node probes call the same Python verifier through `pi-reference.mjs`; they also
require Python 3. The terminal runner loads Pi through its source resolver to
avoid stale workspace `dist` outputs. Drivers that consume compiled Pi packages
must use an offline build made at the verified reference. Normal CI runs local
unit/contract tests and PiSharp MCP conformance without a live Pi checkout.
Terminal fixtures keep PiSharp session locks and imports under the owned fixture
agent directory, including fixtures that explicitly resume a session.

The official MCP expectation file retains its historical `42705224` oracle
provenance. MCP source has since changed; conformance at the new configured
`3daa2028` reference was freshly rerun and retains the same 261 passing checks
and eight failures. The conformance runner compares PiSharp with that reviewed
expectation file and classifies every known failure; it does not acquire a floating
Pi checkout. This bounded conformance result does not close the new MCP behavioral
deltas or the final scope review of retained failures.

## Intentional reference updates

The architecture refactor and composition review were merged in PiSharp
`22eeb2a53dbaa94e41f78a40a997e3b3156bb3ed` on 2026-10-13. The frozen reference
`ee409a85f6ad92bf8c6ab8ed735c6cd24f2e749e` supplied the completed post-merge baseline.
The [current refresh audit](fixtures/current-pi-refresh-2026-10-13/README.md)
classifies every one of the 50 intervening commits before intentionally advancing
the configured reference to `3daa20280f6a7c7cbe0935680c9a38fdc3863005`.
The new source checkout is clean and rebuilt; one delta is matched and 17 need
implementation or stronger current-source proof. Advancing the pin selects the
behavioral target; it does not claim that those open requirements are satisfied.

Parity work must select a new commit from an explicit source audit, not merely upstream HEAD
or a convenient green run. Update `pi-reference.txt` in a reviewed parity change,
obtain/rebuild a fresh checkout, and verify its actual SHA. Run standard .NET,
Node/Python tests, affected process and calibrated terminal differentials, and
MCP conformance. Record source deltas, exact pins, commands, outcomes and remaining
gaps in the parity execution ledger and fixtures. Update historical baselines only
after separately reviewing changed expectations and known failures.

Do not change a configured SHA to rescue a failing test. A mismatch is a setup
error; an unexplained failure at the evidenced source requires a baseline decision.
