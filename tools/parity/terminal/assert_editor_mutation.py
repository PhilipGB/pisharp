"""Check pinned-Pi editor mutation checkpoints and provider effects."""
import argparse
import json
from pathlib import Path
import sys

from assertion_evidence import load_assertion_evidence

PI_SHA = 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6'
TEMPORARY_PRIVATE_MODES = ('1000', '1002', '1003', '1004', '1006', '1049', '2004', '2026')
EXPECTED_EDITOR = {
    'startup': '',
    'type-draft': 'one two three',
    'ctrl-w-first': 'one two',
    'ctrl-w-second': 'one',
    'ctrl-y-restore': 'one two three',
    'alt-d-delete': 'two three',
    'ctrl-k-line-end': '',
    'ctrl-y-line': 'one two three',
    'undo-yank': '',
    'ctrl-y-line-restored': 'one two three',
    'backspace-draft': 'ab',
    'delete-grapheme': 'b',
    'kill-ring-yank': 'one',
    'kill-ring-pop-older': 'alpha',
    'kill-ring-pop-newer': 'b',
    'kill-ring-pop-inert': 'bx',
    'submit-mutations': '',
    'submit-visible': 'provider visible input',
    'submit-request': '',
    'reload-bindings': '',
    'configured-type': 'gamma delta',
    'configured-delete': 'gamma',
    'configured-yank': 'gamma delta',
    'configured-undo': 'gamma',
}


def viewport_lines(state):
    return [''.join(cell.get('chars', ' ') for cell in line.get('cells', [])).rstrip()
            for line in state.get('viewport', [])]


def editor_text(lines):
    separators = [index for index, line in enumerate(lines) if line.lstrip().startswith('─')]
    if len(separators) < 2:
        raise RuntimeError('Editor separators were not rendered.')
    bottom = separators[-1]
    top = max((index for index in separators if index < bottom), default=None)
    if top is None:
        raise RuntimeError('Editor top separator was not rendered.')
    region = [line for line in lines[top + 1:bottom] if line.strip()]
    text = '\n'.join(region)
    for prefix in ('❯', '>', '│'):
        if text.startswith(prefix):
            text = text[len(prefix):].lstrip()
    text = text.replace('│', '').strip()
    return ' '.join(text.split())


def user_text(request):
    body = request.get('body') or {}
    messages = body.get('messages') or []
    for message in reversed(messages):
        if message.get('role') != 'user':
            continue
        content = message.get('content')
        if isinstance(content, str):
            return ' '.join(content.split())
        parts = []
        for part in content or []:
            if isinstance(part, dict) and isinstance(part.get('text'), str):
                parts.append(part['text'])
        return ' '.join(' '.join(parts).split())
    raise RuntimeError('Request had no user message.')


def check_case(case, evidence):
    for product in ('pi', 'pisharp'):
        if evidence[product].get('scenarioError'):
            raise RuntimeError(f"{case['id']}/{product} scenario error: {evidence[product]['scenarioError']}")
    frames = {product: {frame['id']: frame for frame in evidence[product]['frames']}
              for product in ('pi', 'pisharp')}
    if set(frames['pi']) != set(frames['pisharp']):
        raise RuntimeError(f"{case['id']} named checkpoints differ between products.")
    missing = sorted(set(EXPECTED_EDITOR) - set(frames['pi']))
    if missing:
        raise RuntimeError(f"{case['id']} missing checkpoints: {missing}")
    for product in ('pi', 'pisharp'):
        for checkpoint, expected in EXPECTED_EDITOR.items():
            actual = editor_text(viewport_lines(frames[product][checkpoint]['state']))
            if actual != expected:
                raise RuntimeError(
                    f"{case['id']}/{product}/{checkpoint} editor is {actual!r}, expected {expected!r}.")
        state = frames[product]['exit']['state']
        if state.get('activeScreen') != 'normal':
            raise RuntimeError(f"{case['id']}/{product} did not restore the normal screen.")
        if state.get('cursor', {}).get('visible') is not True:
            raise RuntimeError(f"{case['id']}/{product} did not restore a visible cursor.")
        modes = state.get('modes', {}).get('private', {})
        leaked = [mode for mode in TEMPORARY_PRIVATE_MODES if modes.get(mode, False)]
        if leaked:
            raise RuntimeError(f"{case['id']}/{product} left temporary modes active: {leaked}.")
        requests = evidence[product]['http']
        if not requests:
            raise RuntimeError(f"{case['id']}/{product} submitted no provider request.")
        final = user_text(requests[-1])
        if final != 'provider visible input':
            raise RuntimeError(
                f"{case['id']}/{product} provider-visible user text is {final!r}, "
                "expected 'provider visible input'.")
        for leftover in ('draft prompt', 'one two', 'gamma'):
            if leftover in final:
                raise RuntimeError(f"{case['id']}/{product} provider text retained {leftover!r}.")
    print(f"{case['id']}: {len(EXPECTED_EDITOR)} editor checkpoints and the final provider request matched")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('report', type=Path, help='paired terminal run report.json')
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding='utf-8'))
    if report.get('fixture') != 'terminal-editor-mutation':
        raise RuntimeError('Report is not the terminal-editor-mutation fixture.')
    if report.get('piSha') != PI_SHA:
        raise RuntimeError(f"Pi source pin is {report.get('piSha')!r}, expected {PI_SHA}.")
    for case in report.get('cases', []):
        if case.get('scenarioErrors'):
            raise RuntimeError(f"{case['id']} scenario errors: {case['scenarioErrors']}")
        if not case.get('httpMatch'):
            raise RuntimeError(f"{case['id']} HTTP traces differ: {case.get('httpDifferences')}")
        check_case(case, load_assertion_evidence(args.report.parent, case))


if __name__ == '__main__':
    try:
        main()
    except (OSError, RuntimeError, ValueError, KeyError) as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
