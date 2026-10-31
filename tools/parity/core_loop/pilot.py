#!/usr/bin/env python3
"""Pilot: one prompt per product through the harness; print the observations."""
import argparse
import json
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from harness import ROOT, run_case  # noqa: E402
from run_differential import PI_DEFAULT  # noqa: E402

PI = PI_DEFAULT
PISHARP_DLL = ROOT / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll'

PILOT = dict(
    provider=[dict(t='text', text='PILOT_OK')],
    script=[
        ('prompt', ['GO']),
        'settled',
        'get_state',
    ],
    gates=[],
)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--product', choices=['pi', 'pisharp'], required=True)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='cl-pilot-') as directory:
        work = Path(directory)
        observations = run_case(PILOT, args.product, PI, PISHARP_DLL, work)
        slim = dict(
            providerRequests=observations['providerRequests'],
            events=[(r.get('type'), r.get('command'), r.get('success'), r.get('data') if r.get('type') == 'response'
                     and r.get('command') in ('get_state',) else None) for r in observations['records']],
            effects=observations['effects'],
            mcpCalls=observations['mcpCalls'],
            state=observations['state'],
            sessionEntries=observations['sessionEntries'],
            stderr=observations['stderr'][-500:],
        )
        print(json.dumps(slim, indent=1, default=str)[:6000])


if __name__ == '__main__':
    main()
