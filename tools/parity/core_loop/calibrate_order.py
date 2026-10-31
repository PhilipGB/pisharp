#!/usr/bin/env python3
"""Pi-vs-Pi and PiSharp-vs-PiSharp ordering calibration.

Runs each selected case several times for both products and reports, per ordered observable:

- ``piStable``       - Pi produced the same ordered value every run.
- ``pisharpStable``  - PiSharp produced the same ordered value every run.

An observable that varies in Pi is genuinely nondeterministic, so the permitted alternatives are
frozen in ``order_calibration.json`` and compared as a set of allowed ordered values. An
observable that is stable in Pi is compared strictly: a PiSharp difference there is a real parity
gap to fix, and a PiSharp observable that varies only in PiSharp is a PiSharp defect, not a
justified alternative.
"""

import argparse
import json
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


def observe(case, product, pi, dll):
    with tempfile.TemporaryDirectory(prefix=f'cal-{case["id"]}-{product}-') as directory:
        return project_observation(run_case(case, product, pi, dll, Path(directory)))


def distinct(runs, key):
    values = []
    for run in runs:
        value = run.get(key)
        if value not in values:
            values.append(value)
    return values


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runs', type=int, default=3)
    parser.add_argument('--cases', nargs='*', default=None)
    parser.add_argument('--pi', default=str(PI_DEFAULT))
    parser.add_argument('--pisharp-dll', default=str(PISHARP_DLL))
    parser.add_argument('--write', action='store_true',
                        help='freeze permitted alternatives for observables that vary in Pi')
    args = parser.parse_args()

    wanted = set(args.cases) if args.cases else None
    cases = [case for case in CASES if wanted is None or case['id'] in wanted]
    permitted = {}
    pi_unstable, pisharp_unstable, gaps = [], [], []
    for case in cases:
        pi_runs = [observe(case, 'pi', Path(args.pi), Path(args.pisharp_dll)) for _ in range(args.runs)]
        sharp_runs = [observe(case, 'pisharp', Path(args.pi), Path(args.pisharp_dll))
                      for _ in range(args.runs)]
        frozen = {}
        for key in ORDERED_KEYS:
            pi_values = distinct(pi_runs, key)
            sharp_values = distinct(sharp_runs, key)
            if len(sharp_values) > 1:
                pisharp_unstable.append(f'{case["id"]}.{key}')
            if len(pi_values) > 1:
                pi_unstable.append(f'{case["id"]}.{key}')
                frozen[key] = pi_values
            elif sharp_values != pi_values:
                gaps.append(f'{case["id"]}.{key}')
        if frozen:
            permitted[case['id']] = frozen
        print(f'{case["id"]}: pi={"stable" if not frozen else "varies"} '
              f'pisharp={"stable" if len(distinct(sharp_runs, "effects")) < 2 and len(distinct(sharp_runs, "mcpCalls")) < 2 else "varies"} '
              f'{"GAP" if any(g.startswith(case["id"] + ".") for g in gaps) else "ok"}')

    print()
    if pi_unstable:
        print(f'nondeterministic in Pi (freeze alternatives): {" ".join(pi_unstable)}')
    if pisharp_unstable:
        print(f'nondeterministic in PiSharp (defect): {" ".join(pisharp_unstable)}')
    if gaps:
        print(f'stable-but-different (parity gaps): {" ".join(gaps)}')
    if not pi_unstable and not pisharp_unstable and not gaps:
        print('every ordered observable is stable and agrees; compare strictly')

    if args.write:
        write_json(CALIBRATION_PATH, dict(runs=args.runs, piSha=_git_sha(args.pi),
                                          pisharpSha=_git_sha(ROOT),
                                          permittedAlternatives=permitted))
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
