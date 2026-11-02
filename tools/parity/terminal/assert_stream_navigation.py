"""Check live streaming assistant semantic-anchor navigation against pinned Pi."""
import argparse
import json
from pathlib import Path
import sys

from assertion_evidence import load_assertion_evidence

PI_SHA = 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6'
TEMPORARY_PRIVATE_MODES = ('1000', '1002', '1003', '1004', '1006', '1049', '2004', '2026')


def viewport_lines(state):
    return [''.join(cell.get('chars', ' ') for cell in line.get('cells', [])).rstrip()
            for line in state.get('viewport', [])]


def first_content(lines):
    for line in lines:
        value = line.translate(str.maketrans({'│': ' ', '┃': ' '})).strip()
        if not value or 'Jump to latest message' in value or 'Working' in value:
            continue
        return value
    return ''


def check_case(case, evidence):
    for product in ('pi', 'pisharp'):
        if evidence[product].get('scenarioError'):
            raise RuntimeError(f"{case['id']}/{product} scenario error: {evidence[product]['scenarioError']}")
    frames = {product: {frame['id']: frame for frame in evidence[product]['frames']}
              for product in ('pi', 'pisharp')}
    if set(frames['pi']) != set(frames['pisharp']):
        raise RuntimeError(f"{case['id']} named checkpoints differ between products.")
    first = {product: {checkpoint: first_content(viewport_lines(frame['state']))
                       for checkpoint, frame in frames[product].items()}
             for product in frames}
    for checkpoint in ('live-previous-assistant', 'live-previous-user',
                       'live-next-assistant', 'live-return-bottom'):
        if first['pi'][checkpoint] != first['pisharp'][checkpoint]:
            raise RuntimeError(
                f"{case['id']}/{checkpoint} live anchor differs: "
                f"pi={first['pi'][checkpoint]!r} pisharp={first['pisharp'][checkpoint]!r}.")
    expected = {
        'live-previous-assistant': 'STREAM_ROW_00',
        'live-previous-user': 'Stream a long answer.',
        'live-next-assistant': 'STREAM_ROW_00',
        'live-return-bottom': 'STREAM_ROW_16',
    }
    for product in ('pi', 'pisharp'):
        for checkpoint, value in expected.items():
            if first[product][checkpoint] != value:
                raise RuntimeError(
                    f"{case['id']}/{product}/{checkpoint} first content is "
                    f"{first[product][checkpoint]!r}, expected {value!r}.")
        abort = '\n'.join(viewport_lines(frames[product]['abort-stream']['state']))
        if 'Operation aborted' not in abort:
            raise RuntimeError(f"{case['id']}/{product} did not show the abort result.")
        state = frames[product]['exit']['state']
        if state.get('activeScreen') != 'normal':
            raise RuntimeError(f"{case['id']}/{product} did not restore the normal screen.")
        if state.get('cursor', {}).get('visible') is not True:
            raise RuntimeError(f"{case['id']}/{product} did not restore a visible cursor.")
        modes = state.get('modes', {}).get('private', {})
        leaked = [mode for mode in TEMPORARY_PRIVATE_MODES if modes.get(mode, False)]
        if leaked:
            raise RuntimeError(f"{case['id']}/{product} left temporary modes active: {leaked}.")
    print(f"{case['id']}: live user/assistant anchors matched across {len(frames['pi'])} checkpoints")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('report', type=Path, help='paired terminal run report.json')
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding='utf-8'))
    if report.get('fixture') != 'terminal-fullscreen-stream-navigation':
        raise RuntimeError('Report is not the terminal-fullscreen-stream-navigation fixture.')
    if report.get('piSha') != PI_SHA:
        raise RuntimeError(f"Pi source pin is {report.get('piSha')!r}, expected {PI_SHA}.")
    for case in report.get('cases', []):
        if case.get('scenarioErrors'):
            raise RuntimeError(f"{case['id']} scenario errors: {case['scenarioErrors']}")
        if not case.get('httpMatch'):
            raise RuntimeError(f"{case['id']} HTTP traces differ.")
        check_case(case, load_assertion_evidence(args.report.parent, case))


if __name__ == '__main__':
    try:
        main()
    except (OSError, RuntimeError, ValueError, KeyError) as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
