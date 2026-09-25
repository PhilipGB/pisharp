# Pinned `read` image behavior

Reference: `earendil-works/pi@d5629e20489ccf770ed90b5a33941cb3b7ef24d0`, `packages/coding-agent/src/core/tools/read.ts` and `src/utils/{mime,image-process,image-resize-core,image-convert}.ts`. These paths are unchanged at refreshed `main` `5fd446ca1843682e8da3fec4ceb71c42f56fbace`.

## Pinned contract

- The tool detects images from file bytes with a 4100-byte sniff: JPEG except JPEG-LS, valid static PNG except APNG, GIF87a/GIF89a, WebP, and validated BMP headers.
- BMP and unsupported inline formats are converted to PNG; supported JPEG/PNG/GIF/WebP retain their MIME type unless resizing selects another encoding.
- Resize defaults to 2000×2000 and a 4.5 MiB base64 payload ceiling. Pi tries PNG and JPEG quality levels, then reduces dimensions until a candidate fits. Resized output includes a coordinate mapping note; failed conversion/resize returns an omission note.
- Pi applies EXIF orientation before measuring/resizing and uses Lanczos3 sampling. Models without image input receive an omission note and no image block.
- Text reads use Node UTF-8 byte decoding. Offset, limit and head truncation apply only to text.

## PiSharp evidence and current differences

- `ReadImageDetector` tests the same signature and animation exclusions. `ReadImageProcessor` decodes detected image formats with SkiaSharp, rejects malformed/oversized pixel dimensions, normalizes BMP to PNG, keeps small supported payloads byte-for-byte, and resizes to the pinned default dimensions and base64 ceiling. It tries PNG, then JPEG qualities 80/85/70/55/40, and progressively shrinks dimensions.
- Processor tests cover small-image passthrough, dimension resizing and coordinate hints, BMP conversion, corrupt-image omission, pre-cancelled processing, and oversized dimensions rejected from codec metadata before pixel decode. A 1200×1200 high-entropy PNG above the inline payload ceiling is read through the real MAF loop against both local OpenAI Responses and Chat Completions endpoints; the provider receives a decodable JPEG below the ceiling. The same tool-loop fixture checks ordinary image passthrough, model capability filtering, `images.blockImages`, and canonical history save/reload.
- Resampling currently uses Skia cubic sampling, not Pi's Lanczos3. EXIF orientation is not applied. Inputs over 20 MiB and decoded images over 32 million pixels are omitted; these resource limits differ from Pi. No per-model resize profile is loaded. Oversized animated GIF/WebP transformations and broader provider limits are not verified.
- These are local PiSharp-to-provider loopbacks and processor cases, not a pinned Pi request/result differential. No whole image-read feature is marked verified.
