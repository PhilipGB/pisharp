"""Validate evidence, source provenance and remote workflow bindings."""

from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable
import json
import re
import sys

from errors import MandatoryFailure, VerificationError


@dataclass(frozen=True)
class ValidationContext:
    FIXTURE_VERSION: str
    ROOT: Path
    SCHEMA_PATH: Path
    _platform_metadata: Callable[..., Any]
    _runtime_state: Callable[..., Any]
    _safe_artifact: Callable[..., Any]
    _sha256_file: Callable[..., Any]
    _source_commit_matches_current_tree: Callable[..., Any]
    load_contract: Callable[..., Any]
    _case_artifact_path: Callable[..., Any]
    _checkpoint_map: Callable[..., Any]
    _load_json: Callable[..., Any]
    _schema_errors: Callable[..., Any]
    _sha256_bytes: Callable[..., Any]
    _validate_inventory: Callable[..., Any]
    _validate_provenance: Callable[..., Any]
    _validate_remote_ci_provenance: Callable[..., Any]


def _validate_provenance(context: ValidationContext, provenance: dict[str, Any], evidence_root: Path, allow_synthetic: bool) -> None:
    runtime = context._runtime_state()
    if provenance['repository'] != runtime['remote']:
        raise MandatoryFailure('Evidence repository remote does not match the current checkout')
    if provenance['piSha'] != runtime['piSha']:
        raise MandatoryFailure('Evidence Pi SHA differs from tools/parity/pi-reference.txt')
    try:
        sys.path.insert(0, str(context.ROOT / 'tools/parity'))
        from pi_reference import verify_pi_checkout
        pi_path = Path(provenance['piPath'])
        actual_pi_sha = verify_pi_checkout(pi_path)
    except Exception as error:
        raise MandatoryFailure(f'Cannot verify the actual pinned Pi checkout from provenance: {error}') from error
    if actual_pi_sha != runtime['piSha']:
        raise MandatoryFailure('Evidence Pi checkout does not match the committed pin')
    contract = context.load_contract()
    missing_sources = [source for checkpoint in contract['checkpoints']
                       for source in checkpoint['upstreamSourceReferences'] if not (pi_path / source).is_file()]
    if missing_sources:
        raise VerificationError('Pinned Pi checkout is missing contract source references: ' + ', '.join(sorted(set(missing_sources))))
    if provenance['pisharpSourceSha'] != provenance['checkedOutSourceSha']:
        raise MandatoryFailure('Evidence PiSharp source and checked-out Git HEAD differ')
    if ((provenance['checkedOutSourceSha'] != runtime['headSha'] or not allow_synthetic)
            and not context._source_commit_matches_current_tree(provenance['checkedOutSourceSha'], runtime['headSha'])):
        raise MandatoryFailure('Evidence source commit is stale or tracked implementation sources changed')
    if provenance['branch'] != runtime['branch']:
        raise MandatoryFailure('Evidence branch does not match the current checkout')
    if provenance['baseSha'] != runtime['baseSha']:
        raise MandatoryFailure('Evidence base SHA does not match the current branch merge base')
    if provenance['fixtureVersion'] != context.FIXTURE_VERSION:
        raise MandatoryFailure('Evidence fixture version is stale')
    if provenance['environment'] != context._platform_metadata():
        raise MandatoryFailure('Evidence platform or toolchain metadata does not match this execution environment')
    if allow_synthetic:
        if provenance['testControl'] is not True:
            raise VerificationError('Synthetic verification is permitted only for explicit test-control evidence')
    elif provenance['testControl'] is not False:
        raise MandatoryFailure('Test-control evidence cannot be accepted as milestone evidence')
    identity = provenance['binaryIdentity']
    binary = context._safe_artifact(evidence_root, identity['path']) if identity['scope'] == 'evidence' else (context.ROOT / identity['path']).resolve()
    if identity['scope'] == 'repository' and context.ROOT not in binary.parents:
        raise VerificationError('Binary identity path escapes the repository')
    if not binary.is_file():
        raise MandatoryFailure(f'Required binary identity artifact is missing: {identity["path"]}')
    if context._sha256_file(binary) != identity['sha256']:
        raise MandatoryFailure('PiSharp binary identity hash is stale')


def _validate_remote_ci_provenance(context: ValidationContext, evidence: dict[str, Any], *, run: dict[str, Any],
                                   pull_request: dict[str, Any] | None) -> None:
    provenance = evidence['provenance']
    contract = context.load_contract()
    source_sha = provenance.get('checkedOutSourceSha')
    if provenance.get('repository') not in (
            'https://github.com/PhilipGB/pisharp.git', 'git@github.com:PhilipGB/pisharp.git',
            'https://github.com/PhilipGB/pisharp', 'git@github.com:PhilipGB/pisharp'):
        raise MandatoryFailure('Uploaded CI evidence repository is not PhilipGB/pisharp')
    if provenance.get('piSha') != contract['upstream']['revision']:
        raise MandatoryFailure('Uploaded CI evidence uses a different pinned Pi revision')
    if provenance.get('pisharpSourceSha') != source_sha or not re.fullmatch(r'[0-9a-f]{40}', str(source_sha)):
        raise MandatoryFailure('Uploaded CI evidence source identity is malformed')
    if not re.fullmatch(r'[0-9a-f]{40}', str(provenance.get('baseSha', ''))):
        raise MandatoryFailure('Uploaded CI evidence base identity is malformed')
    if provenance.get('fixtureVersion') != context.FIXTURE_VERSION or provenance.get('testControl') is not False:
        raise MandatoryFailure('Uploaded CI evidence has an unsupported fixture or test-control identity')
    if (run.get('event') == 'push' and provenance.get('branch') != run.get('head_branch')):
        raise MandatoryFailure('Push artifact branch does not match its exact-head workflow run')
    if (run.get('event') == 'pull_request'
            and provenance.get('branch') not in ('', run.get('head_branch'))):
        raise MandatoryFailure('PR artifact branch does not match its exact-head workflow run')
    if run.get('event') == 'push' and source_sha != run.get('head_sha'):
        raise MandatoryFailure('Push artifact source SHA does not match its exact-head workflow run')
    if pull_request is not None:
        if run.get('event') != 'pull_request':
            raise MandatoryFailure('Pull-request evidence was downloaded from a non-PR workflow run')
        case_map = {item.get('caseId'): item for item in evidence.get('cases', [])}
        pr_case = case_map.get('ci.pr-head-base', {})
        actual = (pr_case.get('actual') or {}).get('state') or {}
        if (actual.get('headSha') != (pull_request.get('head') or {}).get('sha')
                or actual.get('baseSha') != (pull_request.get('base') or {}).get('sha')
                or actual.get('baseRef') != 'main' or actual.get('mergeContainsHead') is not True
                or actual.get('mergeContainsBase') is not True or actual.get('mergeSha') != source_sha):
            raise MandatoryFailure('PR artifact does not prove the current PR head and base merge identity')
    identity = provenance.get('binaryIdentity') or {}
    if (identity.get('scope') != 'repository' or not re.fullmatch(r'[0-9a-f]{64}', str(identity.get('sha256', '')))
            or not isinstance(identity.get('path'), str) or not identity['path'].startswith('src/PiSharp.Cli/')):
        raise MandatoryFailure('Uploaded CI evidence binary identity is malformed')


def validate_evidence(context: ValidationContext, evidence: dict[str, Any], evidence_root: Path, *, allow_synthetic: bool = False,
                      checkpoint_id: str | None = None,
                      defer_publication_checkpoints: bool = False,
                      defer_ci_integration: bool = False,
                      remote_ci_run: dict[str, Any] | None = None,
                      remote_pull_request: dict[str, Any] | None = None) -> int:
    schema = context._load_json(context.SCHEMA_PATH)
    errors = context._schema_errors(evidence, schema)
    if errors:
        raise VerificationError('Evidence schema validation failed: ' + '; '.join(errors[:12]))
    contract = context.load_contract()
    checkpoints = context._checkpoint_map(contract)
    if checkpoint_id is not None and checkpoint_id not in checkpoints:
        raise VerificationError(f'Unknown checkpoint ID: {checkpoint_id}')
    expected_ids = contract['requiredCheckpointIds']
    observed_checkpoint_ids = [item['checkpointId'] for item in evidence['checkpointResults']]
    if len(observed_checkpoint_ids) != len(set(observed_checkpoint_ids)):
        raise MandatoryFailure('Evidence contains duplicate checkpoint results')
    if set(observed_checkpoint_ids) != set(expected_ids):
        missing = sorted(set(expected_ids) - set(observed_checkpoint_ids))
        extra = sorted(set(observed_checkpoint_ids) - set(expected_ids))
        raise MandatoryFailure(f'Checkpoint set differs from contract; missing={missing}, unknown={extra}')
    if remote_ci_run is None:
        context._validate_provenance(evidence['provenance'], evidence_root, allow_synthetic)
    else:
        context._validate_remote_ci_provenance(evidence, run=remote_ci_run, pull_request=remote_pull_request)
    if (checkpoint_id is None or checkpoint_id == 'source-inventory') and not allow_synthetic and remote_ci_run is None:
        context._validate_inventory(Path(evidence['provenance']['piPath']))

    expected_cases = {case_id: checkpoint for checkpoint in contract['checkpoints']
                      for case_id in checkpoint['requiredCaseIds']}
    case_ids = [case['caseId'] for case in evidence['cases']]
    if len(case_ids) != len(set(case_ids)):
        raise MandatoryFailure('Evidence contains duplicate case IDs')
    if set(case_ids) != set(expected_cases):
        missing = sorted(set(expected_cases) - set(case_ids))
        extra = sorted(set(case_ids) - set(expected_cases))
        raise MandatoryFailure(f'Case inventory differs from contract; missing={missing}, unknown={extra}')

    artifacts: dict[str, dict[str, Any]] = {}
    for item in evidence['artifacts']:
        if item['path'] in artifacts:
            raise MandatoryFailure(f'Duplicate artifact path: {item["path"]}')
        artifacts[item['path']] = item
        artifact_path = context._safe_artifact(evidence_root, item['path'])
        if not artifact_path.is_file():
            raise MandatoryFailure(f'Required evidence artifact is missing: {item["path"]}')
        if context._sha256_file(artifact_path) != item['sha256']:
            raise MandatoryFailure(f'Evidence artifact hash mismatch: {item["path"]}')

    case_by_id = {case['caseId']: case for case in evidence['cases']}
    checkpoint_results = {item['checkpointId']: item for item in evidence['checkpointResults']}
    if set(checkpoint_results) != set(expected_ids):
        raise MandatoryFailure('Checkpoint results are incomplete')

    failed_cases: list[str] = []
    scoped_checkpoint_ids = ([checkpoint_id] if checkpoint_id is not None else
                             [item for item in checkpoints
                              if not (defer_publication_checkpoints
                                      and item in ('draft-publication', 'exact-head-ci'))
                              and not (defer_ci_integration and item == 'ci-integration')])
    for scoped_id in scoped_checkpoint_ids:
        checkpoint = checkpoints[scoped_id]
        result = checkpoint_results[scoped_id]
        required = checkpoint['requiredCaseIds']
        for dependency in checkpoint['dependencies']:
            if checkpoint_results[dependency]['status'] != 'passed':
                failed_cases.append(f'{scoped_id}: dependency checkpoint {dependency} is not passed')
        if result['runnerIds'] != checkpoint['requiredExecutableVerification']:
            failed_cases.append(f'{scoped_id}: executable verification inventory differs from contract')
        observed = [case_id for case_id in required if case_id in case_by_id]
        if result['discovered'] != len(required):
            failed_cases.append(f'{scoped_id}: incorrect discovered case count')
        if result['executed'] != sum(1 for case_id in observed if case_by_id[case_id]['executed']):
            failed_cases.append(f'{scoped_id}: executed count does not match case evidence')
        if result['failed'] != sum(1 for case_id in observed if not case_by_id[case_id]['passed'] and case_by_id[case_id]['executed']):
            failed_cases.append(f'{scoped_id}: failed count does not match case evidence')
        if result['skipped'] != sum(1 for case_id in observed if case_by_id[case_id]['skipped']):
            failed_cases.append(f'{scoped_id}: skipped count does not match case evidence')
        if result['commandExitCode'] != 0:
            failed_cases.append(f'{scoped_id}: mandatory runner command failed')
        if result['status'] != 'passed':
            failed_cases.append(f'{scoped_id}: status is {result["status"]}')

    approved_exclusions = {item['caseId']: item for item in contract.get('approvedExclusions', [])}
    exclusion_ids = [item['caseId'] for item in evidence['exclusions']]
    if len(exclusion_ids) != len(set(exclusion_ids)):
        raise MandatoryFailure('Evidence contains duplicate exclusions')
    for exclusion in evidence['exclusions']:
        approved = approved_exclusions.get(exclusion['caseId'])
        if approved is None or exclusion != approved:
            failed_cases.append(f'{exclusion["caseId"]}: unapproved exclusion')

    scoped_case_ids = set()
    for case_id, checkpoint in expected_cases.items():
        if checkpoint_id is not None:
            include_case = checkpoint['id'] == checkpoint_id
        else:
            include_case = not (defer_publication_checkpoints
                                and checkpoint['id'] in ('draft-publication', 'exact-head-ci'))
            include_case = include_case and not (defer_ci_integration and checkpoint['id'] == 'ci-integration')
        if include_case:
            scoped_case_ids.add(case_id)
    for case_id in scoped_case_ids:
        checkpoint = expected_cases[case_id]
        case = case_by_id[case_id]
        required_assertions = checkpoint['requiredAssertions'].get(case_id, [case_id])
        if case['checkpoint'] != checkpoint['id']:
            failed_cases.append(f'{case_id}: assigned to wrong checkpoint')
        if case['execution']['fixtureIdentity'] not in checkpoint['requiredFixtureFamilies']:
            failed_cases.append(f'{case_id}: fixture family is not allowed by contract')
        if not allow_synthetic and case['execution']['commandIdentity'] not in checkpoint['requiredExecutableVerification']:
            failed_cases.append(f'{case_id}: command identity is not an approved executable verification')
        if set(case['assertions']) != set(required_assertions):
            failed_cases.append(f'{case_id}: assertion inventory differs from contract')
        if case['skipped']:
            failed_cases.append(f'{case_id}: mandatory case was skipped')
        artifact_path = context._case_artifact_path(case_id)
        artifact = artifacts.get(artifact_path)
        path = context._safe_artifact(evidence_root, artifact_path)
        if artifact is None or not path.is_file():
            failed_cases.append(f'{case_id}: required result artifact is missing')
            continue
        artifact_bytes = path.read_bytes()
        if context._sha256_bytes(artifact_bytes) != artifact['sha256']:
            failed_cases.append(f'{case_id}: result artifact hash mismatch')
            continue
        try:
            raw_result = json.loads(artifact_bytes)
        except (UnicodeError, json.JSONDecodeError):
            failed_cases.append(f'{case_id}: result artifact is malformed JSON')
            continue
        projection = {key: case[key] for key in ('caseId', 'checkpoint', 'executed', 'classification',
                                                 'oracle', 'actual', 'assertions', 'execution')}
        if raw_result != projection:
            failed_cases.append(f'{case_id}: report result differs from immutable run artifact')
        if not case['executed']:
            failed_cases.append(f'{case_id}: required case was not executed')
            continue
        if case['execution']['commandExitCode'] != 0:
            failed_cases.append(f'{case_id}: case runner command failed')
        if case['oracle'] is None or case['actual'] is None:
            failed_cases.append(f'{case_id}: oracle or actual result is missing')
            continue
        if not all(case['assertions'].values()):
            failed_cases.append(f'{case_id}: one or more required assertions failed')
        if case['classification'] in ('mismatch', 'unexecuted'):
            failed_cases.append(f'{case_id}: classification {case["classification"]} cannot pass')
        elif case['classification'] in ('strict', 'equivalent'):
            if case['oracle'] != case['actual']:
                failed_cases.append(f'{case_id}: strict comparison differs between Pi and PiSharp')
        elif case['classification'] not in checkpoint['allowedClassifications']:
            failed_cases.append(f'{case_id}: classification is not allowed by contract')
        elif case['classification'] == 'intentional-safety-difference':
            exclusion = approved_exclusions.get(case_id)
            if exclusion is None:
                failed_cases.append(f'{case_id}: safety difference is not approved')
        elif case['classification'].startswith('unsupported-'):
            exclusion = approved_exclusions.get(case_id)
            if exclusion is None:
                failed_cases.append(f'{case_id}: unsupported classification lacks approved source evidence')
        if case['passed'] is not True:
            failed_cases.append(f'{case_id}: case is not marked passed')

    test_results = evidence['testResults']
    if test_results['discovered'] != len(expected_cases):
        failed_cases.append('global test selection count differs from the required case inventory')
    if test_results['discovered'] == 0:
        failed_cases.append('zero mandatory tests were discovered')
    if test_results['executed'] != sum(1 for case in evidence['cases'] if case['executed']):
        failed_cases.append('global executed test count does not match case evidence')
    if test_results['failed'] != sum(1 for case in evidence['cases'] if case['executed'] and not case['passed']):
        failed_cases.append('global failed test count does not match case evidence')
    if test_results['skipped'] != sum(1 for case in evidence['cases'] if case['skipped']):
        failed_cases.append('global skipped test count does not match case evidence')
    if checkpoint_id is None and (test_results['failed'] != 0 or test_results['skipped'] != 0):
        failed_cases.append('mandatory verification has failures or skips')
    if checkpoint_id is None and evidence['executionExitCode'] != 0:
        failed_cases.append('execute command did not complete successfully')
    if failed_cases:
        raise MandatoryFailure('Mandatory evidence failed: ' + '; '.join(failed_cases[:30]))
    return 0
