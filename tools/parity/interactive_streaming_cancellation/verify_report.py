#!/usr/bin/env python3
"""Verify the deterministic streaming/cancellation evidence report.

The verifier never launches a product. It recomputes the frozen contract for both
Pi and PiSharp, checks that every capture is bound to an exact source state,
recomputes scores from recorded checks, verifies post-cancellation stream scan
evidence, and rejects any difference not mapped by an explicit finding
predicate.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import evaluate  # noqa: E402

SEVERITIES = {'P0', 'P1', 'P2', 'Inconclusive'}
CATEGORY_ORDER = ['text', 'style', 'layout']
CLEANUP_SCENARIOS = {'S04', 'S05'}
REQUIRED_FILES = ['report.json', 'comparison.json', 'ui-report.json']
PROVENANCE_FIELDS = ('sha', 'branch', 'dirty', 'trackedChanges')
SHA_PATTERN = evaluate.SHA_PATTERN


def read_json(path):
    return json.loads(path.read_text())


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def check(checks, name, condition, detail=''):
    checks.append(dict(name=name, passed=bool(condition), detail=str(detail)))
    return bool(condition)


def provenance_valid(value):
    return evaluate.provenance_valid(value)


def same_state(left, right):
    return left == right


def contract_expected(scenario_id, scenario, contract):
    """Recompute the frozen contract from one product/case contract record."""
    complete = (not contract.get('errors')
                and contract.get('checkpointOrder') == scenario['checkpoints'])
    content = contract.get('content') or []
    requests = contract.get('requests') or []
    content_ok = bool(content) and all(
        not item.get('missing') and not item.get('duplicates') for item in content)
    requests_ok = bool(requests) and all(
        item.get('countMatch') and item.get('toolResultExact', True)
        and not item.get('duplicateToolCall') for item in requests)
    absences = contract.get('absenceFailures') or []
    transitions = complete and bool(contract.get('checkpointOrder')) and not absences
    idle_missing = contract.get('idleMissing') or {}
    idle_has_working = contract.get('idleHasWorking') or {}
    idle_ok = (bool(idle_missing) and not any(idle_missing.values())
               and bool(idle_has_working) and not any(idle_has_working.values()))
    subsequent = contract.get('subsequentPrompt') or {}
    subsequent_ok = bool(subsequent) and all(subsequent.values())
    cleanup_evidence = contract.get('cleanupEvidence') or {}
    process_ok = (cleanup_evidence.get('processesGone') is True
                  if 'cleanupCheckpoint' in scenario else True)
    forbidden = contract.get('forbiddenStream') or {}
    forbidden_ok = (forbidden.get('valid') is True
                    if forbidden.get('required') else True)

    functional_checks = dict(
        transitions=transitions,
        content=content_ok,
        finalState=transitions and requests_ok and idle_ok and process_ok,
        subsequent=subsequent_ok,
    )
    temporal_checks = dict(complete=complete, absence=transitions, idle=idle_ok,
                           requests=requests_ok)
    cleanup_checks = None
    if scenario_id in CLEANUP_SCENARIOS:
        cancelled = contract.get('cancelledHasWorking') or {}
        cancellation_settled = bool(cancelled) and not any(cancelled.values())
        no_late = not absences and forbidden_ok
        terminal_restored = bool(cleanup_evidence.get('normalScreenRestored')
                                 and cleanup_evidence.get('cursorVisible')
                                 and cleanup_evidence.get('synchronizedOutputOff'))
        cleanup_target = (cleanup_evidence.get('processesGone') is True
                          if 'cleanupCheckpoint' in scenario
                          else (bool(idle_has_working) and not any(idle_has_working.values())))
        cleanup_checks = dict(
            cancellationSettled=cancellation_settled,
            noLateOutput=no_late,
            terminalRestored=terminal_restored,
            cleanupTarget=cleanup_target,
            subsequentPrompt=subsequent_ok,
        )
    functional = sum(2 for value in functional_checks.values() if value)
    temporal = sum(1 for value in temporal_checks.values() if value)
    cleanup = sum(1 for value in cleanup_checks.values() if value) if cleanup_checks else None
    passes = (all(functional_checks.values()) and all(temporal_checks.values())
              and (cleanup_checks is None or all(cleanup_checks.values())))
    return dict(
        complete=complete,
        functionalChecks=functional_checks,
        temporalChecks=temporal_checks,
        cleanupChecks=cleanup_checks,
        functional=functional,
        temporal=temporal,
        cleanup=cleanup,
        passes=passes,
    )


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
    fixture_root = Path(__file__).resolve().parents[1] / 'terminal' / 'fixtures' / manifest['fixture']

    check(checks, 'fixture id', report.get('fixture') == manifest['fixture'], report.get('fixture'))
    check(checks, 'comparison fixture id',
          comparison.get('fixture') == manifest['fixture'], comparison.get('fixture'))
    check(checks, 'ui fixture id', ui_report.get('fixture') == manifest['fixture'],
          ui_report.get('fixture'))
    check(checks, 'reference revision',
          report.get('reference') == manifest.get('piReference'), report.get('reference'))

    # Capture provenance must be exact and internally consistent.
    capture_provenance = report.get('captureProvenance') or {}
    check(checks, 'capture provenance consistent',
          capture_provenance.get('consistent') is True, capture_provenance.get('errors'))
    check(checks, 'capture provenance errors empty',
          not capture_provenance.get('errors'), capture_provenance.get('errors'))
    pi_states = capture_provenance.get('pi') or []
    pisharp_states = capture_provenance.get('pisharp') or []
    check(checks, 'single Pi capture state', len(pi_states) == 1, pi_states)
    check(checks, 'single PiSharp capture state', len(pisharp_states) == 1, pisharp_states)
    if pi_states:
        check(checks, 'Pi capture state valid', provenance_valid(pi_states[0]), pi_states[0])
        check(checks, 'Pi capture is pinned reference',
              pi_states[0].get('sha') == manifest.get('piReference'), pi_states[0].get('sha'))
    if pisharp_states:
        check(checks, 'PiSharp capture state valid', provenance_valid(pisharp_states[0]),
              pisharp_states[0])

    manifest_file = evidence_dir / 'fixture-manifest.json'
    if manifest_file.is_file():
        fixture_manifest = read_json(manifest_file)
        check(checks, 'fixture manifest id',
              fixture_manifest.get('fixture') == manifest['fixture'],
              fixture_manifest.get('fixture'))
        check(checks, 'fixture manifest reference',
              fixture_manifest.get('piReference') == manifest.get('piReference'),
              fixture_manifest.get('piReference'))
        if 'captureProvenance' in fixture_manifest:
            check(checks, 'fixture manifest capture provenance',
                  fixture_manifest.get('captureProvenance') == capture_provenance,
                  'fixture-manifest capture provenance differs from report')
        for item in fixture_manifest.get('scenarios', []):
            fixture_path = fixture_root / item.get('fixture', '')
            actual = sha256(fixture_path) if fixture_path.is_file() else None
            check(checks, f'fixture manifest checksum {item.get("id")}',
                  item.get('sha256') == actual
                  and report.get('fixtureChecksums', {}).get(item.get('fixture')) == actual,
                  f'{item.get("sha256")} != {actual}')

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
        check(checks, f'{scenario_id} calibration consistency',
              all(calibration.get(case) == bool(calibration_details.get(case))
                  for case in case_ids), calibration_details)
        calibrates = all(not value for value in calibration.values())

        # Per-case capture provenance must match the top-level unique states.
        captures = scenario.get('captures', {})
        check(checks, f'{scenario_id} capture cases', set(captures) == set(case_ids),
              sorted(captures))
        scenario_provenance_ok = True
        for case_id in case_ids:
            capture = captures.get(case_id) or {}
            for group in ('calibration', 'comparison'):
                for product in ('pi', 'pisharp'):
                    state = (capture.get(group) or {}).get(product)
                    scenario_provenance_ok &= provenance_valid(state)
            calibration_pair = capture.get('calibration') or {}
            scenario_provenance_ok &= same_state(calibration_pair.get('pi'),
                                                 calibration_pair.get('pisharp'))
            comparison_pair = capture.get('comparison') or {}
            scenario_provenance_ok &= (comparison_pair.get('pi') in pi_states
                                       and comparison_pair.get('pisharp') in pisharp_states)
        check(checks, f'{scenario_id} capture provenance', scenario_provenance_ok, captures)

        contracts = scenario.get('contracts') or {}
        pi_contracts = contracts.get('pi') or {}
        pisharp_contracts = contracts.get('pisharp') or {}
        check(checks, f'{scenario_id} Pi contracts', set(pi_contracts) == set(case_ids),
              sorted(pi_contracts))
        check(checks, f'{scenario_id} PiSharp contracts', set(pisharp_contracts) == set(case_ids),
              sorted(pisharp_contracts))
        pi_passes = True
        pisharp_complete = True
        pi_complete = True
        recomputed_pi = {}
        recomputed_pisharp = {}
        for case_id in case_ids:
            pi_contract = pi_contracts.get(case_id)
            ps_contract = pisharp_contracts.get(case_id)
            if not check(checks, f'{scenario_id} {case_id} Pi contract present',
                         isinstance(pi_contract, dict)):
                pi_passes = False
                continue
            if not check(checks, f'{scenario_id} {case_id} PiSharp contract present',
                         isinstance(ps_contract, dict)):
                pisharp_complete = False
                continue
            expected_pi = contract_expected(scenario_id, spec, pi_contract)
            expected_ps = contract_expected(scenario_id, spec, ps_contract)
            recomputed_pi[case_id] = expected_pi
            recomputed_pisharp[case_id] = expected_ps
            check(checks, f'{scenario_id} {case_id} Pi contract checks',
                  pi_contract.get('functionalChecks') == expected_pi['functionalChecks']
                  and pi_contract.get('temporalChecks') == expected_pi['temporalChecks']
                  and pi_contract.get('cleanupChecks') == expected_pi['cleanupChecks'],
                  f'Pi={pi_contract.get("functionalChecks")}/{pi_contract.get("temporalChecks")}'
                  f' expected={expected_pi["functionalChecks"]}/{expected_pi["temporalChecks"]}')
            check(checks, f'{scenario_id} {case_id} Pi contract points',
                  pi_contract.get('functional') == expected_pi['functional']
                  and pi_contract.get('temporal') == expected_pi['temporal']
                  and pi_contract.get('cleanup') == expected_pi['cleanup']
                  and pi_contract.get('passes') == expected_pi['passes'],
                  f'Pi={pi_contract.get("functional")}/{pi_contract.get("temporal")}/'
                  f'{pi_contract.get("cleanup")}/{pi_contract.get("passes")}')
            check(checks, f'{scenario_id} {case_id} PiSharp contract checks',
                  ps_contract.get('functionalChecks') == expected_ps['functionalChecks']
                  and ps_contract.get('temporalChecks') == expected_ps['temporalChecks']
                  and ps_contract.get('cleanupChecks') == expected_ps['cleanupChecks'],
                  f'PiSharp={ps_contract.get("functionalChecks")}/{ps_contract.get("temporalChecks")}'
                  f' expected={expected_ps["functionalChecks"]}/{expected_ps["temporalChecks"]}')
            check(checks, f'{scenario_id} {case_id} PiSharp contract points',
                  ps_contract.get('functional') == expected_ps['functional']
                  and ps_contract.get('temporal') == expected_ps['temporal']
                  and ps_contract.get('cleanup') == expected_ps['cleanup']
                  and ps_contract.get('passes') == expected_ps['passes'],
                  f'PiSharp={ps_contract.get("functional")}/{ps_contract.get("temporal")}/'
                  f'{ps_contract.get("cleanup")}/{ps_contract.get("passes")}')
            pi_passes &= expected_pi['passes']
            pi_complete &= expected_pi['complete']
            pisharp_complete &= expected_ps['complete']
            if scenario_id in CLEANUP_SCENARIOS:
                for product, product_contract in (('Pi', pi_contract),
                                                  ('PiSharp', ps_contract)):
                    forbidden = product_contract.get('forbiddenStream') or {}
                    check(checks, f'{scenario_id} {case_id} {product} forbidden stream evidence',
                          forbidden.get('required') is True
                          and forbidden.get('scanned') is True
                          and forbidden.get('tokens') == spec.get('forbiddenStreamTokens'),
                          forbidden)
                    if forbidden.get('occurrenceCount', 0) > 0:
                        check(checks, f'{scenario_id} {case_id} {product} forbidden stream not marked clean',
                              (product_contract.get('cleanupChecks') or {}).get('noLateOutput') is False,
                              product_contract.get('cleanupChecks'))
                    cleanup_evidence = product_contract.get('cleanupEvidence') or {}
                    check(checks, f'{scenario_id} {case_id} {product} cleanup evidence',
                          {'normalScreenRestored', 'cursorVisible', 'synchronizedOutputOff'} <= set(
                              cleanup_evidence), cleanup_evidence)
                    if 'cleanupCheckpoint' in spec:
                        check(checks, f'{scenario_id} {case_id} {product} process cleanup evidence',
                              isinstance(cleanup_evidence.get('processesGone'), bool)
                              and cleanup_evidence.get('processesGone') is True,
                              cleanup_evidence)

        check(checks, f'{scenario_id} Pi contract passes', pi_passes, pi_passes)
        check(checks, f'{scenario_id} checkpoint coverage', pi_complete and pisharp_complete,
              f'pi={pi_complete} pisharp={pisharp_complete}')

        comparison_case_data = comparison_scenarios.get(scenario_id, {}).get('cases', {})
        check(checks, f'{scenario_id} comparison cases',
              set(comparison_case_data) == set(case_ids), sorted(comparison_case_data))
        visual = 0
        visual_by_size = scenario.get('visualBySize') or {}
        unmatched_total = 0
        scenario_diffs_total = 0
        for case_id in case_ids:
            case_data = comparison_case_data.get(case_id, {})
            by_category = case_data.get('byCategory', {})
            differences = case_data.get('differences')
            scenario_diffs_total += differences or 0
            unmatched_total += case_data.get('unmatchedDifferenceCount', 0)
            check(checks, f'{scenario_id} {case_id} difference count',
                  differences == sum(by_category.get(category, 0)
                                     for category in CATEGORY_ORDER),
                  f'{differences} {by_category}')
            expected_points = {category: 1 if by_category.get(category, 0) == 0 else 0
                               for category in CATEGORY_ORDER}
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
                      bool(finding_ids) and case_data.get('unmatchedDifferenceCount', 0) == 0,
                      f'findingIds={sorted(finding_ids)} unmatched={case_data.get("unmatchedDifferenceCount")}')
            for sample in case_data.get('samples', []):
                finding_id = sample.get('findingId')
                if finding_id:
                    expected_finding = evaluate.match_finding(scenario_id, spec, sample)
                    check(checks, f'{scenario_id} {case_id} sample predicate',
                          expected_finding == finding_id,
                          f'{sample.get("checkpoint")} {sample.get("path")} -> '
                          f'{finding_id} expected {expected_finding}')

        scenario_findings = set(scenario.get('findings', []))
        check(checks, f'{scenario_id} findings listed',
              scenario_findings == {diff for case in comparison_case_data.values()
                                    for diff in case.get('findingIds', [])},
              sorted(scenario_findings))
        check(checks, f'{scenario_id} unmatched differences',
              unmatched_total == 0 and not scenario.get('unmatchedDifferences'),
              f'unmatched={unmatched_total} records={scenario.get("unmatchedDifferences")}')
        finding_difference_total = sum(finding.get('differenceCount', 0)
                                       for finding in findings.values()
                                       if finding.get('scenario') == scenario_id)
        check(checks, f'{scenario_id} findings cover every difference',
              not scenario.get('assessable') or finding_difference_total == scenario_diffs_total,
              f'findings={finding_difference_total} differences={scenario_diffs_total}')

        assessable = (calibrates and pi_passes and pi_complete and pisharp_complete
                      and scenario_provenance_ok and unmatched_total == 0
                      and not scenario.get('unmatchedDifferences'))
        check(checks, f'{scenario_id} assessable flag', scenario.get('assessable') == assessable,
              scenario.get('assessable'))
        if not assessable:
            check(checks, f'{scenario_id} unassessable recorded', scenario_id in unassessable,
                  sorted(unassessable))

        if assessable:
            functional = min(recomputed_pisharp[case]['functional'] for case in case_ids)
            temporal = min(recomputed_pisharp[case]['temporal'] for case in case_ids)
            cleanup = None
            if scenario_id in CLEANUP_SCENARIOS:
                cleanup = min(recomputed_pisharp[case]['cleanup'] for case in case_ids)
            maximum = 18 + (5 if scenario_id in CLEANUP_SCENARIOS else 0)
            subtotal = functional + temporal + visual + (cleanup or 0)
            check(checks, f'{scenario_id} functional score',
                  scenario.get('functional') == functional,
                  f'{scenario.get("functional")} != {functional}')
            check(checks, f'{scenario_id} temporal score',
                  scenario.get('temporal') == temporal,
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

    validation = report.get('validation') or {}
    check(checks, 'validation passed', validation.get('passed') is True, validation)
    check(checks, 'validation errors empty', not validation.get('errors'), validation.get('errors'))
    check(checks, 'validation unmatched zero',
          validation.get('unmatchedDifferenceCount') == 0,
          validation.get('unmatchedDifferenceCount'))
    check(checks, 'validation provenance consistent',
          validation.get('provenanceConsistent') is True, validation)
    check(checks, 'validation Pi contracts pass',
          validation.get('piContractsPass') is True, validation)

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
        check(checks, f'{finding_id} explicit predicate',
              finding_id in evaluate.FINDINGS and 'unclassified' not in finding_id,
              finding_id)
        check(checks, f'{finding_id} severity', finding.get('severity') in SEVERITIES,
              finding.get('severity'))
        check(checks, f'{finding_id} difference count', finding.get('differenceCount', 0) > 0,
              finding.get('differenceCount'))
        check(checks, f'{finding_id} sample paths', bool(finding.get('samplePaths')),
              finding.get('samplePaths'))
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
