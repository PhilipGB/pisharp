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
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
ARTIFACTS = ROOT / 'artifacts/parity/core-loop'
MILESTONE_DIR = 'core-loop-active-queue-parity-2026-10-31'
TARGET = ROOT / 'docs/parity/fixtures' / MILESTONE_DIR / 'evidence'


def main():
    if not ARTIFACTS.exists():
        print(f'error: {ARTIFACTS} missing', file=sys.stderr)
        return 1
    if TARGET.exists():
        shutil.rmtree(TARGET)
    TARGET.mkdir(parents=True)
    total = {'match': 0, 'differ': 0, 'one-sided': 0, 'error': 0}
    count = 0
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
    summary = {'total': count, **total}
    (TARGET / '_summary.json').write_text(json.dumps(summary, indent=2) + '\n', encoding='utf-8')
    print(f'wrote {count} compact evidence files to {TARGET}')
    print(f'totals: {json.dumps(total)}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
