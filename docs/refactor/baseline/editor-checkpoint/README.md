# Editor interaction checkpoint

H03 separates dialog input loops/panel rendering, progress settlement, transcript
search state and authorization input from ordinary draft editing, completion/history,
active-run queue/abort and application dispatch. TerminalEditor remains 579 lines;
concrete owners stay inside CLI. No project, package, interface, public editor API,
assembly identity or persistence/protocol format changes.

## Characterization and source evidence

`ownership-stages/` contains the original Editor snapshot, extraction/source-audit
scripts, prototype/permanent characterization, formatting/build and focused evidence.
The prototype's two operations require real terminal mode: pipe execution fails
with tcgetattr, while the unchanged test passes both rows under a PTY. The permanent
Fact hosts one filtered child case in a real Linux PTY; that case exercises both
fast success/failure. It passes against unchanged production Editor in an 89-case
scope before extraction. Both operations preserve draft and result/original exception,
render no transient progress panel, invoke neither explicit backend cancellation nor
after-cancel callback, and cancel the operation token during settlement cleanup.
The final focused editor/input/keymap/screen/PTY/architecture/docs scope passes 276
cases with zero skips. The Linux guard does not establish other-platform coverage.

Source audit accounts for all 22 moved method bodies after explicit owner/current-
binding normalization, preserves public editor method/constructor signatures and the
literal set. Six optional-default occurrences are duplicated on concrete owners;
no moved-body/control literal changes. This supporting token evidence does not replace
behavioral checks. Two existing tests reference the new internal panel producer;
their logical identities, data rows and expectations are unchanged. Mappings remain
empty. Initial stale NuGet-cache and pre-staging documentation errors are retained;
forced restore and explicit intended-path staging correct the environment/inventory.
No expectations or package declarations were weakened.

## Complete checkpoint

`capture-metadata.json`, `candidate-working-tree.patch.gz`, setup/run invocation
records and raw logs/TRX/reports describe the frozen candidate source. The complete
capture finishes with no source change. Format and warnings-as-errors build pass;
.NET 1,382/1,382 with zero skips, Node 24/24, Python 47/47, docs 124 documents/393
references, MCP 269 checks (261 passing/eight classified baseline failures), all 17
process drivers/six total virtual-routing variants, all 33 terminal fixtures in both
modes (66 reports/136 cases), and all 13 semantic assertion invocations are retained.
Known classifier-input, regular-mode/presentation and malformed-Markdown baseline
conditions remain. Runtime verification proves only its covered behavior.

`editor-preservation-comparison.json` passes all 1,858 original identities with
1,894 candidate identities. The three .NET truncated-display collisions retain exact
row counts/outcome multisets. New candidate identities comprise 26 passing .NET groups,
nine Python guard tests and the docs-path suite. No original identity disappears or
unexpected skip occurs. All four frozen flaky sets stay byte-for-byte unchanged.

## Model-picker investigation

Initial capture and first/second complete unchanged fixture repeats match Pi exactly
(20 render frames), outside the frozen model-picker outcome set. Each original report,
log/invocation and stream-omission hash remains in separate outside-baseline-first,
second and third-attempt folders. Earlier H01/H02 captures also produced exact matches
before Editor extraction. A newly rebuilt untouched baseline CLI at 064bcc7b likewise
matches all 20 frames/terminal/HTTP; the earlier H02 baseline control instead reproduces
the accepted 19-frame difference. This demonstrates pre-existing intermediate-frame
variance independently of H03. Baseline source/fixture/Pi/DLL hashes and harness-vs-CLI
revision disambiguation are explicit in `baseline-model-picker-control/`.

The third complete unchanged candidate fixture repeat reproduces the accepted
19-vs-20-frame outcome (37,205 render differences, HTTP match, no scenario error).
Full strict comparison then passes. No fixture, source, baseline set, pin, parser
normalization or expectation changed to accept the result. Investigation and every
attempt remain; exact-match attempts are not silently substituted into frozen sets.

`compression-manifest.json` records lossless gzip source hashes for logs/TRX/patches
and C# snapshots. Terminal stream omissions retain names/bytes/hashes; semantic
assertion captures remain. `user-integrity.json` verifies all 1,748 unaffected starting
files and the original PDF hash; only the authorized AGENTS pointer differs.

H03 is verified and resolved. Phase A final metrics/coverage/compatibility/docs/main
audit, independent review, preservation and cleanup/fresh-checkout phases still remain.
