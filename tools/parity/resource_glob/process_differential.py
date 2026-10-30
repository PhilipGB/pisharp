#!/usr/bin/env python3
"""Run frozen local-settings CLI scenarios in pinned Pi and PiSharp processes."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[3]
CASE_INDEX = Path(__file__).with_name('process_cases.json')
CASE_INDEX_SHA256 = '47204f1d8eb1e3c5d66f07c8faea6aa0707486d3033af49a63b73b348a633186'
PI_PROCESS = ROOT / 'tools/parity/resource-invocation-process-differential.py'
DEFAULT_PISHARP = ROOT / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll'


def run(command):
    result = subprocess.run(command, cwd=ROOT, text=True, capture_output=True)
    if result.returncode:
        raise RuntimeError(f"command failed ({result.returncode}): {' '.join(command)}\n{result.stdout}{result.stderr}")
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--pisharp', type=Path, default=DEFAULT_PISHARP)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    cases = json.loads(CASE_INDEX.read_text())
    ids = [case['id'] for case in cases]
    if len(ids) != len(set(ids)):
        raise SystemExit('duplicate process case IDs')
    index_sha = hashlib.sha256(CASE_INDEX.read_bytes()).hexdigest()
    if index_sha != CASE_INDEX_SHA256:
        raise SystemExit(f'process case index SHA-256 mismatch: expected {CASE_INDEX_SHA256}, found {index_sha}')

    results = []
    with tempfile.TemporaryDirectory(prefix='pisharp-resource-settings-process-') as temporary:
        for case in cases:
            output = Path(temporary) / f'{case["id"]}.json'
            run(['python3', str(PI_PROCESS), '--pi', str(args.pi.resolve()), '--pisharp', str(args.pisharp.resolve()),
                 '--scenario', case['id'], '--output', str(output)])
            observed = json.loads(output.read_text())
            if observed['piSha'] != 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6':
                raise SystemExit(f"wrong Pi source for {case['id']}: {observed['piSha']}")
            pi = observed['results']['pi']['settingsSelection']
            pisharp = observed['results']['pisharp']['settingsSelection']
            passed = pi == case['expectedPi'] == pisharp
            record = {**case, 'piActual': pi, 'piSharpActual': pisharp}
            if case['id'] == 'settings-extensions':
                pi_extensions = observed['results']['pi']['localExtensionSelection']
                pisharp_extensions = observed['results']['pisharp']['localExtensionSelection']
                record['piExtensionsActual'] = pi_extensions
                record['piSharpExtensionsActual'] = pisharp_extensions
                passed = passed and pi_extensions == case['expectedExtensions'] == pisharp_extensions
            record['status'] = 'passed' if passed else 'failed'
            results.append(record)

    report = {'piSha': 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6',
              'piSharpHead': run(['git', 'rev-parse', 'HEAD']).strip(),
              'caseIndexSha256': index_sha, 'required': len(results),
              'passed': sum(case['status'] == 'passed' for case in results),
              'failed': sum(case['status'] == 'failed' for case in results), 'skipped': 0,
              'cases': results}
    serialized = json.dumps(report, indent=2) + '\n'
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(serialized)
    print(serialized, end='')
    if report['failed']:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
