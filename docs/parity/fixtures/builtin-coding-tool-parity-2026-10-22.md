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
Fresh clean-source paired evidence is next. Corpus inventory is 285 (read 119).
A forced restore using the writable
NuGet HTTP cache passes and clears the earlier cached audit warnings; final
warning-as-error gates have not yet been claimed. No dependency upgrade or
audit-policy change was made.

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
F (read) is in progress; G–T are pending. Completing
an inventory or passing local tests does not close the milestone. PR #22 must
be published as draft and remain unmerged; PR #23 is excluded. Global Pi parity
remains `in_progress`.
