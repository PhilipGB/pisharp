# Codemode sandbox comparison — 2026-09-29

Pi source: `earendil-works/pi@4df1574339bfbd1a9750ff485bb618da397ba135`.
PiSharp base: `d6a9170f4c002e313562c2ae3f22541fdeb54ace`, with the follow-up in this slice.

The current Pi package's `CodemodeSandbox` ran in a Node 24 process with `quickjs-wasi` 3.6.2, a 32 MiB guest heap and a two-second test deadline. Each vector used a fresh VM. The output below records only stable observable fields; call durations and stack columns vary.

| Script | Current Pi observed | PiSharp evidence |
| --- | --- | --- |
| `text([typeof process, typeof require, typeof fetch].join(','))` | `undefined,undefined,undefined` | `CodemodeTests.SandboxHasNoHostProcessAndStopsInfiniteLoopOnCancellation` checks the same three missing globals. |
| `text('start'); return {answer:7}` | Output `start`, return `{answer:7}` | The same test checks model-facing `start` followed by compact returned JSON. |
| `store('answer',42); text(load('answer'))` | Output `42`, store write `answer=42` | The fake-provider run checks store read/write, branch navigation, JSONL export/import and resume. |
| `text('before'); throw new Error('boom')` | Script failure, partial output `before` | Direct test checks a failed result, `boom` error and no committed store state. |
| `text(await tools.echo({value:3}))` | Output `{"value":3}` for a text-returning echo | Fake-provider and direct tests check nested calls through PiSharp's shared tool path. |
| `try { await tools.fail({}) } catch(e) { text(e.message) }` | Output `blocked` | PiSharp's nested execution returns explicit error state to the worker; broader hook/block differential is still open. |

This is a bounded sandbox comparison, not a claim of complete Codemode parity. PiSharp currently has a deliberate 30-second maximum deadline, a 32 MiB QuickJS heap and a 64 KiB text-output limit. Current Pi permits larger memory and caller-selected deadlines, truncates long model output, and offers optional `models.*` globals. PiSharp also lacks the `codemode.mode` and `inlineBudget` settings. Those differences remain in the ledger.
