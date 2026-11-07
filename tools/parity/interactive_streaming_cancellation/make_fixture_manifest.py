#!/usr/bin/env python3
"""Write the deterministic fixture manifest from the scored report."""
import argparse
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--manifest', type=Path,
                        default=Path(__file__).with_name('scenarios.json'))
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()

    manifest = json.loads(args.manifest.read_text())
    report = json.loads(args.report.read_text())
    fixture_root = Path(__file__).resolve().parents[1] / 'terminal' / 'fixtures' / manifest['fixture']
    scenarios = []
    for scenario_id, spec in manifest['scenarios'].items():
        fixture = fixture_root / spec['fixture']
        scenarios.append(dict(
            id=scenario_id,
            title=spec['title'],
            fixture=spec['fixture'],
            sha256=hashlib.sha256(fixture.read_bytes()).hexdigest(),
            checkpoints=spec['checkpoints'],
            forbiddenStreamTokens=spec.get('forbiddenStreamTokens', []),
            forbiddenStreamFrom=spec.get('forbiddenStreamFrom'),
        ))
    revisions = report.get('revisions', {})
    document = dict(
        schema=1,
        fixture=manifest['fixture'],
        title='Interactive streaming, reasoning, tool progress and cancellation parity',
        piReference=manifest['piReference'],
        baseMain=revisions.get('baseMain'),
        branch=revisions.get('branch'),
        captureSource=revisions.get('captureSource'),
        scenariosManifest='tools/parity/interactive_streaming_cancellation/scenarios.json',
        evaluate='tools/parity/interactive_streaming_cancellation/evaluate.py',
        verifier='tools/parity/interactive_streaming_cancellation/verify_report.py',
        dimensions=manifest['dimensions'],
        scenarios=scenarios,
        captureProvenance=report.get('captureProvenance'),
        normalization=manifest['normalization'],
        evidence=dict(
            report='report.json',
            comparison='comparison.json',
            uiReport='ui-report.json',
            verification='verification.json',
            readme='README.md',
        ),
        capture=dict(
            calibration='python3 tools/parity/terminal/run.py --pi /tmp/pisharp-current-pi '
                        '--fixture tools/parity/terminal/fixtures/interactive-streaming-cancellation-001/'
                        '<scenario>.json --calibrate --output '
                        'artifacts/parity/interactive-streaming-cancellation-001/calibration/<stem>',
            comparison='python3 tools/parity/terminal/run.py --pi /tmp/pisharp-current-pi '
                       '--pisharp src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll '
                       '--fixture tools/parity/terminal/fixtures/interactive-streaming-cancellation-001/'
                       '<scenario>.json --output '
                       'artifacts/parity/interactive-streaming-cancellation-001/comparison/<stem>',
            evaluate='python3 tools/parity/interactive_streaming_cancellation/run_evaluation.py '
                     '--artifacts artifacts/parity/interactive-streaming-cancellation-001 '
                     '--evidence docs/parity/fixtures/interactive-streaming-cancellation-001',
            verify='python3 tools/parity/interactive_streaming_cancellation/verify_report.py '
                   '--evidence docs/parity/fixtures/interactive-streaming-cancellation-001 '
                   '--require-verification',
            tests='python3 -m unittest discover -s tools/parity/interactive_streaming_cancellation '
                  "-p 'test_*.py'",
        ),
        rawArtifacts='artifacts/parity/interactive-streaming-cancellation-001 (Git ignored)',
    )
    args.output.write_text(json.dumps(document, indent=2) + '\n')
    print(args.output)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
