# Multimodal User Input and Image Parity

## Scope and pins

The upstream reference is Pi `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
PR #25 is merged at `897bc5778f87ed09ea42e48b260479ee6a6a218c`; this milestone
branch is based on that commit. Draft PR #26 is open and unmerged. Its previous
published head before the current evidence follow-up is
`bacbdc47bba64e599beaff3ff51976cbe7b63738`; the current head is tracked in the
PR description. The Pi pin has
not moved.

## Current evidence

The direct `processImage` differential passes **13/13 strict cases** and **5/5
codec-equivalent cases**, with one documented safety difference. Pinned Pi
output is stable across two runs, including encoded bytes. Strict cases cover
PNG pass-through, resize-disabled pass-through, valid JPEG/GIF/WebP pass-through,
and malformed/truncated PNG/JPEG/GIF/WebP inputs. Calibrated cases cover
model-limited PNG/JPEG/GIF/WebP resize and BMP-to-PNG conversion; they compare
MIME, decoded dimensions and processing hints rather than codec bytes. Pi accepts
a WebP whose RIFF length exceeds available data while Skia rejects it; PiSharp
omits this malformed input rather than forwarding it. See the [machine-readable report](multimodal-image-differential-2026-10-25.json)
and its [reproducible runner](../../../tools/parity/multimodal-image-differential.py).

The pinned `processFileArguments` plus `buildInitialMessage` differential passes
7/7 application cases with stable two-run Pi output. It checks mixed UTF-8 text,
Unicode and spaced paths, content-sniffed PNG under a `.data` suffix, duplicates,
empty files, BMP conversion, JPEG/GIF/WebP pass-through, a 256-image sequence,
hints, image order, prompt composition, and relative, absolute, home-expanded
and symlinked image paths. Its first run reproduced an
extra blank line before the prompt in PiSharp; removing that separator now
matches pinned Pi. PNG bytes match strictly; BMP conversion is compared by MIME
and decoded dimensions. See the [file-input report](multimodal-file-argument-differential-2026-10-25.json)
and [runner](../../../tools/parity/multimodal-file-argument-differential.py).

A separate provider-context recovery differential passes **1/1** and is stable
across two pinned Pi runs. It follows Pi's deferred path (`autoResizeImages`
disabled during file expansion, then image normalization after model selection)
for a truncated PNG followed by a valid PNG. Both sides omit the truncated image
with the same diagnostic and preserve the later image with matching MIME and
bytes. This mode alignment matters: direct file expansion with early resizing
has a different hint placement, but it is not the CLI print/JSON/RPC path under
test here. See the [recovery report](multimodal-file-recovery-differential-2026-10-25.json)
and [runner](../../../tools/parity/multimodal-file-recovery-differential.py).

Physical provider-wire comparisons pass **3/3 protocols**: OpenAI Responses,
OpenAI Chat Completions and Anthropic Messages. Each comparison uses a pinned Pi
provider stream and a PiSharp MAF `ChatClient` HTTP boundary; path, text/image
ordering, MIME and encoded image data match, and each Pi request is stable over
two runs. See the [provider-wire report](multimodal-provider-wire-differential-2026-10-25.json)
and [runner](../../../tools/parity/multimodal-provider-wire-differential.py).

Saved-session continuation passes **3/3 strict comparisons** against pinned Pi:
initial provider content, restored canonical user content and continuation
provider content preserve ordered text and exact image MIME/data after reopening
the session. Pinned Pi is stable across two independent runs. A separate
post-restart physical provider-wire differential passes **3/3 protocols** and
is stable over two pinned Pi runs. It branches after a base turn, verifies the
selected branch's image reaches OpenAI Responses, Chat Completions and Anthropic
after saving/reopening, and verifies an image on the inactive branch is absent.
See the [session report](multimodal-session-continuation-differential-2026-10-25.json),
[provider-wire report](multimodal-session-provider-wire-differential-2026-10-25.json),
and [runners](../../../tools/parity/multimodal-session-continuation-differential.py).

The session probes use temporary empty generated model-catalog files only when
the pinned checkout lacks them. The sessions register a faux provider; catalog
contents and catalog behavior are not under test.

Clipboard behavior passes **4/4 comparisons** against pinned Pi with stable
two-run calibration. Three cases cover Wayland PNG preference over JPEG, an
empty Wayland clipboard avoiding stale X11 contents, and Wayland failure
falling back to X11; the fourth confirms native file paths take precedence over
image and text fallback. Command order, selected MIME, path insertion and exact
PNG bytes match. This does not exercise a live desktop clipboard. See the
[clipboard report](multimodal-clipboard-differential-2026-10-25.json) and
[runner](../../../tools/parity/multimodal-clipboard-differential.py).

JSON-mode positional input and RPC image prompts pass **2/2 strict comparisons**
against pinned Pi with stable two-run calibration. The provider-visible text,
image ordering, MIME and exact PNG data match for both interfaces. The JSON
case sends a truncated PNG before a valid PNG and confirms the omission note
and surviving image match through file-reference prompt composition. The RPC
case exercises an image-bearing prompt command. See the
[protocol report](multimodal-protocol-image-differential-2026-10-25.json) and
[runner](../../../tools/parity/multimodal-protocol-image-differential.py).

Pinned Pi's `AgentSession` test
`agent-session-prompt.test.ts: uses the model selected by before_agent_start
for image normalization` passes 1/1. The pinned test verifies that the selected
model's resize profile reaches `processImage` and that normalized image data is
stored in the user session message. The related 12 pinned image, clipboard,
blocking, tool-result and prompt suites pass 96/96 at the same SHA. The temporary
checkout required empty generated model-catalog placeholders because the
catalog data files were unavailable; this does not validate catalog contents.
PiSharp now normalizes accepted prompt images after its before-agent-start
handlers and before conversation persistence. The regression also verifies a
profile change from a before-agent-start handler.

Focused PiSharp coverage includes positional image detection and bounded reads,
image processing, interactive input, JSON and RPC prompts, provider loopback
serialization, clipboard fallbacks, image blocking and session continuation. A
CLI process test verifies that print mode sends an `@file` image and prompt text
in the same provider request. The current focused input/clipboard/JSON/RPC/print
selection passed 35/35 with zero skips; the saved-session continuation
regression and deferred positional recovery test pass 1/1 each. Pinned Pi's clipboard, clipboard BMP,
print-mode and agent-session prompt selections passed 51/51 at the configured
SHA. An exploratory RPC semantics batch had 8 setup failures because the
temporary checkout has no generated model-catalog entries; those failures
occurred before image handling and are not counted as acceptance evidence.
The current full .NET suite passes **3,886/3,886** with zero failures or skips.
It includes the deferred image-file regression, a concurrent loopback-listener
allocation regression, and deterministic clock injection for an existing
terminal elapsed-time assertion. The focused image-file class passes 8/8, and
the combined listener/virtual-model/timer selection passes 4/4.
`TerminalClipboardTests` passes 30/30 on Linux, including a fake-runner check of
the Windows STA command and temporary-file cleanup. Format verification and a
warning-as-error build pass with zero warnings and errors. Python parity tests
pass 27/27; terminal Python passes 66/66; terminal Node passes 24/24.
MCP conformance reports no regressions against its classified baseline; three
pre-existing cases remain classified. Documentation paths pass for 154
documents and 672 references.

## Corrections recorded

- Positional images are detected from file content, bounded while reading, and
  converted when needed; oversized files produce an omission note while the
  rest of the prompt continues.
- Clipboard native errors are no longer treated as a successful empty result,
  preventing an image-read failure from silently falling through to text.
- Prompt image normalization now occurs before canonical persistence, matching
  Pi's model-selected resize, conversion, omission and hint behavior. The MAF
  agent pipeline and physical-provider projection remain in place.
- Initial prompt file text and the first user message now concatenate using the
  pinned `buildInitialMessage` contract, without an extra blank separator.
- Model image resize settings and `autoResizeImages` reach prompt and image-tool
  processing.
- Deferred selected-model normalization omits a truncated positional image and
  preserves a following valid image at provider context, matching the pinned
  CLI path.

## Limits and remaining work

The paired file, provider, clipboard and protocol reports do not establish
complete application parity. Corrupt-format variants beyond the tested
malformed and truncated cases, combined/request limits and non-cancellation
temporary-write failures remain outside the focused evidence. The paired file
corpus preserves 256 image attachments without truncation. Local tests reject
excessive decoded dimensions before pixel allocation and observe cancellation
before decoding and after pixel decoding. A focused test confirms cancellation
deletes a partially written clipboard image. macOS and Windows CI compile
native clipboard scripts and exercise platform-specific cleanup contracts; live desktop clipboard access
is unavailable on hosted runners and is not claimed. Previous head `f734c0c8`'s push
run `40196042863` and PR run `40196046840` both failed in the protocol image
oracle because that runner did not create generated model-catalog files in the
disposable Pi checkout; platform and MCP jobs passed. The protocol runner now
uses the same temporary catalog helper as the other pinned oracles, and its
local differential passes 2/2. The correction is published at `bacbdc47` and
passes exact-head push and PR runs `40196652776` and `40196656515`. The expanded
file differential passes 7/7 locally, including a 256-image sequence,
JPEG/GIF/WebP pass-through and relative, absolute, home-expanded and symlinked
paths. The processor differential passes 13/13 strict and 5/5 codec-equivalent
cases, covering pass-through and model-limited processing for PNG/JPEG/GIF/WebP,
malformed-format handling, and one documented stricter WebP rejection. The
partial-file cancellation
test passes 1/1. These extensions are still in the worktree. Fresh exact-head CI
after publishing them,
the final PR description update and broader adversarial acceptance cases remain.

Overall Pi feature parity remains `in_progress`. This receipt will be updated
with the published draft PR and exact-head workflow results after the remaining
acceptance gates pass.
