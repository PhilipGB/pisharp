# Objective agent parity — ENV-PARSER-001

This milestone measures whether Pi and PiSharp can independently complete the
same small coding task, whether their agent loops handle tools and continuation
correctly, and whether their terminals present the same meaningful progress
states. It is deliberately independent of the normal PiSharp test suite.

## Evidence layers

1. **Real-model runs** — five sequential runs per application with Luna through
   each application's existing OpenAI subscription authentication, `xhigh`
   reasoning, the exact prompt in `run_real.py`, a ten-minute monotonic limit,
   and identical process limits. Each run is restored from the verified fixture
   baseline and evaluated from its saved production source, never from the
   agent's final prose.
2. **Deterministic provider contract** — `contract_eval.py` serves one scripted
   bash tool call and one final answer through a local OpenAI-compatible server.
   The server retains the exact provider request bodies, so continuation is
   checked at the wire boundary for both products.
3. **Deterministic terminal parity** — the reusable terminal harness runs
   `tools/parity/terminal/fixtures/objective-agent-progress.json` at 80×24 and
   120×40 after a same-Pi calibration. `ui_score.py` scores five progress
   checkpoints (10 points) and decoded cells/styles (10 points) using only the
   rubric's allowed normalisation.

## Fixture

`fixture/ENV-PARSER-001/` is a standalone .NET 10 xUnit project. Its only
intentional production defect is in `EnvFile.Parse`: `line.Split('=')` accepts a
line only when the split produces exactly two elements, so `TOKEN=abc=def` is
discarded. The documented behaviour and the three visible tests live with the
fixture; the third visible test fails on the baseline.

`baseline.json` records the exact baseline manifest/checksum and the observed
baseline result (build succeeds, two tests pass, one intended test fails).

`private/` is the independent evaluator. It is never copied into the agent
workspace. H01-H08 are compiled against a copy of the final production source
in an isolated directory.

## Commands

```sh
# Baseline and private oracle tests
python3 -m unittest tools/parity/objective_agent_parity/test_objective_agent_parity.py

# Deterministic agent-loop/provider contract
python3 tools/parity/objective_agent_parity/contract_eval.py \
  --output artifacts/parity/objective-agent-contract

# Five real runs per application (raw traces remain under artifacts/parity/)
python3 tools/parity/objective_agent_parity/run_real.py --runs 5 \
  --output artifacts/parity/objective-agent-parity-20261106

# Deterministic terminal calibration, comparison and scoring
python3 tools/parity/objective_agent_parity/ui_capture.py \
  --output artifacts/parity/objective-agent-ui-20261106

# Aggregate and verify
python3 tools/parity/objective_agent_parity/make_report.py \
  --runs artifacts/parity/objective-agent-parity-20261106 \
  --ui artifacts/parity/objective-agent-ui-20261106/ui-report.json \
  --contract artifacts/parity/objective-agent-contract/contract-report.json \
  --output artifacts/parity/objective-agent-parity-20261106/report.json
python3 tools/parity/objective_agent_parity/verify_report.py \
  artifacts/parity/objective-agent-parity-20261106/report.json

# Publish compact, credential-free evidence into docs/parity/fixtures/
python3 tools/parity/objective_agent_parity/publish_evidence.py \
  --report artifacts/parity/objective-agent-parity-20261106/report.json \
  --ui artifacts/parity/objective-agent-ui-20261106/ui-report.json \
  --contract artifacts/parity/objective-agent-contract-20261106/contract-report.json \
  --verification artifacts/parity/objective-agent-parity-20261106/verification.json \
  --output docs/parity/fixtures/objective-agent-parity-2026-11-06
```

Each real run applies the same 600 s monotonic deadline, 605 s CPU limit,
8 GiB virtual-address limit and 4096-file descriptor limit. The recorded
2026-11-06 evaluation exercised compiler memory pressure from that address-space
limit in both products; both recovered, reported the workaround and completed
the task, so no run was excluded.

## Scoring

- Functional correctness: 50 points (H01-H08 at 5 each, visible suite 5,
  change integrity 5).
- Agent-loop/tool correctness: 30 points, six independent 5-point checks.
- Deterministic UI: 20 points, independent of live model text.

Real-model means use assessable runs only. Blocked and timed-out runs are
reported as such and are never scored as zero. The report verifier rejects
falsified score arithmetic, missing runs, altered fixture checksums, forged
change integrity and inconsistent UI totals. `verify_report.py` runs those
negative controls on every invocation.

## Evidence policy

Raw streams, session documents and full workspace snapshots stay under
`artifacts/parity/` (Git ignored). Review the compact `report.json`,
`ui-report.json`, `contract-report.json` and the milestone receipt under
`docs/parity/fixtures/`; keep credentials out of both locations.
