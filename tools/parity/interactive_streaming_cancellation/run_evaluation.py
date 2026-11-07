#!/usr/bin/env python3
"""Generate and verify the streaming/cancellation evidence bundle."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

import evaluate
import verify_report

EVIDENCE_FILES = ['report.json', 'comparison.json', 'ui-report.json']


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def git(*arguments, cwd):
    return subprocess.run(['git', *arguments], cwd=cwd, check=True,
                          capture_output=True, text=True).stdout.strip()


def default_revisions(manifest):
    root = Path(__file__).resolve().parents[3]
    return dict(
        piReference=manifest.get('piReference'),
        baseMain=manifest.get('baseMain') or git('merge-base', 'HEAD', 'origin/main', cwd=root),
        branch=manifest.get('branch') or git('rev-parse', '--abbrev-ref', 'HEAD', cwd=root),
        captureSource='branch working tree',
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--artifacts', type=Path, required=True)
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--manifest', type=Path,
                        default=Path(__file__).with_name('scenarios.json'))
    parser.add_argument('--revisions', type=Path)
    args = parser.parse_args()

    manifest = json.loads(args.manifest.read_text())
    revisions = (json.loads(args.revisions.read_text()) if args.revisions
                 else default_revisions(manifest))
    report, comparison, ui_report = evaluate.build_evidence(manifest, args.artifacts, revisions)

    args.evidence.mkdir(parents=True, exist_ok=True)
    for name, document in (('report.json', report), ('comparison.json', comparison),
                           ('ui-report.json', ui_report)):
        (args.evidence / name).write_text(json.dumps(document, indent=2) + '\n')

    checks = verify_report.verify(args.evidence, args.manifest, require_verification=False)
    evidence_checksums = {name: digest(args.evidence / name) for name in EVIDENCE_FILES}
    verification = dict(
        fixture=manifest['fixture'],
        verifier='tools/parity/interactive_streaming_cancellation/verify_report.py',
        revisions=revisions,
        command='python3 tools/parity/interactive_streaming_cancellation/run_evaluation.py '
                '--artifacts artifacts/parity/interactive-streaming-cancellation-001 '
                '--evidence docs/parity/fixtures/interactive-streaming-cancellation-001',
        checks=checks,
        evidenceChecksums=evidence_checksums,
        passed=all(item['passed'] for item in checks),
    )
    (args.evidence / 'verification.json').write_text(json.dumps(verification, indent=2) + '\n')

    final_checks = verify_report.verify(args.evidence, args.manifest, require_verification=True)
    if not verification['passed'] or not all(item['passed'] for item in final_checks):
        verification['passed'] = False
        verification['checks'] = final_checks
        (args.evidence / 'verification.json').write_text(json.dumps(verification, indent=2) + '\n')
        print(json.dumps(dict(passed=False, failed=[
            item for item in final_checks if not item['passed']]), indent=2))
        return 1

    print(json.dumps(dict(
        passed=True,
        total=report['total'],
        maximum=report['assessedMaximum'],
        categoryTotals=report['categoryTotals'],
        findings=len(report['findings']),
        checks=len(final_checks),
    ), indent=2))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
