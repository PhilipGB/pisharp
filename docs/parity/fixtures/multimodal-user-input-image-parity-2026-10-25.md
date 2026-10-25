# Multimodal User Input and Image Parity

## Scope and pins

The upstream reference is Pi `b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6`.
PR #25 is merged at `897bc5778f87ed09ea42e48b260479ee6a6a218c`; this milestone
branch is based on that commit. Draft PR #26 is open and unmerged; current
published head is `73cdce52f5f911bf04f800af20dffd98c4c5fcf4` before the focused
clipboard follow-up is committed. The Pi pin has not moved.

## Current evidence

The direct `processImage` differential passes **3/3 strict cases** and **2/2
codec-equivalent cases**. Pinned Pi output is stable across two runs, including
encoded bytes. Strict cases cover unchanged PNG pass-through, resize-disabled
pass-through and a truncated PNG omission. Calibrated cases cover model-limited
PNG resize and BMP-to-PNG conversion; they compare MIME, decoded dimensions and
processing hints rather than codec bytes. See the [machine-readable report](multimodal-image-differential-2026-10-25.json)
and its [reproducible runner](../../../tools/parity/multimodal-image-differential.py).

The pinned `processFileArguments` plus `buildInitialMessage` differential passes
2/2 application cases with stable two-run Pi output. It checks mixed UTF-8 text,
Unicode and spaced paths, content-sniffed PNG under a `.data` suffix, duplicates,
empty files, BMP conversion, hints, image order and prompt composition. Its first
run reproduced an extra blank line before the prompt in PiSharp; removing that
separator now matches pinned Pi. PNG bytes match strictly; BMP conversion is
compared by MIME and decoded dimensions. See the [file-input report](multimodal-file-argument-differential-2026-10-25.json)
and [runner](../../../tools/parity/multimodal-file-argument-differential.py).

Physical provider-wire comparisons pass **3/3 protocols**: OpenAI Responses,
OpenAI Chat Completions and Anthropic Messages. Each comparison uses a pinned Pi
provider stream and a PiSharp MAF `ChatClient` HTTP boundary; path, text/image
ordering, MIME and encoded image data match, and each Pi request is stable over
two runs. See the [provider-wire report](multimodal-provider-wire-differential-2026-10-25.json)
and [runner](../../../tools/parity/multimodal-provider-wire-differential.py).

Saved-session continuation passes **3/3 strict comparisons** against pinned Pi:
initial provider content, restored canonical user content and continuation
provider content preserve ordered text and exact image MIME/data after reopening
the session. Pinned Pi is stable across two independent runs. This uses a local
provider stub and does not claim physical HTTP wire verification after restart.
See the [session report](multimodal-session-continuation-differential-2026-10-25.json)
and [runner](../../../tools/parity/multimodal-session-continuation-differential.py).

Linux clipboard acquisition passes **3/3 strict comparisons** against pinned
Pi with stable two-run calibration. The cases cover Wayland PNG preference over
JPEG, an empty Wayland clipboard avoiding stale X11 contents, and Wayland
failure falling back to X11. Command order, selected MIME and exact PNG bytes
match. This does not exercise a live desktop clipboard. See the
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
regression passed 1/1. Pinned Pi's clipboard, clipboard BMP,
print-mode and agent-session prompt selections passed 51/51 at the configured
SHA. An exploratory RPC semantics batch had 8 setup failures because the
temporary checkout has no generated model-catalog entries; those failures
occurred before image handling and are not counted as acceptance evidence.
The fresh .NET suite passes **3,881/3,881** with zero failures or skips.
`TerminalClipboardTests` passes 30/30 on Linux, including a fake-runner check of
the Windows STA command and temporary-file cleanup. Format verification and a
warning-as-error build pass with zero warnings and errors. Python parity tests
pass 27/27; terminal Python passes 66/66; terminal Node passes 24/24.

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

## Limits and remaining work

The paired file, provider, clipboard and protocol reports do not establish
complete application parity. Physical provider-wire continuation after
restart, active/inactive branch checks and additional adversarial cases remain
open. macOS and Windows CI compile their native clipboard scripts and exercise
platform-specific cleanup contracts; live desktop clipboard access is
unavailable on hosted runners and is not claimed. Exact-head CI and final
documentation-path validation are pending; Python, terminal Python/Node,
format, build and full .NET checks have passed locally.

Overall Pi feature parity remains `in_progress`. This receipt will be updated
with the published draft PR and exact-head workflow results after the remaining
acceptance gates pass.
