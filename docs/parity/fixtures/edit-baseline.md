# Pinned upstream edit-planning fixtures

Executed at `/tmp/pisharp-upstream` after `npm run build:offline`, revision `002fc8385268300ca91a5fc95f935c2afbbdac02`:

```sh
node --input-type=module -e 'import {applyEditsToNormalizedContent} from "./packages/coding-agent/dist/core/tools/edit-diff.js";for(const [source,edits] of [["alpha beta",[{oldText:"alpha",newText:"A"},{oldText:"beta",newText:"B"}]],["alpha beta",[{oldText:"alpha beta",newText:"A"},{oldText:"beta",newText:"B"}]],["a—b  \nnext\n",[{oldText:"a-b\nnext",newText:"done"}]]){try{console.log(JSON.stringify(applyEditsToNormalizedContent(source,edits,"sample.txt")))}catch(e){console.log(e.message)}}'
```

Observed (order preserved):

```text
{"baseContent":"alpha beta","newContent":"A B"}
edits[0] and edits[1] overlap in sample.txt. Merge them into one edit or target disjoint regions.
{"baseContent":"a—b  \nnext\n","newContent":"done\n"}
```

Additional pinned outputs: `one\r\ntwo\n` with old `one\ntwo`, new `three\nfour` produces `three\nfour\n` **after LF normalization**; `a a` with old `a` produces `Found 2 occurrences of the text in sample.txt. The text must be unique. Please provide more context to make it unique.`

## Renderer details

Node `generateDiffString` and `generateUnifiedPatch` outputs from `packages/coding-agent/src/core/tools/edit-diff.ts` at the revision above are encoded as exact expectations in `EditConformanceTests.cs`. Cases cover replacement formatting and no-final-newline markers; line numbering and context; leading insertion and deletion; empty-file addition/removal headers; duplicate-line tie ordering; and separated hunks with omitted display context. The tested upstream `edit.ts`, `edit-diff.ts`, and `tools.test.ts` files are unchanged at pinned `d5629e20489ccf770ed90b5a33941cb3b7ef24d0` and refreshed main `5fd446ca1843682e8da3fec4ceb71c42f56fbace`.

The MAF edit turn test also verifies that the provider sees only the success text, lifecycle events carry `diff`/`patch`/`firstChangedLine`, and the structured result survives conversation save/reload. The full tool schema, permission/error details, broader filesystem/cancellation behavior and terminal rendering remain **not verified**.
