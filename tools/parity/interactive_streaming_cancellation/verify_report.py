#!/usr/bin/env python3
"""Verify the deterministic streaming/cancellation evidence report.

The verifier never launches a product and never trusts a score merely because
it is present. It recomputes every point from the recorded score inputs,
compares those inputs with the checkpoint assertions and difference counts,
checks fixture checksums, and rejects calibration or cleanup evidence that
would make a failed run look successful.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys

SEVERITIES = {'P0', 'P1', 'P2', 'Inconclusive'}
CATEGORY_ORDER = ['text', 'style', 'layout']
CLEANUP_SCENARIOS = {'S04', 'S05'}
REQUIRED_FILES = ['report.json', 'comparison.json', 'ui-report.json']


def read_json(path):
    return json.loads(path.read_text())


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def check(checks, name, condition, detail=''):
    checks.append(dict(name=name, passed=bool(condition), detail=str(detail)))
    return bool(condition)


def verify(evidence_dir, manifest_path=None, require_verification=False):
    evidence_dir = Path(evidence_dir)
    manifest_path = Path(manifest_path or Path(__file__).with_name('scenarios.json'))
    checks = []
    documents = {}
    for name in REQUIRED_FILES:
        path = evidence_dir / name
        exists = path.is_file()
        check(checks, f'{name} exists', exists, name)
        if exists:
            try:
                documents[name] = read_json(path)
            except (json.JSONDecodeError, OSError) as error:
                check(checks, f'{name} parses', False, error)
        else:
            check(checks, f'{name} parses', False, 'missing')
    if len(documents) != len(REQUIRED_FILES):
        return checks

    manifest = read_json(manifest_path)
    report = documents['report.json']
    comparison = documents['comparison.json']
    ui_report = documents['ui-report.json']
    dimensions = manifest['dimensions']
    case_ids = [dimension['id'] for dimension in dimensions]
    expected_scenarios = manifest['scenarios']

    check(checks, 'fixture id', report.get('fixture') == manifest['fixture'], report.get('fixture'))
    check(checks, 'comparison fixture id',
          comparison.get('fixture') == manifest['fixture'], comparison.get('fixture'))
    check(checks, 'ui fixture id', ui_report.get('fixture') == manifest['fixture'],
          ui_report.get('fixture'))
    check(checks, 'reference revision',
          report.get('reference') == manifest.get('piReference'), report.get('reference'))

    scenarios = {scenario['id']: scenario for scenario in report.get('scenarios', [])}
    comparison_scenarios = comparison.get('scenarios', {})
    ui_scenarios = {scenario['id']: scenario for scenario in ui_report.get('scenarios', [])}
    check(checks, 'scenario set', set(scenarios) == set(expected_scenarios),
          f'report={sorted(scenarios)} expected={sorted(expected_scenarios)}')
    check(checks, 'comparison scenario set',
          set(comparison_scenarios) == set(expected_scenarios),
          sorted(comparison_scenarios))
    check(checks, 'ui scenario set', set(ui_scenarios) == set(expected_scenarios),
          sorted(ui_scenarios))

    fixture_root = Path(__file__).resolve().parents[1] / 'terminal' / 'fixtures' / manifest['fixture']
    manifest_file = evidence_dir / 'fixture-manifest.json'
    if manifest_file.is_file():
        fixture_manifest = read_json(manifest_file)
        check(checks, 'fixture manifest id', fixture_manifest.get('fixture') == manifest['fixture'],
              fixture_manifest.get('fixture'))
        check(checks, 'fixture manifest reference',
              fixture_manifest.get('piReference') == manifest.get('piReference'),
              fixture_manifest.get('piReference'))
        for item in fixture_manifest.get('scenarios', []):
            fixture_path = fixture_root / item.get('fixture', '')
            actual = sha256(fixture_path) if fixture_path.is_file() else None
            check(checks, f'fixture manifest checksum {item.get("id")}',
                  item.get('sha256') == actual
                  and report.get('fixtureChecksums', {}).get(item.get('fixture')) == actual,
                  f'{item.get("sha256")} != {actual}')

    findings = {finding['id']: finding for finding in report.get('findings', [])}
    all_comparison_findings = set()
    unassessable = {item.get('scenario'): item for item in report.get('unassessable', [])}

    weights = report.get('weights', {})
    check(checks, 'rubric weights',
          weights.get('functional') == 40 and weights.get('temporal') == 20
          and weights.get('visual') == 30 and weights.get('cleanup') == 10,
          weights)

    recomputed_category_totals = dict(functional=0, temporal=0, visual=0, cleanup=0)
    recomputed_maximum = 0
    recomputed_total = 0

    for scenario_id, spec in expected_scenarios.items():
        scenario = scenarios.get(scenario_id)
        if not check(checks, f'{scenario_id} present', scenario is not None):
            continue
        check(checks, f'{scenario_id} title', scenario.get('title') == spec['title'],
              scenario.get('title'))
        check(checks, f'{scenario_id} fixture', scenario.get('fixture') == spec['fixture'],
              scenario.get('fixture'))

        fixture_path = fixture_root / spec['fixture']
        expected_checksum = sha256(fixture_path) if fixture_path.is_file() else None
        check(checks, f'{scenario_id} fixture checksum',
              report.get('fixtureChecksums', {}).get(spec['fixture']) == expected_checksum,
              report.get('fixtureChecksums', {}).get(spec['fixture']))

        calibration = scenario.get('calibration', {})
        calibration_details = scenario.get('calibrationDifferences', {})
        check(checks, f'{scenario_id} calibration cases', set(calibration) == set(case_ids),
              sorted(calibration))
        calibration_consistent = all(
            calibration.get(case) == bool(calibration_details.get(case)) for case in case_ids)
        check(checks, f'{scenario_id} calibration consistency', calibration_consistent,
              calibration_details)
        calibrates = all(not value for value in calibration.values())

        assertions = scenario.get('assertions', {})
        errors = assertions.get('scenarioErrors', [])
        orders = assertions.get('checkpointOrder', [])
        complete = (not any(errors) and len(orders) == len(case_ids)
                    and all(order == spec['checkpoints'] for order in orders))
        check(checks, f'{scenario_id} checkpoint coverage', complete,
              f'orders={orders} errors={errors}')
        assessable = calibrates and complete

        check(checks, f'{scenario_id} calibration flag', scenario.get('calibrates') == calibrates,
              calibration)
        check(checks, f'{scenario_id} assessable flag', scenario.get('assessable') == assessable,
              scenario.get('assessable'))
        if not assessable:
            check(checks, f'{scenario_id} unassessable recorded', scenario_id in unassessable,
                  sorted(unassessable))
        if calibrates and not complete:
            check(checks, f'{scenario_id} incomplete reason',
                  unassessable.get(scenario_id, {}).get('reason')
                  == 'scenario did not reach every named checkpoint',
                  unassessable.get(scenario_id))
        if not calibrates:
            check(checks, f'{scenario_id} calibration reason',
                  unassessable.get(scenario_id, {}).get('reason') == 'same-Pi calibration failed',
                  unassessable.get(scenario_id))

        content = assertions.get('content', [])
        requests = assertions.get('requests', [])
        absences = assertions.get('absenceFailures', [])
        score_inputs = scenario.get('scoreInputs', {})
        content_ok = bool(content) and all(not item.get('missing') and not item.get('duplicates')
                                           for item in content)
        requests_ok = bool(requests) and all(
            item.get('countMatch') and item.get('toolResultExact', True)
            and not item.get('duplicateToolCall') for item in requests)
        transitions = complete and bool(absences) and all(not item for item in absences)
        idle_has_working = score_inputs.get('idleHasWorking', {})
        idle_ok = bool(idle_has_working) and not any(idle_has_working.values())
        subsequent = score_inputs.get('subsequentPrompt', {})
        subsequent_ok = bool(subsequent) and all(subsequent.values())

        cleanup_by_case = assertions.get('cleanup', {})
        cleanup_items = list(cleanup_by_case.values()) if isinstance(cleanup_by_case, dict) else []
        cleanup_complete = isinstance(cleanup_by_case, dict) and set(cleanup_by_case) == set(case_ids)
        process_ok = True
        if scenario_id in CLEANUP_SCENARIOS:
            process_ok = cleanup_complete and (
                all(item.get('processesGone') is True for item in cleanup_items)
                if 'cleanupCheckpoint' in spec else True)
        else:
            process_ok = True

        functional_checks = score_inputs.get('functionalChecks', {})
        expected_functional_checks = dict(
            transitions=transitions,
            content=content_ok,
            finalState=transitions and requests_ok and idle_ok and process_ok,
            subsequent=subsequent_ok,
        )
        check(checks, f'{scenario_id} functional inputs', functional_checks == expected_functional_checks,
              f'actual={functional_checks} expected={expected_functional_checks}')
        functional = sum(2 for value in expected_functional_checks.values() if value)

        expected_temporal_checks = dict(complete=complete, absence=transitions,
                                        idle=idle_ok, requests=requests_ok)
        temporal_checks = score_inputs.get('temporalChecks', {})
        check(checks, f'{scenario_id} temporal inputs', temporal_checks == expected_temporal_checks,
              f'actual={temporal_checks} expected={expected_temporal_checks}')
        temporal = sum(1 for value in expected_temporal_checks.values() if value)

        comparison_case_data = comparison_scenarios.get(scenario_id, {}).get('cases', {})
        check(checks, f'{scenario_id} comparison cases',
              set(comparison_case_data) == set(case_ids), sorted(comparison_case_data))
        visual = 0
        visual_by_size = scenario.get('visualBySize') or {}
        for case_id in case_ids:
            case_data = comparison_case_data.get(case_id, {})
            by_category = case_data.get('byCategory', {})
            differences = case_data.get('differences')
            check(checks, f'{scenario_id} {case_id} difference count',
                  differences == sum(by_category.get(category, 0)
                                     for category in CATEGORY_ORDER),
                  f'{differences} {by_category}')
            expected_points = {category: 1 if by_category.get(category, 0) == 0 else 0
                               for category in CATEGORY_ORDER}
            if assessable:
                recorded = visual_by_size.get(case_id, {})
                check(checks, f'{scenario_id} {case_id} visual points',
                      all(recorded.get(category) == expected_points[category]
                          for category in CATEGORY_ORDER),
                      f'recorded={recorded} expected={expected_points}')
                check(checks, f'{scenario_id} {case_id} visual differences',
                      recorded.get('differences') == by_category,
                      f'recorded={recorded.get("differences")} expected={by_category}')
                visual += sum(expected_points.values())

            finding_ids = set(case_data.get('findingIds', []))
            all_comparison_findings.update(finding_ids)
            check(checks, f'{scenario_id} {case_id} findings known',
                  finding_ids <= set(findings), sorted(finding_ids - set(findings)))
            if differences:
                check(checks, f'{scenario_id} {case_id} differences classified',
                      bool(finding_ids), 'no finding id')

        cleanup = None
        if scenario_id in CLEANUP_SCENARIOS:
            expected_cleanup_checks = dict(
                cancellationSettled=not any(score_inputs.get('cancelledHasWorking', {}).values())
                and bool(score_inputs.get('cancelledHasWorking')),
                noLateOutput=bool(absences) and all(not item for item in absences),
                terminalRestored=cleanup_complete and all(
                    item.get('normalScreenRestored') and item.get('cursorVisible')
                    and item.get('synchronizedOutputOff') for item in cleanup_items),
                cleanupTarget=process_ok if 'cleanupCheckpoint' in spec
                else bool(idle_has_working) and not any(idle_has_working.values()),
                subsequentPrompt=subsequent_ok,
            )
            recorded_cleanup = score_inputs.get('cleanupChecks') or {}
            check(checks, f'{scenario_id} cleanup inputs',
                  recorded_cleanup == expected_cleanup_checks,
                  f'actual={recorded_cleanup} expected={expected_cleanup_checks}')
            cleanup = sum(1 for value in expected_cleanup_checks.values() if value)
            check(checks, f'{scenario_id} cleanup evidence present',
                  cleanup_complete and all(
                      {'normalScreenRestored', 'cursorVisible', 'synchronizedOutputOff'} <= set(item)
                      for item in cleanup_items),
                  f'{len(cleanup_items)} items')
            if 'cleanupCheckpoint' in spec:
                check(checks, f'{scenario_id} process cleanup evidence',
                      all('cleanupProcesses' in item and isinstance(item.get('processesGone'), bool)
                          for item in cleanup_items),
                      cleanup_items)

        if assessable:
            maximum = 18 + (5 if scenario_id in CLEANUP_SCENARIOS else 0)
            subtotal = functional + temporal + visual + (cleanup or 0)
            check(checks, f'{scenario_id} functional score', scenario.get('functional') == functional,
                  f'{scenario.get("functional")} != {functional}')
            check(checks, f'{scenario_id} temporal score', scenario.get('temporal') == temporal,
                  f'{scenario.get("temporal")} != {temporal}')
            check(checks, f'{scenario_id} visual score', scenario.get('visual') == visual,
                  f'{scenario.get("visual")} != {visual}')
            check(checks, f'{scenario_id} cleanup score', scenario.get('cleanup') == cleanup,
                  f'{scenario.get("cleanup")} != {cleanup}')
            check(checks, f'{scenario_id} subtotal', scenario.get('subtotal') == subtotal,
                  f'{scenario.get("subtotal")} != {subtotal}')
            check(checks, f'{scenario_id} maximum', scenario.get('maximum') == maximum,
                  f'{scenario.get("maximum")} != {maximum}')
            recomputed_category_totals['functional'] += functional
            recomputed_category_totals['temporal'] += temporal
            recomputed_category_totals['visual'] += visual
            recomputed_category_totals['cleanup'] += cleanup or 0
            recomputed_maximum += maximum
            recomputed_total += subtotal
        else:
            check(checks, f'{scenario_id} unassessable scores unset',
                  all(scenario.get(field) is None
                      for field in ('functional', 'temporal', 'visual', 'cleanup', 'subtotal')),
                  {field: scenario.get(field) for field in
                   ('functional', 'temporal', 'visual', 'cleanup', 'subtotal')})
            check(checks, f'{scenario_id} maximum retained',
                  scenario.get('maximum') == 18 + (5 if scenario_id in CLEANUP_SCENARIOS else 0),
                  scenario.get('maximum'))

        expected_finding_ids = set()
        for case_data in comparison_case_data.values():
            expected_finding_ids.update(case_data.get('findingIds', []))
        check(checks, f'{scenario_id} findings listed', set(scenario.get('findings', []))
              == expected_finding_ids, sorted(expected_finding_ids))

    check(checks, 'category totals',
          report.get('categoryTotals') == recomputed_category_totals,
          f'{report.get("categoryTotals")} != {recomputed_category_totals}')
    check(checks, 'total', report.get('total') == recomputed_total,
          f'{report.get("total")} != {recomputed_total}')
    check(checks, 'assessed maximum', report.get('assessedMaximum') == recomputed_maximum,
          f'{report.get("assessedMaximum")} != {recomputed_maximum}')
    check(checks, 'total within maximum',
          0 <= recomputed_total <= recomputed_maximum <= report.get('availableMaximum', 100),
          f'{recomputed_total}/{recomputed_maximum}')
    coverage = report.get('coverage', {})
    check(checks, 'coverage',
          coverage.get('assessedScenarios') == sum(1 for item in scenarios.values()
                                                   if item.get('assessable'))
          and coverage.get('totalScenarios') == len(expected_scenarios)
          and coverage.get('assessedPoints') == recomputed_maximum
          and coverage.get('availablePoints') == 100,
          coverage)

    check(checks, 'findings complete', set(findings) == all_comparison_findings,
          f'missing={sorted(all_comparison_findings - set(findings))} '
          f'orphan={sorted(set(findings) - all_comparison_findings)}')
    required_finding_fields = {'id', 'scenario', 'cases', 'checkpoints', 'differenceCount',
                               'samplePaths', 'severity', 'category', 'piObservation',
                               'pisharpObservation', 'source', 'correction', 'reproduction',
                               'evidence'}
    for finding_id, finding in findings.items():
        check(checks, f'{finding_id} fields', required_finding_fields <= set(finding),
              sorted(required_finding_fields - set(finding)))
        check(checks, f'{finding_id} severity', finding.get('severity') in SEVERITIES,
              finding.get('severity'))
        check(checks, f'{finding_id} difference count', finding.get('differenceCount', 0) > 0,
              finding.get('differenceCount'))
        check(checks, f'{finding_id} scenario',
              finding.get('scenario') in expected_scenarios, finding.get('scenario'))
        check(checks, f'{finding_id} cases',
              bool(finding.get('cases')) and set(finding['cases']) <= set(case_ids),
              finding.get('cases'))

    for scenario_id, ui_scenario in ui_scenarios.items():
        spec = expected_scenarios.get(scenario_id)
        if spec is None:
            continue
        for case_id in case_ids:
            case = ui_scenario.get('cases', {}).get(case_id)
            if not check(checks, f'{scenario_id} {case_id} ui case', case is not None):
                continue
            check(checks, f'{scenario_id} {case_id} ui checkpoints',
                  [item.get('id') for item in case.get('checkpoints', [])] == spec['checkpoints'],
                  [item.get('id') for item in case.get('checkpoints', [])])

    if require_verification:
        verification_path = evidence_dir / 'verification.json'
        if check(checks, 'verification.json exists', verification_path.is_file(),
                 'verification.json'):
            verification = read_json(verification_path)
            check(checks, 'verification passed', verification.get('passed') is True,
                  verification.get('passed'))
            checksums = verification.get('evidenceChecksums', {})
            for name in REQUIRED_FILES:
                check(checks, f'{name} checksum',
                      checksums.get(name) == sha256(evidence_dir / name),
                      checksums.get(name))
            check(checks, 'verification checks',
                  all(item.get('passed') for item in verification.get('checks', [])),
                  [item for item in verification.get('checks', []) if not item.get('passed')])

    return checks


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--evidence', type=Path, required=True)
    parser.add_argument('--manifest', type=Path,
                        default=Path(__file__).with_name('scenarios.json'))
    parser.add_argument('--require-verification', action='store_true')
    args = parser.parse_args()
    checks = verify(args.evidence, args.manifest, args.require_verification)
    failed = [item for item in checks if not item['passed']]
    for item in failed:
        print(f'FAIL {item["name"]}: {item["detail"]}')
    print(json.dumps(dict(passed=not failed, checks=len(checks), failed=len(failed)), indent=2))
    return 0 if not failed else 1


if __name__ == '__main__':
    raise SystemExit(main())
