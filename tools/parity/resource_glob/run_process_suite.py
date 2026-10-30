#!/usr/bin/env python3
"""Execute every frozen user/project resource settings process case."""
import argparse
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    cases = json.loads((HERE / 'process_cases.json').read_text())
    reports = []
    runner = ROOT / 'tools/parity/resource-invocation-process-differential.py'
    for case in cases:
        output = args.output / f'{case["id"]}.json'
        subprocess.run([sys.executable, str(runner), '--pi', str(args.pi.resolve()),
                        '--scenario', case['id'], '--output', str(output)], cwd=ROOT, check=True)
        reports.append(output)
    verifier = HERE / 'acceptance.py'
    subprocess.run([sys.executable, str(verifier), 'process', '--source-sha',
                    subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
                    *sum((['--report', str(path)] for path in reports), [])], cwd=ROOT, check=True)


if __name__ == '__main__':
    main()
