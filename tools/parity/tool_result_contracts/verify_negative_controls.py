#!/usr/bin/env python3
"""Negative controls for the tool-result-contracts acceptance verifier.

Proves the verifier rejects tampered evidence, including provider-bound result
identity, order and error state:

  * a missing case evidence file is rejected,
  * an altered provider-bound result call ID is rejected,
  * reordered provider-bound results are rejected,
  * an altered provider-bound error state is rejected,
  * missing provider-bound results are rejected,
  * an altered canonical result error state is rejected.

Usage:
    python3 verify_negative_controls.py [--manifest <acceptance.json>]

Exit status 0 means every control behaved as expected; non-zero means the
verifier accepted at least one tampered evidence file.
"""
import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
VERIFIER = ROOT / 'tools/parity/tool_result_contracts/verify.py'
DEFAULT_MANIFEST = ROOT / 'docs/parity/fixtures/tool-result-contracts/acceptance-manifest.json'


def load(path):
    with Path(path).open(encoding='utf-8') as handle:
        return json.load(handle)


def write(path, value):
    Path(path).write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')


def run_verifier(manifest_path, verifier):
    result = subprocess.run(
        [sys.executable, str(verifier), '--repo', str(ROOT), '--manifest', str(manifest_path)],
        capture_output=True, text=True)
    return result.returncode, (result.stdout + result.stderr)


def evidence_with_provider_results(evidence, minimum=1):
    for path in sorted(evidence.glob('*.json')):
        payload = load(path)
        for request in payload.get('observations', {}).get('providerRequests', []):
            if len(request.get('toolResults', [])) >= minimum:
                return path, payload, request
    raise SystemExit('no evidence file with provider-bound results')


def with_tamper(manifest_path, tamper, name, verifier):
    manifest = load(manifest_path)
    evidence = Path(manifest['evidenceDirectory'])
    if not evidence.is_absolute():
        evidence = ROOT / evidence
    with tempfile.TemporaryDirectory(prefix=f'negctl-{name}-') as tmp:
        tmp_path = Path(tmp)
        manifest_copy = tmp_path / 'acceptance-manifest.json'
        evidence_copy = tmp_path / 'evidence'
        shutil.copytree(evidence, evidence_copy)
        manifest['evidenceDirectory'] = str(evidence_copy)
        tamper(manifest, evidence_copy)
        write(manifest_copy, manifest)
        return run_verifier(manifest_copy, verifier)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument('--verifier', type=Path, default=VERIFIER)
    args = parser.parse_args(argv)
    manifest_path = args.manifest.resolve()
    verifier = args.verifier.resolve()

    def tamper_drop_case(manifest, evidence):
        dropped = manifest['cases'].pop()
        dropped_file = evidence / f"{dropped['id']}.json"
        if dropped_file.exists():
            dropped_file.unlink()
        manifest['summary']['total'] -= 1

    def tamper_provider_call_id(manifest, evidence):
        path, payload, request = evidence_with_provider_results(evidence)
        request['toolResults'][0]['callId'] = 'forged-call'
        write(path, payload)

    def tamper_provider_order(manifest, evidence):
        path, payload, request = evidence_with_provider_results(evidence, minimum=2)
        request['toolResults'].reverse()
        write(path, payload)

    def tamper_provider_error(manifest, evidence):
        path, payload, request = evidence_with_provider_results(evidence)
        request['toolResults'][0]['isError'] = not request['toolResults'][0]['isError']
        write(path, payload)

    def tamper_missing_provider_results(manifest, evidence):
        path, payload, request = evidence_with_provider_results(evidence)
        request['toolResults'] = []
        write(path, payload)

    def tamper_canonical_error(manifest, evidence):
        for path in sorted(evidence.glob('*.json')):
            payload = load(path)
            canonical = payload.get('observations', {}).get('canonical', [])
            if canonical:
                canonical[0]['isError'] = not canonical[0]['isError']
                write(path, payload)
                return
        raise SystemExit('no canonical result evidence')

    controls = [
        ('drop-case', tamper_drop_case),
        ('provider-call-id', tamper_provider_call_id),
        ('provider-order', tamper_provider_order),
        ('provider-error', tamper_provider_error),
        ('provider-results-missing', tamper_missing_provider_results),
        ('canonical-error', tamper_canonical_error),
    ]

    ok = True
    for name, tamper in controls:
        code, output = with_tamper(manifest_path, tamper, name, verifier)
        rejected = code != 0
        ok &= rejected
        status = 'ok (rejected)' if rejected else 'FAIL (not rejected)'
        print(f'  {name}: {status}')
        for line in output.strip().splitlines()[:2]:
            print(f'      {line}')

    if not ok:
        print('NEGATIVE CONTROLS FAILED: the verifier accepted tampered evidence.')
        return 1
    print('NEGATIVE CONTROLS PASSED: the verifier rejected all tampered evidence.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
