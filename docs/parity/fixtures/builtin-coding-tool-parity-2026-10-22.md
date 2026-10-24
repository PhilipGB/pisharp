# Complete Built-in Coding Tool Parity — 2026-10-22

This receipt records the supported Linux contract against Pi
`b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`. Broader Pi parity remains
`in_progress`. Strict equivalence excludes the named safety differences below;
codec equivalence is reported separately from exact matches.

## Repository prerequisite

| Item | Verified value |
| --- | --- |
| PR #21 reviewed head | `64fc15b7317a2307b066bbba6ebb3ea630b19ee1` |
| PR #21 reviewed base | `5e1e5aa8ddc66a8e829a880604060b8483107284` |
| PR #21 actual merge | `ebfeb24c9dd5e03592c3b388236f22a3a3a3299a` |
| PR #22 branch base | `ebfeb24c9dd5e03592c3b388236f22a3a3a3299a` |
| Branch | `milestone/builtin-coding-tool-parity` |
| Pi pin | `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` |

The reviewed PR #21 head had no added commits or review threads. Push CI
[39791573299](https://github.com/PhilipGB/pisharp/actions/runs/39791573299)
checked out that head. PR CI
[39791580371](https://github.com/PhilipGB/pisharp/actions/runs/39791580371)
tested temporary merge `3514f04a66554e85d6f05ffa88b8a72c312d3707`, whose
parents were verified against the reviewed base and head. Both passed 2,711 tests
with zero failures/skips and successful MCP jobs. PR #21 was moved out of draft
and merged using the expected head and a normal merge commit.

The actual two-parent merge, terminal implementation and unchanged pin were
verified on fetched `origin/main` before creating the new branch or changing
product code. Main was fetched again during final acceptance and remains at that
merge. The PR head and actual merge commit are distinct.

## Inventory and independent oracle

The [initial inventory](builtin-coding-tool-inventory-2026-10-22.json) was committed
at `9cfa97adab23433724ec41e4f4386d32cd7e53ad` before product changes. It contains
all seven source-extracted declarations, source hashes, defaults, result/error
contracts, details, cancellation, side effects, provider/history boundaries,
initial differences and regression plans. Its initial observations remain
historical; acceptance updates identify later executed evidence.

Authoritative tool sources are `packages/coding-agent/src/core/tools/`:
`read.ts`, `write.ts`, `edit.ts`, `edit-diff.ts`, `bash.ts`, `grep.ts`, `find.ts`,
`ls.ts`, `truncate.ts`, `output-accumulator.ts`, `file-mutation-queue.ts`,
`path-utils.ts` and `tool-definition-wrapper.ts`. Image behavior also uses
`utils/{mime,image-process,image-resize-core,exif-orientation,photon}.ts`;
process behavior uses `utils/{shell,child-process,output-files}.ts`. Relevant
upstream tool, selection, lifecycle and regression tests were inspected and run.

The oracle imports those actual definitions through upstream's source resolver,
validates arguments with upstream TypeBox and executes in disposable workspaces.
It never substitutes PiSharp expectations or newer/stale Pi output. Real installed
executables are required; the Linux acceptance environment has ripgrep 15.2.0,
fdfind 10.3.0, Node 24.21.0 and .NET 10.0.113. Oracle runs use `PI_OFFLINE=1`.
Windows/macOS native behavior is not claimed from these Linux executions.

The [initial differential](builtin-coding-tool-baseline-2026-10-22.json) passes
23/211 before corrections. The final corpus is larger, so those totals are not
a percentage comparison of an unchanged suite.

## Per-tool acceptance

| Tool | Cases | Exact | JPEG codec equivalent | Explicit exclusions |
| --- | ---: | ---: | ---: | ---: |
| read | 121 | 109 | 10 | 2 |
| write | 39 | 20 | 0 | 19 |
| edit | 69 | 31 | 0 | 38 |
| bash | 44 | 44 | 0 | 0 |
| grep | 68 | 67 | 0 | 1 |
| find | 62 | 61 | 0 | 1 |
| ls | 53 | 53 | 0 | 0 |
| Total | 456 | 385 | 10 | 61 |

The [corpus receipt](builtin-coding-tool-corpus-acceptance-2026-10-22.json)
records each case, source SHAs, strict differences and classifications. There are
395 supported passes, 61 strict-equivalence exclusions and zero unresolved
cases. A separate same-Pi control passes 456/456; PiSharp is not used by it. A preceding control had 455 exact matches and one ordered privacy-case difference between two actual Pi runs; both attempts remain recorded. The successful bounded repeat does not erase that observation.
Protocol vectors, repeated observations and agent/provider workflows below do
not inflate the 456 distinct-case count.

`read` now preserves pinned argument/path/offset semantics, BOM and newline
behavior, replacement UTF-8 decoding, decoded byte/line counts and exact notices.
The large-file path scans with bounded buffers and retains only its output
window; a 64 MiB single-line test verifies complete counts and requests no buffer
larger than 64 KiB. Image handling retains SkiaSharp and implements the pinned
Lanczos3 sampling path, EXIF orientation, first-frame handling, alpha/conversion,
model resize profiles, MIME and payload limits. Ten named JPEG cases use the
[calibrated encoder envelope](builtin-coding-tool-jpeg-codec-2026-10-22.json),
checking decoded pixels, dimensions, quantization/sampling and nonpixel results;
no JPEG byte-identity claim is made across different encoders. See
[read acceptance](builtin-coding-tool-read-acceptance-2026-10-22.json).

`write` now matches supported paths, Linux access/error categories, parent and
link resolution, exact text and mutation coordination. New files remain private;
atomic replacement remains intentional. Existing modes and ownership are
preserved where supported. Real aliases, write/edit races and cancellation use
the existing shared per-file queue, with independent-file progress verified.
See [write acceptance](builtin-coding-tool-write-acceptance-2026-10-22.json)
and [coordination evidence](builtin-coding-tool-mutation-coordination-2026-10-22.json).

`edit` plans all replacements against the original snapshot, rejects invalid,
ambiguous and overlapping batches without partial logical acceptance, preserves
BOM/CRLF behavior, uses replacement UTF-8 decoding and matches fuzzy boundaries,
patches, diff and `firstChangedLine`. A parent removed after the initial snapshot
is not silently recreated. Atomic replacement and its filesystem consequences
remain explicit exclusions. See
[edit acceptance](builtin-coding-tool-edit-acceptance-2026-10-22.json).

`bash` now matches initial empty/cumulative raw updates, deterministic 100 ms
coalescing, UTF-8/BOM/incomplete sequences, ignored stdin, timeout/spawn failures,
exit/signal outcomes, tail notices, private complete-output artifacts and result
metadata. Session/provider/model/reasoning environment values are resolved for
each execution after stale inherited values are removed. Process groups and
captured descendants are cleaned up on abort, including detached grandchildren.
Direct user Bash, RPC and extension interception retain their separate contracts.
See [Bash acceptance](builtin-coding-tool-bash-acceptance-2026-10-22.json),
[environment](builtin-coding-tool-bash-environment-2026-10-22.json) and
[shutdown evidence](builtin-coding-tool-bash-shutdown-2026-10-22.json).

`grep` delegates Rust regex/glob/ignore and binary/invalid-UTF-8 behavior to real
`rg`, using a bounded streaming JSON reader and output builder. The former
10 MiB per-file and 20,000-path inventory caps no longer hide valid results.
Match/context/long-line limits, notices, error handling and cancellation have
physical and synthetic process evidence. A 12 MiB line uses less than 256 KiB of
parser allocation. See [grep acceptance](builtin-coding-tool-grep-acceptance-2026-10-22.json).

`find` delegates discovery grammar, ignore rules, smart case, symlinks and
encounter order to real `fd`/`fdfind`. It streams results without the old inventory
cap and preserves numeric limit and process error behavior. A real 20,050-file
fixture reaches the output barrier, cancels/reaps fd and recovers safely. One older directory-symlink expectation was corrected from the actual pinned result, which has no slash for that symlink. See
[find acceptance](builtin-coding-tool-find-acceptance-2026-10-22.json).

`ls` keeps its own directory enumeration and ordering policy. ICU comparison,
JavaScript case conversion/tie ordering, stat filtering, broken links, directory
markers, fractional limits, byte truncation and Linux errors match the defined
source cases. Cancellation during pending enumeration settles safely while an
independent listing continues. See
[ls acceptance](builtin-coding-tool-ls-acceptance-2026-10-22.json).

### Ownership and executable permission hardening

Eight additional [pinned mutation observations](builtin-coding-tool-mutation-ownership-2026-10-22.json)
cover ordinary permissions, executable set-user-ID/set-group-ID bits and the
non-executable group bit. Four focused regressions fail before the correction:
atomic replacement had restored executable privilege bits that pinned direct
writes clear. The publisher now restores owner/group through its private file
handle and applies the kernel-compatible write mode, using the current thread's
effective capabilities. Candidate bytes remain private until the commit barrier.
An ownership-restoration failure leaves the original target in place.

The mode/ownership facet is verified separately from the deliberate inode
replacement difference; these eight observations do not inflate the 456-case
corpus. An alternate supplementary group could not be assigned in this execution
environment: the restricted mapping returns EINVAL and the expanded mapping
returns EPERM. That physical cross-group comparison remains explicitly unverified
locally. Linux GitHub CI captures its own fresh pinned-Pi oracle and requires the native fixture to assign the same alternate group, comparing actual UID/GID/mode/content/results rather than silently omitting that branch. Privileged/CAP_FSETID execution was not
physically compared. Where effective capabilities cannot be read, native
publication conservatively clears executable privilege bits.

The implementation follows the Linux [statx](https://man7.org/linux/man-pages/man2/statx.2.html),
[ownership](https://man7.org/linux/man-pages/man2/chown.2.html) and
[capability](https://man7.org/linux/man-pages/man7/capabilities.7.html) contracts.

## Shared results, provider integration and history

Seven exact schemas/descriptions/output declarations and selector behavior are
verified, including the four defaults, optional tools, exclusions and preserving
registered extension tools when built-ins are disabled. Ninety-three primitive
validation vectors and 140 shared truncation vectors cover conversions, limits,
UTF-8, huge lines, head/tail policy and metadata. Original public facade contracts
and compiled-extension foundations are preserved.

The [agent workflow](builtin-coding-tool-agent-workflow-2026-10-22.json) executes
read/find/grep/ls/edit/failed-edit/write/bash/read through actual pinned Agent and
native MAF. It checks failure recovery, final bytes, correlated authoritative
results, lifecycle ordering and actual session save/reopen. Built-in expected
failures now reach the model as actionable text while retaining failed durable
outcomes; extension and unexpected exception paths retain their contracts.

The [physical provider acceptance](builtin-coding-tool-provider-acceptance-2026-10-22.json)
uses actual RPC CLIs and HTTP SSE fixtures for OpenAI Responses, Chat Completions
and Anthropic Messages. Each product sends eleven requests across two CLI
processes, executes nine calls covering all seven tools and restores all nine
outcomes from its actual session file. Tool declarations, request routes, result
IDs/text/errors, canonical names/details and filesystem bytes are checked. Native
result projection fixes extra JSON quotes on live string results; API schema
projection and JSON/RPC metadata/name preservation are covered by fail-first
regressions. Read images also cross all three physical APIs with model-profile,
text-only, cancellation and restart evidence.

The [concurrent fixture](builtin-coding-tool-concurrent-agent-2026-10-22.json)
runs eight calls in one batch. A real FIFO holds Bash while two independent writes
finish. Provider continuation and saved/compacted history preserve the original
call order and all eight outcomes. This exposed and corrected rejection of valid
assistant compaction boundaries in RPC snapshots. In-flight context accounting
now estimates the existing provider projection rather than hidden presentation
metadata, without rewriting canonical results. The related selection passes
114/114; original failing evidence is retained.

## Safety decisions and reliability

The [safety acceptance](builtin-coding-tool-safety-acceptance-2026-10-22.json)
records five clean-head repetitions of 271 checks each: 1,355 passes, zero
failures/skips/timeouts and no abandoned full-output files. It also records
physical provider cancellation after eight completed calls, including a Bash
append. An HTTP barrier holds the continuation response, RPC abort settles the
agent, and a real CLI restart preserves exactly one appended byte and eight
correlated results. All three API comparisons and three strict same-Pi controls
pass. The usual private write-mode difference remains separate.

The first repeat attempt had four search-order assertion failures and stopped.
Actual pinned search was then executed 64 times for five existing cases on both
two-CPU and 24-CPU profiles. All four native alternatives occur in the
[source observations](builtin-coding-tool-search-order-2026-10-22.json), including
which file fd selects first under a one-result limit. Original expectations are
preserved; only complete independently observed alternatives are additionally
accepted by regression fixtures. A provenance assertion checks identical setup,
content and details. No permutation is inferred, and the differential comparator
continues to report exact ordered differences without sorting.

Strict-equivalence exclusions remain visible:

- Two image cases retain 20 MiB encoded-input and 32-million-pixel allocation
  bounds. Pi may decode inputs beyond those native safety limits.
- Nineteen write and thirty-eight edit cases retain private new-file permissions
  and atomic replacement. Replacement changes inode/hard-link relationships and
  can require parent-directory access where Pi's direct write succeeds. Errors,
  final bytes, modes, ownership and link effects are recorded in the exact
  [write](builtin-coding-tool-write-safety-2026-10-22.json) and
  [edit](builtin-coding-tool-edit-safety-2026-10-22.json) observations.
- One grep and one find case preserve repository-local `core.excludesFile`
  privacy behavior. Complete observed differences, including rescued paths,
  remain in their [grep](builtin-coding-tool-grep-safety-2026-10-22.json) and
  [find](builtin-coding-tool-find-safety-2026-10-22.json) receipts.
- Three additional shutdown cases retain stronger descendant cleanup and native
  cancelled EOF feedback. Pinned Pi leaves the detached grandchild alive at
  settlement; the oracle fixture terminates its own children during cleanup.
  These cases are outside the 456-case direct corpus and remain separately
  excluded from strict shutdown equivalence.

None of these exclusions increases the strict/pass count or hides an unexplained
result, filesystem or cancellation difference. Complete output artifacts are
retained for the established tool contract; tests remove their own artifacts.

## Local validation and reproduction

Current differential and physical provider acceptance source is
`e152e686017f659141d1bd5a83ef8823627c5410`. The last committed full-suite
counter receipt below is from `3d0d7d6db783a036930b5c39edf0796d90fdd447`,
before the final public-facade and fixture follow-up. The live PR description
records the fresh corrected-head full suite and exact-head CI counters.

| Gate | Result |
| --- | --- |
| Format verification | Passed |
| Warning-as-error solution build | Passed; zero warnings/errors |
| Full .NET | 3726/3726; zero failures/skips (1015 additional cases over the PR #21 baseline) |
| Architecture | 7/7, included in full suite |
| Compiled extensions / agent / provider / sessions | Passed in full suite |
| Node terminal | 24/24 |
| Python terminal | 66/66 |
| Differential comparison guards | 27/27 |
| MCP conformance | 261 passing checks; eight unchanged classified failures; zero new failures |
| Final bounded reliability | Five repetitions × 271 checks = 1,355 passes |
| Documentation paths | 151 documents / 617 references; passed |

The [validation receipt](builtin-coding-tool-local-validation-2026-10-22.json) records
source identities and full-suite counters. The first corrected-product full run
had 3,725 passes and one test race: restored startup history was already visible
before the test's output mark. The initial settled frame is now checked before
marking the next prompt, retaining the same bounded deadlines and all product
assertions. Five focused physical repetitions and the later full suite verify
that synchronization correction. A subsequent run exposed a fixture-only HttpListener cleanup race. A standalone .NET 10.0.13 probe reproduces Stop/disposal attempting to rebind a released port, while Close/disposal succeeds. The fixture now closes its owned listener once, preserving all product assertions and bounded deadlines. Five physical repetitions verify that cleanup. Earlier failing evidence remains retained.

The first published head exposed nine CI failures, retained in the local validation receipt. The documentation finalizer had overwritten the existing sixteen-case argument fixture; the original fixture is restored byte-for-byte and this validation receipt has a distinct filename. Public `SearchTools.Grep` retains its legacy ignore behavior outside Git while registered model tools retain the pinned argument vector. Synthetic fd fixtures now create an explicit repository marker; all 27 pinned outcomes are unchanged. CI installs checksum-verified official fd 10.5.0, which passes a 62/62 independent same-Pi find control. The earlier official fd 10.3 build and CI fd 9 reject nested braces accepted by the local distribution build. Search behavior therefore records the actual executable build, alongside its version. Both initial CI ownership gates passed all eight native cases against fresh source observations at UID 1001 / alternate GID 4. The final corrected-head suite and CI results are recorded in the live publication receipt below.

Run from the repository root with .NET 10, Node 24, Python/Pillow, real rg and
fd/fdfind, and the pinned Pi checkout with its dependencies already installed:

```sh
dotnet format PiSharp.slnx --verify-no-changes --no-restore
dotnet build PiSharp.slnx --no-restore --warnaserror
TMPDIR=/tmp DOTNET_PROCESSOR_COUNT=2 dotnet test PiSharp.slnx --no-build --no-restore
python3 tools/parity/builtin-coding-tool-differential.py --pi /tmp/pi-pinned-source --output artifacts/parity/builtin-tools-replay
python3 tools/parity/builtin-coding-tool-differential.py --pi /tmp/pi-pinned-source --calibrate --output artifacts/parity/builtin-tools-control
python3 tools/parity/builtin-coding-tool-provider-differential.py --pi /tmp/pi-pinned-source --output artifacts/parity/builtin-provider-replay
python3 tools/parity/builtin-coding-tool-provider-differential.py --pi /tmp/pi-pinned-source --cancel-after-bash --output artifacts/parity/builtin-provider-abort
python3 tools/parity/builtin-coding-tool-reliability.py --iterations 5 --output artifacts/parity/builtin-tools-reliability
npm test --prefix tools/parity/terminal
python3 -m unittest discover -s tools/parity/terminal -p 'test_*.py'
python3 -m unittest discover -s tools/parity -p 'test_builtin_coding_tool_differential.py'
python3 tools/validate_doc_paths.py
node tools/parity/mcp-conformance/run.mjs
```

The source resolver rejects the wrong Pi revision. Temporary workspace roots,
artifact filenames and inherently variable durations are documented separately;
file bytes, paths, result lines, errors, metadata, order and side effects are not
sorted or discarded. Raw source/native results and failing/passing TRX evidence
are ignored under `artifacts/parity/builtin-coding-tools-2026-10-22/`; compact
reviewed receipts and reproducible inputs are committed here.

## Duration and publication

The mission began at 2026-10-22 17:44:40 UTC. At this documentation revision,
the goal service reports 31,064 active seconds (8h 37m),
excluding the interrupted intervals recorded in the execution ledger. This is an
observed duration; the final publication receipt records the final measured
active engineering duration. Wall time and the eight-hour target are not used
as substitutes for active work.

The live [PR #22 publication/CI receipt](https://github.com/PhilipGB/pisharp/pull/22) records the actual final branch
head, draft/open/unmerged state, exact-head push and PR workflow IDs/results, and
actual alternate-group ownership artifact. It is updated after CI so the tested
commit does not change solely to record a run ID. This repository snapshot records
local A–R acceptance (18/20); consult the live receipt for subsequent S/T completion.
The PR must remain draft and unmerged; PR #23 is outside this mission.
