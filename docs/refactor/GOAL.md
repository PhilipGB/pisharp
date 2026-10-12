# PiSharp — Architecture and Maintainability Refactor

Repositories:

- PiSharp: `https://github.com/PhilipGB/pisharp`
- Upstream Pi: `https://github.com/earendil-works/pi`

---

# Resume digest

This goal may run across multiple fresh Codex sessions.

At the start of every session:

1. determine whether `docs/refactor/` exists in the current checkout;
2. check whether either preservation ref exists remotely:
   - tag: `refactor-baseline-data`
   - fallback branch: `refactor-baseline-data-preserved`
3. load the authoritative goal and progress state using the rules below;
4. read documentation for the active capability when applicable;
5. attempt to fetch remote refs and check `main` divergence;
6. continue from the exact recorded work item rather than replanning the repository.

If remote fetch is unavailable, record:

`main divergence check skipped: no network`

and continue against the last observed remote state.

A failed ordinary divergence fetch alone is not a blocker.

However:

> If the remote cannot be queried, do not create, verify or consume Phase C/E preservation state. Continue only with work that does not depend on preservation-state transitions, or stop. Remote preservation verification is mandatory before cleanup.

A required push that cannot be completed is a durability blocker.

## First-session detection

If neither `docs/refactor/` nor either preservation ref exists and the branch has no Commit 0, this is the first session:

> Start at Section 6.

If neither `docs/refactor/` nor either preservation ref exists but the branch already contains refactor commits:

> Stop and report the inconsistent state.

Do not attempt to reconstruct missing baseline state from memory.

## Resume before cleanup

When `docs/refactor/` exists, read:

1. `AGENTS.md`;
2. `docs/refactor/GOAL.md`;
3. `docs/refactor/PROGRESS.md`;
4. active capability documentation.

## Resume after cleanup

If `docs/refactor/` is absent and either preservation ref exists:

1. determine which preservation ref exists;
2. if exactly one exists, treat it as `<preservation-ref>`;
3. if both exist unexpectedly, stop and report the ambiguous preservation state;
4. create or use a separate read-only worktree/check-out at `<preservation-ref>`;
5. read:
   - `docs/refactor/GOAL.md`;
   - `docs/refactor/PROGRESS.md`
   from that preservation worktree;
6. inspect current refactor-branch HEAD;
7. resume at the first incomplete step in Phase F or G.

After cleanup:

- do not recreate `docs/refactor/**` on the refactor branch;
- track temporary Phase F/G execution state only in:
  - an untracked local note; and/or
  - the PR/review description;
- do not commit Phase F/G progress bookkeeping.

## Preservation safety

If a preservation ref might exist:

> Follow the Phase E precondition in Section 23 before any cleanup.

Section 23 is the only authoritative finalisation sequence.

## Core rules

- Preserve behaviour supported by the frozen PiSharp baseline.
- Baseline PiSharp SHA is the exact `main` commit from which the refactor branch is cut.
- Capture architectural metrics, audits and the frozen hotspot set from that untouched SHA before structural work.
- Upstream Pi is represented by one permanent repository reference SHA.
- Select that Pi SHA from existing parity evidence, not latest upstream HEAD.
- Freeze the Pi SHA for the entire refactor.
- Never move the Pi SHA to make a test pass.
- Work only on the dedicated refactor branch.
- Do not automatically merge or rebase newer `main`.
- Check `main` divergence at every session start where network access permits.
- Refactor incrementally:
  **characterize → change → focused verification → broader verification → compare preserved test identities → commit → push**.
- Update test-identity and invocation mappings in the same commit that moves tests/projects.
- Never weaken tests, parity fixtures or conformance expectations to rescue a refactor.
- Frozen hotspots must end `RESOLVED` or independently reviewer-approved `JUSTIFIED`.
- The preservation ref is created once and never moved.
- Temporary coverage/metrics tooling must not pollute tracked dependency state.
- `test.pdf` must never be modified, staged, deleted or moved.
- Section 23 defines the only finalisation sequence.

The complete goal below remains authoritative.

---

# 1. Objective

Refactor PiSharp into a well-structured, maintainable, idiomatic .NET solution without regressing its existing behavioural parity with Pi.

PiSharp remains:

> A C#/.NET implementation of Pi using Microsoft Agent Framework and Microsoft.Extensions.AI where appropriate.

For this goal, **architecture and maintainability are the primary work**.

Do not pursue unrelated new Pi functionality unless required to understand or preserve existing behaviour.

The desired result is a codebase that is:

- easy to navigate;
- easy to understand;
- easy to test;
- organised around coherent capabilities;
- explicit about dependency boundaries;
- simple rather than heavily abstracted;
- suitable for continued autonomous parity work;
- safe for future coding agents to modify.

Use:

- idiomatic modern C#/.NET;
- Clean Architecture principles where useful;
- vertical-slice organisation where useful;
- SOLID pragmatically rather than dogmatically;
- Microsoft Agent Framework and Microsoft.Extensions.AI directly where appropriate.

Do not turn the repository into a textbook architecture sample.

The objective is a **simple, coherent, well-documented and maintainable .NET implementation of Pi whose structure makes future parity work safer and easier**.

---

# 2. Behavioural and compatibility invariants

## Preserve behaviour

Existing behaviour is a contract.

Treat current:

- tests;
- parity fixtures;
- differential harnesses;
- MCP conformance;
- terminal fixtures;
- persistence tests;
- provider tests;
- compatibility tests

as regression protection.

Do not:

- remove functionality;
- intentionally weaken Pi parity;
- rewrite working behaviour from memory;
- change tests merely because a refactor made them inconvenient.

If a refactor changes observable behaviour:

1. determine whether the previous PiSharp behaviour intentionally matched Pi;
2. compare against the frozen upstream Pi reference where necessary;
3. preserve intended Pi-compatible behaviour.

If a newly discovered Pi gap is unrelated to this structural work:

- record it;
- do not silently ignore it;
- defer it unless it prevents safe refactoring.

## Baseline PiSharp SHA

Define:

> **Baseline PiSharp SHA = the exact `main` commit from which the dedicated refactor branch is created.**

Record it before any refactor-specific commit.

It remains immutable for this goal.

## Select upstream Pi from existing evidence

The Pi SHA frozen for this refactor must **not** automatically be latest upstream `main`.

Determine the upstream Pi commit against which existing PiSharp parity evidence was most recently verified at the baseline PiSharp SHA.

Use, in order of authority:

1. current parity/execution ledger;
2. parity checkpoint/TODO documentation;
3. committed differential-fixture metadata;
4. exact-head parity evidence.

Record:

- chosen Pi SHA;
- evidence source;
- supporting commit/file reference.

### Missing, contradictory or failing evidence

If:

- no evidenced Pi SHA can be determined;
- evidence sources materially contradict one another;
- the evidenced Pi commit is unavailable;

record the problem and stop for a human baseline decision.

If verification at the evidenced SHA is red:

1. reproduce it;
2. classify the failures using Section 10;
3. continue only if they are understood as known, flaky or environmental baseline conditions.

If an **unexplained** baseline failure remains against the evidenced SHA:

> Stop for a human baseline decision.

Do not choose another Pi SHA merely to obtain green results.

## Permanent upstream Pi reference

PiSharp must have one explicit permanent repository mechanism for identifying the Pi commit used for parity verification.

After the unmodified baseline is captured:

1. introduce or confirm one clearly defined repository location holding the Pi SHA;
2. make parity/differential tooling use that SHA;
3. make verification fail clearly if the actual Pi checkout does not match it;
4. document how future parity work intentionally bumps it.

This is **permanent repository infrastructure**, not temporary refactor tooling.

Do not put it on the cleanup allowlist.

Document under `docs/parity/`:

- where the SHA is stored;
- how Pi is obtained;
- how the checkout is verified;
- how the SHA is intentionally bumped;
- what audit/verification accompanies a bump.

## Freeze the Pi reference

Once established:

> **Do not change the configured Pi SHA during this refactor.**

If upstream Pi advances:

- record newer changes as deferred parity work;
- do not move the pin;
- do not treat newer upstream behaviour as a regression in this branch.

## Compatibility surfaces

Treat these as compatibility surfaces unless evidence proves otherwise:

- CLI arguments;
- commands;
- CLI help;
- user-visible error behaviour;
- persisted session formats;
- configuration formats;
- provider configuration;
- MCP configuration;
- protocol payloads;
- externally consumed package IDs;
- externally consumed assembly identities;
- documented public APIs.

Before substantially moving under-tested compatibility code, establish characterization or golden tests.

Examples include:

- CLI help snapshots;
- session serialization;
- configuration serialization;
- user-visible error output;
- provider/MCP configuration;
- protocol payloads;
- protocol ordering.

## Determine external consumers

At baseline, determine and record:

- whether PiSharp publishes NuGet packages;
- whether assemblies expose documented public APIs;
- whether the CLI is distributed as a .NET tool or equivalent;
- whether source projects are documented reusable libraries;
- whether packaging/docs identify external consumers.

Internal type identity is not automatically frozen.

If no evidence shows external consumption, internal APIs, namespaces and project boundaries may change when doing so materially improves architecture.

Do not make implementation types public merely to cross a newly created boundary.

## Strict verification

Preserve:

- warnings-as-errors;
- nullable correctness;
- formatting;
- expected tests;
- parity suites;
- MCP conformance;
- zero unexpected skips.

Do not disable tests, analyzers, architecture checks or conformance cases to make structural work pass.

## `test.pdf`

`test.pdf` is user-owned.

Do not:

- modify it;
- move it;
- delete it;
- commit it;
- stage it.

Never use:

```text
git clean
git add .
git add -A
```

Stage intended paths explicitly.

At baseline:

1. calculate and record SHA-256 of the original `test.pdf`;
2. audit whether tracked code, tests, fixtures, scripts or documentation depend on it.

If no dependency exists, record that.

If verification legitimately depends on it:

- never change the original;
- Phase F may provision a read-only copy;
- that copy remains untracked and unstaged;
- provision it only after clean-tree assertions;
- remove it afterward.

At completion:

- recompute SHA-256 of the original;
- require an exact match with baseline.

---

# 3. Architectural rules

## Vertical slices and projects

Default to:

> **Capabilities are vertical slices inside projects.**

Do not create one project per capability.

Create a project only for a concrete reason such as:

- meaningful dependency boundary;
- specialised infrastructure;
- independent lifecycle/composition;
- reuse across multiple components;
- meaningful build/test ownership;
- useful isolation of volatile dependencies.

Project boundaries primarily express dependency boundaries.

Capability folders primarily express ownership and discoverability.

## Clean Architecture

Use Clean Architecture mainly to improve dependency direction.

Do not mechanically create:

- Domain;
- Application;
- Infrastructure;
- Presentation

projects unless they fit PiSharp naturally.

Avoid both:

- giant projects containing unrelated responsibilities;
- excessive fragmentation into tiny projects.

A project must justify itself as a useful compilation/dependency boundary.

## Interfaces

Keep or introduce an interface when it represents a real boundary, including:

- multiple meaningful implementations;
- I/O;
- network;
- persistence;
- time;
- processes;
- volatile infrastructure;
- significant application contracts;
- meaningful dependency inversion.

An interface justified only because an ordinary in-process collaborator becomes easier to mock is insufficient.

Test isolation counts only when it isolates genuinely external or volatile behaviour.

## Framework abstractions

Prefer Microsoft Agent Framework and Microsoft.Extensions.AI directly where they fit.

Do not wrap them merely to hide framework concepts.

However:

> Do not remove a custom abstraction if it encodes Pi-specific semantics the framework does not provide.

## SOLID

Apply SOLID pragmatically.

Use Single Responsibility strongly.

Use Dependency Inversion at meaningful boundaries.

Do not introduce:

- inheritance;
- interfaces;
- factories;
- mediators;
- repositories;
- pipelines

merely to satisfy a named principle.

## File size and churn

There is no arbitrary LOC limit.

Use size as a diagnostic.

A file is problematic when it reflects:

- multiple responsibilities;
- excessive coupling;
- poor navigation;
- difficult testing;
- unrelated churn.

Do not rename, move or split files purely for cosmetic or line-count reasons.

---

# 4. Refactor branch and `main` divergence

Create a dedicated refactor branch from the baseline PiSharp SHA.

Do not use `main` as scratch space.

Keep verified commits:

- focused;
- reviewable;
- bisectable;
- pushed.

Do not rewrite published history.

## Do not integrate newer `main`

Do not automatically merge or rebase newer `main`.

The baseline PiSharp SHA and frozen Pi SHA define the refactor baseline.

Changes landing on `main` later are external integration divergence.

## Session-start divergence check

At every fresh session:

1. attempt `git fetch`;
2. identify current `origin/main`;
3. record latest observed `main` SHA;
4. compare with previous observation;
5. summarize new changes;
6. classify materiality.

If network is unavailable, record:

`main divergence check skipped: no network`

and continue.

## Material divergence

A change is material when it touches:

- Pi reference/pinning;
- parity fixtures;
- parity/differential harnesses;
- compatibility-surface code;
- tests relevant to refactored capabilities;
- files inside frozen-hotspot capabilities;
- relevant build/architecture configuration.

When detected:

1. record it;
2. identify affected files/capabilities;
3. use `git merge-tree` or equivalent non-mutating forecast;
4. record likely integration conflicts.

Do not:

- merge;
- rebase;
- move the Pi SHA;
- disable tests;
- silently redefine the baseline.

Continue against the frozen baseline unless divergence genuinely blocks the refactor.

Unless explicitly instructed otherwise:

> Do not merge the completed refactor branch into `main`.

---

# 5. Multi-session state

After Stage 1 baseline capture, create:

```text
docs/refactor/
  GOAL.md
  PROGRESS.md
  baseline/
  tools/
```

## `GOAL.md`

Copy this complete goal into:

`docs/refactor/GOAL.md`

## `PROGRESS.md`

Keep it concise, normally one or two pages.

Include:

- baseline PiSharp SHA;
- frozen Pi SHA;
- branch;
- current phase;
- pilot;
- frozen hotspots;
- completed slices;
- active slice;
- important architectural decisions;
- latest observed `main`;
- material divergence;
- merge forecasts;
- blockers;
- exact next action;
- baseline-data locations;
- temporary-tool locations;
- exact temporary `AGENTS.md` pointer block.

## Temporary `AGENTS.md` pointer

Add one clearly identifiable temporary block instructing refactor sessions to read:

- `docs/refactor/GOAL.md`;
- `docs/refactor/PROGRESS.md`.

Record its exact:

- heading;
- text;
- surrounding blank-line block

in `PROGRESS.md`.

This temporary block does not count toward the final permanent `AGENTS.md` line-count target.

---

# 6. Baseline establishment and commit ordering

Baseline establishment occurs before structural refactoring.

## Stage 1 — untouched branch-point behavioural baseline

Before any tracked branch-specific change:

1. create the refactor branch from the baseline PiSharp SHA;
2. determine the evidenced Pi SHA;
3. prepare Pi at that exact SHA;
4. run the full available baseline verification **twice**;
5. capture results in an untracked scratch directory.

If the repository's existing mechanism floats to upstream HEAD or an ambiguous clone:

- do not rely on it for Stage 1;
- manually prepare/check out the evidenced Pi SHA;
- record exactly how Stage 1 was forced to use it.

### Stage 1 captures

Capture:

- logical test identities;
- outcomes;
- skip reasons;
- parity results;
- MCP results;
- build/format results;
- runner commands;
- working directories;
- flags;
- logger/output format;
- relevant environment variables;
- dependency/setup commands;
- machine-readable outputs.

## Interrupted Stage 1 or baseline inventory

Until Commit 0 is pushed, the entire baseline establishment is non-durable.

This includes:

- Stage 1 results;
- architectural metrics;
- external-consumer audit;
- dependency reproducibility audit;
- `test.pdf` audit/hash;
- hotspot ranking;
- frozen hotspot selection;
- temporary parser/metrics tooling developed in scratch.

Therefore:

> If the session ends before Commit 0 is created and pushed, restart Stage 1 and the entire baseline inventory from scratch.

Do not treat partially captured scratch output, metrics, tools or hotspot analysis as authoritative.

## Stage 1 flakiness classification

Because Stage 1 runs twice:

- classify inconsistent tests/scenarios as baseline-flaky;
- record the complete observed semantic outcome set;
- record skip reasons seen;
- do not treat duration/order/timestamp differences as behavioural changes.

## Baseline architectural inventory

Before Commit 0, capture architectural data from the **untouched baseline PiSharp SHA**.

This must occur before the pin mechanism or structural changes can influence the measurements.

Capture:

- solution/project list;
- project-reference graph;
- project/package/publication information;
- external-consumer audit;
- top 15 production files by LOC;
- largest classes where measurable;
- constructor dependency counts;
- maximum constructor dependency count;
- complexity metrics where practical;
- high-complexity methods/classes where practical;
- repository churn data where practical;
- Node lockfile/reproducibility status;
- Python lockfile/reproducibility status;
- `test.pdf` SHA-256;
- `test.pdf` dependency audit;
- hotspot coverage;
- candidate hotspot ranking.

## Freeze hotspot set before Commit 0

Using untouched-baseline measurements, freeze approximately the top 10 meaningful hotspots.

For each record:

- file/type;
- capability;
- reason;
- size;
- churn/coupling evidence;
- constructor dependency count where relevant;
- coverage;
- responsibility problems.

The hotspot list is frozen **before any product/infrastructure refactoring commit**.

It may not later be re-ranked because early refactoring changed the metrics.

## Commit 0 — durable baseline evidence

Immediately after Stage 1 and baseline inventory are complete:

1. create `docs/refactor/`;
2. add `GOAL.md`;
3. add `PROGRESS.md`;
4. copy Stage 1 results into `docs/refactor/baseline/`;
5. store architectural baseline metrics/audits there;
6. store the frozen hotspot list there;
7. add parser/metrics tooling developed in scratch under `docs/refactor/tools/`;
8. add the temporary `AGENTS.md` pointer.

Commit this as **Commit 0**.

Commit 0 must not modify product/runtime behaviour.

### Push Commit 0 immediately

After creating Commit 0:

1. perform applicable docs-only verification;
2. push it immediately.

Do not begin Stage 2 until Commit 0 is durably pushed.

## Stage 2 — permanent Pi-reference mechanism

After Commit 0 is pushed:

1. implement or confirm the permanent Pi-reference mechanism;
2. set it to the evidenced Pi SHA;
3. verify actual Pi checkout SHA;
4. document the future bump procedure under `docs/parity/`;
5. rerun the same logical baseline verification.

This becomes **Commit 1**, the first product/infrastructure change.

## Pin-neutrality comparison

Save the pre-pin vs post-pin comparison under:

`docs/refactor/baseline/`

Compare:

- logical identity;
- pass/fail outcome;
- skip state;
- skip reason;
- parity scenario outcome;
- MCP scenario outcome.

Ignore:

- duration;
- timestamps;
- result ordering;
- absolute paths;
- machine-specific temporary paths.

### Baseline-flaky cases

For a test/scenario classified baseline-flaky:

- post-pin outcome is acceptable when it falls within the semantic outcome set observed during the two Stage 1 runs;
- report these separately from deterministic equivalence.

If the pin introduces a semantic outcome outside the baseline-observed set:

- investigate;
- treat it as a possible regression.

Do not proceed until the pin change is demonstrated behaviour-neutral relative to the established baseline.

Commit and push Commit 1 only when verified.

---

# 7. Reproducible invocation mapping

Final restructuring may move:

- projects;
- tests;
- namespaces;
- Node/Python paths.

Baseline invocations therefore do not need to remain textually identical.

## Prefer stable entry points

Where practical use:

- solution/repository-level .NET commands;
- repository-level Node commands;
- repository-level pytest roots;
- stable scenario identifiers.

Avoid fragile:

- namespace filters;
- source-file filters;
- project-specific paths

when stable higher-level entry points exist.

## Invocation mappings

When a baseline invocation becomes invalid, record:

> baseline invocation → final equivalent invocation

The mapping must preserve equivalent logical scope.

Update this mapping in the **same commit** that changes the relevant project/test path.

Store mappings under:

`docs/refactor/baseline/`

## Temporary capture tooling

If capture depends on scripts in:

`docs/refactor/tools/`

final verification runs those scripts from the preservation-ref worktree and points them at final HEAD/results.

Do not recreate modified copies in final HEAD.

---

# 8. Temporary tooling and dependency reproducibility

Prefer temporary tooling written as:

- Python;
- shell;
- other standalone scripts.

Do not create temporary `.csproj` projects merely for metrics.

Verify `docs/refactor/tools/**` does not accidentally participate in:

- product build;
- test discovery;
- packaging;
- formatting/analyzer globs;
- `Directory.Build.*` behaviour.

## Do not pollute tracked dependency state

Temporary analysis tooling must normally be installed outside tracked repository dependency state.

Do not modify tracked:

- NuGet package declarations;
- .NET tool manifests;
- Node manifests;
- Node lockfiles;
- Python requirement files;
- Python lockfiles

solely to install temporary analysis tooling.

Prefer:

- globally installed tools;
- throwaway local tool directories;
- disposable virtual environments;
- standalone scripts.

If a tracked file must be changed temporarily:

1. record its exact path;
2. add it to the Phase C cleanup allowlist;
3. restore original content during cleanup.

Permanent architecture-test dependencies intentionally adopted by the repository are exempt.

## Dependency reproducibility

Record whether Node has a committed lockfile.

Record whether Python has:

- a committed lockfile;
- pinned requirements;
- another reproducible mechanism.

Do not add a new Node/Python lockfile solely for this goal unless explicitly approved.

Where none exists, record:

- the closest reproducible mechanism;
- its limitation.

---

# 9. Frozen architectural hotspots

The hotspot set captured in Section 6 is authoritative.

Do not:

- re-rank it;
- shrink it;
- replace difficult hotspots with easier ones.

Every hotspot finishes as:

- `RESOLVED`; or
- `JUSTIFIED`.

`JUSTIFIED` requires independent reviewer approval.

Every capability containing a hotspot must receive permanent capability documentation.

---

# 10. Baseline failure policy

If baseline verification is red:

1. reproduce;
2. classify:
   - deterministic known failure;
   - flaky;
   - environmental;
   - unexplained;
3. record it.

Known/classified baseline failures do not automatically require changing the Pi SHA.

If an **unexplained** baseline failure remains against the evidenced Pi SHA:

> Stop for a human baseline decision.

Do not choose a newer Pi commit merely to obtain green results.

If a suite is unavailable because of:

- credentials;
- network;
- platform tooling;
- external service availability,

record that explicitly.

Any suite runnable at baseline must remain runnable at completion unless an independently demonstrated environment problem prevents it.

---

# 11. Stable test identity

Do not depend solely on:

- path;
- namespace;
- fully qualified name.

Maintain stable logical identities.

When tests are:

- moved;
- renamed;
- split;
- merged,

then in the **same commit**:

- update logical identity mappings;
- update relevant invocation mappings;
- verify test preservation immediately.

For .NET, keys may use:

- logical method identity;
- data-row identity/count;
- capability/category.

For Node/Python use logical:

- test name;
- scenario name;
- explicit mapping.

---

# 12. Characterization before restructuring

Before modifying a hotspot:

1. inspect tests;
2. collect coverage where practical;
3. identify weakly protected high-risk paths;
4. add characterization tests where needed.

Prioritise:

- orchestration;
- failure paths;
- persistence;
- providers;
- terminal behaviour;
- protocols;
- state transitions;
- concurrency;
- ordering.

Do not chase arbitrary coverage percentages.

For Pi-sensitive code, differential evidence may be stronger than ordinary unit coverage.

Apply the same rule to under-tested compatibility surfaces outside the hotspot set.

---

# 13. Refactoring sequence

Use this default sequence:

1. baseline;
2. pilot;
3. split responsibilities in place;
4. simplify unnecessary abstraction;
5. clarify capability ownership;
6. infer project boundaries from discovered seams;
7. adjust project boundaries/references;
8. enforce dependency rules;
9. reorganise tests where useful;
10. complete permanent documentation;
11. remove transitional/dead architecture.

The pilot executes steps 3–9 for one representative capability.

Do not start with repository-wide project reshuffling.

Finalisation is defined only in Section 23.

---

# 14. Pilot

Choose a representative mid-sized capability.

Prefer one containing a frozen hotspot.

For the pilot:

1. record current structure;
2. establish characterization protection;
3. split responsibilities;
4. simplify abstraction;
5. organise coherently;
6. decide whether a project boundary is justified;
7. reorganise relevant tests;
8. update logical/invocation mappings immediately;
9. add capability documentation;
10. verify;
11. commit;
12. push.

Record:

- what improved;
- what worsened;
- dependency changes;
- project-count changes;
- whether the pattern should continue.

Do not propagate a pattern that:

- increases project count without solving a dependency problem;
- increases abstraction depth;
- makes navigation harder;
- obscures control flow;
- adds many unjustified interfaces;
- makes tests materially harder.

---

# 15. Decompose god classes and files

Prioritise frozen hotspots.

Look for types that:

- own unrelated concerns;
- have excessive dependencies;
- contain unrelated switches;
- mix state, rendering, orchestration and infrastructure;
- mix providers, tools, MCP, resources, sessions, persistence or UI;
- contain separable private-method clusters;
- require broad setup for narrow behaviour.

Split by actual responsibility.

Do not replace a god class with:

- helpers;
- utils;
- generic managers;
- forwarding-service chains.

Keep main orchestration readable.

Do not move, rename or split code merely to create the appearance of decomposition.

---

# 16. Simplify abstraction

Review:

- pass-through services;
- trivial factories;
- unnecessary interfaces;
- excessive generics;
- unnecessary inheritance;
- redundant mappings;
- meaningless framework wrappers;
- service-location patterns;
- excessive result wrappers.

Avoid:

- mediator/CQRS without demonstrated need;
- generic repositories/unit-of-work;
- event buses for ordinary calls;
- interface-per-class;
- one-project-per-feature;
- factories that merely invoke constructors.

Do not rename, move or split files purely for cosmetic reasons or merely to reduce line count.

A structural change must materially improve at least one of:

- responsibility isolation;
- dependency direction;
- readability;
- discoverability;
- testability;
- change isolation.

---

# 17. Composition and architecture enforcement

Create an identifiable composition root.

Configuration, service registration and implementation selection should be easy to locate.

Prefer constructor injection.

Avoid widespread service-location.

Treat large constructors as diagnostic evidence.

## Mechanical architecture rules

When meaningful boundaries are introduced, enforce them using an appropriate mechanism such as:

- NetArchTest;
- ArchUnitNET;
- focused reflection tests;
- project-reference constraints.

Possible rules include:

- runtime/core cannot depend on CLI/TUI;
- generic runtime cannot directly depend on provider-specific implementations;
- lower-level components cannot depend on presentation;
- project references follow the documented graph.

Do not encode aesthetic preferences.

Wire architecture checks into standard verification/CI immediately when introduced.

---

# 18. Runtime, TUI and implementation quality

## Runtime

Avoid central classes unnecessarily owning combinations of:

- providers;
- tools;
- MCP;
- resources;
- authentication;
- persistence;
- compaction;
- session lifecycle;
- configuration;
- TUI.

Extract cohesive responsibilities without making the main execution path opaque.

## TUI

Treat terminal behaviour as parity-sensitive.

Separate where useful:

- input;
- state;
- actions;
- rendering;
- layout;
- transcript;
- dialogs;
- auth;
- resource/trust interaction.

Do not create a new TUI framework.

Preserve existing terminal differential evidence.

## Error handling

Prefer:

- narrow catches;
- cancellation propagation;
- useful original exception context;
- explicit expected-failure handling.

Avoid:

- catch-all swallowing;
- exception-based ordinary control flow;
- unnecessary wrapping.

Preserve Pi-compatible user-visible error output.

## Naming and comments

Use precise capability/responsibility names.

Avoid vague `Manager`, `Helper`, `Processor`, `Utility`, `Context`, `Service` when a better name exists.

Keep comments concise and limited to non-obvious constraints such as:

- parity;
- protocol;
- ordering;
- concurrency;
- terminal behaviour;
- framework limitations.

---

# 19. Verification cadence

For every bounded slice:

1. focused capability tests;
2. affected parity/differential tests;
3. affected MCP/conformance tests;
4. formatting;
5. warnings-as-errors build;
6. architecture tests;
7. affected docs/path checks;
8. update stable logical test mappings where relevant;
9. update invocation mappings where relevant;
10. update `PROGRESS.md`;
11. commit;
12. push.

## Full-suite checkpoints

Run the full available verification suite:

- after the pilot;
- after project moves;
- after major dependency-boundary changes;
- before independent review;
- at final HEAD.

At **every full-suite checkpoint**, also run the stable logical test-preservation comparison against the frozen baseline.

Do not wait until Phase A to discover missing tests.

## Mapping updates

Any commit that moves or renames:

- a test;
- a test project;
- a Node/Python suite;
- a project referenced by capture commands

must update its logical-test and/or invocation mappings in that same commit.

## Flaky comparisons

For a baseline-flaky test/scenario:

- accept a later outcome only when it falls within the semantic outcome set observed in the two Stage 1 runs;
- report baseline-flaky cases separately;
- do not treat duration/order differences as failures.

A new semantic outcome remains a regression candidate.

## Capture run reuse

If machine-readable capture executes exactly the same logical scope as required verification, one run may satisfy both purposes.

## Push failure

If push cannot be completed:

- stop rather than accumulating undurable multi-session work;
- record local branch/SHA and verification state.

---

# 20. Rollback mechanics

After either:

- three substantive failed fixes against the same failure; or
- one full session without reaching green,

roll back the slice.

## Uncommitted failure

Discard local structural changes.

Retain independently useful characterization tests where appropriate.

## Already-pushed failure

Use:

`git revert`

Never force-rewrite published verified history.

Record:

- what was attempted;
- why it failed;
- what blocked it.

## WIP branches

A durability-only WIP branch may use:

`wip/...`

It:

- is not a verified checkpoint;
- cannot be a resume point in `PROGRESS.md`;
- should be deleted before completion unless retained intentionally for human investigation.

Any retained WIP branch must be listed in the final report with:

- purpose;
- SHA.

---

# 21. Project/build concerns

When moving code, explicitly review:

- project references;
- assembly names;
- namespaces;
- accessibility;
- `InternalsVisibleTo`;
- package management;
- `Directory.Build.props`;
- nullable/analyzer settings;
- generated code;
- packaging;
- test references.

Do not make internal types public merely to satisfy a poor project split.

Preserve trimming, Native AOT, startup-time or package-size requirements only if already present.

---

# 22. Documentation

Use coherent permanent locations such as:

```text
docs/
  architecture/
  capabilities/
  development/
  parity/
```

## `AGENTS.md`

Target approximately:

> **100 permanent lines or fewer**

unless a concrete reason requires more.

The temporary refactor pointer does not count.

Do not discard useful rules merely to reduce length.

Relocate valid detailed rules into permanent docs.

Record the exact temporary pointer block in `PROGRESS.md`.

## Capability docs

Every frozen-hotspot capability must document:

- purpose;
- entry points;
- important types;
- flow;
- dependencies;
- Pi constraints;
- tests;
- parity evidence;
- non-obvious decisions.

## Architecture docs

Document:

- project map;
- responsibilities;
- dependency graph;
- vertical-slice convention;
- composition/startup;
- infrastructure boundaries;
- testing strategy;
- parity strategy.

## ADRs

Use concise ADRs only for significant, non-obvious decisions such as:

- introducing a project boundary;
- using MAF directly;
- retaining a Pi-specific abstraction;
- major TUI/runtime separation.

## Path validation

Validate repository paths referenced by:

- `AGENTS.md`;
- architecture docs;
- capability docs;
- parity docs.

Exclude:

`docs/refactor/**`

Wire validation into normal verification when introduced.

---

# 23. Final review, preservation ref, cleanup and exact-head verification

Follow this sequence exactly.

No other section defines a competing finalisation sequence.

## Phase A — pre-cleanup verification

1. complete structural work;
2. confirm architecture tests are wired into CI/standard verification;
3. confirm docs/path validation is wired likewise;
4. run complete available verification;
5. rerun stable logical test-preservation comparison;
6. produce final architecture metrics;
7. compare baseline vs final:
   - project graph;
   - top files;
   - constructor dependencies;
   - complexity where available;
   - hotspot dispositions;
   - coverage;
8. verify compatibility characterization/golden tests;
9. update permanent docs;
10. perform final `main` divergence check;
11. record integration risk;
12. mark branch review-ready.

## Phase B — independent review

Use a fresh read-only reviewer.

Material findings are those that:

- violate completion criteria;
- create meaningful maintainability/coupling risk;
- weaken dependency boundaries;
- create unnecessary architecture;
- increase parity risk;
- reject a `JUSTIFIED` hotspot.

Run at most two rounds.

### Round 1

Review the actual repository.

Resolve material findings.

### Round 2

Review only:

- whether Round 1 material findings were resolved;
- whether those fixes directly introduced a new completion-criterion violation.

If material findings remain after Round 2:

1. mark `INCOMPLETE`;
2. record findings and exact next action in `PROGRESS.md`;
3. commit and push the last verified state;
4. do not run Phases C–G;
5. leave temporary refactor material intact.

Do not enter an unlimited review loop.

## Phase C — preservation state

After review passes:

1. rerun full verification;
2. rerun stable test comparison;
3. update final temporary metrics;
4. define the complete Phase C cleanup allowlist;
5. ensure every permanent repository change is already complete;
6. update `PROGRESS.md` so its exact next action is:

   `Phase E — cleanup, but only after remote preservation-ref verification`;

7. commit and push this complete pre-cleanup state.

Let this commit be:

`<preservation-commit>`.

### Create preservation ref

Create exactly once:

`refactor-baseline-data`

as an immutable tag pointing to `<preservation-commit>`.

Push it.

If tag push is impossible, create and push:

`refactor-baseline-data-preserved`

as a fallback branch pointing to `<preservation-commit>`.

Define whichever successfully exists remotely as:

`<preservation-ref>`.

Never move it.

If both possible preservation refs exist unexpectedly:

> Stop and report the ambiguous state.

### Verify preservation ref before leaving Phase C

Before Phase C is complete:

1. query the remote;
2. verify `<preservation-ref>` exists remotely;
3. verify it resolves exactly to `<preservation-commit>`;
4. verify `<preservation-commit>` contains:
   - `docs/refactor/GOAL.md`;
   - `docs/refactor/PROGRESS.md`;
   - baseline data;
   - comparison tooling;
   - cleanup allowlist.

If remote preservation cannot be verified:

> Phase C is incomplete. Do not proceed to cleanup.

If the remote cannot be queried:

> Do not perform Phase C preservation actions. Continue only work not dependent on the preservation transition, or stop.

## Phase D — external summary only

After `<preservation-ref>` exists:

> Do not modify or commit repository files before the cleanup commit.

Prepare PR/final-report summary material outside repository history.

If information should be permanent in the repo, it must already have been committed before preservation.

There must be no repository commit between:

- `<preservation-commit>`;
- `<cleanup-commit>`.

## Phase E — cleanup

### Mandatory precondition

Before deleting or modifying temporary refactor material:

1. query the remote;
2. check both possible names:
   - tag `refactor-baseline-data`;
   - fallback branch `refactor-baseline-data-preserved`;
3. if exactly one exists, define it as `<preservation-ref>`;
4. if both exist unexpectedly, stop and report the ambiguous state;
5. verify `<preservation-ref>` resolves to an ancestor of, **or is equal to**, current HEAD;
6. verify the referenced commit still contains:
   - `docs/refactor/GOAL.md`;
   - `docs/refactor/PROGRESS.md`;
   - baseline data;
   - comparison tooling.

If the remote cannot be queried:

> Do not perform cleanup.

If `PROGRESS.md` says Phase E but the remote preservation ref is missing:

- if HEAD is exactly `<preservation-commit>` and there are no later commits:
  1. create the appropriate preservation ref at HEAD;
  2. push it;
  3. verify it remotely;
  4. then continue;
- if HEAD has moved beyond the intended preservation commit:
  > Stop and report the inconsistent state.

Never perform cleanup without a verified remote preservation copy.

### Cleanup operation

Remove:

- `docs/refactor/GOAL.md`;
- `docs/refactor/PROGRESS.md`;
- `docs/refactor/baseline/`;
- `docs/refactor/tools/`;
- temporary manifests;
- temporary `AGENTS.md` pointer;
- Phase C allowlisted paths.

Do not expand the allowlist after preservation.

Commit exactly this cleanup.

The cleanup commit must be a **direct child of `<preservation-commit>`**.

No intermediate commit is allowed.

If another commit appears between the preservation commit and intended cleanup commit:

> Stop and report the sequencing violation.

Let the cleanup SHA be:

`<cleanup-commit>`.

Verify:

```text
git diff --name-only <preservation-ref>..<cleanup-commit>
```

Only expected temporary paths may differ.

### Mechanical `AGENTS.md` cleanup check

If `AGENTS.md` differs:

- inspect its content diff against `<preservation-ref>`;
- require the cleanup diff to remove exactly the pointer block recorded in `PROGRESS.md`;
- no other `AGENTS.md` content may change in the cleanup commit.

Any permanent `AGENTS.md` restructuring must already have happened before preservation.

Push the cleanup commit.

### WIP branches

Delete obsolete remote `wip/...` branches separately after the cleanup commit is pushed.

Branch deletion is not part of the cleanup commit.

Retain a WIP branch only when deliberately needed for human investigation.

## Phase F — fresh clean-checkout verification

Create a fresh clone/worktree at final candidate HEAD.

### Resume after cleanup

A new session entering this phase must:

1. inspect the two possible preservation ref names;
2. identify the single valid `<preservation-ref>`;
3. read the goal and pre-cleanup progress state from its worktree;
4. track Phase F/G progress externally/untracked;
5. resume at the first incomplete Phase F/G step.

Do not recreate temporary refactor files in final HEAD.

### Clean state

Before any untracked `test.pdf` copy:

1. verify checkout SHA;
2. verify clean Git status.

Do not reuse:

- `bin`;
- `obj`;
- `node_modules`;
- existing Python environments;
- generated test state;
- an existing Pi checkout.

### Dependency setup

Use baseline-recorded mechanisms.

For Node:

- use committed lockfile when one exists;
- otherwise use the recorded closest reproducible mechanism.

For Python:

- use committed pinned/locked mechanism where available;
- otherwise use the recorded closest reproducible mechanism.

Do not add new lockfiles during Phase F.

### Upstream Pi

Obtain a fresh Pi checkout through the permanent Pi-reference mechanism.

Verify the actual Pi SHA matches the frozen configured reference.

### `test.pdf`

If baseline audit proved verification requires `test.pdf`:

- provision a read-only copy only after clean-tree assertions;
- keep the copy untracked and unstaged;
- never modify the original;
- remove the copy after verification.

### Final verification

Run:

- standard repository verification;
- required parity/differential suites;
- MCP conformance;
- final machine-readable result capture.

## Final invocation selection

For every baseline suite:

- reuse the stable baseline invocation if still valid; or
- use its recorded baseline→final invocation mapping.

The mapped invocation must preserve equivalent logical scope.

If capture scripts exist only in the preservation worktree:

- run them there;
- point them at final HEAD/results.

If machine-readable capture executes the same logical scope as normal verification, one run may satisfy both.

## Phase G — preservation comparison

Create a separate worktree at `<preservation-ref>`.

Use that worktree as:

- authoritative Phase F/G instruction source;
- source of preserved parser/comparison tooling;
- source of baseline results;
- source of mappings and capture metadata.

Run exactly the preserved parser/comparison tooling.

Compare:

- preserved baseline output;
- final HEAD output.

Apply:

- logical identities;
- rename/move mappings;
- invocation mappings;
- baseline deltas;
- any documented supplemental post-tag mapping.

### Equivalence

Compare:

- logical identity;
- semantic outcome;
- skip state;
- skip reason;
- parity/conformance scenario outcome.

Ignore:

- duration;
- timestamps;
- result ordering;
- absolute paths;
- machine-specific temporary paths.

For baseline-flaky cases:

- a final result is acceptable when it belongs to the semantic outcome set observed in Stage 1;
- list these separately.

Confirm:

- no expected logical test disappeared;
- no expected scenario disappeared;
- no unexpected skip appeared.

## Post-tag fixes

If Phase F/G reveals a genuine source defect:

1. fix it on the refactor branch;
2. run normal verification;
3. commit/push;
4. repeat Phase F/G.

Do not move `<preservation-ref>`.

Avoid post-tag test movement.

If unavoidable:

- record supplemental mappings in PR/review summary;
- record them in final report;
- apply them explicitly during Phase G.

---

# 24. Completion criteria

The goal is complete only when all of the following are true.

## Baseline integrity

- baseline PiSharp SHA is the untouched `main` branch point;
- baseline architectural metrics/audits were captured from that SHA before structural changes;
- frozen hotspot list was established before structural changes;
- evidenced Pi SHA was identified from committed parity evidence;
- baseline verification ran against that exact SHA;
- any red baseline conditions were classified;
- no unexplained baseline failure was bypassed by changing Pi SHA;
- Commit 0 containing baseline evidence was pushed before structural work began.

## Pin neutrality

- permanent Pi-reference mechanism exists;
- it points to the evidenced Pi SHA;
- pre-pin vs post-pin semantic results are equivalent;
- baseline-flaky cases remain within recorded baseline outcome sets;
- future bump procedure is documented.

## Behaviour

- baseline-supported behaviour remains intact;
- no Pi-compatible behaviour regressed;
- MCP conformance matches or improves baseline;
- no expected logical tests/scenarios disappeared;
- no unexpected skips appeared;
- baseline-runnable suites remain runnable and passing;
- stable-preservation comparison was run at every full-suite checkpoint.

## `test.pdf`

- baseline and final SHA-256 match;
- original was never modified, staged or committed;
- any clean-checkout verification copy was read-only and untracked.

## Architecture

- project responsibilities are coherent;
- important dependency rules are mechanically enforced;
- vertical slices are clear;
- all frozen hotspots are:
  - `RESOLVED`; or
  - independently approved `JUSTIFIED`;
- unnecessary coupling and abstraction are reduced;
- composition is understandable;
- no cosmetic-only splitting/movement masquerades as architectural improvement.

## Documentation

- permanent `AGENTS.md` is focused and normally ≤100 lines;
- temporary pointer removed exactly;
- architecture/capability/parity docs match final paths;
- Pi reference/bump process documented;
- path validation passes;
- no temporary `docs/refactor/**` remains from final HEAD.

## Preservation/recovery

- `<preservation-ref>` exists remotely;
- it was created once and never moved;
- it points to the complete pre-cleanup state;
- cleanup commit is a direct child of the preservation commit;
- Phase F/G can be resumed using the preservation worktree even though temporary files are gone from HEAD.

## Exact-head verification

From fresh final HEAD:

- dependency setup uses recorded reproducible mechanisms;
- fresh Pi checkout matches frozen reference;
- format/build succeed;
- all baseline-runnable .NET tests succeed;
- all baseline-runnable Node tests succeed;
- all baseline-runnable Python tests succeed;
- all baseline-runnable MCP conformance tests succeed;
- architecture tests pass;
- documentation/path validation passes;
- preservation-ref logical-test comparison passes;
- baseline-flaky cases remain within their recorded acceptable semantic outcome sets;
- zero unexpected skips remain.

## Independent review

No material finding remains unresolved.

If one remains after Round 2:

- status is `INCOMPLETE`;
- temporary cleanup is not performed.

## Repository

- refactor branch pushed;
- preservation ref pushed;
- final SHA recorded;
- preservation SHA recorded;
- `test.pdf` untouched;
- no unintended WIP branches;
- no temporary refactor artefacts remain at final HEAD;
- clean state except preserved user-owned files.

---

# 25. Autonomous execution

Continue until the complete goal is satisfied.

At each checkpoint ask:

> Is the complete architecture and maintainability goal satisfied?

If not:

1. choose the highest-value unresolved problem;
2. update the ledger when it still exists on HEAD;
3. implement a bounded slice;
4. verify;
5. compare preserved test identities when applicable;
6. commit;
7. push;
8. continue.

If a session ends before Commit 0 is pushed:

> Stage 1 and the complete baseline inventory/hotspot freeze must be rerun from scratch.

If a normal pre-cleanup session limit is reached:

- leave a pushed green checkpoint;
- leave exact resume action in `PROGRESS.md`.

If cleanup has already occurred:

- resume Phase F/G through `<preservation-ref>`;
- use external/untracked execution notes;
- do not recreate repository bookkeeping.

If push is impossible:

- stop;
- report the local branch/SHA;
- report verification state;
- do not accumulate further undurable work.

Stop permanently only when:

- every completion criterion is satisfied;
- independent review remains materially unresolved after the permitted review cycle, in which case report `INCOMPLETE`; or
- a genuine external blocker prevents further progress.

---

# 26. Final report

Report every completion item required by Section 24.

Also include:

- completion status;
- refactor branch;
- baseline PiSharp SHA;
- final PiSharp SHA;
- frozen Pi SHA;
- evidence source used to select that Pi SHA;
- permanent Pi-reference configuration path;
- pre-pin/post-pin comparison result;
- preservation-ref type, name and SHA;
- preservation-commit SHA;
- cleanup-commit SHA;
- confirmation that cleanup commit directly descends from preservation commit;
- baseline vs final project/dependency metrics;
- frozen-hotspot dispositions;
- independent decisions for `JUSTIFIED` hotspots;
- major decompositions;
- abstractions simplified/removed;
- architecture rules added to CI;
- significant ADRs;
- final permanent `AGENTS.md` line count;
- test/conformance counts;
- stable test-preservation result;
- baseline-flaky tests/scenarios and final outcomes;
- invocation mappings used;
- supplemental post-tag mappings, if any;
- baseline-unrunnable suites;
- `test.pdf` baseline/final SHA-256;
- deferred parity gaps;
- latest observed `main` SHA;
- material `main` divergence;
- merge-tree integration-risk summary.

Classify current `main`'s Pi-reference **mechanism** as one of:

- `INTRODUCED_BY_REFACTOR`;
- `ALIGNED_WITH_CURRENT_MAIN`;
- `SUPERSEDES_CURRENT_MAIN`;
- `DUPLICATES_CURRENT_MAIN`;
- `CONFLICTS_WITH_CURRENT_MAIN`.

Separately classify the Pi-reference **value** as one of:

- `PIN_VALUE_MATCHES_MAIN`;
- `MAIN_PIN_ADVANCED`;
- `MAIN_HAS_NO_PIN`;
- `MAIN_PIN_VALUE_UNKNOWN`.

Also confirm:

- newer `main` was not silently integrated;
- frozen Pi SHA never changed;
- future pin-bump procedure is documented;
- architecture tests and docs validation are wired into normal verification;
- no expected logical tests were lost;
- temporary refactor files/tooling were removed;
- temporary analysis tooling left no accidental tracked dependency changes;
- final Phase F/G instructions remained recoverable through `<preservation-ref>`;
- `test.pdf` was untouched;
- final verification ran from a fresh checkout;
- preservation ref was created once and never moved;
- any retained WIP branch is identified by purpose and SHA;
- final state is clean except preserved user-owned files;
- branch was pushed but not merged into `main`.

The target is not maximal abstraction or architectural purity.

The target is a **simple, coherent, well-documented and maintainable .NET implementation of Pi whose architecture makes future parity work safer, easier and more predictable**.