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

Stable checkpoints are A–T from the mission. A (verified merge) and B (fresh
branch) and C (initial inventory) are complete. D–T are pending. Completing
an inventory or passing local tests does not close the milestone. PR #22 must
be published as draft and remain unmerged; PR #23 is excluded. Global Pi parity
remains `in_progress`.
