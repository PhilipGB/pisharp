# Pinned Pi v3 JSONL branch/context fixture

At `/tmp/pisharp-upstream` pinned to `002fc8385268300ca91a5fc95f935c2afbbdac02`, after `npm run build:offline`, run:

```sh
node --input-type=module -e 'import {readFileSync} from "node:fs"; import {buildSessionContext} from "./packages/coding-agent/dist/core/session-manager.js"; const entries=readFileSync("/home/philip/Documents/projects/dotnet/pisharp/docs/parity/fixtures/session-v3-branch.jsonl","utf8").trim().split("\n").slice(1).map(JSON.parse); for(const id of ["f6666666","daaaaaaa"]) console.log(id,JSON.stringify(buildSessionContext(entries,id)))'
```

Legacy migration baseline (random v1 IDs are deliberately normalized to relationships):

```sh
node --input-type=module -e 'import {readFileSync} from "node:fs"; import {migrateSessionEntries,buildSessionContext} from "./packages/coding-agent/dist/core/session-manager.js"; for(const v of [1,2]) { const e=readFileSync(`/home/philip/Documents/projects/dotnet/pisharp/docs/parity/fixtures/session-v${v}-linear.jsonl`,"utf8").trim().split("\n").map(JSON.parse); migrateSessionEntries(e); console.log(v,JSON.stringify({version:e[0].version,roles:e.slice(1).filter(x=>x.type==="message").map(x=>x.message.role),links:e.slice(2).map(x=>x.parentId===e[e.indexOf(x)-1].id),firstKept:e.at(-1).firstKeptEntryId===e[1].id,context:buildSessionContext(e.slice(1))})); }'
```

The observed outputs (two branch heads) are recorded in [`session-v3-context.json`](session-v3-context.json). `PiSessionJournalTests` assert the Core projection against these JSON values, including compaction, append-only context edit, branch summary, model and thinking level. The input is a small hand-authored v3 Pi-format fixture, not an exported live Pi session; only these projection results were differentially compared. Small hand-authored [v1](session-v1-linear.jsonl) and [v2](session-v2-linear.jsonl) fixtures were additionally passed through pinned `migrateSessionEntries()` and `buildSessionContext()`: both yielded v3, roles `user,custom`, sequential parent links, `firstKeptEntryId` pointing at the first message, and context `compactionSummary,user,custom` with the exact values asserted by `PiSessionJournalTests`. v1 random IDs are compared by relationships, not bytes. This is *narrow* migration evidence. Malformed-line recovery, incomplete tool turns, live MAF reconstruction, branch selection persistence and the full Pi session manager API remain unverified or unimplemented. The prototype CLI continues to write only its **separate MAF snapshot**, not JSONL.
