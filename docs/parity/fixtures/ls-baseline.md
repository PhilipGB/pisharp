# Pinned `ls` fixture (small ASCII directory)

At pinned upstream `002fc8385268300ca91a5fc95f935c2afbbdac02`, after `npm run build:offline`:

```sh
node --input-type=module -e 'import {createLsToolDefinition} from "./packages/coding-agent/dist/core/tools/ls.js"; const ops={exists:async()=>true,stat:async p=>({isDirectory:()=>p==="/fixture"||p.endsWith("/Beta")}),readdir:async()=>["z.txt","Beta",".env","alpha.txt"]}; for(const limit of [undefined,2,0]){const t=createLsToolDefinition("/fixture",{operations:ops}); const r=await t.execute("id",{limit});console.log(JSON.stringify({limit,output:r.content[0].text}));}'
```

Observed outputs: default `.env\nalpha.txt\nBeta/\nz.txt`; limit 2 `.env\nalpha.txt\n\n[2 entries limit reached. Use limit=4 for more]`; limit 0 `(empty directory)`. Local `ToolSelectionTests` compares a matching real directory. PiSharp does not yet match all locale-sensitive sort, error/cancellation/truncation *details*, plugin operations or directory-race semantics. `ls` is **opt-in** via `--tools ls` (the four Pi default tools remain default); grep and find are not implemented.
