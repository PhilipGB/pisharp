#!/usr/bin/env python3
"""Freeze the acceptance manifest from the pinned-Pi column of a calibrated report."""
import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.path.insert(0, str(HERE))
from acceptance_cases import CASES, DEFERRED  # noqa: E402

PI_SHA = (ROOT / 'tools/parity/pi-reference.txt').read_text().split()[0].strip()
SIZES = ('80x24', '120x40')
OUTPUT = ROOT / 'docs/parity/fixtures/tui-editor-completion-paste-001/acceptance.json'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('report', type=Path)
    parser.add_argument('--output', type=Path, default=OUTPUT)
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding='utf-8'))
    if report.get('piSha') != PI_SHA:
        raise SystemExit('Report pins a different Pi revision.')
    entries = {(entry['id'], entry['size']): entry for entry in report['scenarios']}
    cases = []
    for case in CASES:
        expectations = {}
        for size in SIZES:
            entry = entries.get((case['scenario'], size))
            if entry is None:
                raise SystemExit(f"Missing evidence for {case['scenario']}/{size}.")
            if not (entry.get('calibration') or {}).get('match'):
                raise SystemExit(f"Same-Pi calibration failed for {case['scenario']}/{size}.")
            if entry.get('status') != 'pass':
                raise SystemExit(f"{case['scenario']}/{size} did not pass.")
            if entry['pi'] != entry['pisharp']:
                raise SystemExit(f"{case['scenario']}/{size} products differ.")
            observations = entry['pi']['checkpoints']
            for checkpoint in case['checkpoints']:
                if checkpoint not in observations:
                    raise SystemExit(f"Missing checkpoint {checkpoint} in {case['scenario']}/{size}.")
                for field in case['fields']:
                    value = observations[checkpoint][field]
                    if field in expectations and expectations[field] != value:
                        raise SystemExit(
                            f'{case["id"]} {field} differs between sizes; freeze a narrower case.')
                    if field not in expectations:
                        expectations[field] = value
            if case.get('providerTexts'):
                texts = entry['pi']['providerTexts']
                if not texts:
                    raise SystemExit(f'{case["id"]} has no provider-visible text.')
                if 'providerTexts' in expectations and expectations['providerTexts'] != texts:
                    raise SystemExit(f'{case["id"]} provider text differs between sizes.')
                expectations['providerTexts'] = texts
        cases.append(dict(id=case['id'], area=case['area'], mandatory=True, status='pass',
                          scenario=case['scenario'], checkpoints=case['checkpoints'],
                          oracle=case['oracle'], expect=expectations))
    manifest = dict(schema=1, capability='tui-editor-completion-paste', piSha=PI_SHA,
                    cases=cases, deferred=DEFERRED)
    args.output.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + '\n',
                           encoding='utf-8')
    print(f'Wrote {len(cases)} frozen cases to {args.output}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
