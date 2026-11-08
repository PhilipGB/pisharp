#!/usr/bin/env python3
"""Capture same-Pi calibration and Pi/PiSharp differential evidence for the editor milestone."""
import argparse
import gzip
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
TERMINAL = ROOT / 'tools/parity/terminal/run.py'
DEFAULT_PISHARP = ROOT / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll'
PI_REFERENCE = ROOT / 'tools/parity/pi-reference.txt'
WORKSPACE = '/tmp/pisharp-terminal-fixture-workspace'
SIZES = ((80, 24), (120, 40))
MODES = ('1061', '2004', '2026', '1000', '1002', '1003', '1004', '1006', '1049', '7')
ARROW = re.compile(r'^\s*(?:\u2192|\u276f|>)\s*')
POSITION = re.compile(r'^\(\d+/\d+\)$')


def load_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))


def load_assertions(path):
    with gzip.open(path, 'rt', encoding='utf-8') as stream:
        return json.load(stream)


def viewport_lines(state):
    return [''.join(cell.get('chars', ' ') if cell else ' ' for cell in line.get('cells', [])).rstrip()
            for line in state.get('viewport', [])]


def editor_region(lines):
    separators = [index for index, line in enumerate(lines) if line.lstrip().startswith('\u2500')]
    if len(separators) < 2:
        return None, None, []
    bottom = separators[-1]
    tops = [index for index in separators if index < bottom]
    if not tops:
        return None, None, []
    top = max(tops)
    return top, bottom, [line for line in lines[top + 1:bottom]]


def overlay_region(lines, bottom):
    path = next((index for index, line in enumerate(lines)
                 if index > bottom and line.startswith(WORKSPACE)), None)
    end = path if path is not None else max(0, len(lines) - 1)
    return [line for line in lines[bottom + 1:end] if line.strip()]


def parse_overlay(rows):
    candidates = []
    selected = None
    position = None
    for row in rows:
        stripped = row.strip()
        if POSITION.match(stripped):
            position = stripped
            continue
        arrow = bool(ARROW.match(row))
        text = ARROW.sub('', row).strip()
        if not text:
            continue
        label = re.split(r'\s{2,}', text, maxsplit=1)[0].strip()
        label = label[1:] if label.startswith('@') else label
        candidates.append(label)
        if arrow and selected is None:
            selected = label
    return candidates, selected, position


def user_text(request):
    body = request.get('body') or {}
    messages = body.get('messages') or body.get('input') or []
    if isinstance(messages, str):
        return messages
    for message in reversed(messages):
        if not isinstance(message, dict) or message.get('role') != 'user':
            continue
        content = message.get('content')
        if isinstance(content, str):
            return content
        return '\n'.join(part.get('text', '') for part in content or []
                         if isinstance(part, dict))
    return None


def observe(evidence, product):
    frames = {frame['id']: frame for frame in evidence[product].get('frames') or []}
    checkpoints = {}
    for checkpoint_id, frame in frames.items():
        state = frame.get('state') or {}
        lines = viewport_lines(state)
        top, bottom, editor = editor_region(lines)
        rows = overlay_region(lines, bottom) if bottom is not None else []
        candidates, selected, position = parse_overlay(rows)
        cursor = state.get('cursor') or {}
        private = {key for key, value in (state.get('modes') or {}).get('private', {}).items() if value}
        checkpoints[checkpoint_id] = {
            'editor': editor,
            'editorText': '\n'.join(editor),
            'cursor': {'x': cursor.get('x'), 'y': cursor.get('y'),
                       'visible': cursor.get('visible'),
                       'editorRow': None if top is None or cursor.get('y') is None
                       else cursor.get('y') - top - 1},
            'overlay': rows,
            'candidates': candidates,
            'selected': selected,
            'position': position,
            'activeScreen': state.get('activeScreen'),
            'modes': sorted(private),
        }
    return {
        'checkpoints': checkpoints,
        'providerTexts': [user_text(request) for request in evidence[product].get('http') or []],
        'capturedWorkspaceFiles': evidence[product].get('capturedWorkspaceFiles') or {},
        'scenarioError': evidence[product].get('scenarioError'),
    }


def compare(pi, pisharp):
    differences = []
    pi_frames = pi['checkpoints']
    sharp_frames = pisharp['checkpoints']
    for checkpoint_id in sorted(set(pi_frames) | set(sharp_frames)):
        if checkpoint_id == 'exit':
            continue
        left = pi_frames.get(checkpoint_id)
        right = sharp_frames.get(checkpoint_id)
        if left is None or right is None:
            differences.append({'checkpoint': checkpoint_id, 'field': 'missing',
                                'pi': left is not None, 'pisharp': right is not None})
            continue
        for field in ('editorText', 'cursor', 'candidates', 'selected', 'activeScreen'):
            if left[field] != right[field]:
                differences.append({'checkpoint': checkpoint_id, 'field': field,
                                    'pi': left[field], 'pisharp': right[field]})
    if pi['providerTexts'] != pisharp['providerTexts']:
        differences.append({'checkpoint': None, 'field': 'providerTexts',
                            'pi': pi['providerTexts'], 'pisharp': pisharp['providerTexts']})
    return differences


def calibration_differences(first, second):
    differences = []
    for checkpoint_id in sorted(set(first['checkpoints']) | set(second['checkpoints'])):
        if checkpoint_id == 'exit':
            continue
        left = first['checkpoints'].get(checkpoint_id)
        right = second['checkpoints'].get(checkpoint_id)
        if left is None or right is None:
            differences.append({'checkpoint': checkpoint_id, 'field': 'missing'})
            continue
        for field in ('editorText', 'cursor', 'candidates', 'selected'):
            if left[field] != right[field]:
                differences.append({'checkpoint': checkpoint_id, 'field': field})
    if first['providerTexts'] != second['providerTexts']:
        differences.append({'checkpoint': None, 'field': 'providerTexts'})
    return differences


def exit_state(evidence, product, scenario):
    state = {'exitCode': None, 'activeScreen': None, 'cursorVisible': None, 'privateModes': []}
    frames = {frame['id']: frame for frame in evidence[product].get('frames') or []}
    last = next((frame for frame in reversed(list(frames.values()))
                 if (frame.get('state') or {}).get('exitCode') is not None), None)
    if last is None:
        return state
    frame = last['state']
    state.update(exitCode=frame.get('exitCode'), activeScreen=frame.get('activeScreen'),
                 cursorVisible=(frame.get('cursor') or {}).get('visible'),
                 privateModes=sorted(key for key, value in
                                     (frame.get('modes') or {}).get('private', {}).items() if value))
    return state


def run_terminal(scenario_path, output, case, calibrate, pisharp):
    command = [sys.executable, str(TERMINAL), '--pi', str(args.pi), '--pisharp', str(pisharp),
               '--fixture', str(scenario_path), '--output', str(output), '--case', case,
               '--semantic-only']
    if calibrate:
        command.insert(-1, '--calibrate')
    completed = subprocess.run(command, cwd=ROOT, text=True, capture_output=True)
    if completed.returncode != 0:
        raise RuntimeError(f'Terminal harness failed for {scenario_path.name}/{case}:\n'
                           f'{completed.stdout}\n{completed.stderr}')
    report = load_json(output / 'report.json')
    case_report = report['cases'][0]
    assertions = load_assertions(output / case_report['assertionEvidence'])
    return case_report, assertions


def provenance():
    sha = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()
    branch = subprocess.check_output(['git', 'rev-parse', '--abbrev-ref', 'HEAD'], cwd=ROOT,
                                     text=True).strip()
    status = subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT, text=True).strip()
    return {'pisharpSha': sha, 'branch': branch, 'treeClean': not status,
            'trackedChanges': status.splitlines()[:50]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--pisharp', type=Path, default=DEFAULT_PISHARP)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--report', type=Path,
                        default=ROOT / 'docs/parity/fixtures/tui-editor-completion-paste-001/report.json')
    parser.add_argument('--only', action='append', help='Run only the named scenario id.')
    parser.add_argument('--size', action='append', help='Run only the named size, e.g. 80x24.')
    parser.add_argument('--skip-calibration', action='store_true')
    global args
    args = parser.parse_args()

    sys.path.insert(0, str(HERE))
    from inventory import KNOWN_DIFFERENCES, NORMALIZATIONS
    from scenarios import all_scenarios, SIZES as SCENARIO_SIZES
    pi_sha = (PI_REFERENCE.read_text().split()[0]).strip()
    actual_pi = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=args.pi,
                                        text=True).strip()
    if actual_pi != pi_sha:
        raise SystemExit(f'Pi checkout is {actual_pi}, expected pinned {pi_sha}.')
    args.output.mkdir(parents=True, exist_ok=True)
    state = provenance()
    selected_sizes = {(int(value.split('x')[0]), int(value.split('x')[1]))
                      for value in args.size} if args.size else {(size['columns'], size['rows']) for size in SCENARIO_SIZES}
    scenarios = [scenario for scenario in all_scenarios()
                 if not args.only or scenario['id'] in args.only]
    result = {
        'schema': 1,
        'capability': 'tui-editor-completion-paste',
        'piSha': pi_sha,
        'pisharpSha': state['pisharpSha'],
        'branch': state['branch'],
        'treeClean': state['treeClean'],
        'trackedChanges': state['trackedChanges'],
        'sizes': [f'{columns}x{rows}' for columns, rows in sorted(selected_sizes)],
        'scoring': {
            'rubric': {
                'cursor-unicode': 15, 'multiline': 10, 'history': 10,
                'slash-completion': 15, 'path-completion': 15, 'bracketed-paste': 10,
                'escape-ctrl-c': 10, 'external-editor': 10, 'journey': 5,
            },
            'total': 100,
        },
        'normalizations': NORMALIZATIONS,
        'knownDifferences': KNOWN_DIFFERENCES,
        'scenarios': [],
    }
    with tempfile.TemporaryDirectory(prefix='tui-editor-milestone-') as temporary:
        temporary = Path(temporary)
        for scenario in scenarios:
            scenario_path = temporary / (scenario['id'] + '.json')
            scenario_path.write_text(json.dumps(scenario, indent=2) + '\n')
            for columns, rows in sorted(selected_sizes):
                case = f'{columns}x{rows}-dark-fullscreen'
                entry = {'id': scenario['id'], 'task': scenario['task'], 'size': f'{columns}x{rows}',
                         'case': case, 'calibration': None, 'pi': None, 'pisharp': None,
                         'differences': [], 'status': 'pass'}
                output = args.output / scenario['id'] / case
                output.mkdir(parents=True, exist_ok=True)
                if not args.skip_calibration:
                    calibrated = output / 'calibration'
                    _, evidence = run_terminal(scenario_path, calibrated, case, True, args.pisharp)
                    first, second = observe(evidence, 'pi'), observe(evidence, 'pisharp')
                    differences = calibration_differences(first, second)
                    entry['calibration'] = {'match': not differences, 'differences': differences,
                                            'checkpoints': len(first['checkpoints'])}
                    if differences:
                        entry['status'] = 'fail'
                _, evidence = run_terminal(scenario_path, output / 'paired', case, False, args.pisharp)
                pi, pisharp = observe(evidence, 'pi'), observe(evidence, 'pisharp')
                entry['pi'] = pi
                entry['pisharp'] = pisharp
                entry['differences'] = compare(pi, pisharp)
                blocked = set()
                for difference in entry['differences']:
                    for known in KNOWN_DIFFERENCES:
                        if (scenario['task'] in known['tasks'] and
                                scenario['id'] in known['scenarios'] and
                                difference['field'] in known['fields']):
                            blocked.add(known['id'])
                            difference['classification'] = known['id']
                entry['blockedBy'] = sorted(blocked)
                entry['exit'] = {'pi': exit_state(evidence, 'pi', scenario),
                                 'pisharp': exit_state(evidence, 'pisharp', scenario)}
                if pi['scenarioError'] or pisharp['scenarioError']:
                    entry['status'] = 'fail'
                if entry['differences']:
                    entry['status'] = 'fail'
                if entry['exit']['pi'] != entry['exit']['pisharp']:
                    entry['status'] = 'fail'
                result['scenarios'].append(entry)
                print(f"{scenario['id']}/{case}: {'pass' if entry['status'] == 'pass' else 'fail'} "
                      f"({len(entry['differences'])} checkpoint differences)", flush=True)
    by_task = {}
    for entry in result['scenarios']:
        by_task.setdefault(entry['task'], []).append(entry)
    points = {}
    for task, weight in result['scoring']['rubric'].items():
        entries = by_task.get(task) or []
        points[task] = weight if entries and all(entry['status'] == 'pass' for entry in entries) else 0
    result['score'] = {'points': points, 'total': sum(points.values()),
                       'maximum': result['scoring']['total']}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(result, indent=2, ensure_ascii=False) + '\n')
    print(json.dumps(result['score']), flush=True)
    return 0 if result['score']['total'] == result['score']['maximum'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
