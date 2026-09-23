# Pinned Pi v3 JSONL branch/context fixture

At `/tmp/pisharp-upstream` pinned to `002fc8385268300ca91a5fc95f935c2afbbdac02`, after `npm run build:offline`, run:

```sh
node --input-type=module -e 'import {readFileSync} from "node:fs"; import {buildSessionContext} from "./packages/coding-agent/dist/core/session-manager.js"; const entries=readFileSync("/home/philip/Documents/projects/dotnet/pisharp/docs/parity/fixtures/session-v3-branch.jsonl","utf8").trim().split("\n").slice(1).map(JSON.parse); for(const id of ["f6666666","daaaaaaa"]) console.log(id,JSON.stringify(buildSessionContext(entries,id)))'
```

The observed outputs (two branch heads) are recorded in [`session-v3-context.json`](session-v3-context.json). `PiSessionJournalTests` assert the Core projection against these JSON values, including compaction, append-only context edit, branch summary, model and thinking level. The input is a small hand-authored v3 Pi-format fixture, not an exported live Pi session; only these projection results were differentially compared. v1/v2 migration, malformed-line recovery, incomplete tool turns, live MAF reconstruction, branch selection persistence and the full Pi session manager API remain unverified or unimplemented. The prototype CLI continues to write only its **separate MAF snapshot**, not JSONL.
