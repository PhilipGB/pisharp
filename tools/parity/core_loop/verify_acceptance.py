#!/usr/bin/env python3
"""Machine-verifiable acceptance contract for the core-loop active-queue parity milestone.

The acceptance contract (a committed JSON file) freezes:
  * the checkpoint id,
  * the pinned Pi source SHA,
  * the PiSharp source SHA the evidence was produced from,
  * the comparison mode,
  * every required case id with its evidence file and expected outcome,
  * the frozen case totals.

The verifier fails (exit 1) if any of the following holds:
  * a mandatory case is absent (no evidence file),
  * a mandatory case is skipped (no comparison.json produced),
  * a required report is missing,
  * the evidence belongs to the wrong Pi SHA,
  * the evidence belongs to the wrong PiSharp SHA,
  * a required result is unresolved (one-sided / error / absent status),
  * the observed case totals do not match the frozen contract.

It never relies on a summary file alone: every claim is re-derived from the
per-case evidence files.

Usage:
    python3 verify_acceptance.py --contract <acceptance.json> [--strict]

Exit status 0 means the contract is satisfied by the evidence; non-zero means
at least one check failed (the failures are printed to stdout).
"""
import argparse
import json
import sys
from pathlib import Path

# Outcomes that count as a resolved, acceptable result for a mandatory case.
RESOLVED_STATUSES = {'match', 'differ'}
# Outcomes that are never acceptable for a mandatory case.
UNRESOLVED_STATUSES = {'one-sided', 'error', 'missing', 'skipped'}


def load_json(path):
    with open(path, encoding='utf-8') as handle:
        return json.load(handle)


def verify(contract_path):
    contract_path = Path(contract_path)
    contract = load_json(contract_path)
    contract_dir = contract_path.parent

    failures = []
    expected_total = int(contract['summary']['total'])
    expected_match = int(contract['summary']['match'])
    expected_safety = int(contract['summary'].get('intentionalSafety', 0))
    expected_unresolved = int(contract['summary'].get('unresolved', 0))

    observed_match = 0
    observed_safety = 0
    observed_unresolved = 0
    observed_total = 0

    for entry in contract['cases']:
        case_id = entry['id']
        mandatory = bool(entry.get('mandatory', True))
        expected_status = entry['expectedStatus']
        evidence_rel = entry['evidence']
        evidence_path = (contract_dir / evidence_rel)

        observed_total += 1

        if not evidence_path.exists():
            failures.append(f'{case_id}: evidence file missing: {evidence_rel}')
            if mandatory:
                observed_unresolved += 1
            continue

        try:
            comparison = load_json(evidence_path)
        except (json.JSONDecodeError, OSError) as exc:
            failures.append(f'{case_id}: evidence unreadable: {exc}')
            if mandatory:
                observed_unresolved += 1
            continue

        # The evidence must describe this exact case.
        if comparison.get('case') != case_id:
            failures.append(f'{case_id}: evidence case id mismatch: {comparison.get("case")}')

        # The evidence must belong to the pinned Pi source.
        if comparison.get('piSha') != contract['piSha']:
            failures.append(
                f'{case_id}: evidence Pi SHA {comparison.get("piSha")} != contract {contract["piSha"]}')

        # The evidence must belong to the PiSharp source SHA the milestone shipped.
        if comparison.get('pisharpSha') != contract['piSharpSha']:
            failures.append(
                f'{case_id}: evidence PiSharp SHA {comparison.get("pisharpSha")} != contract '
                f'{contract["piSharpSha"]}')

        status = comparison.get('status')
        if status in UNRESOLVED_STATUSES or status not in RESOLVED_STATUSES:
            failures.append(f'{case_id}: unresolved status: {status!r}')
            if mandatory:
                observed_unresolved += 1
            continue

        if status == 'match':
            observed_match += 1
            if expected_status != 'match':
                failures.append(f'{case_id}: status match but contract expects {expected_status}')
        else:  # differ
            if expected_status == 'intentional-safety':
                observed_safety += 1
                # An intentional safety difference must name the justification.
                if not entry.get('justification'):
                    failures.append(f'{case_id}: intentional-safety case lacks a justification')
            elif expected_status == 'match':
                failures.append(f'{case_id}: status differ but contract expects match')
            else:
                failures.append(f'{case_id}: unexpected contract status {expected_status}')

    # Frozen totals must match the observed evidence.
    if observed_total != expected_total:
        failures.append(f'total mismatch: observed {observed_total} != frozen {expected_total}')
    if observed_match != expected_match:
        failures.append(f'match mismatch: observed {observed_match} != frozen {expected_match}')
    if observed_safety != expected_safety:
        failures.append(
            f'intentionalSafety mismatch: observed {observed_safety} != frozen {expected_safety}')
    if observed_unresolved != expected_unresolved:
        failures.append(
            f'unresolved mismatch: observed {observed_unresolved} != frozen {expected_unresolved}')
    if expected_unresolved != 0:
        failures.append(f'frozen contract declares {expected_unresolved} unresolved mandatory cases')

    return failures


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--contract', required=True, help='Path to the acceptance contract JSON.')
    args = parser.parse_args(argv)

    failures = verify(args.contract)
    if failures:
        print(f'ACCEPTANCE FAILED ({len(failures)} violation(s)):')
        for failure in failures:
            print(f'  - {failure}')
        return 1
    print('ACCEPTANCE PASSED: contract satisfied by per-case evidence.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
