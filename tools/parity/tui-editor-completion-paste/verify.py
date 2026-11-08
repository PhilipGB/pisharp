#!/usr/bin/env python3
"""Verify a tui-editor-completion-paste parity report and its negative controls."""
import argparse
import copy
import json
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
sys.path.insert(0, str(HERE))
from inventory import KNOWN_DIFFERENCES, NORMALIZATIONS  # noqa: E402

PI_SHA = (ROOT / 'tools/parity/pi-reference.txt').read_text().split()[0].strip()
SIZES = {'80x24', '120x40'}
REQUIRED = {
    'cursor-unicode': ['cursor-unicode'],
    'multiline': ['multiline', 'soft-wrap'],
    'history': ['history'],
    'slash-completion': ['slash-completion'],
    'path-completion': ['path-completion'],
    'bracketed-paste': ['bracketed-paste'],
    'escape-ctrl-c': ['escape-ctrl-c', 'exit-empty-ctrl-d'],
    'external-editor': ['external-editor', 'external-editor-failure',
                        'external-editor-visual', 'external-editor-editor'],
    'journey': ['journey'],
}
RUBRIC = {'cursor-unicode': 15, 'multiline': 10, 'history': 10, 'slash-completion': 15,
          'path-completion': 15, 'bracketed-paste': 10, 'escape-ctrl-c': 10,
          'external-editor': 10, 'journey': 5}
CHECKPOINT_FIELDS = ('editorText', 'cursor', 'candidates', 'selected', 'activeScreen')
NORMALIZATION_IDS = {'post-exit-scroll-position', 'completion-label-display-prefix',
                     'completion-position-row', 'external-editor-temporary-path'}


class VerificationError(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise VerificationError(message)


def load(path):
    try:
        return json.loads(Path(path).read_text(encoding='utf-8'))
    except (OSError, ValueError) as error:
        raise VerificationError(f'Cannot read {path}: {error}') from error


def git_head():
    return subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip()


def is_ancestor(sha):
    completed = subprocess.run(['git', 'merge-base', '--is-ancestor', sha, git_head()], cwd=ROOT,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return completed.returncode == 0


def expected_differences(pi, pisharp):
    differences = []
    left = pi['checkpoints']
    right = pisharp['checkpoints']
    for checkpoint in sorted(set(left) | set(right)):
        if checkpoint == 'exit':
            continue
        if checkpoint not in left or checkpoint not in right:
            differences.append((checkpoint, 'missing'))
            continue
        for field in CHECKPOINT_FIELDS:
            if left[checkpoint][field] != right[checkpoint][field]:
                differences.append((checkpoint, field))
    if pi['providerTexts'] != pisharp['providerTexts']:
        differences.append((None, 'providerTexts'))
    return differences


def known_for(task, scenario, checkpoint, field):
    matches = []
    for known in KNOWN_DIFFERENCES:
        if task not in known['tasks'] or scenario not in known['scenarios']:
            continue
        if field not in known['fields']:
            continue
        if known.get('checkpoints') and checkpoint not in known['checkpoints']:
            continue
        matches.append(known['id'])
    return matches


def verify(report, *, head=None, require_clean=False):
    require(report.get('schema') == 1, 'Report schema must be 1.')
    require(report.get('capability') == 'tui-editor-completion-paste',
            'Report capability is wrong.')
    require(report.get('piSha') == PI_SHA,
            f"Pi pin is {report.get('piSha')!r}, expected {PI_SHA}.")
    sha = report.get('pisharpSha') or ''
    require(len(sha) == 40 and all(character in '0123456789abcdef' for character in sha),
            'Report must record a 40-character lowercase implementation SHA.')
    if head is not None:
        require(sha == head or is_ancestor(sha),
                f'Stale implementation provenance: {sha} is not an ancestor of {head}.')
    if require_clean:
        require(report.get('treeClean') is True, 'Report was captured from a dirty tree.')
    require(isinstance(report.get('branch'), str) and report['branch'],
            'Report must record the branch.')
    require(set(report.get('sizes') or []) == SIZES,
            f"Report sizes are {report.get('sizes')!r}, expected {sorted(SIZES)}.")
    normalizations = {entry.get('id') for entry in report.get('normalizations') or []}
    require(NORMALIZATION_IDS <= normalizations,
            f'Missing normalisation declarations: {sorted(NORMALIZATION_IDS - normalizations)}.')
    require(report.get('normalizations') == NORMALIZATIONS,
            'Report normalisations differ from the frozen declaration.')
    require(report.get('knownDifferences') == KNOWN_DIFFERENCES,
            'Report known differences differ from the frozen inventory.')
    entries = report.get('scenarios') or []
    by_key = {}
    for entry in entries:
        key = (entry.get('id'), entry.get('size'))
        require(key not in by_key, f'Duplicate scenario/size entry: {key}.')
        by_key[key] = entry
    required = {(scenario, size) for scenarios in REQUIRED.values()
                for scenario in scenarios for size in SIZES}
    require(set(by_key) == required,
            f'Scenario inventory differs. Missing={sorted(required - set(by_key))} '
            f'extra={sorted(set(by_key) - required)}.')
    used_known = set()
    for entry in entries:
        scenario = entry['id']
        task = entry['task']
        require(task in REQUIRED and scenario in REQUIRED[task],
                f'{scenario} is attached to the wrong task {task!r}.')
        calibration = entry.get('calibration') or {}
        require(calibration.get('checkpoints', 0) > 0,
                f'{scenario}/{entry["size"]} has no calibration checkpoints.')
        require(calibration.get('match') is True and not calibration.get('differences'),
                f'{scenario}/{entry["size"]} failed same-Pi calibration.')
        for product in ('pi', 'pisharp'):
            value = entry.get(product) or {}
            require(value.get('scenarioError') is None,
                    f'{scenario}/{entry["size"]}/{product} reported '
                    f'{value.get("scenarioError")!r}.')
            require(isinstance(value.get('providerTexts'), list),
                    f'{scenario}/{entry["size"]}/{product} has no provider text list.')
            checkpoints = value.get('checkpoints') or {}
            require(checkpoints, f'{scenario}/{entry["size"]}/{product} has no checkpoints.')
            for checkpoint, observation in checkpoints.items():
                require(isinstance(observation.get('editorText'), str),
                        f'{scenario}/{entry["size"]}/{checkpoint} has no editor text.')
                cursor = observation.get('cursor') or {}
                require(isinstance(cursor.get('x'), int) and isinstance(cursor.get('y'), int),
                        f'{scenario}/{entry["size"]}/{checkpoint} has no cursor cell.')
                require(isinstance(observation.get('candidates'), list),
                        f'{scenario}/{entry["size"]}/{checkpoint} has no candidate list.')
                require('selected' in observation,
                        f'{scenario}/{entry["size"]}/{checkpoint} has no selected candidate.')
        pi = entry['pi']
        pisharp = entry['pisharp']
        expected = expected_differences(pi, pisharp)
        recorded = {(difference.get('checkpoint'), difference.get('field'))
                    for difference in entry.get('differences') or []}
        require(recorded == set(expected),
                f'{scenario}/{entry["size"]} recorded differences {sorted(recorded)} '
                f'do not match recomputed {sorted(set(expected))}.')
        exit_match = entry.get('exit', {}).get('pi') == entry.get('exit', {}).get('pisharp')
        should_pass = not expected and exit_match
        require((entry.get('status') == 'pass') == should_pass,
                f'{scenario}/{entry["size"]} status {entry.get("status")!r} is inconsistent.')
        blocked = set()
        for checkpoint, field in expected:
            matches = known_for(task, scenario, checkpoint, field)
            if should_pass:
                continue
            require(matches, f'{scenario}/{entry["size"]} has an unexplained difference '
                             f'{checkpoint}/{field}.')
            blocked.update(matches)
            used_known.update(matches)
        require(set(entry.get('blockedBy') or []) == blocked,
                f'{scenario}/{entry["size"]} blockedBy {entry.get("blockedBy")!r} '
                f'does not match {sorted(blocked)}.')
    require(used_known == {known['id'] for known in KNOWN_DIFFERENCES},
            f'Known differences not evidenced by any failure: '
            f'{sorted({known["id"] for known in KNOWN_DIFFERENCES} - used_known)}.')
    points = {}
    for task, scenarios in REQUIRED.items():
        task_entries = [entry for entry in entries if entry['task'] == task]
        points[task] = RUBRIC[task] if all(entry['status'] == 'pass' for entry in task_entries) else 0
    score = report.get('score') or {}
    require(score.get('points') == points,
            f'Report points {score.get("points")!r} do not match recomputed {points!r}.')
    require(score.get('total') == sum(points.values()) and score.get('maximum') == 100,
            'Report total score is incorrect.')
    return {'total': sum(points.values()), 'points': points, 'differences': sum(
        len(entry.get('differences') or []) for entry in entries)}


def mutations(report):
    """Yield (name, mutated report, expected message fragment) negative controls."""
    def scenario(report, scenario_id, size='80x24'):
        return next(entry for entry in report['scenarios']
                    if entry['id'] == scenario_id and entry['size'] == size)

    def failing_entry(report):
        return next(entry for entry in report['scenarios'] if entry['differences'])

    yield ('missing-scenario', _drop_scenario(report, 'history', '80x24'), 'inventory')
    yield ('missing-size', _drop_scenario(report, 'cursor-unicode', '120x40'), 'inventory')
    yield ('missing-checkpoint', _mutate(report, lambda r: scenario(r, 'cursor-unicode')['pi']
                                         ['checkpoints'].pop('typed')), 'recorded differences')
    yield ('unexpected-pi-failure', _mutate(report, lambda r: scenario(r, 'history')['pi']
                                            .__setitem__('scenarioError', 'timeout')), 'reported')
    yield ('hidden-candidate-mismatch', _mutate(report, lambda r: _hide(r, 'candidates')), 'recorded differences')
    yield ('hidden-provider-mismatch', _mutate(report, lambda r: _hide(r, 'providerTexts')),
           'recorded differences')
    yield ('hidden-cursor-mismatch', _mutate(report, lambda r: _hide(r, 'cursor')),
           'recorded differences')
    yield ('calibration-failure', _mutate(report, lambda r: scenario(r, 'multiline')['calibration']
                                          .__setitem__('match', False)), 'calibration')
    yield ('stale-provenance', _mutate(report, lambda r: r.__setitem__('pisharpSha', '0' * 40)),
           'Stale implementation provenance')
    yield ('omitted-known-difference', _mutate(report, _unclassify), 'unexplained difference')
    yield ('false-known-difference', _mutate(report, _false_known), 'known differences')
    yield ('altered-score', _mutate(report, lambda r: r['score']['points'].__setitem__('history', 0)),
           'points')
    yield ('wrong-pi-pin', _mutate(report, lambda r: r.__setitem__('piSha', '1' * 40)), 'Pi pin')
    yield ('duplicate-entry', _mutate(report, lambda r: r['scenarios'].append(
        copy.deepcopy(r['scenarios'][0]))), 'Duplicate')


def _mutate(report, action):
    value = copy.deepcopy(report)
    action(value)
    return value


def _drop_scenario(report, scenario_id, size):
    value = copy.deepcopy(report)
    value['scenarios'] = [entry for entry in value['scenarios']
                          if not (entry['id'] == scenario_id and entry['size'] == size)]
    return value


def _hide(report, field):
    entry = next(entry for entry in report['scenarios'] if entry['status'] == 'pass')
    checkpoint = next(iter(entry['pi']['checkpoints']))
    if field == 'providerTexts':
        entry['pisharp']['providerTexts'] = entry['pi']['providerTexts'] + ['hidden mismatch']
    elif field == 'candidates':
        entry['pisharp']['checkpoints'][checkpoint]['candidates'] = ['invented']
    elif field == 'cursor':
        entry['pisharp']['checkpoints'][checkpoint]['cursor'] = {'x': -1, 'y': -1}


def _unclassify(report):
    entry = next(entry for entry in report['scenarios'] if entry['status'] == 'pass')
    checkpoint = next(iter(entry['pi']['checkpoints']))
    entry['pisharp']['checkpoints'][checkpoint]['editorText'] = 'unclassified drift'
    entry['differences'].append({'checkpoint': checkpoint, 'field': 'editorText'})
    entry['blockedBy'] = []
    entry['status'] = 'fail'


def _false_known(report):
    global KNOWN_DIFFERENCES
    conditions = copy.deepcopy(KNOWN_DIFFERENCES)
    conditions.append({'id': 'invented-difference', 'tasks': ['history'], 'scenarios': ['history'],
                       'fields': ['editorText'], 'classification': 'invented'})
    report['knownDifferences'] = conditions


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('report', nargs='?', type=Path,
                        default=ROOT / 'docs/parity/fixtures/tui-editor-completion-paste-001/report.json')
    parser.add_argument('--head', help='Reject provenance that is not an ancestor of this SHA.')
    parser.add_argument('--require-clean', action='store_true')
    parser.add_argument('--self-test', action='store_true', help='Run negative controls.')
    args = parser.parse_args()
    try:
        report = load(args.report)
        result = verify(report, head=args.head, require_clean=args.require_clean)
        if args.self_test:
            controls = 0
            for name, mutated, fragment in mutations(report):
                controls += 1
                try:
                    verify(mutated, head=git_head())
                except VerificationError as error:
                    if fragment not in str(error):
                        raise VerificationError(
                            f'Negative control {name} was rejected for the wrong reason: {error}')
                else:
                    raise VerificationError(f'Negative control {name} was accepted.')
            print(f'{controls} negative controls rejected.')
        print(json.dumps(result))
        return 0 if result['total'] == 100 else 2
    except VerificationError as error:
        print(error, file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
