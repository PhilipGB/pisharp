#!/usr/bin/env python3
"""Verify frozen resource glob, process, artifact, and source/CI receipts."""
import hashlib
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PI_SHA = (ROOT / 'tools/parity/pi-reference.txt').read_text().strip()
BASE_SHA = '480f058a95713fbc51b6904d4450da68bcda886c'
GLOB_INDEX = Path(__file__).with_name('cases.json')
GLOB_INDEX_SHA256 = '274a00c4e5d5da5b751b1babd7b5e81fe3e1872410bec09680f5f763abefb53c'
PROCESS_INDEX = Path(__file__).with_name('process_cases.json')
PROCESS_INDEX_SHA256 = 'b596adb6e1cee1bcc55ce9855799288d3028f2077d6fa21167cc360ef49b863e'
THEME_INDEX = Path(__file__).with_name('theme_cases.json')
THEME_INDEX_SHA256 = '55cac20f6ad2473956b902a311eb25e37fbb9033c21f231435da0d38b2daccc9'
SELECTION_INDEX = Path(__file__).with_name('selection_cases.json')
SELECTION_INDEX_SHA256 = '3d3e29f748236a1c40971924d68e6dd8195120742a67c6801f4b3f96caecbae0'
PATH_INDEX = Path(__file__).with_name('path_cases.json')
PATH_INDEX_SHA256 = '7bd88472c14c425524d8fa536e8ffcb3d2b7aece7bc7a2e10c102d89eced5a6b'


def _json(path):
    return json.loads(Path(path).read_text())


def _ids(cases, label):
    ids = [case.get('id') for case in cases]
    if any(not isinstance(value, str) or not value for value in ids):
        raise ValueError(f'{label} contains a missing case ID')
    if len(ids) != len(set(ids)):
        raise ValueError(f'{label} contains duplicate case IDs')
    return ids


def verify_glob_report(index_path, report_path, source_sha, expected_index_sha=None):
    cases = _json(index_path)
    ids = _ids(cases, 'glob index')
    digest = hashlib.sha256(Path(index_path).read_bytes()).hexdigest()
    expected_index_sha = expected_index_sha or GLOB_INDEX_SHA256
    if digest != expected_index_sha:
        raise ValueError('glob index hash mismatch')
    report = _json(report_path)
    if report.get('piSha') != PI_SHA:
        raise ValueError('wrong Pi SHA')
    if report.get('piSharpSha') != source_sha:
        raise ValueError('wrong PiSharp SHA')
    if report.get('caseIndexSha256') != digest:
        raise ValueError('glob report is bound to a different case index')
    observed = report.get('cases', [])
    observed_ids = _ids(observed, 'glob report')
    if observed_ids != ids:
        raise ValueError('glob report has missing, duplicate, or reordered mandatory executions')
    for case, result in zip(cases, observed, strict=True):
        if result.get('expectedPi') != case.get('expectedPi'):
            raise ValueError(f'changed Pi expectation: {case["id"]}')
        if result.get('piActual') != case.get('expectedPi'):
            raise ValueError(f'Pi oracle mismatch: {case["id"]}')
        status = 'passed' if result.get('piSharpActual') == case.get('expectedPi') else 'failed'
        if result.get('status') != status or status != 'passed':
            raise ValueError(f'PiSharp matcher mismatch: {case["id"]}')
    expected_counts = dict(required=len(ids), passed=len(ids), failed=0, skipped=0)
    if any(report.get(key) != value for key, value in expected_counts.items()):
        raise ValueError('incorrect glob aggregate counts')
    return dict(caseIndexSha256=digest, **expected_counts)


def verify_process_cases(index_path, report_paths, source_sha):
    if hashlib.sha256(Path(index_path).read_bytes()).hexdigest() != PROCESS_INDEX_SHA256:
        raise ValueError('process case index hash mismatch')
    cases = _json(index_path)
    ids = _ids(cases, 'process index')
    reports = [_json(path) for path in report_paths]
    report_ids = [report.get('scenario') for report in reports]
    _ids([{'id': value} for value in report_ids], 'process report')
    if report_ids != ids:
        raise ValueError('missing, duplicate, unexecuted, or reordered process case')
    for case, report in zip(cases, reports, strict=True):
        if report.get('piSha') != PI_SHA:
            raise ValueError(f'wrong Pi SHA in process case {case["id"]}')
        if report.get('piSharpBaseSha') != source_sha:
            raise ValueError(f'stale PiSharp source in process case {case["id"]}')
        if not report.get('match'):
            raise ValueError(f'process differential failed: {case["id"]}')
        pi, pisharp = report.get('results', {}).get('pi', {}), report.get('results', {}).get('pisharp', {})
        if pi.get('settingsSelection') != case.get('expectedPi'):
            raise ValueError(f'Pi selection differs from frozen expectation: {case["id"]}')
        if pisharp.get('settingsSelection') != case.get('expectedPi'):
            raise ValueError(f'PiSharp selection differs from frozen expectation: {case["id"]}')
        extensions = case.get('expectedExtensions')
        if extensions is not None:
            for product, result in [('Pi', pi), ('PiSharp', pisharp)]:
                selection = result.get('localExtensionSelection')
                if selection is None or any(selection.get(key) != value for key, value in extensions.items()):
                    raise ValueError(f'{product} extension selection/order differs: {case["id"]}')
    return dict(required=len(ids), passed=len(ids), failed=0, skipped=0)


def verify_theme_report(index_path, report_path):
    if hashlib.sha256(Path(index_path).read_bytes()).hexdigest() != THEME_INDEX_SHA256:
        raise ValueError('theme case index hash mismatch')
    cases = _json(index_path)
    ids = _ids(cases, 'theme case index')
    report = _json(report_path)
    if report.get('piSha') != PI_SHA:
        raise ValueError('wrong Pi SHA in theme integration')
    observed_pi, observed_pisharp = report.get('pi', []), report.get('pisharp', [])
    pi_ids, pisharp_ids = _ids(observed_pi, 'Pi theme result'), _ids(observed_pisharp, 'PiSharp theme result')
    if not set(ids).issubset(pi_ids) or not set(ids).issubset(pisharp_ids):
        raise ValueError('missing or skipped frozen theme case')
    pi_by_id = {item['id']: item for item in observed_pi}
    pisharp_by_id = {item['id']: item for item in observed_pisharp}
    for case in cases:
        case_id = case['id']
        if pi_by_id[case_id] != case['expectedPi']:
            raise ValueError(f'Pi theme selection changed: {case_id}')
        if pisharp_by_id[case_id] != case['expectedPi']:
            raise ValueError(f'PiSharp theme selection differs: {case_id}')
    return dict(required=len(ids), passed=len(ids), failed=0, skipped=0)


def verify_selection_report(index_path, report_path):
    if hashlib.sha256(Path(index_path).read_bytes()).hexdigest() != SELECTION_INDEX_SHA256:
        raise ValueError('selection case index hash mismatch')
    cases = _json(index_path)
    ids = _ids(cases, 'selection case index')
    report = _json(report_path)
    digest = hashlib.sha256(Path(index_path).read_bytes()).hexdigest()
    if report.get('piSha') != PI_SHA:
        raise ValueError('wrong Pi SHA in selection integration')
    if report.get('selectionCaseIndexSha256') != digest:
        raise ValueError('selection report is bound to a different case index')
    if report.get('scenarios') != [case['input'] for case in cases]:
        raise ValueError('selection report inputs changed')
    for product in ('pi', 'pisharp'):
        observed = report.get(product, [])
        observed_ids = _ids(observed, f'{product} selection result')
        if observed_ids != ids:
            raise ValueError(f'{product} selection has missing, duplicate, skipped, or reordered case')
        for case, result in zip(cases, observed, strict=True):
            expected = case['expectedPi']
            if product == 'pi' and result != expected:
                raise ValueError(f'Pi selection expectation changed: {case["id"]}')
            if product == 'pisharp' and result != expected:
                raise ValueError(f'PiSharp selection differs: {case["id"]}')
    differences = report.get('differences', [])
    if differences or not report.get('match'):
        raise ValueError('full-loader selection differential failed')
    return dict(required=len(ids), passed=len(ids), failed=0, skipped=0)


def verify_path_report(index_path, report_path):
    if hashlib.sha256(Path(index_path).read_bytes()).hexdigest() != PATH_INDEX_SHA256:
        raise ValueError('path case index hash mismatch')
    cases = _json(index_path)
    ids = _ids(cases, 'path case index')
    report = _json(report_path)
    digest = hashlib.sha256(Path(index_path).read_bytes()).hexdigest()
    if report.get('piSha') != PI_SHA:
        raise ValueError('wrong Pi SHA in explicit path report')
    if report.get('caseIndexSha256') != digest:
        raise ValueError('explicit path report is bound to a different case index')
    observed = report.get('cases', [])
    observed_ids = _ids(observed, 'explicit path report')
    if observed_ids != ids:
        raise ValueError('explicit path report has missing, duplicate, skipped, or reordered case')
    for case, result in zip(cases, observed, strict=True):
        if result.get('input') != case['input'] or result.get('expectedPi') != case['expectedPi']:
            raise ValueError(f'explicit path input or expectation changed: {case["id"]}')
        if result.get('piActual') != case['expectedPi']:
            raise ValueError(f'Pi explicit path expectation changed: {case["id"]}')
        if result.get('piSharpActual') != case['expectedPi'] or result.get('status') != 'passed':
            raise ValueError(f'PiSharp explicit path selection differs: {case["id"]}')
    if not report.get('match') or report.get('differences'):
        raise ValueError('explicit path differential failed')
    return dict(required=len(ids), passed=len(ids), failed=0, skipped=0)


def verify_evidence_manifest(manifest_path, source_sha, ci_run_id, pr_head_sha=None):
    manifest = _json(manifest_path)
    if manifest.get('sourceSha') != source_sha:
        raise ValueError('stale evidence source SHA')
    if str(manifest.get('ciRunId')) != str(ci_run_id):
        raise ValueError('stale CI run receipt')
    if manifest.get('piSha') != PI_SHA:
        raise ValueError('wrong Pi SHA in evidence receipt')
    if manifest.get('baseSha') != BASE_SHA:
        raise ValueError('wrong main/base SHA in evidence receipt')
    if pr_head_sha is not None and manifest.get('prHeadSha') != pr_head_sha:
        raise ValueError('stale PR head in evidence receipt')
    files = manifest.get('files')
    if not isinstance(files, list) or not files:
        raise ValueError('missing evidence artifacts')
    _ids([{'id': item.get('path')} for item in files if isinstance(item, dict)], 'evidence file list')
    root = Path(manifest_path).resolve().parent
    for item in files:
        path = root / item['path']
        if not path.is_file():
            raise ValueError(f'missing evidence artifact: {item["path"]}')
        if hashlib.sha256(path.read_bytes()).hexdigest() != item.get('sha256'):
            raise ValueError(f'changed evidence artifact: {item["path"]}')
    expected_indexes = {'cases.json': hashlib.sha256(GLOB_INDEX.read_bytes()).hexdigest(),
                        'process_cases.json': PROCESS_INDEX_SHA256,
                        'theme_cases.json': THEME_INDEX_SHA256,
                        'selection_cases.json': SELECTION_INDEX_SHA256,
                        'path_cases.json': PATH_INDEX_SHA256}
    for name, expected in expected_indexes.items():
        path = root / 'contracts' / name
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
            raise ValueError(f'missing or changed frozen contract: {name}')
        if manifest.get('caseIndexSha256', {}).get(name) != expected:
            raise ValueError(f'evidence receipt has wrong contract hash: {name}')
    if manifest.get('requiredFiles') != len(files):
        raise ValueError('incorrect evidence file aggregate')
    return len(files)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    glob = sub.add_parser('glob')
    glob.add_argument('--report', type=Path, required=True)
    glob.add_argument('--source-sha', required=True)
    process = sub.add_parser('process')
    process.add_argument('--report', type=Path, action='append', required=True)
    process.add_argument('--source-sha', required=True)
    theme = sub.add_parser('theme')
    theme.add_argument('--report', type=Path, required=True)
    selection = sub.add_parser('selection')
    selection.add_argument('--report', type=Path, required=True)
    path_report = sub.add_parser('path')
    path_report.add_argument('--report', type=Path, required=True)
    manifest = sub.add_parser('manifest')
    manifest.add_argument('--manifest', type=Path, required=True)
    manifest.add_argument('--source-sha', required=True)
    manifest.add_argument('--ci-run-id', required=True)
    manifest.add_argument('--pr-head-sha')
    args = parser.parse_args()
    if args.command == 'glob':
        result = verify_glob_report(GLOB_INDEX, args.report, args.source_sha)
    elif args.command == 'process':
        result = verify_process_cases(PROCESS_INDEX, args.report, args.source_sha)
    elif args.command == 'theme':
        result = verify_theme_report(THEME_INDEX, args.report)
    elif args.command == 'selection':
        result = verify_selection_report(SELECTION_INDEX, args.report)
    elif args.command == 'path':
        result = verify_path_report(PATH_INDEX, args.report)
    else:
        result = {'artifacts': verify_evidence_manifest(args.manifest, args.source_sha, args.ci_run_id,
                                                        args.pr_head_sha)}
    print(json.dumps(result, sort_keys=True))


if __name__ == '__main__':
    main()
