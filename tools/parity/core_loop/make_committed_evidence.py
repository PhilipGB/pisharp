#!/usr/bin/env python3
"""Create compact, committable per-case evidence from the differential artifacts.

The full comparison.json files (with viewPi/viewPisharp observations) live in the
Git-ignored artifacts/parity/core-loop/ directory. This script writes compact
evidence files (case, title, status, piSha, pisharpSha, diffs) under
docs/parity/fixtures/<milestone-dir>/ so the acceptance contract and verifier can
be checked against committed evidence without the raw observations.
"""
import json
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
ARTIFACTS = ROOT / 'artifacts/parity/core-loop'
MILESTONE_DIR = 'core-loop-active-queue-parity-2026-10-31'
FIXTURES = ROOT / 'docs/parity/fixtures'
TARGET = FIXTURES / MILESTONE_DIR / 'evidence'
CONTRACT = FIXTURES / f'{MILESTONE_DIR}.json'

COMPARISON_NOTE = (
    'Compares externally observable behavior only: provider request message sequences, '
    'provider-visible tool calls/results, agent lifecycle events, queue update events, '
    'steering/follow-up counts, response ordering, cancellation/abort outcome, tool result/error '
    'projection, session entries and active branch state, persisted results, MCP side effects, and '
    'settlement. Every ordered observable is compared as observed: effects, lifecycle events and '
    'declared tool lists are never sorted, and there is no known-difference allowance. The one '
    'exception is the arrival order of MCP calls issued from a single assistant turn: both '
    'implementations dispatch those concurrently (pinned Pi maps over the call array and awaits '
    'Promise.all), and pinned Pi was measured producing both arrival orders for the same case, so '
    'that observable is compared as a multiset while the set of calls is still required to match. '
    'The system/context entry position relative to the first user '
    'prompt is a session-format detail compared as counted presence (system xN) rather than exact '
    'position. assistant is_error is projected from the explicit pisharp.isError flag (Pi records '
    'provider failure via stopReason, not assistant isError).')


def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT, text=True).strip()


def write_contract(entries, totals):
    shas = {entry['piSha'] for entry in entries}
    sharp_shas = {entry['pisharpSha'] for entry in entries}
    if len(shas) != 1 or len(sharp_shas) != 1:
        raise SystemExit(f'evidence mixes revisions: pi={sorted(shas)} pisharp={sorted(sharp_shas)}')
    contract = {
        'schema': 1,
        'milestone': 'core_loop_active_queue_parity',
        'piSha': shas.pop(),
        'piSharpSha': sharp_shas.pop(),
        'pisharpBranch': git('rev-parse', '--abbrev-ref', 'HEAD'),
        'pisharpBranchBaseSha': git('merge-base', 'HEAD', 'origin/main'),
        'comparisonMode': 'projected-observable',
        'orderingPolicy': 'strict-projected-equality',
        'orderingNote': (
            'Strict equality of every projected observable. Two normalisations are disclosed rather '
            'than implied: MCP call-arrival order is unordered within a single concurrent tool batch '
            '(calls issued from different assistant turns stay ordered), and the persisted '
            'system/context entry position is compared as counted presence rather than exact '
            'position.'),
        'allowDocumentedDifferences': False,
        'comparisonNote': COMPARISON_NOTE,
        'summary': {'total': len(entries), **totals, 'unresolved': 0},
        'cases': [{'id': entry['case'], 'title': entry['title'], 'mandatory': True,
                   'expectedStatus': entry['status'],
                   'evidence': f'{MILESTONE_DIR}/evidence/{entry["case"]}.json'}
                  for entry in sorted(entries, key=lambda item: item['case'])],
    }
    CONTRACT.write_text(json.dumps(contract, indent=2) + '\n', encoding='utf-8')
    print(f'wrote acceptance contract {CONTRACT}')


def main():
    if not ARTIFACTS.exists():
        print(f'error: {ARTIFACTS} missing', file=sys.stderr)
        return 1
    if TARGET.exists():
        shutil.rmtree(TARGET)
    TARGET.mkdir(parents=True)
    total = {'match': 0, 'differ': 0, 'one-sided': 0, 'error': 0}
    count = 0
    compacts = []
    for case_dir in sorted(ARTIFACTS.iterdir()):
        comparison_path = case_dir / 'comparison.json'
        if not comparison_path.exists():
            continue
        comparison = json.loads(comparison_path.read_text(encoding='utf-8'))
        compact = {
            'case': comparison.get('case'),
            'title': comparison.get('title'),
            'status': comparison.get('status'),
            'piSha': comparison.get('piSha'),
            'pisharpSha': comparison.get('pisharpSha'),
            'diffs': comparison.get('diffs', []),
        }
        out = TARGET / f'{compact["case"]}.json'
        out.write_text(json.dumps(compact, indent=2) + '\n', encoding='utf-8')
        total[compact['status']] = total.get(compact['status'], 0) + 1
        count += 1
        compacts.append(compact)
    summary = {'total': count, **total}
    (TARGET / '_summary.json').write_text(json.dumps(summary, indent=2) + '\n', encoding='utf-8')
    print(f'wrote {count} compact evidence files to {TARGET}')
    print(f'totals: {json.dumps(total)}')
    write_contract(compacts, total)
    return 0


if __name__ == '__main__':
    sys.exit(main())
