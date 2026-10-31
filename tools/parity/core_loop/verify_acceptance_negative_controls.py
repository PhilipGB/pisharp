#!/usr/bin/env python3
"""Negative controls for the core-loop acceptance verifier.

Proves the verifier rejects incomplete or stale evidence, so a summary file or a
forged/stale per-case file cannot claim acceptance:

  * a mandatory case with no evidence file is rejected,
  * a case whose evidence status is unresolved (one-sided) is rejected,
  * evidence belonging to the wrong Pi SHA is rejected,
  * evidence belonging to the wrong PiSharp SHA is rejected,
  * a frozen total that does not match the evidence is rejected,
  * a differ case whose expected status is 'match' is rejected,
  * a known-difference case without a justification is rejected.

Usage:
    python3 verify_acceptance_negative_controls.py --contract <acceptance.json>

Exit status 0 means every negative control behaved as expected (the verifier
rejected the tampered evidence); non-zero means at least one control did not.
"""
import argparse
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
VERIFIER = ROOT / 'tools/parity/core_loop/verify_acceptance.py'


def load(path):
    with open(path, encoding='utf-8') as handle:
        return json.load(handle)


def run_verifier(contract_path):
    result = subprocess.run(
        [sys.executable, str(VERIFIER), '--contract', str(contract_path)],
        capture_output=True, text=True)
    return result.returncode, (result.stdout + result.stderr)


def copy_tree(src, dst):
    if dst.exists():
        shutil.rmtree(dst)
    shutil.copytree(src, dst)


def with_tamper(contract_path, evidence_dir, tamper, name):
    """Copy the contract + evidence into a temp dir, apply the tamper, run the verifier."""
    with tempfile.TemporaryDirectory(prefix=f'negctl-{name}-') as tmp:
        tmp_path = Path(tmp)
        contract_copy = tmp_path / 'contract.json'
        evidence_copy = tmp_path / 'evidence'
        shutil.copy(contract_path, contract_copy)
        copy_tree(evidence_dir, evidence_copy)
        contract = load(contract_copy)
        # Point every evidence path at the temp evidence copy.
        for entry in contract['cases']:
            entry['evidence'] = f'evidence/{entry["id"]}.json'
        tamper(contract, evidence_copy)
        contract_copy.write_text(json.dumps(contract, indent=2) + '\n', encoding='utf-8')
        code, output = run_verifier(contract_copy)
        return code, output


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--contract', required=True)
    args = parser.parse_args(argv)

    contract_path = Path(args.contract).resolve()
    contract = load(contract_path)
    evidence_dir = contract_path.parent / 'core-loop-active-queue-parity-2026-10-31/evidence'
    if not evidence_dir.exists():
        print(f'error: evidence directory missing: {evidence_dir}', file=sys.stderr)
        return 2

    controls = []

    def expect_reject(name, tamper):
        code, output = with_tamper(contract_path, evidence_dir, tamper, name)
        rejected = code != 0
        controls.append((name, rejected, output.strip().splitlines()[:2]))
        return rejected

    def tamper_drop_case(contract, evidence):
        dropped = contract['cases'].pop()
        contract['summary']['total'] -= 1
        evidence_file = evidence / f"{dropped['id']}.json"
        if evidence_file.exists():
            evidence_file.unlink()

    def tamper_unresolved(contract, evidence):
        target = next((e for e in contract['cases'] if e['expectedStatus'] == 'match'), None)
        if target is None:
            raise SystemExit('no match case to mark unresolved')
        evidence_file = evidence / f"{target['id']}.json"
        data = load(evidence_file)
        data['status'] = 'one-sided'
        evidence_file.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')

    def tamper_wrong_pi(contract, evidence):
        for entry in contract['cases']:
            evidence_file = evidence / f"{entry['id']}.json"
            data = load(evidence_file)
            data['piSha'] = '0' * 40
            evidence_file.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')

    def tamper_wrong_pisharp(contract, evidence):
        for entry in contract['cases']:
            evidence_file = evidence / f"{entry['id']}.json"
            data = load(evidence_file)
            data['pisharpSha'] = 'f' * 40
            evidence_file.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')

    def tamper_total_mismatch(contract, evidence):
        contract['summary']['total'] += 1

    def tamper_differ_as_match(contract, evidence):
        target = next((e for e in contract['cases'] if e['expectedStatus'] == 'known-difference'), None)
        if target is None:
            raise SystemExit('no differ case')
        target['expectedStatus'] = 'match'
        del target['justification']

    def tamper_missing_justification(contract, evidence):
        target = next((e for e in contract['cases'] if e['expectedStatus'] == 'known-difference'), None)
        if target is None:
            raise SystemExit('no differ case')
        del target['justification']

    ok = True
    ok &= expect_reject('drop-case', tamper_drop_case)
    ok &= expect_reject('unresolved-status', tamper_unresolved)
    ok &= expect_reject('wrong-pi-sha', tamper_wrong_pi)
    ok &= expect_reject('wrong-pisharp-sha', tamper_wrong_pisharp)
    ok &= expect_reject('total-mismatch', tamper_total_mismatch)
    ok &= expect_reject('differ-as-match', tamper_differ_as_match)
    ok &= expect_reject('missing-justification', tamper_missing_justification)

    for name, rejected, snippet in controls:
        status = 'ok (rejected)' if rejected else 'FAIL (not rejected)'
        print(f'  {name}: {status}')
        for line in snippet:
            print(f'      {line}')

    if not ok:
        print('NEGATIVE CONTROLS FAILED: the verifier accepted tampered evidence.')
        return 1
    print('NEGATIVE CONTROLS PASSED: the verifier rejected all tampered evidence.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
