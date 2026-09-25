# Pinned `ls` fixture (small ASCII directory)

At pinned upstream `002fc8385268300ca91a5fc95f935c2afbbdac02`, after `npm run build:offline`:

```sh
node --input-type=module -e 'import {createLsToolDefinition} from "./packages/coding-agent/dist/core/tools/ls.js"; const ops={exists:async()=>true,stat:async p=>({isDirectory:()=>p==="/fixture"||p.endsWith("/Beta")}),readdir:async()=>["z.txt","Beta",".env","alpha.txt"]}; for(const limit of [undefined,2,0]){const t=createLsToolDefinition("/fixture",{operations:ops}); const r=await t.execute("id",{limit});console.log(JSON.stringify({limit,output:r.content[0].text}));}'
```

Observed text outputs: default `.env\nalpha.txt\nBeta/\nz.txt`; limit 2 `.env\nalpha.txt\n\n[2 entries limit reached. Use limit=4 for more]`; limit 0 `(empty directory)`. The pinned `ls.ts` result type also exposes `entryLimitReached` and truncation details. `ToolSelectionTests` compares a matching real directory, and `StructuredSearchToolTests.GrepFindAndLsExposeTheirLimitDetails` checks PiSharp's entry-limit metadata. All three tools are **opt-in** via `--tools`; the four Pi default tools remain default. Locale-sensitive sorting, error/cancellation behavior, byte-truncation detail differentials, plugin operations and directory-race semantics remain open.
