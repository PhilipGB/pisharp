#!/usr/bin/env python3
"""Verify the keys-editor-movement-history acceptance manifest and its executable evidence."""
import argparse
import copy
import gzip
import json
import os
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
REQUIRED_CASES = [f'N{index:02d}' for index in range(1, 17)]
PI_SHA = 'b06c1f2cf1dea6af8748ddeee18c23587d2cb7c6'
EXPECTED_REQUESTS = ['first history prompt', 'second history prompt', 'provider movement input']
TEMPORARY_PRIVATE_MODES = ('1000', '1002', '1003', '1004', '1006', '1049', '2004', '2026')


class VerificationError(RuntimeError):
    pass


def load_json(path):
    try:
        return json.loads(Path(path).read_text(encoding='utf-8'))
    except (OSError, ValueError) as error:
        raise VerificationError(f'Cannot read {path}: {error}') from error


def load_assertions(path):
    try:
        with gzip.open(path, 'rt', encoding='utf-8') as stream:
            return json.load(stream)
    except (OSError, ValueError) as error:
        raise VerificationError(f'Cannot read assertion evidence {path}: {error}') from error


def require(condition, message):
    if not condition:
        raise VerificationError(message)


def validate_manifest(manifest, implementation):
    require(manifest.get('schema') == 1, 'Manifest schema must be 1.')
    require(manifest.get('capability') == 'keys-editor-movement-history',
            'Manifest capability must be keys-editor-movement-history.')
    require(manifest.get('piSha') == PI_SHA, f"Manifest Pi SHA is {manifest.get('piSha')!r}, expected {PI_SHA}.")
    recorded = manifest.get('implementationSha')
    require(len(recorded or '') == 40, 'Manifest must record a 40-character implementation SHA.')
    require(recorded == implementation,
            f'Stale implementation SHA: manifest has {recorded!r}, tested head is {implementation!r}.')
    cases = manifest.get('cases') or []
    ids = [case.get('id') for case in cases]
    require(len(ids) == len(set(ids)), 'Manifest contains duplicate case ids.')
    require(ids == REQUIRED_CASES, f'Manifest cases must be {REQUIRED_CASES}, found {ids}.')
    for case in cases:
        require(case.get('required') is True, f"{case['id']} must be required.")
        require(case.get('status') == 'pass',
                f"{case['id']} is {case.get('status')!r}; required cases may only be pass.")
        require(any(case.get(key) for key in ('oracle', 'tests', 'pty', 'terminalCheckpoints')),
                f"{case['id']} has no executable evidence.")
    comparisons = manifest.get('requiredComparisons') or []
    require(comparisons, 'Manifest has no required comparisons.')
    for comparison in comparisons:
        require(comparison.get('required') is True, f"Comparison {comparison.get('id')} must be required.")
        require(comparison.get('status') == 'match',
                f"Unresolved required comparison {comparison.get('id')}: {comparison.get('status')!r}.")
    for finding in manifest.get('findings') or []:
        require(finding.get('required') is False,
                f"Finding {finding.get('id')} expands the required contract.")
    tests = manifest.get('requiredTests') or {}
    require(tests.get('theories') and tests.get('facts') and tests.get('ptyEvidence'),
            'Manifest must list required theories, facts and PTY evidence ids.')


def validate_case_references(manifest, oracle, results, evidence_ids, checkpoint_ids):
    tests = manifest['requiredTests']
    allowed_tests = set(tests['facts']) | set(tests['theories'])
    passing = {name for name, outcome in results if outcome == 'Passed'}
    scenario_names = set((oracle.get('scenarios') or {}).keys())
    for case in manifest.get('cases') or []:
        for name in case.get('oracle') or []:
            require(name in scenario_names,
                    f"{case['id']} references a nonexistent oracle scenario: {name!r}.")
        for method in case.get('tests') or []:
            require(method in allowed_tests,
                    f"{case['id']} references a test outside requiredTests: {method!r}.")
            require(any(name == method or name.startswith(method + '(') for name in passing),
                    f"{case['id']} references a test that did not pass: {method!r}.")
        for evidence_id in case.get('pty') or []:
            require(evidence_id in tests['ptyEvidence'],
                    f"{case['id']} references a PTY record outside requiredTests: {evidence_id!r}.")
            require(evidence_id in evidence_ids,
                    f"{case['id']} references a missing PTY record: {evidence_id!r}.")
        for checkpoint in case.get('terminalCheckpoints') or []:
            require(checkpoint in checkpoint_ids,
                    f"{case['id']} references a missing terminal checkpoint: {checkpoint!r}.")
    referenced_tests = {method for case in manifest['cases'] for method in (case.get('tests') or [])}
    require(referenced_tests == allowed_tests,
            f'Required tests are not referenced by any case: {sorted(allowed_tests - referenced_tests)}.')
    referenced_evidence = {evidence_id for case in manifest['cases'] for evidence_id in (case.get('pty') or [])}
    require(referenced_evidence == set(tests['ptyEvidence']),
            f'PTY evidence is not referenced by any case: '
            f'{sorted(set(tests["ptyEvidence"]) - referenced_evidence)}.')
    referenced_scenarios = {name for case in manifest['cases'] for name in (case.get('oracle') or [])}
    require(referenced_scenarios == scenario_names,
            f'Oracle scenarios are not referenced by any case: '
            f'{sorted(scenario_names - referenced_scenarios)}.')
    referenced_checkpoints = {checkpoint for case in manifest['cases']
                              for checkpoint in (case.get('terminalCheckpoints') or [])}
    require(referenced_checkpoints == checkpoint_ids,
            f'Terminal checkpoints are not referenced by any case: '
            f'{sorted(checkpoint_ids - referenced_checkpoints)}.')


def validate_oracle(oracle, manifest):
    require(oracle.get('piSha') == manifest.get('piSha'),
            f"Oracle pin is {oracle.get('piSha')!r}, expected {manifest.get('piSha')!r}.")
    scenarios = oracle.get('scenarios') or {}
    comparison = next((item for item in manifest.get('requiredComparisons', [])
                       if item.get('id') == 'editor-buffer-pinned-pi-oracle'), None)
    require(comparison is not None, 'Manifest is missing the editor-buffer oracle comparison.')
    require(len(scenarios) == comparison.get('scenarios'),
            f"Oracle has {len(scenarios)} scenarios, manifest claims {comparison.get('scenarios')}.")
    for name, scenario in scenarios.items():
        steps = scenario.get('steps') or []
        expected = scenario.get('expected') or []
        require(len(steps) == len(expected), f'Oracle scenario {name!r} has mismatched steps and expectations.')
        require(expected, f'Oracle scenario {name!r} has no expected state.')
        for observation in expected:
            require(observation.get('text') is not None, f'Oracle scenario {name!r} lacks text.')
            require(observation.get('cursor', {}).get('line') is not None,
                    f'Oracle scenario {name!r} lacks a logical cursor.')
            require(observation.get('visual', {}).get('row') is not None,
                    f'Oracle scenario {name!r} lacks a visual cursor.')
    for finding in oracle.get('findings') or []:
        require(finding.get('required') is False, f"Oracle finding {finding.get('id')} is marked required.")


def trx_results(path):
    try:
        tree = ET.parse(path)
    except (OSError, ET.ParseError) as error:
        raise VerificationError(f'Cannot read TRX {path}: {error}') from error
    results = []
    for element in tree.getroot().iter('{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult'):
        results.append((element.get('testName') or '', element.get('outcome') or ''))
    require(results, f'TRX {path} contains no test results.')
    return results


def validate_trx(results, manifest, oracle_count):
    tests = manifest['requiredTests']
    skipped = {'NotExecuted', 'Skipped', 'Inconclusive'}
    for method in tests['facts']:
        matching = [(name, outcome) for name, outcome in results
                    if name == method or name.startswith(method + '(')]
        require(matching, f'Required test did not run: {method}')
        for name, outcome in matching:
            require(outcome not in skipped, f'Required test was skipped: {name} ({outcome}).')
            require(outcome == 'Passed', f'Required test failed: {name} ({outcome}).')
    theory = tests['theories'][0]
    passed_theories = [name for name, outcome in results
                       if name.startswith(theory) and outcome == 'Passed']
    require(len(passed_theories) == oracle_count,
            f'Expected {oracle_count} passing pinned-Pi theory cases, found {len(passed_theories)}.')


def validate_full_trx(results):
    skipped = {'NotExecuted', 'Skipped', 'Inconclusive'}
    failures = [name for name, outcome in results if outcome not in ('Passed',) and outcome not in skipped]
    skips = [name for name, outcome in results if outcome in skipped]
    require(not failures, f'Full suite has failing tests: {failures[:5]}')
    require(not skips, f'Full suite has skipped tests: {skips[:5]}')


def validate_evidence(path, manifest, implementation):
    try:
        lines = [line for line in Path(path).read_text(encoding='utf-8').splitlines() if line.strip()]
    except OSError as error:
        raise VerificationError(f'Cannot read PTY evidence {path}: {error}') from error
    require(lines, 'PTY evidence file is empty; the real Linux PTY cases did not run.')
    records = [json.loads(line) for line in lines]
    expected = manifest['requiredTests']['ptyEvidence']
    ids = [record.get('id') for record in records]
    require(sorted(ids) == sorted(expected), f'PTY evidence ids are {sorted(ids)}, expected {sorted(expected)}.')
    for record in records:
        require(record.get('implementation') == implementation,
                f"PTY evidence {record.get('id')} has stale implementation {record.get('implementation')!r}.")
        require(record.get('platform') == 'linux',
                f"PTY evidence {record.get('id')} did not run on Linux.")
        details = record.get('details') or {}
        require(details.get('exitCode') == 0, f"PTY evidence {record.get('id')} recorded a nonzero exit.")
    provider = next(record for record in records if record.get('id') == 'pty-provider-lifecycle')
    details = provider.get('details') or {}
    require(details.get('providerText') == 'provider movement input',
            'Provider lifecycle evidence does not contain the expected provider-visible text.')
    require(details.get('requests') == 3, 'Provider lifecycle evidence did not capture all three requests.')
    require(details.get('restoredDraft') == 'draft line one\ndraft line two',
            'Provider lifecycle evidence did not record the restored multiline draft.')
    require(details.get('historyEntries') == 2, 'Provider lifecycle evidence did not record both history entries.')
    for record in records:
        if record.get('id') in ('pty-provider-lifecycle', 'pty-external-editor'):
            require((record.get('details') or {}).get('termiosRestored') is True,
                    f"PTY evidence {record.get('id')} did not record terminal restoration.")
    visual = next(record for record in records if record.get('id') == 'pty-visual-page')
    visual_details = visual.get('details') or {}
    require(visual_details.get('pageDownRow', 0) > visual_details.get('firstVisualRow', 0),
            'Visual-page evidence did not record page-down cursor movement.')
    require(visual_details.get('pageUpRow') == visual_details.get('firstVisualRow'),
            'Visual-page evidence did not record page-up restoration.')
    return set(ids)


def validate_report_data(report, manifest):
    require(report.get('piSha') == manifest.get('piSha'),
            f"Report pin is {report.get('piSha')!r}, expected {manifest.get('piSha')!r}.")
    require(report.get('fixture') == 'terminal-editor-movement',
            'Report is not for the terminal-editor-movement fixture.')
    cases = report.get('cases') or []
    require(len(cases) == 2, f'Report has {len(cases)} terminal cases, expected two heights.')
    for case in cases:
        errors = case.get('scenarioErrors') or {}
        require(not errors, f"{case.get('id')} has scenario errors: {errors}")
        require(case.get('httpMatch') is True, f"{case.get('id')} provider HTTP traces differ.")
        require(case.get('rawHttpMatch') is True, f"{case.get('id')} raw provider HTTP traces differ.")


def editor_lines(lines):
    separators = [index for index, line in enumerate(lines) if line.lstrip().startswith('─')]
    if len(separators) < 2:
        raise VerificationError('Editor separators were not rendered.')
    bottom = separators[-1]
    top = max((index for index in separators if index < bottom), default=None)
    if top is None:
        raise VerificationError('Editor top separator was not rendered.')
    return [line for line in lines[top + 1:bottom] if line.strip()]


def viewport_lines(state):
    return [''.join(cell.get('chars', ' ') for cell in line.get('cells', [])).rstrip()
            for line in state.get('viewport', [])]


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
    raise VerificationError('Request had no user message.')


def validate_checkpoint_case(case_id, expected_case, observed):
    checkpoints = expected_case.get('checkpoints') or {}
    frames = observed['frames']
    require(set(frames['pi']) == set(frames['pisharp']),
            f"{case_id} named checkpoints differ between products.")
    require(set(checkpoints) <= set(frames['pi']),
            f"{case_id} is missing pinned-Pi checkpoints.")
    for product in ('pi', 'pisharp'):
        for checkpoint, state_expected in checkpoints.items():
            state = frames[product][checkpoint]['state']
            actual_lines = editor_lines(viewport_lines(state))
            require(actual_lines == state_expected['editor'],
                    f"{case_id}/{product}/{checkpoint} editor differs from pinned Pi.")
            cursor = state.get('cursor') or {}
            require((cursor.get('x'), cursor.get('y')) ==
                    (state_expected['cursor']['x'], state_expected['cursor']['y']),
                    f"{case_id}/{product}/{checkpoint} cursor differs from pinned Pi.")
        exit_state = frames[product]['exit']['state']
        require(exit_state.get('activeScreen') == 'normal',
                f"{case_id}/{product} did not restore the normal screen.")
        require(exit_state.get('cursor', {}).get('visible') is True,
                f"{case_id}/{product} did not restore a visible cursor.")
        modes = exit_state.get('modes', {}).get('private', {})
        leaked = [mode for mode in TEMPORARY_PRIVATE_MODES if modes.get(mode, False)]
        require(not leaked, f"{case_id}/{product} left temporary modes active: {leaked}.")
        texts = observed['requests'][product]
        require(len(texts) == len(EXPECTED_REQUESTS),
                f"{case_id}/{product} submitted {len(texts)} requests, "
                f"expected {len(EXPECTED_REQUESTS)}.")
        require(texts == EXPECTED_REQUESTS,
                f"{case_id}/{product} provider texts are {texts!r}, expected {EXPECTED_REQUESTS!r}.")


def validate_checkpoints(checkpoint_file, report):
    expected = load_json(checkpoint_file)
    require(expected.get('piSha') == PI_SHA,
            f"Checkpoint pin is {expected.get('piSha')!r}, expected {PI_SHA}.")
    cases = expected.get('cases') or {}
    require(len(cases) == 2, f'Checkpoint evidence has {len(cases)} cases, expected two heights.')
    checkpoint_ids = set()
    for case in report.get('cases') or []:
        observation = cases.get(case['id'])
        require(observation is not None, f"{case['id']} has no pinned-Pi checkpoint observation.")
        checkpoint_ids.update(observation.get('checkpoints') or {})
        evidence = load_assertions(Path(report.get('_directory', '')) / case['assertionEvidence'])
        frames = {product: {frame['id']: frame for frame in evidence[product]['frames']}
                  for product in ('pi', 'pisharp')}
        requests = {product: [user_text(request) for request in evidence[product]['http']]
                    for product in ('pi', 'pisharp')}
        validate_checkpoint_case(case['id'], observation, {'frames': frames, 'requests': requests})
    return checkpoint_ids


def run(command, env=None, timeout=1800):
    result = subprocess.run(command, cwd=ROOT, env=env, text=True, capture_output=True, timeout=timeout)
    if result.returncode != 0:
        tail = (result.stdout[-3000:] + result.stderr[-3000:]).strip()
        raise VerificationError(f'Command failed: {" ".join(command)}\n{tail}')
    return result.stdout


def run_focused_tests(implementation):
    directory = tempfile.mkdtemp(prefix='pisharp-movement-verify-')
    trx = Path(directory) / 'movement.trx'
    evidence = Path(directory) / 'pty-evidence.jsonl'
    environment = dict(os.environ)
    environment['PISHARP_EDITOR_MOVEMENT_EVIDENCE'] = str(evidence)
    environment['PISHARP_IMPLEMENTATION_SHA'] = implementation
    filter_value = '|'.join([
        'FullyQualifiedName~EditorMovementParityTests',
        'FullyQualifiedName~EditorMovementPtyTests',
        'FullyQualifiedName~EditorSelectionMovementTests',
        'FullyQualifiedName~EditorMovementBindingTests',
        'FullyQualifiedName~MovementFailFirstTests',
        'FullyQualifiedName~EditorViewportTests',
        'FullyQualifiedName~EditorKeymapTests.HistoryBindingsRemainAboveApplicationShortcuts',
        'FullyQualifiedName~EditorKeymapTests.ApplicationShortcutTakesPrecedenceOverEditorBindingInTheEditorContext',
        'FullyQualifiedName~EditorKeymapTests.ActiveScreenSearchAndPagingBindingsDecodeFromTerminalInput',
        'FullyQualifiedName~EditorKeymapTests.PromptSelectionUsesConfigurableNamedEditorActions',
        'FullyQualifiedName~EditorBufferTests.PromptHistoryTrimsConsecutiveDuplicatesAndKeepsTheNewestHundredEntries',
        'FullyQualifiedName~EditorBufferTests.KeyboardSelectionMovesByGraphemeAndPlainMovementCollapsesIt',
        'FullyQualifiedName~EditorBufferTests.TypingAndDeletingReplaceKeyboardSelectionAndUndoRestoresIt',
        'FullyQualifiedName~EditorMutationParityTests.SelectionDeletionReplacementAndKillsKeepCursorAndUndoIntegrity',
        'FullyQualifiedName~TerminalInputTests.DecodesLegacyControlBytesForMutationKeybindings'
    ])
    run(['dotnet', 'test', 'tests/PiSharp.Tests/PiSharp.Tests.csproj', '--filter', filter_value,
         '--logger', 'trx;LogFileName=movement.trx', '--results-directory', directory,
         '--nologo', '-v', 'q'], env=environment)
    return trx, evidence


def self_test(manifest, oracle, checkpoint_file):
    def rejects(call, label):
        try:
            call()
        except VerificationError:
            print(f'negative control rejected: {label}')
            return
        raise VerificationError(f'Negative control accepted an invalid case: {label}')

    missing = copy.deepcopy(manifest)
    missing['cases'] = [case for case in missing['cases'] if case['id'] != 'N16']
    rejects(lambda: validate_manifest(missing, manifest['implementationSha']), 'missing matrix case')

    duplicate = copy.deepcopy(manifest)
    duplicate['cases'].append(copy.deepcopy(duplicate['cases'][0]))
    rejects(lambda: validate_manifest(duplicate, manifest['implementationSha']), 'duplicate matrix case')

    failed = copy.deepcopy(manifest)
    failed['cases'][0]['status'] = 'failed'
    rejects(lambda: validate_manifest(failed, manifest['implementationSha']), 'failed matrix case')

    unsupported = copy.deepcopy(manifest)
    unsupported['cases'][1]['status'] = 'unsupported'
    rejects(lambda: validate_manifest(unsupported, manifest['implementationSha']), 'unsupported matrix case')

    stale = copy.deepcopy(manifest)
    stale['implementationSha'] = '0' * 40
    rejects(lambda: validate_manifest(stale, manifest['implementationSha']), 'stale implementation SHA')

    wrong_pi = copy.deepcopy(manifest)
    wrong_pi['piSha'] = '1' * 40
    rejects(lambda: validate_manifest(wrong_pi, manifest['implementationSha']), 'incorrect Pi SHA')

    unresolved = copy.deepcopy(manifest)
    unresolved['requiredComparisons'][0]['status'] = 'differs'
    rejects(lambda: validate_manifest(unresolved, manifest['implementationSha']),
            'unresolved required comparison')

    bad_oracle = copy.deepcopy(oracle)
    bad_oracle['scenarios']['n01-left-right-graphemes']['expected'].pop()
    rejects(lambda: validate_oracle(bad_oracle, manifest), 'incomplete oracle scenario')

    with tempfile.TemporaryDirectory(prefix='pisharp-movement-self-test-') as directory:
        skipped = Path(directory) / 'skipped.trx'
        skipped.write_text(_trx_document([
            ('PiSharp.Tests.EditorMovementParityTests.MatchesPinnedPi(name: "x")', 'NotExecuted'),
            (manifest['requiredTests']['facts'][0], 'Passed'),
        ]), encoding='utf-8')
        rejects(lambda: validate_trx(trx_results(skipped), manifest, 1), 'skipped test')

        failed_trx = Path(directory) / 'failed.trx'
        failed_trx.write_text(_trx_document([
            ('PiSharp.Tests.EditorMovementParityTests.MatchesPinnedPi(name: "x")', 'Failed'),
            (manifest['requiredTests']['facts'][0], 'Passed'),
        ]), encoding='utf-8')
        rejects(lambda: validate_trx(trx_results(failed_trx), manifest, 1), 'failed test')

        evidence = Path(directory) / 'evidence.jsonl'
        evidence.write_text('', encoding='utf-8')
        rejects(lambda: validate_evidence(evidence, manifest, manifest['implementationSha']),
                'missing PTY record')

        evidence.write_text(json.dumps({'id': 'pty-movement-keys',
                                        'implementation': '0' * 40, 'platform': 'linux',
                                        'details': {'exitCode': 0}}) + '\n', encoding='utf-8')
        rejects(lambda: validate_evidence(evidence, manifest, manifest['implementationSha']),
                'stale PTY evidence')

    bad_report = {'fixture': 'terminal-editor-movement', 'piSha': PI_SHA,
                  'cases': [{'id': '100x30-dark-fullscreen', 'scenarioErrors': {'pi': 'timeout'},
                             'httpMatch': True, 'rawHttpMatch': True}]}
    rejects(lambda: validate_report_data(bad_report, manifest), 'unresolved terminal scenario error')
    bad_http = {'fixture': 'terminal-editor-movement', 'piSha': PI_SHA,
                'cases': [{'id': '100x30-dark-fullscreen', 'scenarioErrors': {},
                           'httpMatch': False, 'rawHttpMatch': True},
                          {'id': '80x24-dark-fullscreen', 'scenarioErrors': {},
                           'httpMatch': True, 'rawHttpMatch': True}]}
    rejects(lambda: validate_report_data(bad_http, manifest), 'provider HTTP mismatch')

    compact = load_json(checkpoint_file)
    checkpoint_ids = set(compact['cases']['100x30-dark-fullscreen']['checkpoints'])
    evidence_ids = set(manifest['requiredTests']['ptyEvidence'])
    synthetic_results = ([(method, 'Passed') for method in manifest['requiredTests']['facts']] +
                         [(manifest['requiredTests']['theories'][0], 'Passed')])
    validate_case_references(manifest, oracle, synthetic_results, evidence_ids, checkpoint_ids)

    bad_oracle_ref = copy.deepcopy(manifest)
    bad_oracle_ref['cases'][0]['oracle'] = ['nonexistent oracle scenario']
    rejects(lambda: validate_case_references(bad_oracle_ref, oracle, synthetic_results,
                                             evidence_ids, checkpoint_ids),
            'nonexistent oracle reference')

    bad_test_ref = copy.deepcopy(manifest)
    bad_test_ref['cases'][0]['tests'] = ['PiSharp.Tests.EditorMovementParityTests.DoesNotExist']
    rejects(lambda: validate_case_references(bad_test_ref, oracle, synthetic_results,
                                             evidence_ids, checkpoint_ids),
            'nonexistent test reference')

    bad_pty_ref = copy.deepcopy(manifest)
    bad_pty_ref['cases'][0]['pty'] = ['pty-nonexistent']
    rejects(lambda: validate_case_references(bad_pty_ref, oracle, synthetic_results,
                                             evidence_ids, checkpoint_ids),
            'nonexistent PTY evidence reference')

    bad_checkpoint_ref = copy.deepcopy(manifest)
    bad_checkpoint_ref['cases'][0]['terminalCheckpoints'] = ['nonexistent-checkpoint']
    rejects(lambda: validate_case_references(bad_checkpoint_ref, oracle, synthetic_results,
                                             evidence_ids, checkpoint_ids),
            'nonexistent terminal checkpoint reference')

    unreferenced = copy.deepcopy(manifest)
    next(case for case in unreferenced['cases'] if case['id'] == 'N15')['tests'] = []
    rejects(lambda: validate_case_references(unreferenced, oracle, synthetic_results,
                                             evidence_ids, checkpoint_ids),
            'required test not referenced by any case')

    checkpoint_case = next(iter(compact['cases'].values()))
    observed = _observed_from_checkpoint_case(checkpoint_case)
    validate_checkpoint_case('100x30-dark-fullscreen', checkpoint_case, observed)

    bad_cursor = copy.deepcopy(checkpoint_case)
    bad_cursor['checkpoints']['movement-draft']['cursor']['x'] += 1
    rejects(lambda: validate_checkpoint_case('100x30-dark-fullscreen', bad_cursor, observed),
            'altered expected cursor observation')

    bad_history = copy.deepcopy(checkpoint_case)
    bad_history['checkpoints']['history-up-newest']['editor'] = ['draft line one', 'draft line two']
    rejects(lambda: validate_checkpoint_case('100x30-dark-fullscreen', bad_history, observed),
            'invalid history ordering')

    bad_provider_text = copy.deepcopy(observed)
    bad_provider_text['requests']['pi'] = [EXPECTED_REQUESTS[0], EXPECTED_REQUESTS[1], 'wrong text']
    rejects(lambda: validate_checkpoint_case('100x30-dark-fullscreen', checkpoint_case, bad_provider_text),
            'incorrect provider-visible text')

    bad_provider_count = copy.deepcopy(observed)
    bad_provider_count['requests']['pisharp'] = EXPECTED_REQUESTS[:2]
    rejects(lambda: validate_checkpoint_case('100x30-dark-fullscreen', checkpoint_case, bad_provider_count),
            'incorrect provider request count/order')

    missing_restoration = copy.deepcopy(observed)
    missing_restoration['frames']['pi']['exit']['state']['cursor']['visible'] = False
    rejects(lambda: validate_checkpoint_case('100x30-dark-fullscreen', checkpoint_case, missing_restoration),
            'missing terminal-restoration evidence')

    print('negative controls passed')


def _observed_from_checkpoint_case(checkpoint_case):
    frames = {}
    for product in ('pi', 'pisharp'):
        product_frames = {}
        for checkpoint, observation in checkpoint_case['checkpoints'].items():
            product_frames[checkpoint] = {'state': {
                'viewport': _viewport_for(observation['editor']),
                'cursor': dict(observation['cursor']),
                'activeScreen': 'alternate',
                'modes': {'private': {}},
            }}
        exit_cursor = dict(checkpoint_case['checkpoints']['exit']['cursor'])
        exit_cursor['visible'] = True
        product_frames['exit'] = {'state': {
            'viewport': _viewport_for([]),
            'cursor': exit_cursor,
            'activeScreen': 'normal',
            'modes': {'private': {}},
        }}
        frames[product] = product_frames
    return {'frames': frames, 'requests': {product: list(EXPECTED_REQUESTS) for product in ('pi', 'pisharp')}}


def _viewport_for(lines):
    separator = {'cells': [{'chars': '─' * 20}]}
    return [separator, *[{'cells': [{'chars': line}]} for line in lines], separator]


def _trx_document(results):
    rows = ''.join(f'<UnitTestResult testName="{name}" outcome="{outcome}" />' for name, outcome in results)
    return ('<?xml version="1.0" encoding="utf-8"?>'
            '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            f'<Results>{rows}</Results></TestRun>')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path,
                        default=ROOT / 'docs/parity/fixtures/keys-editor-movement-acceptance-2026-11-02.json')
    parser.add_argument('--oracle', type=Path,
                        default=ROOT / 'docs/parity/fixtures/keys-editor-movement-pi-oracle-2026-11-02.json')
    parser.add_argument('--checkpoints', type=Path,
                        default=ROOT / 'docs/parity/fixtures/keys-editor-movement-checkpoints-2026-11-02.json')
    parser.add_argument('--report', type=Path,
                        default=ROOT / 'artifacts/parity/keys-editor-movement-paired/report.json')
    parser.add_argument('--implementation')
    parser.add_argument('--pi', type=Path)
    parser.add_argument('--trx', type=Path)
    parser.add_argument('--evidence', type=Path)
    parser.add_argument('--full-trx', type=Path)
    parser.add_argument('--skip-tests', action='store_true')
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()

    implementation = args.implementation or subprocess.check_output(
        ['git', 'log', '-1', '--format=%H', '--', 'src', 'tests'], cwd=ROOT, text=True).strip()
    latest_implementation = subprocess.check_output(
        ['git', 'log', '-1', '--format=%H', '--', 'src', 'tests'], cwd=ROOT, text=True).strip()
    if implementation != latest_implementation:
        raise VerificationError(
            f'Stale implementation SHA: requested {implementation!r}, latest src/tests commit is '
            f'{latest_implementation!r}.')
    manifest = load_json(args.manifest)
    oracle = load_json(args.oracle)
    checkpoint_file = args.checkpoints
    if args.self_test:
        self_test(manifest, oracle, checkpoint_file)
        return
    validate_manifest(manifest, implementation)
    validate_oracle(oracle, manifest)
    if args.pi:
        run(['python3', 'tools/parity/pi_reference.py', 'verify', '--pi', str(args.pi)])
    if args.full_trx:
        validate_full_trx(trx_results(args.full_trx))
    if args.trx and args.evidence:
        trx, evidence = args.trx, args.evidence
    elif args.skip_tests:
        raise VerificationError('--skip-tests requires --trx and --evidence; required executable evidence is missing.')
    else:
        trx, evidence = run_focused_tests(implementation)
    results = trx_results(trx)
    validate_trx(results, manifest, len(oracle['scenarios']))
    evidence_ids = validate_evidence(evidence, manifest, implementation)
    report = load_json(args.report)
    report['_directory'] = str(args.report.parent)
    validate_report_data(report, manifest)
    checkpoint_ids = validate_checkpoints(checkpoint_file, report)
    validate_case_references(manifest, oracle, results, evidence_ids, checkpoint_ids)
    run(['python3', str(ROOT / manifest['terminalAssertion']), str(args.report)])
    print(f"keys-editor-movement-history verified at {implementation}: 16 cases, "
          f"{len(oracle['scenarios'])} pinned-Pi scenarios, {len(checkpoint_ids)} terminal checkpoints.")


if __name__ == '__main__':
    try:
        main()
    except (VerificationError, subprocess.SubprocessError, OSError, ValueError) as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
