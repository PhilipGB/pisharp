#!/usr/bin/env python3
"""Run the core-loop differential case matrix against pinned Pi and PiSharp.

Usage:
    python3 run_differential.py                        # run every case, both products
    python3 run_differential.py --cases A1 D3 H1       # a subset
    python3 run_differential.py --product pisharp      # refresh one side only
    Repeat the whole command for the concurrency-sensitive re-run requirement.

Evidence layout (default artifacts/parity/core-loop):
    <case>/pi.obs.json, <case>/pisharp.obs.json, <case>/comparison.json
    summary.json
"""
import argparse
import json
import os
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from cases import CASES  # noqa: E402
from compare import compare_case, project_observation, render_diff  # noqa: E402
from harness import ROOT, run_case  # noqa: E402

# The one place the pinned Pi checkout is resolved: --pi, then PISHARP_PI_CHECKOUT. There is no
# embedded developer path, so a checkout that is not configured is reported instead of guessed.
PI_DEFAULT = os.environ.get('PISHARP_PI_CHECKOUT')
PISHARP_DLL = ROOT / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll'


def run_product(case, product, pi, dll):
    with tempfile.TemporaryDirectory(prefix=f'cl-{case["id"]}-{product}-') as directory:
        return run_case(case, product, pi, dll, Path(directory))


def write_json(path, payload):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=1, sort_keys=True, default=str) + '\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', default=str(ROOT / 'artifacts/parity/core-loop'))
    parser.add_argument('--pi', default=PI_DEFAULT,
                        help='pinned Pi checkout (defaults to PISHARP_PI_CHECKOUT)')
    parser.add_argument('--pisharp-dll', default=str(PISHARP_DLL))
    parser.add_argument('--cases', nargs='*', default=None)
    parser.add_argument('--product', choices=('pi', 'pisharp'), default=None,
                        help='run only one product (obs are refreshed; comparison needs both)')
    parser.add_argument('--compare-only', action='store_true',
                        help='do not run products; re-compare saved obs and rewrite comparisons')
    args = parser.parse_args()
    if not args.pi and not args.compare_only:
        parser.error('a pinned Pi checkout is required: pass --pi or set PISHARP_PI_CHECKOUT')

    out = Path(args.out)
    if args.cases:
        wanted = set(args.cases)
        cases = [c for c in CASES if c['id'] in wanted]
    else:
        cases = CASES
    products = [args.product] if args.product else ['pi', 'pisharp']
    summary = {}
    failures = []

    if args.compare_only:
        for case in cases:
            entry = dict(id=case['id'], title=case['title'])
            try:
                observations = {}
                for product in ('pi', 'pisharp'):
                    path = out / case['id'] / f'{product}.obs.json'
                    if not path.exists():
                        raise RuntimeError(f'missing saved obs: {path}')
                    observations[product] = json.loads(path.read_text())
                pi_view = project_observation(observations['pi'])
                ps_view = project_observation(observations['pisharp'])
                diffs = compare_case(pi_view, ps_view)
                comparison = dict(
                    case=case['id'], title=case['title'],
                    status='match' if not diffs else 'differ',
                    diffs=diffs, diff=render_diff(pi_view, ps_view) if diffs else None,
                    piSha=observations['pi']['piSha'],
                    pisharpSha=observations['pisharp']['pisharpSha'],
                    viewPi=pi_view, viewPisharp=ps_view)
                write_json(out / case['id'] / 'comparison.json', comparison)
                entry['status'] = comparison['status']
            except Exception as exc:  # noqa: BLE001 - captured per case
                entry['status'] = 'error'
                entry['error'] = repr(exc)
                failures.append(f"{case['id']}: {exc}")
            summary[case['id']] = entry
            print(f"case {case['id']:<4} {entry['status']:<9} {case['title']}")
        _write_summary(out, summary)
        return 1 if failures else 0

    for case in cases:
        entry = dict(id=case['id'], title=case['title'], products={})
        try:
            observations = {}
            for product in products:
                started = time.monotonic()
                observations[product] = run_product(case, product, Path(args.pi), Path(args.pisharp_dll))
                observations[product]['__runtimeSeconds'] = round(time.monotonic() - started, 2)
                write_json(out / case['id'] / f'{product}.obs.json', observations[product])
                entry['products'][product] = 'ok'
            if 'pi' in observations and 'pisharp' in observations:
                pi_view = project_observation(observations['pi'])
                ps_view = project_observation(observations['pisharp'])
                diffs = compare_case(pi_view, ps_view)
                comparison = dict(
                    case=case['id'], title=case['title'],
                    status='match' if not diffs else 'differ',
                    diffs=diffs, diff=render_diff(pi_view, ps_view) if diffs else None,
                    piSha=observations['pi']['piSha'],
                    pisharpSha=observations['pisharp']['pisharpSha'],
                    viewPi=pi_view, viewPisharp=ps_view)
                write_json(out / case['id'] / 'comparison.json', comparison)
                entry['status'] = comparison['status']
            else:
                entry['status'] = 'one-sided'
        except Exception as exc:  # noqa: BLE001 - captured per case
            entry['status'] = 'error'
            entry['error'] = repr(exc)
            failures.append(f"{case['id']}: {exc}")
        summary[case['id']] = entry
        print(f"case {case['id']:<4} {entry['status']:<9} {case['title']}")

    _write_summary(out, summary)
    if failures:
        print('failures:', *failures, sep='\n  ')
    return 1 if failures else 0


def _write_summary(out, summary):
    totals = {status: sum(1 for entry in summary.values() if entry['status'] == status)
              for status in ('match', 'differ', 'one-sided', 'error')}
    write_json(out / 'summary.json', dict(
        cases={case_id: dict(status=entry['status'], title=entry['title'],
                             **({'error': entry['error']} if 'error' in entry else {}))
               for case_id, entry in summary.items()},
        totals=totals))
    print(f"totals: {json.dumps(totals)}")


if __name__ == '__main__':
    raise SystemExit(main())
