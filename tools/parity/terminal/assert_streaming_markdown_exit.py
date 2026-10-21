"""Check pinned streaming Markdown terminal restoration at the exit checkpoint."""
import argparse
import json
from pathlib import Path
import sys

from assertion_evidence import load_assertion_evidence
from compare import differences, normalize_terminal_state

PI_SHA = 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6'
TEMPORARY_PRIVATE_MODES = ('1000', '1002', '1003', '1004', '1006', '1049', '2004', '2026')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('report', type=Path, help='paired terminal run report.json')
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding='utf-8'))
    if report.get('fixture') != 'terminal-streaming-markdown':
        raise RuntimeError('Report is not the terminal-streaming-markdown fixture.')
    if report.get('piSha') != PI_SHA:
        raise RuntimeError(f"Pi source pin is {report.get('piSha')!r}, expected {PI_SHA}.")

    for case in report.get('cases', []):
        evidence = load_assertion_evidence(args.report.parent, case)
        states = {}
        for product in ('pi', 'pisharp'):
            frames = evidence.get(product, {}).get('frames', [])
            frame = next((item for item in frames if item.get('id') == 'exit'), None)
            if frame is None:
                raise RuntimeError(f"{case.get('id')}/{product} has no captured exit checkpoint.")
            state = frame.get('state', {})
            if state.get('activeScreen') != 'normal':
                raise RuntimeError(f"{case.get('id')}/{product} did not restore the normal screen.")
            if state.get('cursor', {}).get('visible') is not True:
                raise RuntimeError(f"{case.get('id')}/{product} did not restore a visible cursor.")
            private_modes = state.get('modes', {}).get('private', {})
            leaked = [mode for mode in TEMPORARY_PRIVATE_MODES if private_modes.get(mode, False)]
            if leaked:
                raise RuntimeError(f"{case.get('id')}/{product} left temporary modes active: {leaked}.")
            states[product] = normalize_terminal_state(state)

        state_differences = list(differences(states['pi'], states['pisharp']))
        if state_differences:
            details = ', '.join(item['path'] for item in state_differences[:8])
            raise RuntimeError(f"{case.get('id')} exit state differs between Pi and PiSharp: {details}.")
        print(f"{case['id']}: matched restored screen, cursor, modes, and viewport")


if __name__ == '__main__':
    try:
        main()
    except (OSError, RuntimeError, ValueError, KeyError) as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
