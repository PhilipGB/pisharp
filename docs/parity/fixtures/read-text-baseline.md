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

`ReadTextConformanceTests` compares these **text-selection** outputs. The PiSharp tool still reads the whole file into memory, and does not yet return image attachments, Pi error/details metadata, or matching cancellation. Do not infer full read-tool parity.
