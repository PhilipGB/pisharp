"""Check pinned-Pi editor movement/history checkpoints and provider effects."""
import argparse
import gzip
import json
from pathlib import Path

PI_SHA = 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6'
TEMPORARY_PRIVATE_MODES = ('1000', '1002', '1003', '1004', '1006', '1049', '2004', '2026')
EXPECTED_REQUESTS = ['first history prompt', 'second history prompt', 'provider movement input']


def load_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))


def load_assertions(path):
    with gzip.open(path, 'rt', encoding='utf-8') as stream:
        return json.load(stream)


def viewport_lines(state):
    return [''.join(cell.get('chars', ' ') for cell in line.get('cells', [])).rstrip()
            for line in state.get('viewport', [])]


def editor_lines(lines):
    separators = [index for index, line in enumerate(lines) if line.lstrip().startswith('─')]
    if len(separators) < 2:
        raise RuntimeError('Editor separators were not rendered.')
    bottom = separators[-1]
    top = max((index for index in separators if index < bottom), default=None)
    if top is None:
        raise RuntimeError('Editor top separator was not rendered.')
    return [line for line in lines[top + 1:bottom] if line.strip()]


def user_text(request):
    body = request.get('body') or {}
    messages = body.get('messages') or []
    for message in reversed(messages):
        if message.get('role') != 'user':
            continue
        content = message.get('content')
        if isinstance(content, str):
            return content
        parts = []
        for part in content or []:
            if isinstance(part, dict) and isinstance(part.get('text'), str):
                parts.append(part['text'])
        return '\n'.join(parts)
    raise RuntimeError('Request had no user message.')


def check_case(case, expected, evidence):
    checkpoints = expected['checkpoints']
    for product in ('pi', 'pisharp'):
        if evidence[product].get('scenarioError'):
            raise RuntimeError(f"{case['id']}/{product} scenario error: {evidence[product]['scenarioError']}")
    frames = {product: {frame['id']: frame for frame in evidence[product]['frames']}
              for product in ('pi', 'pisharp')}
    if set(frames['pi']) != set(frames['pisharp']):
        raise RuntimeError(f"{case['id']} named checkpoints differ between products.")
    missing = sorted(set(checkpoints) - set(frames['pi']))
    if missing:
        raise RuntimeError(f"{case['id']} missing checkpoints: {missing}")
    for product in ('pi', 'pisharp'):
        for checkpoint, observation in checkpoints.items():
            state = frames[product][checkpoint]['state']
            actual_lines = editor_lines(viewport_lines(state))
            if actual_lines != observation['editor']:
                raise RuntimeError(
                    f"{case['id']}/{product}/{checkpoint} editor is {actual_lines!r}, "
                    f"expected {observation['editor']!r}.")
            cursor = state.get('cursor') or {}
            if (cursor.get('x'), cursor.get('y')) != (observation['cursor']['x'], observation['cursor']['y']):
                raise RuntimeError(
                    f"{case['id']}/{product}/{checkpoint} cursor is "
                    f"{(cursor.get('x'), cursor.get('y'))!r}, expected "
                    f"{(observation['cursor']['x'], observation['cursor']['y'])!r}.")
        exit_state = frames[product]['exit']['state']
        if exit_state.get('activeScreen') != 'normal':
            raise RuntimeError(f"{case['id']}/{product} did not restore the normal screen.")
        if exit_state.get('cursor', {}).get('visible') is not True:
            raise RuntimeError(f"{case['id']}/{product} did not restore a visible cursor.")
        modes = exit_state.get('modes', {}).get('private', {})
        leaked = [mode for mode in TEMPORARY_PRIVATE_MODES if modes.get(mode, False)]
        if leaked:
            raise RuntimeError(f"{case['id']}/{product} left temporary modes active: {leaked}.")
        requests = evidence[product]['http']
        if len(requests) != len(EXPECTED_REQUESTS):
            raise RuntimeError(
                f"{case['id']}/{product} submitted {len(requests)} provider requests, "
                f"expected {len(EXPECTED_REQUESTS)}.")
        actual = [user_text(request) for request in requests]
        if actual != EXPECTED_REQUESTS:
            raise RuntimeError(
                f"{case['id']}/{product} provider user texts are {actual!r}, "
                f"expected {EXPECTED_REQUESTS!r}.")
    print(f"{case['id']}: {len(checkpoints)} movement/history checkpoints, cursor positions, "
          "terminal restoration and provider ordering matched")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('report', type=Path, help='paired terminal run report.json')
    parser.add_argument('--checkpoints', type=Path,
                        default=Path(__file__).resolve().parents[3] / 'docs/parity/fixtures/'
                        'keys-editor-movement-checkpoints-2026-11-02.json')
    args = parser.parse_args()
    report = load_json(args.report)
    if report.get('fixture') != 'terminal-editor-movement':
        raise RuntimeError('Report is not the terminal-editor-movement fixture.')
    if report.get('piSha') != PI_SHA:
        raise RuntimeError(f"Pi source pin is {report.get('piSha')!r}, expected {PI_SHA}.")
    expected = load_json(args.checkpoints)
    if expected.get('piSha') != PI_SHA:
        raise RuntimeError(f"Checkpoint pin is {expected.get('piSha')!r}, expected {PI_SHA}.")
    for case in report.get('cases', []):
        if case.get('scenarioErrors'):
            raise RuntimeError(f"{case['id']} scenario errors: {case['scenarioErrors']}")
        if not case.get('httpMatch'):
            raise RuntimeError(f"{case['id']} HTTP traces differ: {case.get('httpDifferences')}")
        if not case.get('rawHttpMatch'):
            raise RuntimeError(f"{case['id']} raw HTTP traces differ: {case.get('rawHttpDifference')}")
        observation = expected.get('cases', {}).get(case['id'])
        if observation is None:
            raise RuntimeError(f"{case['id']} has no pinned-Pi checkpoint observation.")
        evidence = load_assertions(args.report.parent / case['assertionEvidence'])
        check_case(case, observation, evidence)


if __name__ == '__main__':
    try:
        main()
    except (OSError, RuntimeError, ValueError, KeyError) as error:
        print(error, file=__import__('sys').stderr)
        raise SystemExit(1)
