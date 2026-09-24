# Pinned read text fixture (subset)

At `/tmp/pisharp-upstream`, revision `002fc8385268300ca91a5fc95f935c2afbbdac02`, after `npm run build:offline`:

```sh
node --input-type=module -e 'import {createReadToolDefinition} from "./packages/coding-agent/dist/core/tools/read.js"; const cases=[["a\nb\nc\n",{offset:1,limit:2}],["a\nb\nc\n",{offset:2}],["a\nb\n",{offset:3}],["a\n".repeat(2002),{}],["x".repeat(51201)+"\nlast",{}],["a".repeat(30000)+"\n"+"b".repeat(30000)+"\nthird",{}]]; for(const [source,opts] of cases){const t=createReadToolDefinition("/fixture",{operations:{access:async()=>{},readFile:async()=>Buffer.from(source),detectImageMimeType:async()=>null}}); const r=await t.execute("test",{path:"input.txt",...opts}); const s=r.content[0].text; console.log(JSON.stringify({size:source.length,opts,output:s.length<350?s:s.slice(0,65)+"..."+s.slice(-155)}));}'
```

Observed:

- `a\nb\nc\n`, offset 1/limit 2 → `a\nb\n\n[2 more lines in file. Use offset=3 to continue.]`; offset 2 without limit → `b\nc\n`.
- `a\nb\n`, offset 3 → empty output (the empty final line exists).
- 2002 `a\n` lines → first 2000 `a` lines then `[Showing lines 1-2000 of 2003. Use offset=2001 to continue.]`.
- First line 51201 bytes → `[Line 1 is 50.0KB, exceeds 50.0KB limit. Use bash: sed -n '1p' input.txt | head -c 51200]`.
- Two 30000-byte lines plus `third` → first 30000-byte line then `[Showing lines 1-1 of 3 (50.0KB limit). Use offset=2 to continue.]`.

`ReadTextConformanceTests` retains these **text-selection** expectations. The current pinned `read.ts` decodes its file buffer with `Buffer.toString("utf-8")`; the read source and tests under `packages/coding-agent/src/core/tools/` are unchanged between the current pin (`d5629e20489ccf770ed90b5a33941cb3b7ef24d0`) and fetched upstream main (`b2bd111f2d46eed1a4689c32f30fde6306498827`). A direct Node decoder probe returns `"﻿a"` for UTF-8 BOM bytes and `"��a\u0000"` for UTF-16LE BOM bytes, so BOMs are retained and other BOMs do not select another encoding.

PiSharp now uses the same UTF-8 byte decoding for both small files and streamed large files. `ReadTextConformanceTests.ReadDecodesUtf8BytesWithoutConsumingBomOrDetectingUtf16` covers small UTF-8 BOM and UTF-16LE BOM files; `StreamingReadTests.LargeUtf8ReadPreservesBomLikePinnedNodeDecoder` covers the large-file path. The focused read suite passed 9/9 after both new cases first failed. Large files remain bounded by the streaming reader. Image attachments, image processing, Pi read details/error metadata and exact cancellation behavior remain open; this is not full read-tool parity.
