"""Check pinned fullscreen transcript navigation and search effects at named checkpoints."""
import argparse
import json
import re
from pathlib import Path
import sys

from assertion_evidence import load_assertion_evidence

PI_SHA = 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6'
TEMPORARY_PRIVATE_MODES = ('1000', '1002', '1003', '1004', '1006', '1049', '2004', '2026')
CONTENT = re.compile(r'(user-entry|assistant-entry|Stable transcript|^- |detail \d|↑|↓|'
                     r'Find in transcript|\d+/\d+)')
BORDER = str.maketrans({'│': ' ', '┃': ' '})

EXPECTATIONS = {
    'page-up': {'contains': ['Jump to latest message']},
    'scroll-to-top': {'firstAnchor': 'user-entry-01'},
    'next-prompt-from-top': {'firstAnchor': 'assistant-entry-01'},
    'scroll-live-tail': {'contains': ['assistant-entry-30']},
    'previous-prompt': {'firstAnchor': 'user-entry-29'},
    'previous-prompt-earlier': {'firstAnchor': 'assistant-entry-28'},
    'prompt-after-resize': {'firstAnchor': 'user-entry-28'},
    'search-toggle-close': {'absent': ['Find in transcript']},
    'search-placeholder-reopen': {'contains': ['Find in transcript']},
    'search-paste-query': {'contains': ['1/9']},
    'search-cycle-next': {'contains': ['2/9']},
    'search-cycle-previous': {'contains': ['1/9']},
    'search-escape-close': {'absent': ['↑ Shift+Enter'], 'contains': ['assistant-entry-01']},
    'search-close-live-tail': {'contains': ['assistant-entry-30']},
}


def frame_lines(state):
    lines = []
    for line in state.get('viewport', []):
        characters = ''.join(cell.get('chars', ' ') for cell in line.get('cells', []))
        lines.append(characters.rstrip())
    return lines


def projection(lines):
    values = []
    for line in lines:
        value = line.translate(BORDER).strip()
        if value and 'Jump to latest message' not in value and CONTENT.search(value):
            values.append(value)
    return values


def first_anchor(lines):
    for line in lines:
        match = re.search(r'(?:user|assistant)-entry-\d+', line)
        if match:
            return match.group(0)
    return None


def effect(lines):
    text = '\n'.join(lines)
    result = re.search(r'\b(\d+/\d+)\b', text)
    return (first_anchor(lines), 'Jump to latest message' in text,
            result.group(1) if result else None, 'Find in transcript' in text)


def check_case(case, evidence):
    for product in ('pi', 'pisharp'):
        if evidence[product].get('scenarioError'):
            raise RuntimeError(f"{case['id']}/{product} scenario error: {evidence[product]['scenarioError']}")
    frames = {product: {frame['id']: frame for frame in evidence[product]['frames']}
              for product in ('pi', 'pisharp')}
    if set(frames['pi']) != set(frames['pisharp']):
        raise RuntimeError(f"{case['id']} named checkpoints differ between products.")
    for checkpoint in sorted(frames['pi']):
        states = {product: frame_lines(frames[product][checkpoint]['state'])
                  for product in ('pi', 'pisharp')}
        prompts = {product: first_anchor(states[product]) for product in states}
        if len(set(prompts.values())) != 1:
            raise RuntimeError(f"{case['id']}/{checkpoint} top anchor differs: {prompts}.")
        effects = {product: effect(states[product]) for product in states}
        if len(set(effects.values())) != 1:
            raise RuntimeError(f"{case['id']}/{checkpoint} navigation effect differs: {effects}.")
        expected = EXPECTATIONS.get(checkpoint)
        if not expected:
            continue
        for product in ('pi', 'pisharp'):
            text = '\n'.join(states[product])
            for value in expected.get('contains', []):
                if value not in text:
                    raise RuntimeError(f"{case['id']}/{checkpoint}/{product} lacks {value!r}.")
            for value in expected.get('absent', []):
                if value in text:
                    raise RuntimeError(f"{case['id']}/{checkpoint}/{product} still shows {value!r}.")
            if 'firstAnchor' in expected and prompts[product] != expected['firstAnchor']:
                raise RuntimeError(
                    f"{case['id']}/{checkpoint}/{product} top anchor is {prompts[product]!r}, "
                    f"expected {expected['firstAnchor']!r}.")

    for product in ('pi', 'pisharp'):
        state = frames[product]['exit']['state']
        if state.get('activeScreen') != 'normal':
            raise RuntimeError(f"{case['id']}/{product} did not restore the normal screen.")
        if state.get('cursor', {}).get('visible') is not True:
            raise RuntimeError(f"{case['id']}/{product} did not restore a visible cursor.")
        modes = state.get('modes', {}).get('private', {})
        leaked = [mode for mode in TEMPORARY_PRIVATE_MODES if modes.get(mode, False)]
        if leaked:
            raise RuntimeError(f"{case['id']}/{product} left temporary modes active: {leaked}.")
    print(f"{case['id']}: matched {len(frames['pi'])} named navigation checkpoints and restored the terminal")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('report', type=Path, help='paired terminal run report.json')
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding='utf-8'))
    if report.get('fixture') != 'terminal-long-transcript':
        raise RuntimeError('Report is not the terminal-long-transcript fixture.')
    if report.get('piSha') != PI_SHA:
        raise RuntimeError(f"Pi source pin is {report.get('piSha')!r}, expected {PI_SHA}.")
    for case in report.get('cases', []):
        check_case(case, load_assertion_evidence(args.report.parent, case))


if __name__ == '__main__':
    try:
        main()
    except (OSError, RuntimeError, ValueError, KeyError) as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
