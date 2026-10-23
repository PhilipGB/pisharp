# Complete Built-in Coding Tool Parity — 2026-10-22

Status: **in progress**. This is the initial source inventory, created before
product changes. No tool is newly claimed verified by this inventory.

## Repository prerequisite

| Item | Verified value |
| --- | --- |
| PR #21 reviewed head | `64fc15b7317a2307b066bbba6ebb3ea630b19ee1` |
| PR #21 reviewed base | `5e1e5aa8ddc66a8e829a880604060b8483107284` |
| PR #21 actual merge | `ebfeb24c9dd5e03592c3b388236f22a3a3a3299a` |
| Post-merge `origin/main` | `ebfeb24c9dd5e03592c3b388236f22a3a3a3299a` |
| New branch base | `ebfeb24c9dd5e03592c3b388236f22a3a3a3299a` |
| New branch | `milestone/builtin-coding-tool-parity` |
| Pinned Pi | `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6` |

Before merge, current REST metadata, reviews, review threads and four checks
were inspected. There were no additional commits or review threads. Push run
[39791573299](https://github.com/PhilipGB/pisharp/actions/runs/39791573299)
checked out the reviewed head; PR run
[39791580371](https://github.com/PhilipGB/pisharp/actions/runs/39791580371)
checked out temporary merge `3514f04a66554e85d6f05ffa88b8a72c312d3707`,
whose parents were verified as the reviewed base and head. Both workflows
passed 2,711 tests with zero failures/skips and both MCP jobs passed.

PR #21 was moved out of draft and merged with the expected head supplied to
`gh pr merge --merge --match-head-commit`. Its closed/merged metadata and actual
two-parent merge were then checked against fetched `origin/main`. The terminal
LaTeX correction and unchanged Pi pin were verified there before creating the
new branch. The worktree was clean. Raw receipts are ignored under
`artifacts/parity/builtin-coding-tools-2026-10-22/pr21-merge/`.

## Acceptance policy

The machine-readable [initial inventory](builtin-coding-tool-inventory-2026-10-22.json)
contains each tool's actual source-extracted declaration, source file hashes,
defaults, result/error/details contracts, current implementation, filesystem
effects, cancellation, provider and persistence boundaries, planned fixtures,
regression locations and acceptance status. Source references are relative to
the unchanged Pi pin unless identified as PiSharp.

Initial classifications distinguish source-confirmed differences from executed
differentials. Existing implementation and local tests do not establish new
upstream verification. All seven tools need actual pinned execution, physical
filesystem/process evidence and shared MAF/provider/session acceptance.

Required corpus: at least 70 distinct meaningful cases, at least ten per tool.
Counts will come from executed case IDs. Pending, failed and intentional safety
deviations will remain visible. Temporary workspace roots and inherently variable
durations/PIDs may be identified separately; ordering, file bytes, missing text,
errors, details and cancellation outcomes must not be normalized away.

Linux is required. Native Windows/macOS signal, permissions, errors and image
backend behavior remain unverified unless executed on those platforms.

## Initial per-tool inventory

### Read

Pi: `packages/coding-agent/src/core/tools/read.ts`, `truncate.ts`, `path-utils.ts`,
`utils/{mime,image-process,image-resize-core,exif-orientation}.ts`.
PiSharp: `FileReader`, `StreamingTextReader`, `ReadTextPlanner`,
`ReadImageProcessor`, `ReadImageDetector`, `ReadToolOutput`.

- Schema: required string `path`; optional numeric `offset` and `limit`.
  Defaults: start at line one, no user limit, then 2,000 lines/51,200 UTF-8 bytes.
  Pi resolves `@`, home and Unicode-space paths and read-specific filename variants.
- Normal output is unnumbered selected source, with exact continuation notices.
  Text `structuredContent` is the same string. Image content is a note plus an
  image, and structured content has `type`, `data`, `mimeType`, `note`.
- Truncation details contain the full pinned truncation object. Access/read
  failures reject; the agent converts them to error results. Pre-abort and an
  abort observed during work reject and must not yield successful history.
- Filesystem effects: none. Provider output carries text/image blocks, without
  internal details as ordinary text. Canonical results must preserve metadata
  and images through save/reload and provider continuation.
- Existing text selection has narrow older fixtures. Current text execution
  returns only text, losing truncation details; path normalization and numeric
  offset/limit handling differ by source inspection. The large-file reader
  counts raw bytes, while pinned Pi counts decoded UTF-8 bytes. Invalid UTF-8,
  error projection and boundary behavior need differential execution.
- Image signature checks are close by inspection. Cubic sampling differs from
  Lanczos3; orientation is not applied. Animation, corrupt input, MIME conversion,
  payload equality, model profiles and text-only projection require new evidence.
  The 20 MiB input/32-million-pixel bounds must remain explicit safety candidates.
- Planned acceptance: encoding/newline/window/boundary/error/link/image cases;
  local HTTP image requests for Responses, Chat Completions and Anthropic;
  streaming large input and image-cancellation hardening.

### Write

Pi: `core/tools/{write,file-mutation-queue,path-utils}.ts`.
PiSharp: `FileMutations`, `AtomicFileWriter`, `FileMutationQueue`.

- Required string `path` and `content`; no optional parameters. Parent directories
  are created recursively. Success text is `Successfully wrote to <input path>`;
  details and structured content are absent. Filesystem failures reject.
- Pi queues by resolved real path, checks abort around awaited operations, and
  retains the queue until the in-flight operation settles. It directly writes
  UTF-8, following links and preserving an existing inode/hard-link relationship.
- PiSharp already shares a per-file queue across facades and uses atomic rename.
  New files are private rather than umask-based; replacement changes inode and
  hard-link relationships. Existing-mode, ownership, readonly, dangling-link,
  path-alias and cancellation behavior require actual filesystem comparisons.
- Retaining atomic replacement may be a justified safety deviation. This is
  provisional, not an accepted equivalent result. Before acceptance, record exact
  consequences, errors, unchanged/changed bytes and evidence for each deviation.
- Provider text and canonical result must describe the actual outcome. No late
  filesystem completion may be recorded as a success after cancellation.
- Planned acceptance: create/replace/content/path/ancestor/permission/link cases,
  same-file aliases, unrelated concurrency, write/edit ordering and deterministic
  cancellation while waiting and during coordinated work.

### Edit

Pi: `core/tools/{edit,edit-diff,file-mutation-queue}.ts`.
PiSharp: `FileMutations`, `FileEdits`, `FileEditDiff`, shared atomic writer/queue.

- Required `path` string and `edits` array of required `oldText`/`newText` strings.
  Pi prepares stringified/single-object/legacy replacements before schema
  validation, but advertises only the batched schema. Empty batches are rejected.
- All replacements address one original snapshot, with normalized line endings,
  uniqueness/overlap checks and fuzzy normalization. Invalid batches do not write.
  BOM and detected CRLF are restored. Diff/patch use four lines of context.
- Success text: `Successfully replaced N block(s) in <input path>.` Details are
  `diff`, `patch`, `firstChangedLine`. Access failures use `Could not edit file:
  <path>. Error code: <code>.`; planning failures have distinct precise messages.
- Queue/cancellation contract is shared with write. Provider receives result
  text; details remain structured in lifecycle/canonical history and replay.
- Existing focused planner/diff fixtures are a foundation. Strict UTF-8 decoding
  differs from Pi's replacement decoding by inspection. Prepared argument forms,
  directory/access errors, fuzzy preservation, patches and aliases need new
  pinned evidence. Atomic replacement differences also apply to edit.
- Planned acceptance: all mandatory edit shapes, validation/atomic failure,
  newline/BOM/Unicode/fuzzy cases, patch applicability, concurrent edits and
  write/edit races, cancellation, lifecycle and restart.

### Bash

Pi: `core/tools/{bash,output-accumulator,truncate}.ts`,
`utils/{shell,child-process,output-files}.ts` and direct `bash-executor.ts`.
PiSharp: `ShellExecution`, `ShellOutputBuffer`, `BashOutputUpdates`,
`BashToolOutput`, `ProcessLifetime`; direct user Bash retains separate routing.

- Required string `command`, optional numeric timeout in seconds, no default
  timeout. Positive finite timeout is bounded by 2,147,483,647 milliseconds.
  Shell path/prefix, cwd and current `PI_*` session metadata come from host state.
  Stale inherited session/model variables are removed first.
- Combined stdout/stderr is raw for the model tool. An initial empty update and
  cumulative bounded snapshots use a 100 ms throttle. Display uses a 2,000-line /
  51,200-byte tail and truncation notices referencing a private full-output file.
  Structured output has `output`, `truncated`, optional `full_output_path`,
  `exit_code`, `wall_time_seconds`; its own output bound is one MiB.
- Nonzero exit returns `isError` with output/status and structured result.
  Signals map to 128 plus signal. Timeout/abort reject with partial output and
  status. Pipes get an activity-based 100 ms grace after process exit. Abort kills
  the process group. Detached-descendant cleanup requires physical execution.
- Current output details contain `truncated` rather than Pi's optional
  `truncation` object. Updates currently carry deltas, and complete truncation
  metadata is absent. Existing process/environment tests do not establish all
  pinned error, update, partial UTF-8 and spill-file contracts.
- Filesystem effects are command-specific in disposable workspaces, plus retained
  private spill files. Provider continuation and canonical history must preserve
  one actual correlated outcome; direct user Bash hooks must remain compatible.
- Planned acceptance: stdout/stderr/exit/signal/prefix/environment, exact limits,
  invalid UTF-8/control bytes, spill contents/modes, timeouts, aborts, child and
  grandchild cleanup, continuation and extension interception regressions.

### Grep

Pi: `core/tools/{grep,truncate,path-utils}.ts`, `utils/tools-manager.ts` (ripgrep).
PiSharp: `SearchTools`, `SearchInventory`, `ToolOutputTruncator`.

- Required string `pattern`; optional `path`, `glob`, `ignoreCase`, `literal`,
  numeric `context`, `limit`. Defaults: cwd, regex, case sensitive, zero context,
  100 matches; limit is clamped to at least one.
- Pi runs `rg --json --line-number --color=never --hidden` with separate argv.
  Matching records retain rg encounter order. Context normalizes CRLF/CR on a
  reread. Long lines keep 500 UTF-16 code units plus suffix. Byte-only head
  truncation is applied after formatting. No matches is `No matches found`.
- Details: optional `truncation`, `matchLimitReached`, `linesTruncated`. Missing
  root is `Path not found: <absolute>`; rg errors reject with stderr. Abort rejects.
- Current managed regex accepts a different language. Git/managed inventory,
  negative globs, binary handling, CR-only input and ordering differ or remain
  unverified. A 10 MiB file cap and 20,000-path inventory cap are observable
  resource candidates; no broad search equivalence is established.
- Filesystem is unchanged. Provider gets only text; structured details must
  survive lifecycle/session/JSON/RPC. A bounded adapter to available rg is an
  implementation option if differential evidence warrants it.
- Planned acceptance: regex/literal/case/context, glob/ignore/nested repositories,
  links/binary/invalid UTF-8, large files/lines, limits, errors and cancellation.

### Find

Pi: `core/tools/{find,truncate,path-utils}.ts`, `utils/tools-manager.ts` (fd).
PiSharp: `SearchTools`, `SearchInventory`.

- Required glob `pattern`, optional root `path` and numeric `limit` (default
  1,000). Pi runs fd with glob/color/hidden/max-results options. It adds
  `--no-require-git` outside repositories and `--full-path` plus a `**/` prefix
  for relative patterns containing `/`. Default fd case behavior is retained.
- Results are relative POSIX paths in fd encounter order, with directory slash
  markers. `No files found matching pattern` is the empty result. Details contain
  optional `resultLimitReached` and byte-head `truncation`, with pinned notices.
- Process errors reject unless fd produced usable output; cancellation rejects
  and stops the child. Filesystem is unchanged; provider/history rules match grep.
- Current managed inventory sorts ordinally, synthesizes ancestors and clamps
  limits; fd's complete glob/ignore/symlink/ordering contract is not demonstrated.
  The 20,000-path pre-search cap can discard late matching paths or fail queries
  that fd can answer with a small requested limit.
- Planned acceptance: basename/path/recursive/brace/class/escape/case patterns,
  hidden and ignored entries, nested repositories, file/directory/link roots,
  errors, limits, cancellation and a repository beyond the inventory cap.

### Ls

Pi: `core/tools/{ls,truncate,path-utils}.ts`; no rg/fd traversal.
PiSharp: `DirectoryListingTool`, `ToolOutputTruncator`.

- Optional `path` (cwd) and numeric `limit` (500). Includes dotfiles; sorts with
  lowercased `localeCompare`; follows stat to append `/` for directories and skips
  unstatable entries, including broken links. Empty result: `(empty directory)`.
- Limit notice appears only when the scan encounters another entry after the
  accepted count. Byte-head truncation is separate. Details: optional
  `entryLimitReached` and `truncation`. Missing root/not-directory/read-directory
  errors are distinct. Abort must not record successful enumeration.
- Current ordinal case-insensitive sorting differs from ICU locale comparison;
  broken links can be included, and count metadata uses total names rather than
  accepted stat results. Numeric binding, special entries, error and cancellation
  edges need physical comparisons.
- No filesystem effects. Provider receives text; details persist and replay.
- Planned acceptance: default/relative/absolute/empty roots, Unicode and mixed-case
  sorting, directories/links/broken links/special entries, limits, long filenames,
  errors, cancellation and provider/session round trip.

## Differential harness acceptance

The initial corpus defines 211 distinct cases: read 45, write 21, edit 33, bash
28, grep 32, find 27, ls 25. These include actual files, Git repositories,
permissions, symlinks/hard links, FIFO listing, a repository beyond 20,000
paths, large/invalid-UTF-8 text, image data and real Bash processes.
Expected outputs come from importing the pinned implementations with upstream's
source resolver; neither copied PiSharp output nor stale compiled Pi is used.
Installed search executables are ripgrep 15.2.0 and fdfind 10.3.0.

The same-Pi control passes 211/211 on separately initialized workspaces.
Eleven harness tests verify that comparisons retain lost lines, ordering,
wrong bytes, missing metadata, unexpected success, mode/inode/link differences,
unsafe artifact paths and invalid durations. See the
[harness receipt](builtin-coding-tool-harness-2026-10-22.json) for raw-report
hashes, reproduction commands and limitations. Original raw results are retained
under the ignored artifact root; compact reviewed outcomes will follow repairs.

The native probe uses actual `AIFunction.InvokeAsync` and the shared
`ToolResultOutput` projector. Initial ordinary grep/find/ls results expose a
projection defect when their null details field is omitted. All three optional
declarations also have description/schema differences. Text read omits pinned
structured content/truncation details; Bash details and initial/final update
contracts differ. Atomic-write mode/inode differences remain separately visible.

An early Runtime-only probe lacked the CLI's native Skia assets; its three valid
image captures are not product failure evidence. The corrected probe references
the CLI composition. Its small PNG/alpha payloads are preserved; resized PNG
decoded pixels differ, and read structured content is absent. No encoder byte
identity is imposed on reencoded PNG: its decoded RGBA is compared exactly.

The initial harness does not yet cover every mandatory image, concurrency,
provider and session scenario. Reaching 211 cases does not satisfy the mission
without those remaining behaviors and integrations.

## Shared-contract corrections in progress

The clean committed-harness baseline at `a9ab1c091d003396024685baca44d588bb0f1d31`
passes 23/211 comparisons. Its remaining 188 cases retain their differing
contract paths; this is not a claim of 188 independent implementation defects.
The [compact baseline](builtin-coding-tool-baseline-2026-10-22.json) preserves
all case IDs, arguments, outcomes, source pins and hashes, with reproducible raw
reports under the artifact root.

Product correction `f9f3fa56` preserves a null `Details` discriminator through
MAF serialization, restoring plain untruncated grep/find/ls text at execution,
provider continuation and canonical restart. It also preserves the pinned
truncation `content` field without changing the public truncation constructor.
Seven new tests fail on the old implementation and pass after the correction;
the related result selection passes 14/14. Its clean-source search differential
passes 54/84 (grep 32, find 27, ls 25), with traversal/regex/ordering/validation
differences still open.

The next correction embeds all seven source-extracted declarations, prepares
Pi's stringified/single-object/legacy edit forms, removes optional nulls and
projects validation failures before typed binding. Preparation also precedes
native tool hooks, with a second validation after hook mutation. The initial
argument suite fails 24/31 before this correction, then passes 31/31. Related
coding/edit/result tests pass 64/64; the tool-selection/native-hook/result
selection passes 77/77. No tests are skipped. The
[validation oracle](builtin-coding-tool-validation-2026-10-22.json) records
sixteen exact errors captured from pinned Pi. The five selected upstream
regression files pass 116/116.

Primitive conversion now matches the pinned TypeBox 1.3.27 behavior in
[93 source-captured vectors](builtin-coding-tool-scalar-validation-2026-10-22.json),
including finite-number formatting, radix and numeric strings, Boolean case,
null strings, nested edit values and optional null removal. Twenty-four new
regressions fail before the conversion correction. Two additional compatibility
regressions expose, then verify, anonymous/typed .NET edit representations.
The related expanded selection passes 169/169 with zero skips. Two actual
converted writes use the existing workspace/mutation path and leave the
unrelated fixture unchanged. Objects/arrays are still validated against their
declared shape; the conversions do not bypass filesystem or process ownership.

Reproduce the pure pinned conversion oracle with:

```sh
node tools/parity/builtin-coding-tool-probe.mjs /tmp/pi-pinned-source docs/parity/fixtures/builtin-coding-tool-scalar-validation-2026-10-22.json artifacts/parity/builtin-coding-tools-2026-10-22/scalar-oracle-reproduction.json --validate-only
```

The pure shared text utility now matches
[140 captured head/tail results](builtin-coding-tool-truncation-2026-10-22.json).
Five of 28 existing head-projection regressions fail before the correction;
all 28 now match the complete metadata. Related tests pass 184/184, zero skips.
The utility counts a terminal newline correctly, keeps complete head lines,
selects valid UTF-8 tail boundaries and preserves the oracle's limit-priority
rules. Core remains free of filesystem/process/provider dependencies. Search's
existing public truncation constructor is preserved.

Reproduce the pinned truncation results with the same probe and
`--truncate-only`, using the truncation fixture as input. These component
vectors are separate from the distinct seven-tool corpus counts.

Read text now carries structured output and complete truncation details through
the actual function, MAF lifecycle, provider continuation and native session
round trip. The [13 pinned read results](builtin-coding-tool-read-results-2026-10-22.json)
are captured from the independent baseline, including a large invalid-UTF-8
window. Fifteen regressions fail before the correction; the expanded related
selection passes 39/39, zero skips, including existing OpenAI image replay.
Canonical text reads retain their result carrier while provider projection
remains plain text. The existing image replay representation remains compatible.
The large-file reader counts decoded bytes and keeps a bounded prefix rather
than allocating full lines or the full file. A compiler warning caused by
capturing the delegating function constructor argument is also removed using
the installed API's `InnerFunction` property.

The first clean-source read rerun (`dc624038`) reports 30/45 matches. Two
reported empty-read differences are a native probe bug: it replaces an already
projected empty string with the serialized carrier. Direct pinned empty-read
regressions pass. The probe now falls back only when projection fails, preserving
empty results. This diagnostic run is retained; a corrected probe rerun follows.
The remaining numeric/path/error/resize differences remain product work.

Bash now carries the full tail-truncation record, and untruncated results omit
details as pinned Pi does. The [seven pinned Bash results](builtin-coding-tool-bash-results-2026-10-22.json)
verify actual process output, structured-output hashes, exit/error results,
complete spill-file hashes and private Linux permissions. The registered read/
Bash output schemas and the four default tools' sampling policies match source.
These fourteen checks fail 11/14 before correction; the expanded related suite
passes 201/201. Tool selection, hooks, direct user Bash and Codemode checks pass
60/60. Public shell-result constructors/deconstruction remain compatible.

Two added MAF/JSON/RPC/session tests find JSON's `JsonIgnore` attribute drops
structured content. The additive correction emits it when present. The related
wire suite then passes 23/23, zero skips, including metadata through native
session reload. The native oracle probe now reads output schema and sampling
policy from actual registrations, and its four selected empty/truncated read
cases pass at clean source `35601464`.

Checkpoint E's common contract work is complete. Tool-specific numeric/path/error/
image behavior, Bash update snapshots and process lifecycle, all-seven integration,
physical provider acceptance, stress tests and full repository gates remain open.

The clean `e1ce40d2` shared-contract run has exact declarations for all seven
tools and 6/9 complete matches. All three remaining Bash differences concern
initial/final live updates; final text, details, structured output and spill
artifacts match. Those lifecycle differences remain open for checkpoint I.

Read investigation adds 27 distinct numeric-window, streaming, path-variant and
Linux filesystem-error inputs before further product corrections. The corpus
now contains 238 cases: read 72, write 21, edit 33, Bash 28, grep 32, find 27,
ls 25. Eleven harness regressions pass; new oracle/control runs follow. This
input expansion establishes no new product acceptance.

The expanded same-Pi control passes 238/238 at clean harness source `abb00efc`.
The corresponding native read baseline passes 33/72. The additional
[55 source-captured text/filesystem results](builtin-coding-tool-read-text-2026-10-22.json)
combine with the earlier thirteen to cover 68 actual-function cases. The old
implementation fails 38/68. Numeric-window corrections reduce the remaining
failures to seventeen path/error cases; after those corrections, all 68 pass
and the related selection passes 230/230, zero skips.

Tool invocation accepts Pi's numeric offset/limit schema, including its slice
and fractional-display arithmetic. Existing integer public facade signatures
remain unchanged. Large files use bounded counting/selection passes on the
same open file, preserving decoded UTF-8 counts and avoiding whole-line/file
allocation. Pi's `@`, Unicode-space, file-URL and filename-fallback resolution
is implemented. Linux access errors come from the kernel's `access` check;
directories, missing targets, ancestor permissions and symlink loops retain
the pinned error categories. No permission check is bypassed.

Three additional streaming acceptance tests pass: deterministic cancellation
while counting and while awaiting selected-content I/O, and a 64 MiB single
line whose read requests never exceed 64 KiB. These are component/resource
checks, not additional pinned-Pi cancellation comparisons. A dedicated huge
line input brings the corpus to 239 cases (read 73); fresh differential evidence
follows. Image sampling/orientation, full provider image acceptance and remaining
hardening are still open, so checkpoint F is not complete.

At clean implementation source `600c151d`, the paired read run passes 72/73,
including the new 64 MiB input and filesystem state comparisons. The only
difference is resized PNG RGBA pixels. No text, filesystem error or source
fallback is normalized away. The 75-case final focused read/streaming run passes.

Image investigation freezes 28 input assets (53,081 encoded bytes), generated
with Pillow 12.1.1, JPEG 8.0, WebP 1.5.0 and zlib 1.3.1. The retained bytes make
inputs reproducible independently of future encoder versions. Forty-two new
cases cover original payload preservation, conversions, all EXIF transformations,
alpha, animation, truncated formats, extreme dimensions and payload profiles.
Original supported payloads compare byte-for-byte; selected reencodings compare
decoded RGBA while retaining MIME, hints, metadata and raw bytes. There is no
pixel tolerance in these initial comparisons. The corpus now contains 281 cases
(read 115). New image acceptance remains pending.

Pinned Photon is package 0.3.4; its actual WASM artifact embeds the build marker
`image-0.24.9`. The matching primary sampling source at
`image-rs/image@v0.24.9:src/imageops/sample.rs` has SHA-256
`3d7c373a3dd6b045805b0e9fcf37cb1b37325bea61cb1ae41b75c14503396633`.
The behavior oracle remains the pinned Pi tool and its installed pinned Photon,
not a newly compiled Rust implementation.

The image baseline at `550d5f5c` passes 89/115 read cases; its independent
same-Pi control passes 115/115. The [46 captured image results](builtin-coding-tool-read-image-results-2026-10-22.json)
reproduce 26 failures before correction. The first correction implements the
two-pass float Lanczos3 kernel, unpremultiplied RGBA sampling/encoding, JPEG/WebP
EXIF transforms, complete-decode checks, extreme-ratio source fallback and
JavaScript coordinate rounding. The image tests then pass 36/46; the ten
remaining strict failures are JPEG-related decoded-pixel differences under
investigation. Their dimensions, MIME types and notes match. No pixel tolerance
has been introduced, and checkpoint F remains open.

Skia remains the sole image decode/encode infrastructure. Float row storage is
bounded to 4 MiB; very wide inputs use direct evaluation. Weight caching is
bounded separately, and loops check cancellation. The existing 20 MiB encoded
input and 32-million-pixel safety boundaries remain. JPEG output now uses 4:4:4
sampling and ignores alpha, as confirmed from the pinned encoder and JPEG frame
headers; distinct Huffman/DCT behavior still needs assessment. Seventeen
[captured finite `toFixed` vectors](builtin-coding-tool-fixed-number-2026-10-22.json)
pass, including binary values just below decimal ties. The related first image
selection has 178 passes and ten failures, with zero skips; it is not a green
milestone validation result.

### Native JPEG assessment and payload correction

The clean `c35858a0` run retains 105/115 exact read comparisons and ten JPEG pixel
hash differences. Input decoder calibration shows a maximum two-level difference
in 13/748 channels of each frozen JPEG input; the resulting resized PNGs differ
by at most one level. Both independently captured pre-JPEG Lanczos buffers
(60x40 and 37x24) match Photon byte-for-byte.

The initial native JPEG budget path incorrectly selected quality 80 where Pi
selected quality 40. This is a product defect, not an accepted codec difference.
Skia's optimized Huffman coding made its payload smaller. A focused lossless
entropy adapter retains Skia's quantized coefficients while applying the pinned
standard tables. Both budget cases now match full quantization tables and 4:4:4
sampling. Five sizes/qualities retain every decoded pixel through transcoding;
nine adapter tests cover those, cancellation, malformed input and strict payload
boundaries. Candidates stop when they cannot fit the byte budget.

The [explicit JPEG envelope](builtin-coding-tool-jpeg-codec-2026-10-22.json)
records the requested distinct-encoder allowance for ten named cases. It checks
decoder error bounds, exact alpha/dimensions, full quantization and sampling,
and bounded distortion against the common uncompressed reference. At quality
20, reference/native MSE is 1891.198333/1888.556389; at quality 40 it is
1004.427553/1012.331832. JPEG byte or pixel identity is not asserted. Original
payloads and all other PNG pixel comparisons remain exact. Metadata, text,
filesystem, errors and cancellation are never covered by this exception.

Reports retain `strictPass`, `strictDifferences`, actual hashes and JPEG metrics
alongside contract acceptance. Eighteen harness tests reject wrong quality,
sampling, pixels, dimensions, missing/corrupt witnesses and unrelated differences.
The image/budget/existing OpenAI wire selection passes 66/66, zero skips.
Fresh committed-source paired evidence follows; Anthropic and structured-image
session persistence remain open. CI now installs the already-used Pillow version
from the explicit image-tool requirements and runs the harness guard tests.

At clean source `20d18a51`, the read corpus passes 115/115 under the documented
contract: 105 strict comparisons and ten explicitly reported JPEG equivalents.
All seven full declarations match, including output schema and sampling policy.
The same-Pi control passes 115/115 strictly, with zero codec exceptions.

Physical image tests now include Anthropic Messages alongside Responses and
Chat Completions. Three regressions reproduce lost structured-image data after
native session save/reload. The correction stores structured content in canonical
result metadata while preserving the existing text/image message representation,
and removes that annotation from provider requests. Typed result inspection and
Pi interchange can recover the structured data. The related selection passes
22/22, including all three seven-request image/reload/text-only/block/profile
workflows. Two new nullable test warnings are corrected with explicit assertions.

Four model-context inputs now cover non-vision notes, invalid-image fallback,
explicit vision support and model resize-profile precedence. Their source oracle
and native context projection reproduce three differences at clean `81d9a226`: two
missing canonical non-vision notes and a probe-only model-profile mapping gap.
Two actual MAF fail-first regressions confirm the product note defect. Read now
uses current model support for successful and omitted images, and updates it
when the selected or routed model changes. The source probe now maps the same
model profile precedence. The related image, session and protocol selection
passes 83/83. That selection also exposed a prior lifecycle-JSON regression:
structured image data leaked through the new metadata field. The lifecycle
serializer now omits the image `data` property while retaining other structured
metadata; canonical storage and explicit RPC image content retain their existing
contracts. The original in-process-image-only regression passes unchanged.
Clean source `9ebd03b5` passes 4/4 model-context cases, all strictly, with
zero build warnings/errors. Two additional resource-boundary inputs now exercise
the retained 20 MiB input and 32-million-pixel limits; actual differences remain
to be measured. Corpus inventory is 287 (read 121).
A forced restore using the writable
NuGet HTTP cache passes and clears the earlier cached audit warnings; final
warning-as-error gates have not yet been claimed. No dependency upgrade or
audit-policy change was made.

### Read cancellation and explicit safety boundaries

The [resource receipt](builtin-coding-tool-read-safety-2026-10-22.json) records
two strict differences at clean `f33ffd1f`. Pinned Pi resizes an otherwise valid
PNG with over 20 MiB of trailing input and an 8001×4000 image. PiSharp omits
both with its existing explicit omission notice. Retaining the 20 MiB encoded
input and 32,000,000 decoded-pixel bounds prevents large input/bitmap allocations
from untrusted tool content. Those two cases are excluded from strict parity;
filesystem state is unchanged. The harness keeps `pass=false`, strict differences
and both outputs, and counts intentional safety deviations separately. The
exclusion requires complete named source/native results and identical filesystem
observations; nineteen guard tests prevent broader masking.

A deterministic decoded-image barrier reproduces delayed read cancellation:
the old await cannot settle before the worker is released. The read now awaits
worker completion with the caller's cancellation token; the worker owns its
bounded bitmap/buffer and observes cancellation during cleanup. The regression
fails first and then the related read selection passes 149/149. The internal
observation callback exists solely to place the barrier after real Skia decoding;
it does not replace decoding. No timing sleep is used.

The [pinned cancellation receipt](builtin-coding-tool-read-cancellation-2026-10-22.json)
records actual Pi rejection of pending physical text/image reads before their
operation barriers are released. Reproduce with the existing source probe and
`--read-cancellation-only` using the frozen image asset fixture. These source
barriers and the native post-decode barrier are distinct stage tests; they are
not added to the direct differential case count. Both yield `Operation aborted`,
no updates and unchanged input. The [complete read receipt](builtin-coding-tool-read-acceptance-2026-10-22.json)
at clean `9f733919` records 119/121 passing contracts: 109 strict, ten named JPEG
equivalents and two explicit allocation safety exclusions, with zero unresolved
differences. Its same-Pi control is 121/121 strict. This closes the supported read
checkpoint. Broader MAF cancellation, concurrency and reliability remain M–P work.

### Write paths, permissions and alias coordination

Expanded clean baseline `509aab2d` passes 11/39 write cases. Confirmed product
failures include wrong `@`/Unicode-space/file-URL targets, read-only target
replacement, wrong Linux realpath/open errors and cancellation text. Existing
private creation modes, atomic replacement/inodes, hard links and writing an
existing file inside a read-only parent are separate safety-deviation candidates;
they are not normalized away or classified as resolved yet.

The [write regressions](builtin-coding-tool-write-regressions-2026-10-22.json)
capture actual pinned results, paths and file bytes. After correcting a test
snapshot omission of the workspace root, 14/26 regressions fail on the old
product (12 function cases and two missing-file directory-alias queue cases).
The product reuses common path resolution, checks existing target writability,
uses actual Linux `realpath` errors and retains resolved parents when queue keys
reach a missing descendant. The related write/queue/edit selection passes 52/52.
Unrelated-path execution remains independent; no global execution lock is added.

The old permission regression expected `open` for an inaccessible parent. An
[exact pinned reproduction](builtin-coding-tool-write-permission-2026-10-22.json)
proves `realpath` is the error operation. Its assertion is corrected and also
checks that no filesystem entry was created. No regression was removed or
skipped. Private new-file modes and atomic replacement remain unchanged.
Clean `401a5b26` passes 20/39 strict write comparisons; the remaining nineteen
are the retained safety differences, recorded in the [write safety receipt](builtin-coding-tool-write-safety-2026-10-22.json):
twelve private new-file modes, five inode changes, one hard-link behavior and
one writable existing file in a read-only parent. All result/side-effect
observations remain visible and `pass=false`. Frozen complete observations are
required for classification; actual outputs and ownership are never rewritten.
Expected fixture creation ownership is parameterized by the runner's effective
UID/GID, and both actual snapshots must match. Cross-owner/ACL behavior is not
newly claimed verified. Atomic replacement can change ownership/ACLs along with
the inode; strict equivalence is not claimed for those consequences.

The [coordination oracle](builtin-coding-tool-mutation-coordination-2026-10-22.json)
executes actual Pi write/write, write/edit, edit/write and edit/edit operations
behind deterministic queue barriers. Native tests use those captured call
results and final bytes and preserve progress on unrelated files. Two source
cases record write/edit cancelled-waiter timing: Pi waits for the active operation before
settling the cancelled waiter; native cancellation settles promptly without
releasing the active writer or invoking the cancelled operation. This existing
responsive-cancellation contract is retained and excluded from strict timing
parity. Native coordination of missing-file directory aliases is also stronger
than Pi's lexical fallback when `realpath` returns ENOENT. These differences are
explicit; neither is normalized out of an execution trace.

The source's seven existing mutation-queue regressions pass. The native related
selection passes 67/67 after formatting, including the focused 7/7 coordination
suite and prepared-write-failure cleanup. One fail-first regression reveals that a
prepared temporary file inherited group/world readability from its target.
Temporary contents now remain private until publication, while final target
permissions are preserved. Cancellation retains the queue until candidate
cleanup, leaves the original target intact and permits a following write safely.
The internal optional queue/commit barrier parameters preserve the default
shared coordinator and public facade. A format analyzer removed an unused xUnit
data parameter; fixture-ID validation now uses it, and the corrected selection
passes again. The [fresh write receipt](builtin-coding-tool-write-acceptance-2026-10-22.json)
at clean `a420ce25` confirms 20/39 strict passes and nineteen explicitly excluded
safety differences, with zero unresolved differences. Its same-Pi control is
39/39 strict. The supported write checkpoint is complete; broader M–P acceptance
remains mandatory. Corpus inventory is 305 (write 39, read 121).

### Edit decoding, match planning and Linux errors

Clean expanded baseline `385ac35d` passes 21/67 strict cases. A separate clean
`d1046c7b` case confirms an exact trailing-space range is incorrectly shortened
inside a fuzzy batch. The [captured edit results](builtin-coding-tool-edit-results-2026-10-22.json)
now drive actual-function regressions for text, all diff/patch fields and final
file bytes, including error cases that must leave the snapshot unchanged.
The complete pre-fix selection has seventeen failures: sixteen confirmed product
regressions and one fixture-path comparison error. The latter omitted temporary
root replacement inside a file-URL patch header and is corrected without
removing any result fields. Both raw pre-fix runs remain available locally.

The planner now follows pinned non-overlapping, fuzzy-normalized ambiguity counts
while retaining exact ranges when available, including in a fuzzy batch. As in
Pi, `aaa` with old text `aa` chooses its leftmost overlapping occurrence; all
replacements still target one snapshot, and overlaps between batch entries remain
invalid. This is a deliberate source-backed matching choice, not removal of batch
validation or atomicity. Empty normalized needles follow JavaScript split/count
behavior and preserve the original line blocks or produce the source error.

Edit reads now use Node-compatible UTF-8 replacement decoding, preserve BOM and
line endings, and check Linux read/write access before opening a readable stream.
Directories and file ancestors produce the pinned error categories. Cancellation
is checked across the existing stages and uses `Operation aborted`. An atomic
parent-permission failure names the requested target rather than leaking its
private temporary filename; the read-only-parent side-effect difference itself
remains an explicit atomicity safety candidate.

The related selection passes 327/327, including existing core conformance,
metadata/history and mutation checks. A nullable warning in the new prepared-write
cleanup assertion is corrected. Clean `2250b589` matches all 68 tool-result/diff contracts except the explicitly
retained atomic read-only-parent failure. Its 38 strict differences are thirty-six
inode changes, one hard-link behavior and that parent-permission outcome, frozen
in the [edit safety receipt](builtin-coding-tool-edit-safety-2026-10-22.json).
They remain failed strict comparisons; complete expected observations and exact
fixture ownership are required for exclusion.

A larger invalid batch at clean `9c23bd86` reproduces unstable overlap error
indices: native reports edits[0]/edits[17], while Pi reports edits[0]/edits[1].
Ties now retain the original argument order. Queued cancellation, prepared-batch
cancellation and failure cleanup now cover edit as well as write, including a
cancelled two-replacement batch. The source coordination receipt includes the
edit waiter; the related native selection first passes 331/331. Twenty harness guards
also protect edit safety exclusions against changed bytes, results, metadata,
setup and ownership. A further deterministic post-read deletion case reproduces a product race:
the old shared atomic helper recreates a parent directory removed after the edit
snapshot and publishes stale content successfully. The pinned tool rejects with
ENOENT and leaves that directory absent. Parent creation now belongs to write;
the shared atomic writer no longer recreates it. Edit maps the actual open error
to the requested path, releases its queue and permits an independent recovery
write. The regression fails before correction, then 332 related checks pass.
The source coordination receipt now includes this physical deletion/recovery
case; stage cases still do not inflate the direct corpus count. Fresh paired
The [complete edit receipt](builtin-coding-tool-edit-acceptance-2026-10-22.json)
at clean `00f91836` records 31/69 strict passes and thirty-eight explicit atomicity
exclusions, with zero unexplained differences. The same-Pi control is 69/69
strict. Write is rechecked at 20 strict passes/nineteen exclusions on this same
source. H is complete within this supported scope; M–P acceptance remains
mandatory. Corpus inventory is 341 (edit 69).

## Shared integration and safety gates

Keep the existing registry, MAF `ChatClientAgent` invocation, durable outcome
journal, tool hooks, per-file queue, native Skia pipeline and process ownership.
Installed MAF is 1.21.0; skill examples for later releases do not authorize a
dependency upgrade. No generic tool runtime or second filesystem/image framework
is planned. Search responsibilities should be extracted coherently if changed.

Acceptance additionally requires exact declarations/loadouts; invalid arguments;
head/tail UTF-8 boundaries and metadata; all seven tools through MAF; physical
Responses, Chat Completions and Anthropic tool loops and image requests; a full
read/find/grep/ls/edit/write/bash workflow; persisted correlation and restart;
deterministic cancellation/concurrency barriers; bounded reliability repeats;
full repository gates and fresh final-head push/PR CI.

## Execution accounting and continuation

Started 2026-10-22 17:44:40 UTC. Engineering is ongoing. Final active duration has
not yet been measured and must not be inferred from the eight-hour target.
Idle or externally interrupted intervals must be excluded or identified.
No engineering is claimed between the last active observation on 2026-10-22
19:07:47 UTC and resumption on 2026-10-23 07:13:30 UTC. That interval is excluded
from active engineering duration.

Stable checkpoints are A–T from the mission. A (verified merge) and B (fresh
branch), C (initial inventory), D (harness) and E (shared contracts) are complete.
F (supported read) and G (write with explicit safety exclusions) are complete;
H (edit with explicit atomicity exclusions) is complete; I (Bash) is in progress;
J–T are pending. Completing
an inventory or passing local tests does not close the milestone. PR #22 must
be published as draft and remain unmerged; PR #23 is excluded. Global Pi parity
remains `in_progress`.

No engineering is claimed between 2026-10-23 09:10:33 UTC and resumption at
2026-10-23 12:22:16 UTC; exclude that inactive interval from active duration.

## Bash investigation — updates, decoding, spawn failures and descendants

The expanded independent input corpus contains 357 cases, including 44 Bash
cases. Clean baseline `6c346e4652dddaaffdd01477b8579063d22360e7` passes 2/44
when the initial and final live-update contracts are compared. Initial empty
updates, cumulative raw snapshots and truncation details account for most
failures. Separate confirmed defects include inherited stdin, retained BOMs,
NUL commands being partially executed, and `setsid` masking executable errors.

The [18 pinned update observations](builtin-coding-tool-bash-updates-2026-10-22.json)
fail before correction and pass afterwards. The
[eight pinned spawn failures](builtin-coding-tool-bash-spawn-2026-10-22.json)
verify actual registered-function rejection and the initial empty update.
Native JSON, RPC, MAF lifecycle and transcript rendering retain cumulative
snapshots while direct Bash callbacks retain their presentation deltas.
The related selection passes 139 checks before virtual-clock expansion, then
157/157 after deterministic coalescing, spill and decoder lifecycle checks.

Two controlled Linux fixtures create a child and a grandchild that starts a
separate session. Caller cancellation and AbortBash both return while the
grandchild is still alive before the correction. Walking descendants before
killing the process group fixes both regressions and preserves session recovery.
The [actual pinned process observation](builtin-coding-tool-bash-processes-2026-10-22.json)
shows that Pi also leaves this detached grandchild sleeping at settlement. The
native cleanup is an intentional stronger safety behavior. The oracle and native
regression both explicitly clean up all recorded fixture PIDs after observation.
No PID values are used as expected output.

Checkpoint I remains in progress. Fresh clean-head paired/control evidence,
session environment, timeout/signal/shutdown behavior, physical provider loops
and repeated reliability acceptance are still required.

### Bash continuation, session environment and application shutdown

Clean `b46aff69571749b11247b67bcf27e71d82cceab0` passes all 44 direct
Bash cases; the independent same-Pi control passes 44/44. The compact
[stream/spawn acceptance](builtin-coding-tool-bash-stream-acceptance-2026-10-22.json)
retains source SHAs, declaration results and normalization policy. This evidence
covers settled initial/final snapshots, not identity of every intermediate chunk.

[Four physical pinned environment observations](builtin-coding-tool-bash-environment-2026-10-22.json)
exercise current selection, changed selection, a new session with optional values
absent and execution without tool context. All five inherited PI_* variables are
seeded with fixture-only stale values. Two actual MAF regressions find that
PiSharp captures old provider/model/reasoning values at run start. Resolving the
metadata at each tool execution corrects both. Three tests then verify model and
reasoning transitions, shared-runner new sessions, optional metadata clearing and
the separate direct-Bash contract. The related transition selection passes 29
checks; the complete new environment class passes 3/3.

A separate CLI subprocess fixture creates child and detached-grandchild processes
before EOF, SIGTERM or SIGHUP. Before correction, EOF cleans up but both signals
leave descendants running (two failing regressions). The existing ProcessLifetime
owner now tracks active shell/direct processes. Application exit performs cleanup;
the CLI handles SIGTERM/SIGHUP with cleanup and Pi-compatible 143/129 exit codes.
All three shutdown regressions pass, and the combined environment/lifetime/shutdown
selection passes 8/8. Actual pinned CLI comparisons show native exit codes 0,
143 and 129 with all fixture descendants terminated. Pinned Pi terminates its
original group but leaves the detached grandchild sleeping in all three cases.

Intentional strict-equivalence exclusions: detached-grandchild cleanup is stronger
in PiSharp; on RPC EOF PiSharp preserves its existing explicit cancelled response
while Pi closes without a response. Raw response bytes and process states remain
in the physical report. No successful tool outcome is invented, and no process
state or response is removed from comparison. SIGTERM/SIGHUP response streams are
empty in both products for this fixture.

Reproduce with `python3 tools/parity/builtin-coding-tool-shutdown.py --pi /tmp/pi-pinned-source
--output artifacts/parity/<run>/shutdown.json`. All child PIDs are observed before
explicit fixture cleanup. Five selected upstream Bash persistence/failure/output
regression files also pass 25/25 without skips. Fresh committed-head acceptance and
shared M–P gates remain pending; checkpoint I is still in progress.

The expanded warning-as-error Bash/shell/protocol/selection/extension execution
selection passes 196/196 with zero failures/skips. Active-process tracking is
removed in finally even when output cleanup throws.

### Bash checkpoint acceptance

Clean `011728fb84e998b7dec0da3b056ea0c307c3af73` passes 44/44 direct cases
and its 44/44 strict same-Pi control. The [final Bash acceptance](builtin-coding-tool-bash-acceptance-2026-10-22.json)
records that source and the verified component evidence. The
[fresh physical CLI shutdown report](builtin-coding-tool-bash-shutdown-2026-10-22.json)
retains actual response bytes, live/terminated process states, exit codes and the
three named strict-equivalence exclusions. Final owner-cleanup/extension checks
pass 12/12 after the 196-check broader selection. I is complete for the defined
supported Linux checkpoint. Shared provider/history/reliability gates M–P remain
open. Progress is now 9 of 20 checkpoints complete; J is in progress.

## Grep correction in progress

Clean input source `bf9316fa` expands grep to 67 direct cases. The same-Pi
control passes 67/67; native baseline passes 39/67, and 28 actual-function C#
regressions reproduce the same mismatches. Confirmed gaps include Rust regex
syntax, positive/negative globs, CR/BOM/context semantics, fractional parameters,
pre-cancellation, a 12 MiB single matching line, and a match beyond 20,000 files.
The first adapter passes 64/67; fractional context and broken-symlink handling
are corrected. The related selection now passes 95/95 without skips.

The bounded adapter reuses DirectProcessExecution and ProcessLifetime. It keeps
rg's JSON protocol and matching/ignore engine, projects only displayed fields,
skips huge submatch arrays, retains at most the displayed 500 line characters,
and counts full formatted output while retaining only the 50 KiB head. Context
reading is bounded and supports BOM/CR normalization and pinned fractional rows.
The [pinned result fixtures](builtin-coding-tool-grep-results-2026-10-22.json)
retain complete expected results. No differential sorting policy was introduced.

The [actual CPU-profile order observation](builtin-coding-tool-grep-order-2026-10-22.json)
records eight pinned executions with 24 CPUs. Those consistently produce the
alternate complete output seen in native regression execution; two-CPU oracle
runs consistently produce the other observed order. The C# regression accepts
only these actual complete source-observed texts. The differential still compares
ordering exactly within its controlled two-CPU profile.

The existing repository-local core.excludesFile guard is preserved. Real pinned
execution includes two files that the native implementation has historically
excluded as private. The [explicit safety witness](builtin-coding-tool-grep-safety-2026-10-22.json)
retains both full outputs, unchanged filesystem state, setup and meaningful
ownership/mode fields. That named case remains a failed strict comparison and
does not increase the passed count. Its .ignore rescue rules still work. CI now
installs rg/fd system dependencies explicitly; no NuGet packages are upgraded.

J remains open for malformed output, cancellation and context/failure hardening,
fresh committed-head comparisons and shared M–P acceptance. An interruption
between 2026-10-23 13:57:24 and 17:28:07 UTC is excluded from active engineering
time; it is not productive work toward the eight-hour target.

### Grep process protocol and real cancellation

The [pinned process/protocol fixture](builtin-coding-tool-grep-protocol-2026-10-22.json)
explicitly substitutes a synthetic rg executable for malformed-output/spawn tests.
It is separate from the real-rg corpus and never contributes to its case count or
version claims. The first 19 cases expose eight actual native failures. The
incremental reader now validates JSON structure, literals/numbers, escapes,
physical line framing and EOF before accepting a match; malformed output is
ignored as in pinned Pi. Spawn validation reuses the existing helper, moved to
ProcessSpawnValidation, and preserves Pi's error wrapper.

Five Unicode/duplicate-field cases expand that fixture to 24; three additional
NUL/oversized-argument cases fail before narrow error-projection corrections and
pass after them. The resulting 27 process cases and 67 actual-input regressions,
plus real cancellation, pass 95/95. Byte chunks of sizes 1, 7 and 8,192 verify
Unicode and JSON parsing independently of pipe read boundaries; the earlier
combined chunk/protocol selection passes 84/84. A streamed 12 MiB line reproduces
the pinned prefix while allocating under 256 KiB in the reader.

The same source fixture then removes the synthetic executable and runs real
rg 15.2.0 on a private Linux FIFO. Writer-open completion proves rg has opened
the input, while withholding bytes holds the read. Another real search completes
before cancellation; abort settles with Operation aborted, its one observed rg
process is gone, no updates are emitted, and another search succeeds afterwards.
The native actual-function fixture verifies the same barrier, independent search,
process termination and recovery without sleeps. No product global lock is added.

All 21 comparison guards pass, including rejection of changed result lines,
metadata, ownership, file hashes and inputs in the single exclusion-safety case.
Fresh committed-head real-rg evidence remains required before J is complete.

The final focused warning-as-error selection passes 427/427, including real
inputs, synthetic process boundaries, chunked decoding, bounded allocation,
real cancellation, old search tests, shared truncation and process compatibility.
No existing tests are removed or skipped. J awaits fresh committed-head evidence.

At clean `908c708e`, the real-rg run passes all 67 strict-equivalence cases and
the same-Pi control passes 68/68. The privacy witness initially stays unresolved
because pinned Pi returns its five matching lines in another observed order;
native's three permitted lines and every filesystem field remain unchanged.
That second complete actual Pi observation is retained in the named safety
witness, with its source SHA/report. Its guard accepts only the recorded complete
source outcomes and exact native observation; line/order comparison elsewhere
remains exact. All 21 guards pass, including rejection of changed source text.

### Grep checkpoint acceptance

Clean `6b0017d4` passes 67/68 strict cases, with one explicitly excluded
repository-privacy case and zero unresolved differences. Its same-Pi control
passes 68/68. The [final grep acceptance](builtin-coding-tool-grep-acceptance-2026-10-22.json)
records actual source hashes and case classifications. J is complete for the
defined supported Linux contract; shared M–P acceptance remains open. Progress
is 10 of 20 checkpoints complete; K (find) is in progress.

## Find correction in progress

Clean input source `d27acc4e` has 61 meaningful find cases. The same-Pi control
passes 61/61; native baseline passes 37/61 and all 24 mismatches reproduce in the
expanded C# search fixture. The fd adapter reuses the process owner, bounded
output builder and private-exclusion guard. It preserves fd smart-case/glob
semantics, native path spelling and output ordering, handles zero/invalid limits,
and removes the 20,000-path inventory loss. The first actual-function selection
passes 128/128 (67 grep plus 61 find). The
[pinned find results](builtin-coding-tool-find-results-2026-10-22.json) retain
full outputs, metadata, errors and reproducible inputs.

The older search/process selection passes 186/188. Both failures expect a slash
for a directory symlink, while the independent find-symlink-directory source
case returns its link name without a slash. Those two theory expectations are
corrected from the source evidence; link presence and no-follow behavior remain
asserted. K stays open for process/protocol/cancellation hardening and fresh
committed-head acceptance; shared M–P gates also remain open.

The independent [fd process fixture](builtin-coding-tool-find-protocol-2026-10-22.json)
contains 27 cases for framing, Unicode whitespace, ordering, partial failures,
limits and spawn/argument errors. The shared grep/find protocol selection passes
54/54. [Real fd cancellation](builtin-coding-tool-find-cancellation-2026-10-22.json)
uses 20,050 files and cancels at the first observed live-process output; the child
terminates, input files remain unchanged and another actual tool call succeeds.
The two real search cancellation cases plus protocol cases pass 56/56.

Two preparation-cancellation regressions fail before the privacy guard maps its
abort consistently, then pass. A directory-symlink privacy regression also fails
before classification is corrected, then passes. The three focused cases pass
3/3; the related warning-as-error selection passes 276/276. The retained
[find privacy difference](builtin-coding-tool-find-safety-2026-10-22.json) is an
explicit strict-parity exclusion: native omits previously private file/directory
paths while keeping .ignore rescue rules. It stays a failed strict comparison.
All 22 comparison guards pass, including full-observation checks for that case.
There are now 62 find cases and 428 total; fresh clean-head acceptance remains.

Another 60 checks feed the pinned fd protocol outputs in 1-, 7- and 8,192-byte
chunks, preserving UTF-8, CR/LF framing, whitespace, path order and EOF behavior.

### Find checkpoint acceptance

Clean `99016879` passes 61/62 strict cases with one named privacy exclusion and
zero unresolved differences. The strict same-Pi control passes 62/62. The
[find acceptance](builtin-coding-tool-find-acceptance-2026-10-22.json) records
source hashes and individual classifications. K is complete for the defined
supported Linux contract. Shared M–P acceptance remains pending. Progress is
11 of 20 checkpoints complete; L (ls) is in progress.

## Ls investigation

The expanded [source-pinned ls fixture](builtin-coding-tool-ls-results-2026-10-22.json)
has 51 distinct cases. The initial implementation passes 29/51; the independent
strict same-Pi control passes 51/51. Twenty-two mismatches cover Unicode/stable
case ordering, numeric limits, broken links/stat failures, normalized paths,
Linux permission messages and pre-cancellation. These actual source observations
are committed before the implementation correction. The corpus now has 454 cases.

Two additional physical Unicode cases distinguish contextual Greek lowercasing
and stable ordering of canonically ignorable supplementary scalars. The old
implementation passes 1/2 and the strict source control passes 2/2. With these
cases, ls has 53 meaningful cases and the complete corpus has 456.

The correction retains the public constructor and int-limit facade while the
registered function accepts the source numeric contract. A focused ordering
component applies JavaScript lowercase semantics before locale comparison, with
libuv scalar/UTF-8 order breaking ties. Broken/unsearchable entries are skipped
before consuming the entry limit. Normalized paths, Linux permission errors,
head truncation and structured details match the pinned observations. Output
stays bounded while all selected entries contribute to truncation metadata.

The [ls cancellation fixture](builtin-coding-tool-ls-cancellation-2026-10-22.json)
holds an actual directory snapshot before fulfillment through the upstream
operations hook. Abort settles before release; an independent listing and later
continuation succeed, with both files unchanged. Native verifies the same
barrier and outcomes. The source-backed search/list, ls cancellation and related
registered-tool selection passes 211/211 with warnings as errors. The previous
51-case regression selection had 22 failures and 157 passes across all tools.
Fresh committed-head ls acceptance is still required.

### Ls checkpoint acceptance

Clean `a68d365c` passes 53/53 strict ls cases and 53/53 same-Pi controls. The
[ls acceptance](builtin-coding-tool-ls-acceptance-2026-10-22.json) retains source
hashes and classifications. Related checks pass 211/211, plus 40 older selection
and result tests. L is complete for the defined supported Linux contract.
Shared M–P acceptance remains open. Stable progress is 12/20; M is in progress.

## Tool integration in progress

The [pinned Agent workflow](builtin-coding-tool-agent-workflow-2026-10-22.json)
executes all seven tools in nine actual core-agent calls: read, find, grep, ls,
edit, failed edit, write, bash verification and final read. Its deterministic
provider observes ten requests. Actual SessionManager save/open retains all nine
correlated outcomes and a resumed agent receives them without executing tools.
Seven additional source agent loops capture recoverable errors, one per tool.
This is an in-process provider fixture; physical HTTP remains checkpoint N.

The native MAF workflow initially fails: after the failed edit, the model sees
`Error: Function failed.` instead of the pinned actionable error text. The
failed durable event also lacks result text. This is a confirmed integration
defect beyond direct-function tests. The correction returns the built-in failure
contract while preserving its failed durable status and the normal cancellation
path. The fresh seven-tool error, history and provider checks remain open.

The new MAF checks now pass 15/15: the complete workflow with actual native
ConversationStore save/load, seven independent source-pinned recoverable errors,
and seven excluded built-ins after restoring a previous complete loadout. They
verify exact result text and metadata, call/result IDs, update ordering, failed
mutation state, provider continuation and absence of repeated results on restart.
The excluded-call checks assert unavailability and no side effect; they do not
claim exact generic framework error wording for unknown tools.

Expected built-in ToolFailureException outcomes now return an explicit failed
result with actionable text and empty error details, while the durable outcome
retains the exception. Unexpected exceptions and cancellation retain their
existing paths. The workflow's new-file mode difference remains the previously
documented private-creation safety exclusion (0600 native versus 0644 pinned);
final file contents match. No tool-history metadata is projected as provider text.

Six pinned upstream selection/prompt regression files pass 37/37 with no skips,
covering defaults, exclusions, extension allowlists, no-builtin-tools and
next-turn activation. Physical provider requests remain an independent N gate.

### Tool integration checkpoint acceptance

The focused tool/lifecycle/extension/recovery selection passes 825/825, including
the 15 new workflow/error/exclusion checks, with zero failures/skips. The source
selection tests pass 37/37 across six files. M is complete for its defined schema,
selection, MAF lifecycle and canonical persistence contract. Physical HTTP and
final cross-tool reliability remain N–P gates. Progress is 13/20 complete.

## Physical provider workflow investigation

The [initial physical captures](builtin-coding-tool-provider-before-2026-10-22.json)
run actual pinned/native RPC CLIs against local HTTP Responses, Chat Completions
and Anthropic endpoints. Each product completes nine tool calls in ten requests,
then exits and restarts its CLI; the eleventh request contains the saved nine
results without replaying tools. Final file bytes match; native's private new
file mode remains the already named safety exclusion.

These captures reveal defects not covered by the earlier in-process checks:
live write confirmations acquire JSON quotes before restart, Chat declarations
retain additionalProperties=false for the three optional tools, Responses
retains adapter schema defaults, and RPC history uses generic tool names and
omits result details. The full raw HTTP/RPC captures remain in the ignored
artifact directory; the compact before receipt retains actual observations.
N remains open while focused regressions and narrow corrections are completed.

Nine focused regressions fail on the before implementation and pass after the
provider/history corrections: JSON-string output, Chat/Responses object schema
defaults, result details in RPC/JSONL and tool-name correlation even with reused
legacy call IDs. The physical comparison checks full provider declarations
(including protocol flags), every continuation result, lifecycle order, canonical
call/result correlation and metadata, restart stability and workspace inventory.
All 27 comparison guards pass and reject changed schemas, routes, result text,
IDs, names, details, file bytes/inventory and updates after completion. The private
new-file mode remains a recorded strict filesystem difference.

The first broader selection passes 1,017/1,018. Its one failure catches a change
to an extension hook's JSON-string representation. The correction is narrowed
to results correlated with the seven reserved built-in tool names, preserving
the existing extension contract and its test. Fresh scoped/broad checks and
clean-head physical acceptance remain required before N is closed.

The narrowed extension/built-in selection passes 21/21 and the final broad
provider/RPC/JSONL/tool selection passes 1,018/1,018 with zero failures/skips.
The [source provider workflow fixtures](builtin-coding-tool-provider-results-2026-10-22.json)
retain all declarations including protocol flags, every result continuation,
canonical calls/results and lifecycle events for all three APIs. Each comparison
passes its supported contract with only the named private-file creation mode
excluded from strict filesystem equivalence. A same-Pi control also passes;
its first route assertion was corrected to include the actual Anthropic
?beta=true suffix observed on both sides. Fresh clean-head runs remain required.

### Physical provider checkpoint acceptance

Clean `75e02e64` verifies all three [physical provider workflows](builtin-coding-tool-provider-acceptance-2026-10-22.json).
Each side makes 11 actual HTTP requests, executes nine calls across seven tools,
then exits/restarts its CLI and retains all nine authoritative outcomes. Full
declarations, text, errors, history metadata and workspace inventory match.
The generated file mode remains an explicit failed strict filesystem comparison
(0644 pinned/0600 native). Strict same-Pi controls pass 3/3. N is complete for
its supported contract. Progress is 14/20; O full corpus is in progress.

## Complete corpus execution

At clean `75e02e64`, all 456 cases execute: 395 pass (385 strict plus ten
explicit JPEG codec equivalents), 60 match reviewed safety exclusions, and one
privacy-exclusion case remains initially unclassified. The strict same-Pi
control passes 456/456. The unclassified case contains the same three native
permitted grep lines in a newly observed order; bytes, metadata, ownership and
filesystem state are unchanged. Its complete actual Pi/native observation pair
is retained in the existing privacy witness without sorting. This case remains
strict=false and pass=false; only its safety classification changes. All 27
comparison guards continue to pass, including rejection of extra leaked lines.

### Complete corpus checkpoint acceptance

Clean `acfbe782` has 395/456 passes (385 strict plus ten JPEG equivalents),
61 named safety exclusions and zero unresolved cases. The strict source control
passes 456/456. The [complete corpus receipt](builtin-coding-tool-corpus-acceptance-2026-10-22.json)
retains each case classification and source SHA. Counts per tool are read 121,
write 39, edit 69, bash 44, grep 68, find 62 and ls 53. O is complete for
this defined corpus; P stress/safety is in progress. Progress is 15/20.

## Concurrent workflow and compaction hardening

The [pinned concurrent agent fixture](builtin-coding-tool-concurrent-agent-2026-10-22.json)
executes eight calls in one batch. Bash blocks on a real FIFO while two unrelated
writes finish; reads and searches run alongside it. Provider continuation retains
the original call order. Actual pinned session save/open and compaction retain all
eight outcomes at an assistant boundary. The native MAF workflow reproduces the
same barriers, file bytes, correlated results and persisted context.

This exposed an RPC projector restriction to user boundaries although the native
planner accepts user and assistant boundaries. The regression fails before the
focused correction. A related existing repeated-read tool loop also failed because
the context budget serialized result wrappers and presentation metadata that the
provider never receives. The estimate now uses the existing provider projection;
raw canonical results and bounded history fingerprinting remain unchanged. The
new presentation-details regression fails before that correction.

The corrected compaction/RPC/concurrent-workflow selection passes 114/114, with
zero failures/skips. One interim assertion incorrectly compared completion order
with canonical call order; it was corrected against the actual pinned fixture.
Raw failing and passing TRX files remain under the milestone artifacts directory.
The physical cancellation driver can additionally abort after a non-idempotent
Bash append; its three API comparisons pass with one byte preserved after restart.
Clean-head acceptance and five bounded reliability repetitions remain pending.

### Search reliability: independently observed encounter-order alternatives

The first bounded reliability attempt stopped after 258 passes and four failures,
all exact search-order expectations. It left no full-output artifacts. The raw
failed TRX/report are retained in `reliability/`, without overwriting the attempt.
Actual pinned grep/find were then executed 64 times for each of five existing
cases on both two-CPU and 24-CPU profiles. These 640 observations do not increase
the 456 distinct-case count. All four differing native results also occur in Pi.

The [source witness](builtin-coding-tool-search-order-2026-10-22.json) retains
complete ordered results and per-profile frequencies. Original expected results
remain unchanged; regression fixtures additionally accept only complete results
actually observed from the pinned implementation, including its variable first
result under `find`'s limit. A provenance assertion checks identical setup,
content and details for every accepted alternative. No sorting, inferred
permutation or relaxed differential comparison was introduced. All 182 search
regressions/provenance checks now pass. Five fresh reliability repetitions remain
pending on the updated, committed fixtures.
