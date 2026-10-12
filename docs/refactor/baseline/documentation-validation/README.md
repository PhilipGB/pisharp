# Permanent documentation path validation

tools/validate_doc_paths.py checks required AGENTS/architecture/capability/parity
and development documents against Git-tracked files/directories, including Markdown
links, reference definitions and local inline paths. Upstream Pi shorthand and
synthetic input examples in parity inventories are distinct from PiSharp-owned
paths. The original audit found eight broken local links; all were references to
user-owned untracked raw evidence. None of those artifacts was staged or changed.
A tracked quiet-startup receipt retains historical source/results and limitations;
Llama summaries retain their provenance while explicitly identifying unavailable
raw directories/manifests. Inventory links point to available historical summaries.

Direct validation passes 123 documents and 363 local references. Four new xUnit
controls use real Git/Python fixtures: tracked links/directories/definitions pass,
existing untracked targets fail, missing owned inline paths fail while foreign/
synthetic shorthand remains separate, and temporary refactor documents are excluded.
All ten architecture/documentation rows pass before and after formatting; the
warnings-as-errors build succeeds. This scope changes documentation/test/check
infrastructure only; production and parity expectations remain unchanged.

Linux CI invokes the validator directly; its controls are in ordinary dotnet test.
Candidate capture now runs the new CI gate, and the stable parser recognizes the
additional suite/docs-paths identity. Parser neutrality verifies that every value
of the 1,515 previously captured identities is unchanged. Original baseline scope,
frozen semantic outcomes and mappings were not edited. Future full checkpoints
include this new gate and four additional .NET rows.

Commands/results, parser-neutrality and user-integrity receipts remain here.
Whitespace-bearing logs/TRX are losslessly gzip-compressed with hashes. All 1,748
starting user files outside the exact authorized AGENTS append remain unchanged;
the original AGENTS content and test.pdf hash are checked. H01–H03 and final
review/preservation/cleanup/fresh-checkout phases remain incomplete.
