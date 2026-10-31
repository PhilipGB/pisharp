#!/usr/bin/env python3
"""Ordering calibration for the core-loop differential.

Diagnostic only. It answers one question per ordered observable: is the ordering that Pi produces
*itself* stable, and therefore part of the contract, or does Pi vary it? It deliberately does not
define the acceptance contract: when a nondeterministic ordering needs permitted alternatives, the
alternatives must be derived from the pinned Pi implementation and then verified by executions
covering more than one scheduling condition, and the resulting set must be frozen in the
machine-readable acceptance contract bound to the pinned Pi SHA.

Runs are spread across scheduling conditions (the harness caps child worker/CPU budgets, so the
same case can run with one or two CPUs). A single configuration repeating the same order many
times is not evidence that the order is the only possible one.

Classification per observable:

- ``pi-varies``    - Pi produced more than one order. Permitted alternatives are plausible; derive
                     them from the implementation before freezing anything.
- ``gap``          - Pi was stable and PiSharp differed. A real parity gap to fix.
- ``pisharp-varies`` - Pi was stable and PiSharp varied. A PiSharp defect; do not freeze it.
"""

import argparse
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from cases import CASES  # noqa: E402
from compare import project_observation  # noqa: E402
from harness import ROOT, run_case  # noqa: E402
from run_differential import PI_DEFAULT, PISHARP_DLL, write_json  # noqa: E402

CALIBRATION_PATH = Path(__file__).resolve().parent / 'order_calibration.json'
ORDERED_KEYS = ('effects', 'mcpCalls')
CPU_LIMIT_VARIABLE = 'PISHARP_PARITY_CPU_LIMIT'


def observe(case, product, pi, dll, cpu_limit):
    previous = os.environ.get(CPU_LIMIT_VARIABLE)
    os.environ[CPU_LIMIT_VARIABLE] = str(cpu_limit)
    try:
        with tempfile.TemporaryDirectory(prefix=f'cal-{case["id"]}-{product}-') as directory:
            return project_observation(run_case(case, product, pi, dll, Path(directory)))
    finally:
        if previous is None:
            os.environ.pop(CPU_LIMIT_VARIABLE, None)
        else:
            os.environ[CPU_LIMIT_VARIABLE] = previous


def collect(runs, key):
    """Distinct ordered values for one observable, with the conditions that produced them."""
    values = []
    for value, condition in ((run.get(key), run['condition']) for run in runs):
        for entry in values:
            if entry['value'] == value:
                entry['conditions'].append(condition)
                break
        else:
            values.append(dict(value=value, conditions=[condition]))
    return values


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runs', type=int, default=15,
                        help='runs per product per case, cycling through the CPU conditions')
    parser.add_argument('--cpu-limits', nargs='+', default=['1', '2'],
                        help='scheduling conditions to cycle through')
    parser.add_argument('--cases', nargs='*', default=None)
    parser.add_argument('--pi', default=str(PI_DEFAULT))
    parser.add_argument('--pisharp-dll', default=str(PISHARP_DLL))
    parser.add_argument('--write', action='store_true',
                        help='write the diagnostic report (never the acceptance contract)')
    args = parser.parse_args()

    wanted = set(args.cases) if args.cases else None
    cases = [case for case in CASES if wanted is None or case['id'] in wanted]
    report = {}
    findings = []
    for case in cases:
        pi_runs, sharp_runs = [], []
        for index in range(args.runs):
            condition = args.cpu_limits[index % len(args.cpu_limits)]
            pi_runs.append(dict(observe(case, 'pi', Path(args.pi), Path(args.pisharp_dll), condition),
                                condition=f'cpus={condition}'))
            sharp_runs.append(dict(observe(case, 'pisharp', Path(args.pi), Path(args.pisharp_dll),
                                          condition), condition=f'cpus={condition}'))

        case_report = {}
        for key in ORDERED_KEYS:
            pi_values = collect(pi_runs, key)
            sharp_values = collect(sharp_runs, key)
            if len(pi_values) > 1:
                verdict = 'pi-varies'
            elif [entry['value'] for entry in sharp_values] != [entry['value'] for entry in pi_values]:
                verdict = 'pisharp-varies' if len(sharp_values) > 1 else 'gap'
            elif len(sharp_values) > 1:
                verdict = 'pisharp-varies'
            else:
                verdict = 'stable'
            if verdict != 'stable':
                case_report[key] = dict(
                    verdict=verdict,
                    piOrders=[entry['value'] for entry in pi_values],
                    pisharpOrders=[entry['value'] for entry in sharp_values],
                    piConditions=[entry['conditions'] for entry in pi_values],
                )
                findings.append(f'{case["id"]}.{key}:{verdict}')
        if case_report:
            report[case['id']] = case_report
            detail = ", ".join(f"{key}={entry['verdict']}" for key, entry in case_report.items())
            print(f'{case["id"]}: {detail}')
        else:
            print(f'{case["id"]}: stable across {args.runs} runs '
                  f'({", ".join(f"cpus={limit}" for limit in args.cpu_limits)})')

    print()
    if findings:
        print(f'findings: {" ".join(findings)}')
    else:
        print('every ordered observable is stable and agrees; compare them strictly')
    print(f'note: {args.runs} runs per product across {len(args.cpu_limits)} scheduling '
          f'condition(s) is discovery evidence, not a frozen contract')

    if args.write:
        write_json(CALIBRATION_PATH, dict(
            runs=args.runs, cpuLimits=args.cpu_limits, piSha=_git_sha(args.pi),
            pisharpSha=_git_sha(ROOT), findings=findings, cases=report))
        print(f'wrote {CALIBRATION_PATH.relative_to(ROOT)}')
    return 0


def _git_sha(path):
    try:
        return subprocess.run(['git', '-C', str(path), 'rev-parse', 'HEAD'], capture_output=True,
                              text=True, check=True).stdout.strip()
    except Exception:
        return None


if __name__ == '__main__':
    sys.exit(main())
