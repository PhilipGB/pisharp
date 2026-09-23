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

`tests/PiSharp.Tests/EditConformanceTests.cs` compares only these pure planning results and a few local filesystem effects. The tool's full schema, diff/patch, errors, fuzzy Unicode edge cases and terminal rendering are **not** Verified.
