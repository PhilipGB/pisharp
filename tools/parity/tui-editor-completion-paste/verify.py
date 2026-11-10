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
FIXTURE = ROOT / 'docs/parity/fixtures/tui-editor-completion-paste-001'
ACCEPTANCE = FIXTURE / 'acceptance.json'
HEAD_FILE = FIXTURE / 'head.txt'
SIZES = {'80x24', '120x40'}
REQUIRED = {
    'cursor-unicode': ['cursor-unicode'],
    'multiline': ['multiline', 'soft-wrap'],
    'history': ['history'],
    'slash-completion': ['slash-completion'],
    'path-completion': ['path-completion'],
    'bracketed-paste': ['bracketed-paste', 'large-paste-marker', 'large-paste-external-editor'],
    'escape-ctrl-c': ['escape-ctrl-c', 'exit-empty-ctrl-d'],
    'external-editor': ['external-editor', 'external-editor-failure',
                        'external-editor-visual', 'external-editor-editor'],
    'journey': ['journey'],
}
RUBRIC = {'cursor-unicode': 15, 'multiline': 10, 'history': 10, 'slash-completion': 15,
          'path-completion': 15, 'bracketed-paste': 10, 'escape-ctrl-c': 10,
          'external-editor': 10, 'journey': 5}
CHECKPOINT_FIELDS = ('editorText', 'cursor', 'cursorColumn', 'candidates', 'selected',
                     'activeScreen', 'descriptions')
# Frozen acceptance cases. Removing one from the manifest must fail verification.
FROZEN_CASES = {
    'slash-exact-prefix', 'slash-fuzzy-non-prefix', 'slash-fuzzy-ranking',
    'slash-multiple-candidates-order', 'slash-keyboard-navigation', 'slash-selection-apply',
    'slash-trailing-space', 'slash-no-argument-command', 'slash-argument-command',
    'slash-argument-model-completion', 'slash-argument-thinking-completion',
    'slash-no-matches', 'slash-argument-no-matches', 'slash-escape-preserves-draft',
    'slash-editor-clear', 'slash-pasted-text-does-not-open',
    'slash-typed-key-opens-after-paste', 'slash-submitted-prompt',
    'file-simple-filename', 'file-nested-filename', 'file-fuzzy-filename',
    'file-multiple-candidates-order', 'file-relative-path', 'file-path-with-spaces',
    'file-directory-display', 'file-selection-apply', 'file-trailing-space',
    'file-no-matches', 'file-escape-preserves-draft', 'file-pasted-vs-typed',
    'file-embedded-completion', 'file-provider-visible-text', 'file-project-root-boundary',
    'file-hidden-and-git', 'file-symlink', 'file-forced-tab-completion',
}
NORMALIZATION_IDS = {'post-exit-scroll-position', 'completion-label-display-prefix',
                     'completion-position-row', 'external-editor-temporary-path',
                     'slash-command-description-copy'}


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


def expected_differences(pi, pisharp, compare_descriptions=False):
    differences = []
    left = pi['checkpoints']
    right = pisharp['checkpoints']
    fields = [field for field in CHECKPOINT_FIELDS
              if field != 'descriptions' or compare_descriptions]
    for checkpoint in sorted(set(left) | set(right)):
        if checkpoint == 'exit':
            continue
        if checkpoint not in left or checkpoint not in right:
            differences.append((checkpoint, 'missing'))
            continue
        for field in fields:
            if left[checkpoint][field] != right[checkpoint][field]:
                differences.append((checkpoint, field))
    if pi['providerTexts'] != pisharp['providerTexts']:
        differences.append((None, 'providerTexts'))
    def captured_inputs(value):
        return {name: content for name, content in (value.get('capturedWorkspaceFiles') or {}).items()
                if name.endswith('.input')}
    if captured_inputs(pi) != captured_inputs(pisharp):
        differences.append((None, 'capturedInputs'))
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


def verify_acceptance(report, acceptance, expected_head):
    require(acceptance.get('schema') == 1, 'Acceptance manifest schema must be 1.')
    require(acceptance.get('piSha') == PI_SHA, 'Acceptance manifest pins the wrong Pi revision.')
    cases = acceptance.get('cases') or []
    ids = [case.get('id') for case in cases]
    require(len(ids) == len(set(ids)), 'Acceptance manifest has duplicate case ids.')
    require(set(ids) == FROZEN_CASES,
            f'Acceptance cases differ from the frozen set. Missing={sorted(FROZEN_CASES - set(ids))} '
            f'extra={sorted(set(ids) - FROZEN_CASES)}.')
    deferred = acceptance.get('deferred') or []
    deferred_ids = [entry.get('id') for entry in deferred]
    require(len(deferred_ids) == len(set(deferred_ids)), 'Deferred entries have duplicate ids.')
    require(not (set(deferred_ids) & set(ids)), 'A deferred behaviour is also listed as a case.')
    for entry in deferred:
        require(entry.get('reason') and entry.get('oracle'), 'Deferred entries need a reason and oracle.')
    require(isinstance(expected_head, str) and len(expected_head) == 40,
            'head.txt must pin the 40-character evidence head.')
    require(report.get('pisharpSha') == expected_head,
            f"Evidence head {report.get('pisharpSha')!r} does not match head.txt {expected_head!r}.")
    entries = {(entry['id'], entry['size']): entry for entry in report['scenarios']}
    for case in cases:
        case_id = case['id']
        require(case.get('mandatory') is True, f'{case_id} must be mandatory.')
        require(case.get('status') == 'pass', f'{case_id} is not marked passed.')
        scenario = case.get('scenario')
        require(scenario in REQUIRED.get(case.get('area')),
                f'{case_id} names scenario {scenario!r} outside its area.')
        checkpoints = case.get('checkpoints') or []
        require(checkpoints, f'{case_id} lists no checkpoints.')
        expected = case.get('expect') or {}
        require(expected, f'{case_id} lists no expected observation.')
        require(set(expected) <= set(CHECKPOINT_FIELDS),
                f'{case_id} expects unknown fields {sorted(set(expected) - set(CHECKPOINT_FIELDS))}.')
        if 'providerTexts' in expected:
            require(isinstance(expected['providerTexts'], list) and expected['providerTexts'],
                    f'{case_id} must freeze provider-visible text.')
        for size in SIZES:
            entry = entries.get((scenario, size))
            require(entry is not None, f'{case_id} has no {scenario}/{size} evidence.')
            for product in ('pi', 'pisharp'):
                observations = entry[product].get('checkpoints') or {}
                for checkpoint in checkpoints:
                    require(checkpoint in observations,
                            f'{case_id} ({size}) is missing {product} evidence for {checkpoint}.')
                    observation = observations[checkpoint]
                    for field, value in expected.items():
                        if field == 'providerTexts':
                            continue
                        require(observation.get(field) == value,
                                f'{case_id} ({size}) {field} at {checkpoint} is '
                                f'{observation.get(field)!r}, expected {value!r}.')
            if 'providerTexts' in expected:
                for product in ('pi', 'pisharp'):
                    require(entry[product].get('providerTexts') == expected['providerTexts'],
                            f'{case_id} ({size}) {product} provider-visible text is '
                            f'{entry[product].get("providerTexts")!r}, expected {expected["providerTexts"]!r}.')


def verify(report, *, head=None, require_clean=False, acceptance=None, expected_head=None):
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
        expected = expected_differences(pi, pisharp,
                                        entry.get('compareCandidateDescriptions') is True)
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
    verify_acceptance(report, acceptance if acceptance is not None else load(ACCEPTANCE),
                      expected_head if expected_head is not None
                      else HEAD_FILE.read_text().strip())
    return {'total': sum(points.values()), 'points': points, 'differences': sum(
        len(entry.get('differences') or []) for entry in entries)}


def mutations(report, acceptance):
    """Yield (name, report, manifest, expected message fragment) negative controls."""
    def scenario(value, scenario_id, size='80x24'):
        return next(entry for entry in value['scenarios']
                    if entry['id'] == scenario_id and entry['size'] == size)

    def provider_case(value):
        return next(case for case in value['cases'] if 'providerTexts' in case['expect'])

    yield ('missing-scenario', _drop_scenario(report, 'history', '80x24'), acceptance, 'inventory')
    yield ('missing-size', _drop_scenario(report, 'cursor-unicode', '120x40'), acceptance, 'inventory')
    yield ('missing-checkpoint', _mutate(report, lambda r: scenario(r, 'cursor-unicode')['pi']
                                         ['checkpoints'].pop('typed')), acceptance, 'recorded differences')
    yield ('unexpected-pi-failure', _mutate(report, lambda r: scenario(r, 'history')['pi']
                                            .__setitem__('scenarioError', 'timeout')), acceptance, 'reported')
    yield ('hidden-candidate-mismatch', _mutate(report, lambda r: _hide(r, 'candidates')), acceptance,
           'recorded differences')
    yield ('hidden-provider-mismatch', _mutate(report, lambda r: _hide(r, 'providerTexts')), acceptance,
           'recorded differences')
    yield ('hidden-cursor-mismatch', _mutate(report, lambda r: _hide(r, 'cursor')), acceptance,
           'recorded differences')
    yield ('hidden-description-mismatch', _mutate(report, lambda r: _hide(r, 'descriptions')), acceptance,
           'recorded differences')
    yield ('calibration-failure', _mutate(report, lambda r: scenario(r, 'multiline')['calibration']
                                          .__setitem__('match', False)), acceptance, 'calibration')
    yield ('stale-provenance', _mutate(report, lambda r: r.__setitem__('pisharpSha', '0' * 40)),
           acceptance, 'Stale implementation provenance')
    yield ('omitted-known-difference', _mutate(report, _unclassify), acceptance, 'unexplained difference')
    yield ('false-known-difference', _mutate(report, _false_known), acceptance, 'known differences')
    yield ('altered-score', _mutate(report, lambda r: r['score']['points'].__setitem__('history', 0)),
           acceptance, 'points')
    yield ('wrong-pi-pin', _mutate(report, lambda r: r.__setitem__('piSha', '1' * 40)), acceptance, 'Pi pin')
    yield ('duplicate-entry', _mutate(report, lambda r: r['scenarios'].append(
        copy.deepcopy(r['scenarios'][0]))), acceptance, 'Duplicate')
    yield ('removed-mandatory-case', report,
           _mutate(acceptance, lambda m: m['cases'].pop(0)), 'frozen set')
    yield ('missing-case-evidence', _mutate(report, _drop_case_evidence), acceptance, 'missing')
    yield ('missing-provider-evidence', _mutate(report, _drop_provider_evidence), acceptance, 'provider-visible')
    yield ('altered-candidate-order', report,
           _mutate(acceptance, lambda m: _reverse_candidates(m)), 'expected')
    yield ('wrong-evidence-head', _mutate(report, lambda r: r.__setitem__('pisharpSha', '2' * 40)),
           acceptance, 'head.txt')
    yield ('wrong-source-inventory-sha', report,
           _mutate(acceptance, lambda m: m.__setitem__('piSha', '3' * 40)), 'wrong Pi revision')


def _drop_case_evidence(report):
    entry = next(value for value in report['scenarios'] if value['id'] == 'slash-completion')
    for product in ('pi', 'pisharp'):
        entry[product]['checkpoints'].pop('slash-ambiguous')


def _drop_provider_evidence(report):
    entry = next(value for value in report['scenarios'] if value['id'] == 'path-completion'
                 and value['size'] == '80x24')
    entry['pi']['providerTexts'] = []


def _reverse_candidates(acceptance):
    case = next(case for case in acceptance['cases']
                if len(case['expect'].get('candidates') or []) > 1)
    case['expect']['candidates'] = list(reversed(case['expect']['candidates']))


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
    elif field == 'descriptions':
        entry['pisharp']['checkpoints'][checkpoint]['descriptions'] = ['invented description']


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
            acceptance = load(ACCEPTANCE)
            expected_head = HEAD_FILE.read_text().strip()
            for name, mutated, manifest, fragment in mutations(report, acceptance):
                controls += 1
                try:
                    verify(mutated, head=git_head(), acceptance=manifest,
                           expected_head=expected_head)
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
