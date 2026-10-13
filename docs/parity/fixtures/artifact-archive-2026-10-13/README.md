# Local parity artifact archive — 2026-10-13

The previously untracked fixture backlog contained **1,747 files** totaling
**1,361,001,023 bytes** (1.27 GiB). All originals, including the five Markdown
notes, were moved unchanged to the local ignored directory
`artifacts/parity/archive-2026-10-13-bb12fe95bb/`.

Every original file's size and SHA-256 were recorded before moving and verified
at its destination. The [manifest](manifest.jsonl) lists each source path, size,
hash and selection flag. The [inventory](inventory.json) records counts, the
starting commit, archive mapping and review limitations. Archive destinations
are `archiveRoot/files/<source relative to docs/parity/fixtures>`; the archive
also contains a copy of the manifest.

## Evidence retained in Git

Five historical notes were rewritten around 17 reviewed, unchanged JSON reports
and assertion records:

- [Trusted project-extension UI replay](../project-trust-extension-ui-current-pi-2026-10-12/current-c661bc57-b1cc9013/README.md).
- [Extension resource reload](../resources-terminal-current-pi-2026-10-12/current-04566631-8a1df81/README.md).
- [Quiet-startup resource listing](../resources-terminal-current-pi-2026-10-12/current-42705224-ccc34672/quiet-startup-resources/README.md).
- [Quiet-startup help comparison](../resources-terminal-current-pi-2026-10-12/current-42705224-ff180dac/quiet-startup-resources/README.md).
- [Skill presentation and trust UI](../resources-skill-trust-current-pi-2026-10-10.md).

These records have source pins and support historical ledger references. The
notes distinguish scoped matches from full terminal failures, identify differing
calibration pins and remove unverified claims about historical CI or the current
oracle. Original note text remains intact in the archive. The other 1,725 files
remain archived without promotion to reviewed repository evidence.

This was a storage and record review. No historical process was rerun, no feature
status changed, and the configured Pi reference remains
`ee409a85f6ad92bf8c6ab8ed735c6cd24f2e749e`. Hash verification establishes that
the local backlog was preserved; it does not authenticate historical results.

## Retention and future output

The ignored archive is local and **is not backed up by Git**. Clean checkouts
contain the selected summaries and manifest, but no original capture streams.
Capture pointers inside historical JSON reports describe archived files and are
not repository links. Keep a separate archive backup for long-term retention.

Write new generated runs to distinct directories under `artifacts/parity/`.
Commit reviewed compact evidence with source pins, scope and limitations under
`docs/parity/fixtures/`; that documentation directory remains tracked. See the
[verification storage policy](../../../development/verification.md#parity-evidence-storage).
