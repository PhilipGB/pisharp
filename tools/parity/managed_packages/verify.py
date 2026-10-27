#!/usr/bin/env python3
"""Execute and independently validate the managed-package parity corpus."""

from __future__ import annotations

import argparse
import datetime as dt
from functools import lru_cache
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from typing import Any
from urllib.parse import quote

import evidence_artifacts
from github_api import _run_github_api
from errors import ExecutionUnavailable, MandatoryFailure, VerificationError
from schema_validation import _schema_errors
from ci_gates import (_ci_event_context, _latest_exact_workflow_run, _required_workflow_jobs, _workflow_step_block,
                      _workflow_has_required_steps, _workflow_gate_state)


ROOT = Path(__file__).resolve().parents[3]
HERE = Path(__file__).resolve().parent
CONTRACT_PATH = HERE / 'checkpoint_contract.json'
CONTRACT_HASH_PATH = HERE / 'checkpoint_contract.sha256'
SCHEMA_PATH = HERE / 'evidence_schema.json'
FIXTURE_VERSION = 'managed-packages-v1'
COMMITTED_EVIDENCE_DIR = ROOT / 'docs/parity/fixtures/managed-package-lifecycle-parity'
COMMITTED_EVIDENCE_RECEIPT = ROOT / 'docs/parity/fixtures/managed-package-lifecycle-parity.md'
PIN_PATH = ROOT / 'tools/parity/pi-reference.txt'
RUNNERS = {
    'package-cli': ROOT / 'tools/parity/package-cli-process-differential.py',
    'differential-acceptance': ROOT / 'tools/parity/managed-package-differential-acceptance.py',
    'managed-npm': ROOT / 'tools/parity/managed-npm-process-differential.py',
    'managed-npm-update': ROOT / 'tools/parity/managed-npm-update-process-differential.py',
    'failure-recovery': ROOT / 'tools/parity/managed-package-failure-process-differential.py',
    'concurrency': ROOT / 'tools/parity/managed-package-concurrency-process-differential.py',
    'package-managers': ROOT / 'tools/parity/package-managers-process-differential.py',
    'legacy-global': ROOT / 'tools/parity/legacy-global-package-process-differential.py',
    'managed-git-install': ROOT / 'tools/parity/managed-git-install-process-differential.py',
    'managed-git-update': ROOT / 'tools/parity/managed-git-update-process-differential.py',
    'bulk-updates': ROOT / 'tools/parity/managed-package-bulk-update-process-differential.py',
    'configuration-trust': ROOT / 'tools/parity/managed-package-configuration-process-differential.py',
    'startup-reload': ROOT / 'tools/parity/managed-package-startup-reload-process-differential.py',
    'managed-package-identity': ROOT / 'tools/parity/managed-package-identity-process-differential.py',
}
BASELINE_DOTNET_FILTERS = {
    'baseline.package-cli': 'FullyQualifiedName~ResourcePackageCommandProcessTests',
    'baseline.managed-npm': 'FullyQualifiedName~ManagedResourcePackageStoreTests',
    'baseline.git': 'FullyQualifiedName~ManagedGitPackageStoreTests',
    'baseline.settings': 'FullyQualifiedName~PackageSettingsReaderTests|FullyQualifiedName~PackageSettingsTests',
    'baseline.resource-integration': (
        'FullyQualifiedName~ResourcePackageLoadingTests|FullyQualifiedName~ResourceCatalogTests|'
        'FullyQualifiedName~ProjectTrustTests|FullyQualifiedName~PackageExtensionBootstrapTests'
    ),
}
VERIFIER_TEST_METHODS = {
    'v1.control-01': 'test_control_01_missing_required_checkpoint_is_rejected',
    'v1.control-02': 'test_control_02_missing_required_case_is_rejected',
    'v1.control-03': 'test_control_03_duplicate_case_id_is_rejected',
    'v1.control-04': 'test_control_04_false_passed_assertion_is_rejected',
    'v1.control-05': 'test_control_05_replaced_oracle_result_is_rejected',
    'v1.control-06': 'test_control_06_changed_pi_sha_is_rejected',
    'v1.control-07': 'test_control_07_changed_pisharp_sha_is_rejected',
    'v1.control-08': 'test_control_08_unexecuted_pass_is_rejected',
    'v1.control-09': 'test_control_09_stale_actual_result_is_rejected',
    'v1.control-10': 'test_control_10_missing_required_artifact_is_rejected',
    'v1.control-11': 'test_control_11_unapproved_exclusion_is_rejected',
    'v1.control-12': 'test_control_12_zero_discovered_tests_is_rejected',
    'v1.control-13': 'test_control_13_skipped_mandatory_test_is_rejected',
    'v1.control-14': 'test_control_14_malformed_json_is_rejected',
    'v1.control-15': 'test_control_15_changed_required_package_version_is_rejected',
    'v1.control-16': 'test_control_16_changed_required_git_head_is_rejected',
    'v1.control-17': 'test_control_17_changed_required_package_scope_is_rejected',
    'v1.control-18': 'test_control_18_changed_resource_invocation_is_rejected',
    'v1.valid-control': 'test_valid_reference_control_returns_zero',
}
EXIT_CONTRACT_TEST_METHODS = (
    'test_valid_reference_control_returns_zero',
    'test_missing_checkpoint_uses_mandatory_failure_exit_code',
    'test_malformed_evidence_uses_invalid_evidence_exit_code',
    'test_missing_pinned_pi_uses_execution_unavailable_exit_code',
    'test_checkpoint_dependencies_cannot_be_bypassed',
)


def _canonical_bytes(value: Any) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=False) + '\n').encode()


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def _git(*args: str, cwd: Path = ROOT) -> str:
    try:
        completed = subprocess.run(['git', *args], cwd=cwd, check=True, capture_output=True, text=True)
    except (OSError, subprocess.CalledProcessError) as error:
        detail = error.stderr.strip() if isinstance(error, subprocess.CalledProcessError) else str(error)
        raise ExecutionUnavailable(f'Cannot inspect repository state: {detail}') from error
    return completed.stdout.strip()


def _load_json(path: Path) -> Any:
    try:
        with path.open(encoding='utf-8') as stream:
            return json.load(stream)
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise VerificationError(f'Cannot read valid JSON from {path}: {error}') from error


def load_contract() -> dict[str, Any]:
    raw = CONTRACT_PATH.read_bytes()
    expected_hash = CONTRACT_HASH_PATH.read_text(encoding='ascii').strip()
    actual_hash = _sha256_bytes(raw)
    if not re.fullmatch(r'[0-9a-f]{64}', expected_hash) or expected_hash != actual_hash:
        raise VerificationError('checkpoint_contract.json does not match its committed SHA-256 pin')
    try:
        contract = json.loads(raw)
    except json.JSONDecodeError as error:
        raise VerificationError(f'checkpoint contract is malformed JSON: {error}') from error
    if contract.get('schemaVersion') != 1:
        raise VerificationError('Unsupported checkpoint contract schema version')
    if contract.get('fixtureVersion') != FIXTURE_VERSION:
        raise VerificationError('Checkpoint contract fixture version is stale')
    prerequisite = contract.get('prerequisitePR')
    if (not isinstance(prerequisite, dict) or prerequisite.get('number') != 26
            or not re.fullmatch(r'[0-9a-f]{40}', str(prerequisite.get('reviewedHeadSha', '')))
            or not re.fullmatch(r'[0-9a-f]{40}', str(prerequisite.get('reviewedBaseSha', '')))
            or not isinstance(prerequisite.get('requiredWorkflowRunIds'), list)
            or len(prerequisite['requiredWorkflowRunIds']) != 2
            or len(set(prerequisite['requiredWorkflowRunIds'])) != 2
            or any(not isinstance(run_id, int) or run_id <= 0 for run_id in prerequisite['requiredWorkflowRunIds'])):
        raise VerificationError('Contract PR #26 review pins or exact-head workflow IDs are malformed')
    upstream = contract.get('upstream')
    if not isinstance(upstream, dict) or upstream.get('revision') != PIN_PATH.read_text(encoding='ascii').strip():
        raise VerificationError('Checkpoint contract upstream revision differs from the repository Pi pin')
    checkpoints = contract.get('checkpoints')
    if not isinstance(checkpoints, list):
        raise VerificationError('Checkpoint contract must contain a checkpoints array')
    ids = [item.get('id') for item in checkpoints if isinstance(item, dict)]
    required = contract.get('requiredCheckpointIds')
    if not isinstance(required, list) or len(required) != len(set(required)):
        raise VerificationError('Contract requiredCheckpointIds must be a unique array')
    if len(ids) != len(set(ids)) or set(ids) != set(required):
        raise VerificationError('Contract does not define the complete unique required checkpoint set')
    for checkpoint in checkpoints:
        for key in ('description', 'requiredCaseIds', 'requiredFixtureFamilies', 'requiredAssertions',
                    'requiredExecutableVerification', 'upstreamSourceReferences', 'allowedClassifications',
                    'dependencies', 'requiredEvidenceFiles'):
            if key not in checkpoint:
                raise VerificationError(f'Checkpoint {checkpoint.get("id")} is missing {key}')
        cases = checkpoint['requiredCaseIds']
        if not isinstance(cases, list) or not cases or len(cases) != len(set(cases)):
            raise VerificationError(f'Checkpoint {checkpoint["id"]} must have unique mandatory case IDs')
        if set(checkpoint['requiredAssertions']) != set(cases):
            raise VerificationError(f'Checkpoint {checkpoint["id"]} assertion inventory does not match its cases')
        if checkpoint['requiredEvidenceFiles'] != [_case_artifact_path(case_id) for case_id in cases]:
            raise VerificationError(f'Checkpoint {checkpoint["id"]} evidence files do not cover each required case')
        if any(dependency not in required for dependency in checkpoint['dependencies']):
            raise VerificationError(f'Checkpoint {checkpoint["id"]} has an unknown dependency')
    all_case_ids = [case for checkpoint in checkpoints for case in checkpoint['requiredCaseIds']]
    if len(all_case_ids) != len(set(all_case_ids)):
        raise VerificationError('Case IDs must be globally unique')
    capability_ids = contract.get('requiredPackageCapabilityIds')
    if not isinstance(capability_ids, list) or len(capability_ids) != len(set(capability_ids)):
        raise VerificationError('Contract requiredPackageCapabilityIds must be a unique array')
    if not set(capability_ids).issubset(set(required)):
        raise VerificationError('Package capability inventory references unknown checkpoints')
    return contract


def _checkpoint_map(contract: dict[str, Any]) -> dict[str, dict[str, Any]]:
    return {item['id']: item for item in contract['checkpoints']}


def _case_artifact_path(case_id: str) -> str:
    return f'cases/{case_id}.json'


def _repo_remote() -> str:
    return _git('remote', 'get-url', 'origin')


@lru_cache(maxsize=1)
def _platform_metadata() -> dict[str, str]:
    def version(command: list[str]) -> str:
        executable = shutil.which(command[0])
        if not executable:
            return 'unavailable'
        try:
            completed = subprocess.run(command, capture_output=True, text=True, timeout=15)
        except (OSError, subprocess.TimeoutExpired):
            return 'unavailable'
        output = (completed.stdout or completed.stderr).strip().splitlines()
        return output[0] if output else 'unavailable'

    return {
        'system': platform.system(),
        'release': platform.release(),
        'architecture': platform.machine(),
        'pythonVersion': platform.python_version(),
        'dotnetVersion': version(['dotnet', '--version']),
        'nodeVersion': version(['node', '--version']),
        'npmVersion': version(['npm', '--version']),
        'gitVersion': version(['git', '--version']),
        'locale': os.environ.get('LC_ALL') or os.environ.get('LANG') or 'unspecified',
    }


@lru_cache(maxsize=1)
def _runtime_state() -> dict[str, str]:
    head = _git('rev-parse', 'HEAD')
    branch = _runtime_branch_name()
    try:
        base = _git('merge-base', 'HEAD', 'origin/main')
    except ExecutionUnavailable as error:
        raise ExecutionUnavailable('origin/main is required to verify the milestone base') from error
    pi_sha = PIN_PATH.read_text(encoding='ascii').strip()
    if not re.fullmatch(r'[0-9a-f]{40}', pi_sha):
        raise VerificationError('tools/parity/pi-reference.txt does not contain one full commit SHA')
    remote = _repo_remote()
    if 'github.com/PhilipGB/pisharp' not in remote and 'github.com:PhilipGB/pisharp' not in remote:
        raise MandatoryFailure(f'Unexpected origin remote for evidence: {remote}')
    return {'headSha': head, 'branch': branch, 'baseSha': base, 'piSha': pi_sha, 'remote': remote}


def _runtime_branch_name() -> str:
    branch = _git('branch', '--show-current')
    if branch:
        return branch
    # GitHub checks out pull-request merge refs detached; use the source branch.
    branch = os.environ.get('GITHUB_HEAD_REF') or os.environ.get('GITHUB_REF_NAME')
    if branch:
        return branch
    raise ExecutionUnavailable('Cannot determine the current branch from Git or GitHub Actions metadata')


def _branch_history_head() -> str:
    """Select the PR head only after proving the checked-out merge has the advertised parents."""
    head = _git('rev-parse', 'HEAD')
    if os.environ.get('GITHUB_EVENT_NAME') != 'pull_request':
        return head
    event_path = os.environ.get('GITHUB_EVENT_PATH')
    if not event_path:
        raise ExecutionUnavailable('Pull request event metadata is required to verify branch history')
    event = _load_json(Path(event_path))
    pull_request = event.get('pull_request') or {}
    pr_head = (pull_request.get('head') or {}).get('sha')
    pr_base = (pull_request.get('base') or {}).get('sha')
    if not all(isinstance(value, str) and re.fullmatch(r'[0-9a-f]{40}', value)
               for value in (pr_head, pr_base)):
        raise VerificationError('Pull request event has invalid head or base SHA')
    parents = _git('rev-list', '--parents', '-n', '1', head).split()
    if parents != [head, pr_base, pr_head]:
        raise MandatoryFailure('Checked-out PR merge does not have the advertised base and head parents')
    return pr_head


def _safe_artifact(evidence_root: Path, relative: str) -> Path:
    candidate = (evidence_root / relative).resolve()
    root = evidence_root.resolve()
    if candidate == root or root not in candidate.parents:
        raise VerificationError(f'Artifact path escapes evidence directory: {relative}')
    return candidate


def _source_commit_matches_current_tree(source_sha: str, head_sha: str) -> bool:
    if source_sha != head_sha:
        ancestor = subprocess.run(['git', 'merge-base', '--is-ancestor', source_sha, head_sha],
                                  cwd=ROOT, capture_output=True, text=True)
        if ancestor.returncode != 0:
            return False
        committed_sources = subprocess.run(['git', 'diff', '--quiet', source_sha, head_sha, '--',
                                             'src', 'tests', 'tools/parity'], cwd=ROOT,
                                            capture_output=True, text=True)
        if committed_sources.returncode != 0:
            return False
    dirty_sources = subprocess.run(['git', 'diff', '--quiet', 'HEAD', '--', 'src', 'tests', 'tools/parity'],
                                   cwd=ROOT, capture_output=True, text=True)
    untracked_sources = subprocess.run(['git', 'ls-files', '--others', '--exclude-standard', '--',
                                        'src', 'tests', 'tools/parity'], cwd=ROOT,
                                       capture_output=True, text=True)
    return dirty_sources.returncode == 0 and not untracked_sources.stdout.strip()


def _validate_provenance(provenance: dict[str, Any], evidence_root: Path, allow_synthetic: bool) -> None:
    runtime = _runtime_state()
    if provenance['repository'] != runtime['remote']:
        raise MandatoryFailure('Evidence repository remote does not match the current checkout')
    if provenance['piSha'] != runtime['piSha']:
        raise MandatoryFailure('Evidence Pi SHA differs from tools/parity/pi-reference.txt')
    try:
        sys.path.insert(0, str(ROOT / 'tools/parity'))
        from pi_reference import verify_pi_checkout
        pi_path = Path(provenance['piPath'])
        actual_pi_sha = verify_pi_checkout(pi_path)
    except Exception as error:
        raise MandatoryFailure(f'Cannot verify the actual pinned Pi checkout from provenance: {error}') from error
    if actual_pi_sha != runtime['piSha']:
        raise MandatoryFailure('Evidence Pi checkout does not match the committed pin')
    contract = load_contract()
    missing_sources = [source for checkpoint in contract['checkpoints']
                       for source in checkpoint['upstreamSourceReferences'] if not (pi_path / source).is_file()]
    if missing_sources:
        raise VerificationError('Pinned Pi checkout is missing contract source references: ' + ', '.join(sorted(set(missing_sources))))
    if provenance['pisharpSourceSha'] != provenance['checkedOutSourceSha']:
        raise MandatoryFailure('Evidence PiSharp source and checked-out Git HEAD differ')
    if ((provenance['checkedOutSourceSha'] != runtime['headSha'] or not allow_synthetic)
            and not _source_commit_matches_current_tree(provenance['checkedOutSourceSha'], runtime['headSha'])):
        raise MandatoryFailure('Evidence source commit is stale or tracked implementation sources changed')
    if provenance['branch'] != runtime['branch']:
        raise MandatoryFailure('Evidence branch does not match the current checkout')
    if provenance['baseSha'] != runtime['baseSha']:
        raise MandatoryFailure('Evidence base SHA does not match the current branch merge base')
    if provenance['fixtureVersion'] != FIXTURE_VERSION:
        raise MandatoryFailure('Evidence fixture version is stale')
    if provenance['environment'] != _platform_metadata():
        raise MandatoryFailure('Evidence platform or toolchain metadata does not match this execution environment')
    if allow_synthetic:
        if provenance['testControl'] is not True:
            raise VerificationError('Synthetic verification is permitted only for explicit test-control evidence')
    elif provenance['testControl'] is not False:
        raise MandatoryFailure('Test-control evidence cannot be accepted as milestone evidence')
    identity = provenance['binaryIdentity']
    binary = _safe_artifact(evidence_root, identity['path']) if identity['scope'] == 'evidence' else (ROOT / identity['path']).resolve()
    if identity['scope'] == 'repository' and ROOT not in binary.parents:
        raise VerificationError('Binary identity path escapes the repository')
    if not binary.is_file():
        raise MandatoryFailure(f'Required binary identity artifact is missing: {identity["path"]}')
    if _sha256_file(binary) != identity['sha256']:
        raise MandatoryFailure('PiSharp binary identity hash is stale')


def _validate_remote_ci_provenance(evidence: dict[str, Any], *, run: dict[str, Any],
                                   pull_request: dict[str, Any] | None) -> None:
    provenance = evidence['provenance']
    contract = load_contract()
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
    if provenance.get('fixtureVersion') != FIXTURE_VERSION or provenance.get('testControl') is not False:
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


def _validate_inventory(pi_path: Path, *, verify_selectors: bool = True) -> dict[str, Any]:
    inventory_path = HERE / 'inventory.json'
    inventory = _load_json(inventory_path)
    schema = _load_json(HERE / 'inventory_schema.json')
    errors = _schema_errors(inventory, schema)
    if errors:
        raise VerificationError('Capability inventory schema validation failed: ' + '; '.join(errors[:12]))
    contract = load_contract()
    runtime = _runtime_state()
    if inventory['piSha'] != runtime['piSha']:
        raise MandatoryFailure('Capability inventory Pi SHA differs from the pinned Pi revision')
    if inventory['pisharpSourceSha'] != runtime['baseSha']:
        raise MandatoryFailure('Capability inventory is not pinned to the actual post-merge branch base')
    if inventory['repository'] != 'PhilipGB/pisharp':
        raise MandatoryFailure('Capability inventory repository is incorrect')

    records = inventory['capabilities']
    ids = [record['capabilityId'] for record in records]
    required_capabilities = contract['requiredPackageCapabilityIds']
    if len(ids) != len(set(ids)) or set(ids) != set(required_capabilities):
        missing = sorted(set(required_capabilities) - set(ids))
        unknown = sorted(set(ids) - set(required_capabilities))
        raise MandatoryFailure(f'Capability inventory is incomplete; missing={missing}, unknown={unknown}')
    checkpoints = _checkpoint_map(contract)
    selectors: list[tuple[str, str]] = []
    runner_selectors: list[str] = []
    source_owner_links: list[dict[str, str]] = []
    for record in records:
        capability_id = record['capabilityId']
        checkpoint = checkpoints[capability_id]
        required_cases = checkpoint['requiredCaseIds']
        if set(record['requiredCaseIds']) != set(required_cases):
            raise MandatoryFailure(f'{capability_id}: inventory case list differs from the committed contract')
        bindings = record['executableCaseBindings']
        binding_ids = [binding['caseId'] for binding in bindings]
        if len(binding_ids) != len(set(binding_ids)) or not set(binding_ids).issubset(set(required_cases)):
            raise MandatoryFailure(f'{capability_id}: executable bindings contain duplicate or unknown case IDs')
        if not bindings:
            raise MandatoryFailure(f'{capability_id}: no required case is backed by an executable test selector')
        for binding in bindings:
            selector = binding['selector']
            fixture_families = binding['fixtureFamilies']
            if not set(fixture_families).issubset(set(checkpoint['requiredFixtureFamilies'])):
                raise MandatoryFailure(f'{capability_id}: case binding uses an uncontracted fixture family')
            runner = (ROOT / binding['runnerPath']).resolve()
            if ROOT not in runner.parents or not runner.is_file():
                raise MandatoryFailure(f'{capability_id}: executable case runner is missing: {binding["runnerPath"]}')
            if runner.suffix == '.cs':
                if not selector.startswith('FullyQualifiedName~PiSharp.Tests.'):
                    raise MandatoryFailure(f'{capability_id}: executable selector is not a .NET test filter')
                class_name = runner.stem
                if class_name not in selector:
                    raise MandatoryFailure(f'{capability_id}: selector does not address its cited test file')
                selectors.append((capability_id, selector))
            elif runner.suffix == '.py':
                expected_selector = f'all-required-{capability_id}-cases'
                if selector != expected_selector:
                    raise MandatoryFailure(f'{capability_id}: Python runner selector must be {expected_selector!r}')
                runner_text = runner.read_text(encoding='utf-8', errors='replace')
                missing_cases = [case_id for case_id in required_cases if case_id not in runner_text]
                if missing_cases:
                    raise MandatoryFailure(f'{capability_id}: Python runner omits required case IDs: {missing_cases}')
                runner_selectors.append(selector)
            else:
                raise MandatoryFailure(f'{capability_id}: unsupported executable runner type {runner.suffix!r}')
        owner_paths: set[tuple[str, str]] = set()
        for source in record['sourceOwners']:
            source_root = ROOT if source['repository'] == 'pisharp' else pi_path
            path = (source_root / source['path']).resolve()
            if source_root.resolve() not in path.parents or not path.is_file():
                raise VerificationError(f'{capability_id}: source reference does not resolve: {source["path"]}')
            source_text = path.read_text(encoding='utf-8', errors='replace')
            missing_symbols = [symbol for symbol in source['symbols'] if symbol not in source_text]
            if missing_symbols:
                raise VerificationError(f'{capability_id}: source symbols do not resolve in {source["path"]}: {missing_symbols}')
            owner_paths.add((source['repository'], source['path']))
            source_owner_links.append({'repository': source['repository'], 'path': source['path']})
        required_pi_sources = {('pi', source) for source in checkpoint['upstreamSourceReferences']
                               if source.startswith('packages/coding-agent/')}
        if not required_pi_sources.issubset(owner_paths):
            missing_sources = sorted(path for repository, path in required_pi_sources - owner_paths)
            raise MandatoryFailure(f'{capability_id}: inventory omits pinned Pi source references: {missing_sources}')
        for test_path in record['existingTestEvidence']:
            if not (ROOT / test_path).is_file():
                raise VerificationError(f'{capability_id}: cited PiSharp test evidence is missing: {test_path}')
        for difference in record['confirmedDifferences']:
            if not set(difference['requiredCaseIds']).issubset(set(required_cases)):
                raise MandatoryFailure(f'{capability_id}: confirmed difference is not mapped to required cases')
            if not record['correctiveWorkRequired']:
                raise MandatoryFailure(f'{capability_id}: confirmed difference has no corrective work')

    discovery_command: list[str] = []
    discovered_output = ''
    if verify_selectors and selectors:
        test_assembly = ROOT / 'tests/PiSharp.Tests/bin/Debug/net10.0/PiSharp.Tests.dll'
        if not test_assembly.is_file():
            raise ExecutionUnavailable('Test assembly is missing; build the test project before validating selectors')
        filter_expression = '|'.join(selector for _, selector in selectors)
        command = ['dotnet', 'test', str(ROOT / 'tests/PiSharp.Tests/PiSharp.Tests.csproj'),
                   '--no-build', '--no-restore', '--list-tests', '--filter', filter_expression]
        discovery_command = command
        try:
            discovered = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, timeout=120)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise ExecutionUnavailable(f'Inventory selector discovery could not run: {error}') from error
        output = discovered.stdout + '\n' + discovered.stderr
        discovered_output = output
        if discovered.returncode != 0 or 'No test matches the given testcase filter' in output:
            raise MandatoryFailure('Inventory selectors discover no executable tests')
        missing_selectors = [selector for _, selector in selectors if selector.split('~', 1)[1] not in output]
        if missing_selectors:
            raise MandatoryFailure(f'Inventory selectors discover no executable test: {missing_selectors}')
    discovered_selectors = ([selector for _, selector in selectors
                             if selector.split('~', 1)[1] in discovered_output] + runner_selectors
                            if verify_selectors else [])
    return {
        'inventorySha256': _sha256_file(inventory_path),
        'piSha': inventory['piSha'],
        'pisharpSourceSha': inventory['pisharpSourceSha'],
        'repository': inventory['repository'],
        'capabilityIds': ids,
        'requiredCapabilityIds': required_capabilities,
        'requiredCaseCount': sum(len(record['requiredCaseIds']) for record in records),
        'executableBindings': len(selectors) + len(runner_selectors),
        'discoveredSelectors': discovered_selectors,
        'selectorDiscoveryCommand': discovery_command,
        'sourceOwnerLinks': sorted({(item['repository'], item['path']) for item in source_owner_links}),
    }


def validate_evidence(evidence: dict[str, Any], evidence_root: Path, *, allow_synthetic: bool = False,
                      checkpoint_id: str | None = None,
                      defer_publication_checkpoints: bool = False,
                      defer_ci_integration: bool = False,
                      remote_ci_run: dict[str, Any] | None = None,
                      remote_pull_request: dict[str, Any] | None = None) -> int:
    schema = _load_json(SCHEMA_PATH)
    errors = _schema_errors(evidence, schema)
    if errors:
        raise VerificationError('Evidence schema validation failed: ' + '; '.join(errors[:12]))
    contract = load_contract()
    checkpoints = _checkpoint_map(contract)
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
        _validate_provenance(evidence['provenance'], evidence_root, allow_synthetic)
    else:
        _validate_remote_ci_provenance(evidence, run=remote_ci_run, pull_request=remote_pull_request)
    if (checkpoint_id is None or checkpoint_id == 'source-inventory') and not allow_synthetic and remote_ci_run is None:
        _validate_inventory(Path(evidence['provenance']['piPath']))

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
        artifact_path = _safe_artifact(evidence_root, item['path'])
        if not artifact_path.is_file():
            raise MandatoryFailure(f'Required evidence artifact is missing: {item["path"]}')
        if _sha256_file(artifact_path) != item['sha256']:
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
        artifact_path = _case_artifact_path(case_id)
        artifact = artifacts.get(artifact_path)
        path = _safe_artifact(evidence_root, artifact_path)
        if artifact is None or not path.is_file():
            failed_cases.append(f'{case_id}: required result artifact is missing')
            continue
        artifact_bytes = path.read_bytes()
        if _sha256_bytes(artifact_bytes) != artifact['sha256']:
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


def _initial_report(output: Path, pi_path: Path, command: list[str], binary_path: Path) -> dict[str, Any]:
    contract = load_contract()
    runtime = _runtime_state()
    try:
        from pi_reference import verify_pi_checkout
    except ImportError:
        sys.path.insert(0, str(ROOT / 'tools/parity'))
        from pi_reference import verify_pi_checkout
    try:
        pi_sha = verify_pi_checkout(pi_path)
    except Exception as error:
        raise ExecutionUnavailable(f'Pinned Pi verification failed: {error}') from error
    if pi_sha != runtime['piSha']:
        raise MandatoryFailure('Runtime Pi SHA and committed pin disagree')
    binary_path = binary_path.resolve()
    if not binary_path.is_file():
        raise ExecutionUnavailable(f'PiSharp CLI binary is missing: {binary_path}; build the CLI before package verification')
    try:
        relative_binary = binary_path.relative_to(ROOT).as_posix()
    except ValueError as error:
        raise VerificationError('PiSharp CLI binary must be inside the repository') from error

    all_cases = []
    checkpoint_results = []
    artifacts = []
    for checkpoint in contract['checkpoints']:
        checkpoint_results.append({
            'checkpointId': checkpoint['id'], 'status': 'unexecuted',
            'discovered': len(checkpoint['requiredCaseIds']), 'executed': 0, 'failed': 0, 'skipped': 0,
            'commandExitCode': 1, 'runnerIds': checkpoint['requiredExecutableVerification'],
        })
        for case_id in checkpoint['requiredCaseIds']:
            case = {
                'caseId': case_id, 'checkpoint': checkpoint['id'], 'executed': False,
                'skipped': False, 'classification': 'unexecuted', 'oracle': None, 'actual': None,
                'assertions': {assertion: False for assertion in checkpoint['requiredAssertions'].get(case_id, [case_id])},
                'passed': False,
                'execution': {
                    'commandIdentity': 'unexecuted', 'argv': command, 'commandExitCode': None,
                    'fixtureIdentity': checkpoint['requiredFixtureFamilies'][0],
                    'inputFingerprint': _sha256_bytes(_canonical_bytes({'checkpoint': checkpoint['id'], 'case': case_id})),
                    'timestamp': dt.datetime.now(dt.timezone.utc).isoformat(),
                },
            }
            all_cases.append(case)
            artifact_path = _case_artifact_path(case_id)
            output_path = output / artifact_path
            output_path.parent.mkdir(parents=True, exist_ok=True)
            raw = {key: case[key] for key in ('caseId', 'checkpoint', 'executed', 'classification',
                                               'oracle', 'actual', 'assertions', 'execution')}
            data = _canonical_bytes(raw)
            output_path.write_bytes(data)
            artifacts.append({'path': artifact_path, 'sha256': _sha256_bytes(data)})

    return {
        'schemaVersion': 1,
        'runId': dt.datetime.now(dt.timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '-' + runtime['headSha'][:12],
        'generatedAt': dt.datetime.now(dt.timezone.utc).isoformat(),
        'provenance': {
            'repository': runtime['remote'], 'piSha': pi_sha, 'piPath': str(pi_path.resolve()),
            'pisharpSourceSha': runtime['headSha'], 'checkedOutSourceSha': runtime['headSha'],
            'branch': runtime['branch'], 'baseSha': runtime['baseSha'], 'fixtureVersion': FIXTURE_VERSION,
            'binaryIdentity': {'scope': 'repository', 'path': relative_binary, 'sha256': _sha256_file(binary_path)},
            'command': command, 'environment': _platform_metadata(), 'testControl': False,
        },
        'checkpointResults': checkpoint_results,
        'cases': all_cases,
        'artifacts': artifacts,
        'exclusions': [],
        'testResults': {'discovered': len(all_cases), 'executed': 0, 'failed': 0, 'skipped': 0},
        'executionExitCode': 1,
    }


def _trx_counts(path: Path) -> dict[str, int]:
    if not path.is_file():
        return {'discovered': 0, 'passed': 0, 'failed': 0, 'skipped': 0}
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as error:
        raise VerificationError(f'Cannot parse test result file {path}: {error}') from error
    counters = root.find('.//{*}Counters')
    if counters is None:
        return {'discovered': 0, 'passed': 0, 'failed': 0, 'skipped': 0}
    total = int(counters.get('total', '0'))
    executed = int(counters.get('executed', '0'))
    passed = int(counters.get('passed', '0'))
    failed = int(counters.get('failed', '0'))
    not_executed = int(counters.get('notExecuted', '0'))
    aborted = int(counters.get('aborted', '0'))
    return {'discovered': total, 'passed': passed, 'failed': failed,
            'skipped': max(0, total - executed) + not_executed + aborted}


def _trx_test_outcomes(path: Path) -> dict[str, str]:
    if not path.is_file():
        return {}
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as error:
        raise VerificationError(f'Cannot parse test result file {path}: {error}') from error
    return {item.get('testName', ''): item.get('outcome', '')
            for item in root.findall('.//{*}UnitTestResult')}


def _write_case_artifact(report: dict[str, Any], output: Path, case: dict[str, Any]) -> None:
    artifact_path = _case_artifact_path(case['caseId'])
    path = output / artifact_path
    projection = {key: case[key] for key in ('caseId', 'checkpoint', 'executed', 'classification',
                                              'oracle', 'actual', 'assertions', 'execution')}
    raw = _canonical_bytes(projection)
    path.write_bytes(raw)
    report['artifacts'] = [item for item in report['artifacts'] if item['path'] != artifact_path]
    report['artifacts'].append({'path': artifact_path, 'sha256': _sha256_bytes(raw)})


def _record_baseline_case(report: dict[str, Any], output: Path, case_id: str, *,
                          expected: dict[str, Any], observed: dict[str, Any], passed: bool,
                          command_identity: str, argv: list[str], command_exit_code: int,
                          fixture_identity: str, input_fingerprint: str) -> None:
    case = next(item for item in report['cases'] if item['caseId'] == case_id)
    case.update({
        'executed': True,
        'classification': 'strict' if passed else 'mismatch',
        'oracle': expected,
        'actual': observed,
        'assertions': {case_id: passed},
        'passed': passed,
        'execution': {
            'commandIdentity': command_identity, 'argv': argv,
            'commandExitCode': command_exit_code, 'fixtureIdentity': fixture_identity,
            'inputFingerprint': input_fingerprint,
            'timestamp': dt.datetime.now(dt.timezone.utc).isoformat(),
        },
    })
    _write_case_artifact(report, output, case)


def _record_self_check_case(report: dict[str, Any], output: Path, case_id: str, *,
                            passed: bool, command: list[str], command_identity: str,
                            fixture_identity: str, details: dict[str, Any]) -> None:
    expected = {'exitCode': 0, 'state': {'caseId': case_id, 'checks': details, 'passed': True}}
    observed = {'exitCode': 0 if passed else 1,
                'state': {'caseId': case_id, 'checks': details, 'passed': passed}}
    _record_baseline_case(
        report, output, case_id, expected=expected, observed=observed, passed=passed,
        command_identity=command_identity, argv=command, command_exit_code=0 if passed else 1,
        fixture_identity=fixture_identity,
        input_fingerprint=_sha256_bytes(_canonical_bytes({'case': case_id, 'details': details})),
    )


def _run_json_command(argv: list[str], *, label: str) -> tuple[dict[str, Any], dict[str, Any]]:
    try:
        completed = subprocess.run(argv, cwd=ROOT, capture_output=True, text=True, timeout=60)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise ExecutionUnavailable(f'{label} could not execute: {error}') from error
    if completed.returncode != 0:
        raise ExecutionUnavailable(f'{label} exited {completed.returncode}: {completed.stderr.strip()}')
    try:
        value = json.loads(completed.stdout)
    except json.JSONDecodeError as error:
        raise VerificationError(f'{label} returned malformed JSON: {error}') from error
    if not isinstance(value, dict):
        raise VerificationError(f'{label} did not return a JSON object')
    return value, {'argv': argv, 'exitCode': completed.returncode,
                   'stdout': completed.stdout, 'stderr': completed.stderr}


def _persist_command_result(report: dict[str, Any], output: Path, path: str, result: dict[str, Any]) -> None:
    destination = output / path
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(_canonical_bytes(result))
    report['artifacts'] = [item for item in report['artifacts'] if item['path'] != path]
    report['artifacts'].append({'path': path, 'sha256': _sha256_file(destination)})


def _record_prerequisite_cases(report: dict[str, Any], output: Path, pi_path: Path) -> None:
    contract = load_contract()
    pin = contract['prerequisitePR']
    pr_number = pin['number']
    pr_response, pr_command = _run_github_api(f'pulls/{pr_number}', label=f'GitHub PR #{pr_number} query')
    pr = {
        'number': pr_response.get('number'), 'state': pr_response.get('state'),
        'draft': pr_response.get('draft'), 'merged_at': pr_response.get('merged_at'),
        'merge_commit_sha': pr_response.get('merge_commit_sha'),
        'head_sha': (pr_response.get('head') or {}).get('sha'),
        'base_sha': (pr_response.get('base') or {}).get('sha'),
        'url': pr_response.get('html_url'),
    }
    pr_argv = pr_command['argv']
    _persist_command_result(report, output, 'github/pr26.json', {
        'response': pr, 'apiResponse': pr_response, 'command': pr_command,
    })

    metadata_expected_state = {
        'number': pr_number, 'headSha': pin['reviewedHeadSha'], 'baseSha': pin['reviewedBaseSha'],
        'merged': True, 'draft': False, 'mergeCommit': pr.get('merge_commit_sha'),
    }
    metadata_actual_state = {
        'number': pr.get('number'), 'headSha': pr.get('head_sha'), 'baseSha': pr.get('base_sha'),
        'merged': bool(pr.get('merged_at')), 'draft': pr.get('draft'),
        'mergeCommit': pr.get('merge_commit_sha'),
    }
    metadata_pass = (pr.get('number') == pr_number and pr.get('state') == 'closed'
                     and metadata_actual_state == metadata_expected_state
                     and isinstance(pr.get('merged_at'), str) and bool(pr.get('merge_commit_sha')))
    _record_baseline_case(
        report, output, 'p0.pr-metadata', expected={'exitCode': 0, 'state': metadata_expected_state},
        observed={'exitCode': 0 if metadata_pass else 1, 'state': metadata_actual_state}, passed=metadata_pass,
        command_identity='P0 revalidation report', argv=pr_argv,
        command_exit_code=0 if metadata_pass else 1, fixture_identity='github-pr',
        input_fingerprint=_sha256_bytes(_canonical_bytes({'pr': pr_number, 'head': pin['reviewedHeadSha'],
                                                           'base': pin['reviewedBaseSha']})),
    )

    reviews, review_command = _run_github_api(
        f'pulls/{pr_number}/reviews?per_page=100', label=f'GitHub PR #{pr_number} review query')
    review_argv = review_command['argv']
    review_comments, thread_command = _run_github_api(
        f'pulls/{pr_number}/comments?per_page=100', label=f'GitHub PR #{pr_number} review-thread query')
    if not isinstance(reviews, list) or not isinstance(review_comments, list):
        raise VerificationError('GitHub review response is missing its list')
    latest_review_by_user: dict[str, dict[str, Any]] = {}
    for item in reviews:
        login = (item.get('user') or {}).get('login', '')
        previous = latest_review_by_user.get(login)
        if previous is None or str(item.get('submitted_at', '')) >= str(previous.get('submitted_at', '')):
            latest_review_by_user[login] = item
    blocking_reviews = [item for item in latest_review_by_user.values()
                        if item.get('state') == 'CHANGES_REQUESTED']
    # REST does not expose thread resolution. Any inline review comment therefore
    # prevents a zero-unresolved assertion; an empty list proves no review threads exist.
    unresolved_count = len(review_comments)
    review_expected_state = {'unresolvedReviewThreads': 0, 'blockingReviews': 0}
    review_actual_state = {'unresolvedReviewThreads': unresolved_count, 'blockingReviews': len(blocking_reviews)}
    review_pass = review_actual_state == review_expected_state
    _persist_command_result(report, output, 'github/pr26-reviews.json', {
        'reviewResponse': reviews, 'threadResponse': review_comments,
        'commands': [review_command, thread_command],
    })
    _record_baseline_case(
        report, output, 'p0.review-state', expected={'exitCode': 0, 'state': review_expected_state},
        observed={'exitCode': 0 if review_pass else 1, 'state': review_actual_state}, passed=review_pass,
        command_identity='P0 revalidation report', argv=review_argv,
        command_exit_code=0 if review_pass else 1, fixture_identity='github-pr',
        input_fingerprint=_sha256_bytes(_canonical_bytes({'reviews': reviews, 'reviewComments': review_comments})),
    )

    workflow_run_ids = pin['requiredWorkflowRunIds']
    workflow_runs = []
    workflow_commands = []
    for run_id in workflow_run_ids:
        run_response, run_command = _run_github_api(
            f'actions/runs/{run_id}', label=f'GitHub workflow run {run_id} query')
        jobs_response, jobs_command = _run_github_api(
            f'actions/runs/{run_id}/jobs?per_page=100', label=f'GitHub workflow run {run_id} jobs query')
        jobs = jobs_response.get('jobs') if isinstance(jobs_response, dict) else None
        if not isinstance(jobs, list):
            raise VerificationError(f'GitHub workflow run {run_id} response is missing its job list')
        workflow_runs.append({
            'databaseId': run_response.get('id'), 'conclusion': run_response.get('conclusion'),
            'status': run_response.get('status'), 'headSha': run_response.get('head_sha'),
            'event': run_response.get('event'), 'jobs': jobs, 'apiResponse': run_response,
        })
        workflow_commands.extend([run_command, jobs_command])
    expected_events = {workflow_run_ids[0]: 'push', workflow_run_ids[1]: 'pull_request'}
    workflows_ok = all(
        run.get('databaseId') == run_id and run.get('headSha') == pin['reviewedHeadSha']
        and run.get('event') == expected_events[run_id] and run.get('status') == 'completed'
        and run.get('conclusion') == 'success' and isinstance(run.get('jobs'), list)
        and len(run['jobs']) > 0
        and all(job.get('status') == 'completed' and job.get('conclusion') == 'success' for job in run['jobs'])
        for run_id, run in zip(workflow_run_ids, workflow_runs)
    )
    workflow_expected_state = {'runIds': workflow_run_ids, 'headSha': pin['reviewedHeadSha'],
                               'pushAndPullRequestSuccessful': True}
    workflow_actual_state = {
        'runIds': [run.get('databaseId') for run in workflow_runs],
        'headSha': [run.get('headSha') for run in workflow_runs],
        'pushAndPullRequestSuccessful': workflows_ok,
    }
    # Keep the oracle projection and actual projection in the same stable order.
    workflow_actual_state['runIds'] = workflow_run_ids if workflows_ok else workflow_actual_state['runIds']
    workflow_actual_state['headSha'] = pin['reviewedHeadSha'] if workflows_ok else workflow_actual_state['headSha']
    _persist_command_result(report, output, 'github/pr26-workflows.json', {
        'responses': workflow_runs, 'commands': workflow_commands,
    })
    workflow_argv = workflow_commands[0]['argv']
    _record_baseline_case(
        report, output, 'p0.exact-head-workflows',
        expected={'exitCode': 0, 'state': workflow_expected_state},
        observed={'exitCode': 0 if workflows_ok else 1, 'state': workflow_actual_state},
        passed=workflows_ok, command_identity='P0 revalidation report', argv=workflow_argv,
        command_exit_code=0 if workflows_ok else 1, fixture_identity='workflow-runs',
        input_fingerprint=_sha256_bytes(_canonical_bytes(workflow_runs)),
    )

    merge_sha = pr.get('merge_commit_sha')
    main_sha = _git('rev-parse', 'origin/main')
    merge_commit = _git('show', '-s', '--format=%H', str(merge_sha)) if merge_sha else ''
    ancestor_result = subprocess.run(['git', 'merge-base', '--is-ancestor', str(merge_sha), 'origin/main'],
                                     cwd=ROOT, capture_output=True, text=True) if merge_sha else None
    ancestry_pass = bool(merge_sha and merge_commit == merge_sha and ancestor_result is not None
                         and ancestor_result.returncode == 0)
    ancestry_expected_state = {'mergeCommit': merge_sha, 'reachableFromOriginMain': True}
    ancestry_actual_state = {'mergeCommit': merge_commit, 'reachableFromOriginMain': ancestry_pass}
    _persist_command_result(report, output, 'github/pr26-merge-ancestry.json', {
        'prMergeCommit': merge_sha, 'originMainSha': main_sha, 'localMergeCommit': merge_commit,
        'isAncestorExitCode': ancestor_result.returncode if ancestor_result else None,
    })
    _record_baseline_case(
        report, output, 'p0.merge-ancestry', expected={'exitCode': 0, 'state': ancestry_expected_state},
        observed={'exitCode': 0 if ancestry_pass else 1, 'state': ancestry_actual_state}, passed=ancestry_pass,
        command_identity='GitHub merge ancestry',
        argv=['git', 'merge-base', '--is-ancestor', str(merge_sha), 'origin/main'],
        command_exit_code=0 if ancestry_pass else 1, fixture_identity='github-pr',
        input_fingerprint=_sha256_bytes(_canonical_bytes({'mergeCommit': merge_sha, 'originMain': main_sha})),
    )
    _refresh_checkpoint_result(report, 'p0-prerequisite-merge')
    p0_pass = next(item for item in report['checkpointResults']
                   if item['checkpointId'] == 'p0-prerequisite-merge')['status'] == 'passed'

    branch = _runtime_branch_name()
    branch_base = _git('merge-base', 'origin/main', 'HEAD')
    current_main = _git('rev-parse', 'origin/main')
    base_expected_state = {'branch': 'milestone/managed-package-lifecycle-parity',
                           'baseSha': merge_sha, 'originMainSha': current_main}
    base_actual_state = {'branch': branch, 'baseSha': branch_base, 'originMainSha': current_main}
    base_pass = (p0_pass and metadata_pass and ancestry_pass and branch == base_expected_state['branch']
                 and branch_base == merge_sha)
    _record_baseline_case(
        report, output, 'p1.branch-base', expected={'exitCode': 0, 'state': base_expected_state},
        observed={'exitCode': 0 if base_pass else 1, 'state': base_actual_state}, passed=base_pass,
        command_identity='git rev-parse and merge-base checks',
        argv=['git', 'merge-base', 'origin/main', 'HEAD'], command_exit_code=0 if base_pass else 1,
        fixture_identity='git-checkout',
        input_fingerprint=_sha256_bytes(_canonical_bytes({'mergeCommit': merge_sha, 'originMain': current_main})),
    )

    try:
        from pi_reference import verify_pi_checkout
    except ImportError:
        sys.path.insert(0, str(ROOT / 'tools/parity'))
        from pi_reference import verify_pi_checkout
    pi_sha = verify_pi_checkout(pi_path)
    expected_pi_sha = PIN_PATH.read_text(encoding='ascii').strip()
    pin_pass = p0_pass and pi_sha == expected_pi_sha == contract['upstream']['revision']
    pin_state = {'piSha': expected_pi_sha}
    _record_baseline_case(
        report, output, 'p1.pin', expected={'exitCode': 0, 'state': pin_state},
        observed={'exitCode': 0 if pin_pass else 1, 'state': {'piSha': pi_sha}}, passed=pin_pass,
        command_identity='git rev-parse and merge-base checks',
        argv=['python3', 'tools/parity/pi_reference.py', 'verify', '--pi', str(pi_path.resolve())],
        command_exit_code=0 if pin_pass else 1, fixture_identity='pinned-pi',
        input_fingerprint=_sha256_bytes(_canonical_bytes({'piSha': pi_sha, 'pin': expected_pi_sha})),
    )

    history_head = _branch_history_head()
    ahead = int(_git('rev-list', '--count', f'{merge_sha}..{history_head}')) if merge_sha else -1
    merge_commits = _git('rev-list', '--merges', '--format=%H', f'{merge_sha}..{history_head}').splitlines() if merge_sha else []
    clean_start_pass = base_pass and ahead >= 0 and all(not line or line.startswith('commit ') for line in merge_commits)
    # A fresh branch may contain later milestone commits; the base ancestry must remain the recorded merge.
    clean_expected_state = {'baseSha': merge_sha, 'noImportedMergeCommits': True}
    clean_actual_state = {'baseSha': branch_base, 'noImportedMergeCommits': len(merge_commits) == 0,
                          'milestoneCommitCount': ahead}
    clean_start_pass = clean_start_pass and clean_actual_state['noImportedMergeCommits']
    _record_baseline_case(
        report, output, 'p1.clean-start', expected={'exitCode': 0, 'state': clean_expected_state},
        observed={'exitCode': 0 if clean_start_pass else 1,
                  'state': {'baseSha': clean_actual_state['baseSha'],
                            'noImportedMergeCommits': clean_actual_state['noImportedMergeCommits']}},
        passed=clean_start_pass, command_identity='git rev-parse and merge-base checks',
        argv=['git', 'rev-list', '--merges', f'{merge_sha}..{history_head}'],
        command_exit_code=0 if clean_start_pass else 1, fixture_identity='git-checkout',
        input_fingerprint=_sha256_bytes(_canonical_bytes(clean_actual_state)),
    )
    _persist_command_result(report, output, 'git/branch-establishment.json', {
        'branch': branch, 'branchBaseSha': branch_base, 'originMainSha': current_main,
        'milestoneCommitCount': ahead, 'mergeCommitsSinceBase': merge_commits,
    })
    _refresh_checkpoint_result(report, 'p1-milestone-branch')


def _refresh_checkpoint_result(report: dict[str, Any], checkpoint_id: str) -> None:
    checkpoint = _checkpoint_map(load_contract())[checkpoint_id]
    cases = [item for item in report['cases'] if item['checkpoint'] == checkpoint_id]
    result = next(item for item in report['checkpointResults'] if item['checkpointId'] == checkpoint_id)
    failed = sum(not item['passed'] for item in cases if item['executed'])
    executed = sum(item['executed'] for item in cases)
    skipped = sum(item['skipped'] for item in cases)
    checkpoint_results = {item['checkpointId']: item for item in report['checkpointResults']}
    dependencies_passed = all(checkpoint_results[dependency]['status'] == 'passed'
                              for dependency in checkpoint['dependencies'])
    complete = executed == len(cases) and failed == 0 and skipped == 0
    if failed:
        status = 'failed'
    elif not dependencies_passed:
        status = 'blocked'
    elif executed == 0:
        status = 'unexecuted'
    elif complete:
        status = 'passed'
    else:
        status = 'failed'
    result.update({'status': status, 'discovered': len(cases),
                   'executed': executed, 'failed': failed, 'skipped': skipped,
                   'runnerIds': checkpoint['requiredExecutableVerification'],
                   'commandExitCode': 0 if status == 'passed' else 1})


def _run_verifier_tests(output: Path) -> dict[str, Any]:
    test_path = HERE / 'test_verifier.py'
    argv = [sys.executable, str(test_path), '-v']
    started = dt.datetime.now(dt.timezone.utc).isoformat()
    try:
        completed = subprocess.run(argv, cwd=ROOT, capture_output=True, text=True, timeout=900)
        exit_code, stdout, stderr = completed.returncode, completed.stdout, completed.stderr
    except subprocess.TimeoutExpired as error:
        exit_code = 124
        stdout = error.stdout.decode(errors='replace') if isinstance(error.stdout, bytes) else (error.stdout or '')
        stderr = error.stderr.decode(errors='replace') if isinstance(error.stderr, bytes) else (error.stderr or '')
        stderr += '\nVerifier unit tests timed out after 900 seconds.'
    except OSError as error:
        exit_code, stdout, stderr = 127, '', str(error)
    result = {'runnerId': 'python3 tools/parity/managed_packages/test_verifier.py', 'argv': argv,
              'startedAt': started, 'exitCode': exit_code, 'stdout': stdout, 'stderr': stderr}
    log_path = output / 'verifier-tests.command.json'
    log_path.write_bytes(_canonical_bytes(result))
    result['logPath'] = log_path.relative_to(output).as_posix()
    return result


def _unittest_method_passed(result: dict[str, Any], method: str) -> bool:
    output = result['stdout'] + '\n' + result['stderr']
    return result['exitCode'] == 0 and any(
        method in line and line.rstrip().endswith('... ok') for line in output.splitlines())


def _record_verifier_cases(report: dict[str, Any], output: Path, result: dict[str, Any]) -> None:
    command = result['argv']
    command_identity = 'python3 tools/parity/managed_packages/test_verifier.py'
    if result['logPath']:
        report['artifacts'].append({'path': result['logPath'], 'sha256': _sha256_file(output / result['logPath'])})

    # V0 cases verify the contract, schema, exit-code controls and runtime provenance.
    contract = load_contract()
    runtime = _runtime_state()
    try:
        import fixture_support
    except ImportError:
        sys.path.insert(0, str(HERE))
        import fixture_support
    with tempfile.TemporaryDirectory(prefix='managed-package-schema-control-') as temp_dir:
        valid_fixture = fixture_support.make_valid_evidence(Path(temp_dir))
        schema_errors = _schema_errors(valid_fixture, _load_json(SCHEMA_PATH))
    v0_checks = {
        'v0.schema-valid': {
            'schemaErrors': schema_errors,
            'fixtureBuilt': result['exitCode'] == 0,
        },
        'v0.contract-complete': {
            'requiredCheckpointCount': len(contract['requiredCheckpointIds']),
            'requiredCaseCount': sum(len(item['requiredCaseIds']) for item in contract['checkpoints']),
            'contractHashPinned': True,
        },
        'v0.exit-contract': {
            'controlTestsPassed': all(_unittest_method_passed(result, method)
                                      for method in EXIT_CONTRACT_TEST_METHODS),
            'dependencyOrderEnforced': _unittest_method_passed(
                result, 'test_checkpoint_dependencies_cannot_be_bypassed'),
            'expectedCodes': {'valid': 0, 'mandatoryFailure': 1, 'invalidEvidence': 2,
                              'executionUnavailable': 3},
        },
        'v0.runtime-provenance': {
            'piSha': runtime['piSha'], 'pisharpSourceSha': runtime['headSha'],
            'checkedOutSourceSha': runtime['headSha'], 'branch': runtime['branch'],
            'baseSha': runtime['baseSha'], 'binarySha256': report['provenance']['binaryIdentity']['sha256'],
        },
    }
    v0_pass = {
        'v0.schema-valid': result['exitCode'] == 0 and not schema_errors,
        'v0.contract-complete': result['exitCode'] == 0 and len(contract['requiredCheckpointIds']) == len(contract['checkpoints'])
            and len({case for item in contract['checkpoints'] for case in item['requiredCaseIds']})
            == sum(len(item['requiredCaseIds']) for item in contract['checkpoints']),
        'v0.exit-contract': result['exitCode'] == 0 and all(_unittest_method_passed(result, method)
                                                              for method in EXIT_CONTRACT_TEST_METHODS),
        'v0.runtime-provenance': all(report['provenance'][key] == value for key, value in {
            'piSha': runtime['piSha'], 'pisharpSourceSha': runtime['headSha'],
            'checkedOutSourceSha': runtime['headSha'], 'branch': runtime['branch'],
            'baseSha': runtime['baseSha'],
        }.items()) and _sha256_file(ROOT / report['provenance']['binaryIdentity']['path'])
            == report['provenance']['binaryIdentity']['sha256'],
    }
    for case_id, details in v0_checks.items():
        _record_self_check_case(report, output, case_id, passed=v0_pass[case_id], command=command,
                                command_identity=command_identity, fixture_identity='runtime-provenance',
                                details=details)

    # V1 records each individual negative control, not only the aggregate unittest count.
    for case_id, method in VERIFIER_TEST_METHODS.items():
        passed = _unittest_method_passed(result, method)
        _record_self_check_case(report, output, case_id, passed=passed, command=command,
                                command_identity=command_identity,
                                fixture_identity='synthetic-evidence' if case_id == 'v1.valid-control'
                                else 'tamper-controls',
                                details={'testMethod': method, 'testPassed': passed,
                                         'testRunnerExitCode': result['exitCode']})
    _refresh_checkpoint_result(report, 'verification-infrastructure')
    _refresh_checkpoint_result(report, 'verifier-negative-controls')


def _record_cli_process_cases(report: dict[str, Any], output: Path,
                             runner_results: list[dict[str, Any]]) -> bool:
    report_path = output / 'package-cli.json'
    differential = _load_json(report_path) if report_path.is_file() else {}
    pi_result = differential.get('results', {}).get('pi', {})
    actual_result = differential.get('results', {}).get('pisharp', {})
    runner = next((item for item in runner_results if item.get('runnerId') == 'package-cli'), {})
    differential_pass = (runner.get('exitCode') == 0 and differential.get('match') is True
                         and differential.get('piSha') == load_contract()['upstream']['revision'])
    case_fields = {
        'cli.install': ['npmInstalled', 'userSettingsAfterNpm', 'npmLifecycleCounts'],
        'cli.remove': ['npmRemoveOutputs', 'projectGitRemove', 'emptyList', 'failedRemove'],
        'cli.list': ['userList', 'bothScopesList', 'emptyList'],
        'cli.update': ['gitHeadBeforeUpdate', 'failedUpdate', 'gitHeadAfterFailedUpdate',
                       'gitContentUnchangedAfterFailedUpdate', 'gitSettingsUnchangedAfterFailedUpdate',
                       'gitContentAfterFailedUpdate', 'successfulGitUpdateOutput', 'gitHeadAfterUpdate',
                       'gitLifecycleCountsAfterUpdate'],
        'cli.missing-source': ['missingSourceProbe'],
        'cli.invalid-option': ['invalidOptionProbe'],
        'cli.conflicting-options': ['conflictingOptionsProbe'],
        'cli.local-scope': ['userSettingsAfterNpm', 'projectSettingsAfterGit', 'bothScopesList'],
        'cli.trust-options': ['projectGitInstallOutput', 'projectSettingsAfterGit'],
        'cli.help': ['helpProbe'],
        'cli.spaces-unicode-path': ['spacesUnicodePathProbe'],
        'cli.invalid-package-spec': ['invalidPackageSpecProbe', 'settingsAfterInvalidPackageSpec'],
    }
    all_passed = True
    for case_id, fields in case_fields.items():
        keys_present = all(key in pi_result and key in actual_result for key in fields)
        raw_oracle_state = {key: pi_result.get(key) for key in fields}
        raw_actual_state = {key: actual_result.get(key) for key in fields}
        equivalent_projection = False
        if case_id == 'cli.update' and keys_present:
            comparison_policy = (
                'Compare failed/success exit status, package settings and content preservation, '
                'Git HEAD transition, update status line, and lifecycle effects. The source report '
                'retains complete command output; transient Git transport, progress, and npm diagnostics '
                'are not part of this state comparison.'
            )

            def project_update(state: dict[str, Any]) -> dict[str, Any]:
                failed_update = state['failedUpdate']
                successful_update = state['successfulGitUpdateOutput']
                return {
                    'gitHeadBeforeUpdate': state['gitHeadBeforeUpdate'],
                    'failedUpdate': {'exitCode': failed_update['exitCode'],
                                     'failed': failed_update['exitCode'] != 0},
                    'gitHeadAfterFailedUpdate': state['gitHeadAfterFailedUpdate'],
                    'gitHeadPreservedAfterFailure': (
                        state['gitHeadAfterFailedUpdate'] == state['gitHeadBeforeUpdate']),
                    'gitContentUnchangedAfterFailedUpdate': state['gitContentUnchangedAfterFailedUpdate'],
                    'gitSettingsUnchangedAfterFailedUpdate': state['gitSettingsUnchangedAfterFailedUpdate'],
                    'gitContentAfterFailedUpdate': state['gitContentAfterFailedUpdate'],
                    'successfulGitUpdateOutput': {
                        'exitCode': successful_update['exitCode'],
                        'statusLines': successful_update['statusLines'],
                    },
                    'gitHeadAfterUpdate': state['gitHeadAfterUpdate'],
                    'gitLifecycleCountsAfterUpdate': state['gitLifecycleCountsAfterUpdate'],
                    'comparisonPolicy': comparison_policy,
                }

            oracle_state = project_update(raw_oracle_state)
            actual_state = project_update(raw_actual_state)
            equivalent_projection = raw_oracle_state != raw_actual_state
        else:
            oracle_state = raw_oracle_state
            actual_state = raw_actual_state
        passed = differential_pass and keys_present and oracle_state == actual_state
        _record_baseline_case(
            report, output, case_id, expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else runner.get('exitCode', 1) or 1, 'state': actual_state},
            passed=passed, command_identity='tools/parity/package-cli-process-differential.py',
            argv=runner.get('argv', []), command_exit_code=0 if passed else runner.get('exitCode', 1) or 1,
            fixture_identity='cli-process',
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'reportSha256': _sha256_file(report_path) if report_path.is_file() else None,
                'fields': fields,
            })),
        )
        if case_id == 'cli.help' or (case_id == 'cli.update' and equivalent_projection and passed):
            case = next(item for item in report['cases'] if item['caseId'] == case_id)
            case['classification'] = 'equivalent'
            _write_case_artifact(report, output, case)
        all_passed = all_passed and passed

    idempotent_id = 'npm.install-idempotent'
    idempotent_field = 'npmIdempotentInstall'
    idempotent_present = all(idempotent_field in result for result in (pi_result, actual_result))
    oracle_idempotent = pi_result.get(idempotent_field, {})
    actual_idempotent = actual_result.get(idempotent_field, {})
    idempotent_pass = (differential_pass and idempotent_present
                       and oracle_idempotent == actual_idempotent
                       and oracle_idempotent.get('exitCode') == 0
                       and oracle_idempotent.get('settingsBefore') == oracle_idempotent.get('settingsAfter')
                       and actual_idempotent.get('settingsBefore') == actual_idempotent.get('settingsAfter')
                       and oracle_idempotent.get('sourceOccurrencesAfter') == 1
                       and actual_idempotent.get('sourceOccurrencesAfter') == 1
                       and oracle_idempotent.get('packageInstalled') is True
                       and actual_idempotent.get('packageInstalled') is True)
    _record_baseline_case(
        report, output, idempotent_id,
        expected={'exitCode': 0, 'state': oracle_idempotent},
        observed={'exitCode': 0 if idempotent_pass else runner.get('exitCode', 1) or 1,
                  'state': actual_idempotent},
        passed=idempotent_pass,
        command_identity='tools/parity/package-cli-process-differential.py',
        argv=runner.get('argv', []),
        command_exit_code=0 if idempotent_pass else runner.get('exitCode', 1) or 1,
        fixture_identity='real-npm-processes',
        input_fingerprint=_sha256_bytes(_canonical_bytes({
            'case': idempotent_id, 'piSha': differential.get('piSha'),
            'reportSha256': _sha256_file(report_path) if report_path.is_file() else None,
        })),
    )
    all_passed = all_passed and idempotent_pass

    def scoped_package_installed(state: dict[str, Any]) -> bool:
        return (state.get('settingsContainsSource') is True and state.get('installed') is True
                and state.get('manifest', {}).get('name') == '@fixture/managed-scoped'
                and state.get('manifest', {}).get('version') == '1.0.0')

    def unscoped_packages_installed(state: dict[str, Any]) -> bool:
        return (state.get('installed') is True
                and state.get('names') == ['managed-skill', 'managed-native-extension']
                and all(not source.removeprefix('npm:').startswith('@')
                        for source in state.get('sources', [])))

    for case_id, field, predicate in [
        ('npm.install-scoped', 'scopedPackageInstall', scoped_package_installed),
        ('npm.install-unscoped', 'unscopedPackageInstall', unscoped_packages_installed),
    ]:
        present = all(field in result for result in (pi_result, actual_result))
        oracle_state = pi_result.get(field, {})
        actual_state = actual_result.get(field, {})
        passed = (differential_pass and present and oracle_state == actual_state
                  and predicate(oracle_state) and predicate(actual_state))
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else runner.get('exitCode', 1) or 1,
                      'state': actual_state},
            passed=passed,
            command_identity='tools/parity/package-cli-process-differential.py',
            argv=runner.get('argv', []),
            command_exit_code=0 if passed else runner.get('exitCode', 1) or 1,
            fixture_identity='real-npm-processes',
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'reportSha256': _sha256_file(report_path) if report_path.is_file() else None,
            })),
        )
        all_passed = all_passed and passed

    def npm_install_succeeded(state: dict[str, Any], version: str) -> bool:
        return (state.get('exitCode') == 0 and state.get('sourceConfigured') is True
                and state.get('sourceOccurrences') == 1 and state.get('installed') is True
                and state.get('installedVersion') == version and state.get('expectedVersion') == version)

    def npm_install_failed_without_state(state: dict[str, Any]) -> bool:
        return (state.get('exitCode', 0) != 0 and state.get('sourceConfigured') is False
                and state.get('installed') is False)

    npm_install_cases = [
        ('npm.install-range', 'npmInstallRange', lambda s: npm_install_succeeded(s, '1.1.0')),
        ('npm.install-latest', 'npmInstallLatest', lambda s: npm_install_succeeded(s, '1.0.0')),
        ('npm.install-prerelease', 'npmInstallPrerelease',
         lambda s: npm_install_succeeded(s, '2.0.0-beta.1')),
        ('npm.install-implicit', 'npmInstallImplicit', lambda s: npm_install_succeeded(s, '1.1.0')),
        ('npm.install-existing', 'npmInstallExisting',
         lambda s: npm_install_succeeded(s, '1.0.0') and s.get('preexistingBefore') is True),
        ('npm.install-reinstall', 'npmInstallReinstall',
         lambda s: npm_install_succeeded(s, '1.1.0') and s.get('priorSourceConfigured') is True
                   and s.get('currentVersionSourceCount') == 1),
        ('npm.install-unavailable-version', 'npmInstallUnavailableVersion',
         npm_install_failed_without_state),
        ('npm.install-invalid', 'npmInstallInvalid', npm_install_failed_without_state),
    ]
    for case_id, field, predicate in npm_install_cases:
        present = all(field in result for result in (pi_result, actual_result))
        oracle_state = pi_result.get(field, {})
        actual_state = actual_result.get(field, {})
        passed = (differential_pass and present and oracle_state == actual_state
                  and predicate(oracle_state) and predicate(actual_state))
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else runner.get('exitCode', 1) or 1,
                      'state': actual_state},
            passed=passed,
            command_identity='tools/parity/package-cli-process-differential.py',
            argv=runner.get('argv', []),
            command_exit_code=0 if passed else runner.get('exitCode', 1) or 1,
            fixture_identity='local-npm-registry',
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'reportSha256': _sha256_file(report_path) if report_path.is_file() else None,
            })),
        )
        all_passed = all_passed and passed

    script_failure_id = 'npm.install-script-failure'
    script_failure_field = 'npmScriptFailure'
    script_failure_present = all(script_failure_field in result for result in (pi_result, actual_result))
    oracle_script_failure = pi_result.get(script_failure_field, {})
    actual_script_failure = actual_result.get(script_failure_field, {})

    def failed_script_install_preserves_state(state: dict[str, Any]) -> bool:
        return (state.get('exitCode', 0) != 0
                and state.get('settingsUnchanged') is True
                and state.get('sourceDeclared') is False
                and state.get('packageDirectoryExists') is False
                and state.get('postinstallReached') is True)

    script_failure_pass = (differential_pass and script_failure_present
                           and oracle_script_failure == actual_script_failure
                           and failed_script_install_preserves_state(oracle_script_failure)
                           and failed_script_install_preserves_state(actual_script_failure))
    _record_baseline_case(
        report, output, script_failure_id,
        expected={'exitCode': 0, 'state': oracle_script_failure},
        observed={'exitCode': 0 if script_failure_pass else runner.get('exitCode', 1) or 1,
                  'state': actual_script_failure},
        passed=script_failure_pass,
        command_identity='tools/parity/package-cli-process-differential.py',
        argv=runner.get('argv', []),
        command_exit_code=0 if script_failure_pass else runner.get('exitCode', 1) or 1,
        fixture_identity='real-npm-processes',
        input_fingerprint=_sha256_bytes(_canonical_bytes({
            'case': script_failure_id, 'piSha': differential.get('piSha'),
            'reportSha256': _sha256_file(report_path) if report_path.is_file() else None,
        })),
    )
    all_passed = all_passed and script_failure_pass

    cancelled_id = 'npm.install-cancelled'
    cancelled_field = 'npmInstallCancelled'
    cancelled_present = all(cancelled_field in result for result in (pi_result, actual_result))
    oracle_cancelled = pi_result.get(cancelled_field, {})
    actual_cancelled = actual_result.get(cancelled_field, {})

    def cancelled_install_preserves_declaration(state: dict[str, Any]) -> bool:
        return (state.get('cancelled') is True and state.get('settingsUnchanged') is True
                and state.get('sourceConfigured') is False and state.get('postinstallReached') is True)

    cancelled_pass = (differential_pass and cancelled_present and oracle_cancelled == actual_cancelled
                      and cancelled_install_preserves_declaration(oracle_cancelled)
                      and cancelled_install_preserves_declaration(actual_cancelled))
    _record_baseline_case(
        report, output, cancelled_id,
        expected={'exitCode': 0, 'state': oracle_cancelled},
        observed={'exitCode': 0 if cancelled_pass else runner.get('exitCode', 1) or 1,
                  'state': actual_cancelled},
        passed=cancelled_pass,
        command_identity='tools/parity/package-cli-process-differential.py',
        argv=runner.get('argv', []),
        command_exit_code=0 if cancelled_pass else runner.get('exitCode', 1) or 1,
        fixture_identity='local-npm-registry',
        input_fingerprint=_sha256_bytes(_canonical_bytes({
            'case': cancelled_id, 'piSha': differential.get('piSha'),
            'reportSha256': _sha256_file(report_path) if report_path.is_file() else None,
        })),
    )
    all_passed = all_passed and cancelled_pass
    _refresh_checkpoint_result(report, 'cli')
    return all_passed


def _record_managed_npm_cases(report: dict[str, Any], output: Path,
                              runner_results: list[dict[str, Any]]) -> bool:
    """Record distinct install and resource assertions from the pinned npm process differential."""
    report_path = output / 'managed-npm.json'
    differential = _load_json(report_path) if report_path.is_file() else {}
    pi = differential.get('pi', {})
    actual = differential.get('pisharp', {})
    counts = differential.get('processCounts', {})
    pi_counts = counts.get('pi', {})
    actual_counts = counts.get('pisharp', {})
    runner = next((item for item in runner_results if item.get('runnerId') == 'managed-npm'), {})
    differential_pass = (runner.get('exitCode') == 0 and differential.get('match') is True
                         and differential.get('piSha') == load_contract()['upstream']['revision'])
    fixture_identity = 'real-npm-processes'
    fixture = differential.get('fixture', {})
    pi_installed = pi.get('installedPackage', {})
    actual_installed = actual.get('installedPackage', {})
    cases: dict[str, tuple[dict[str, Any], dict[str, Any], bool, str]] = {
        'npm.install-exact': (
            {'fixturePackage': fixture.get('package'), 'installSpec': fixture.get('installSpec'),
             'installedPackage': pi_installed},
            {'fixturePackage': fixture.get('package'), 'installSpec': fixture.get('installSpec'),
             'installedPackage': actual_installed},
            fixture.get('package') == 'managed-parity@1.0.0'
            and fixture.get('installSpec') == 'npm:<name>@file:<local-tarball>'
            and pi_installed == actual_installed == {'name': 'managed-parity', 'version': '1.0.0'}
            and differential.get('comparisons', {}).get('installedPackage') is True,
            'npm-install'),
        'npm.install-dependencies': (
            {'dependencyInstalled': pi.get('dependencyInstalled'),
             'dependencyInstallScriptRuns': pi_counts.get('dependencyInstallScriptRuns')},
            {'dependencyInstalled': actual.get('dependencyInstalled'),
             'dependencyInstallScriptRuns': actual_counts.get('dependencyInstallScriptRuns')},
            bool(pi.get('dependencyInstalled') and actual.get('dependencyInstalled')
                 and pi_counts.get('dependencyInstallScriptRuns') == 1
                 and actual_counts.get('dependencyInstallScriptRuns') == 1),
            'npm-install'),
        'npm.install-peer-dependencies': (
            {'peerInstalled': pi.get('peerInstalled')},
            {'peerInstalled': actual.get('peerInstalled')},
            pi.get('peerInstalled') is False and actual.get('peerInstalled') is False,
            'npm-install'),
        'npm.install-lifecycle-scripts': (
            {'packageInstallScriptRuns': pi_counts.get('packageInstallScriptRuns'),
             'dependencyInstallScriptRuns': pi_counts.get('dependencyInstallScriptRuns')},
            {'packageInstallScriptRuns': actual_counts.get('packageInstallScriptRuns'),
             'dependencyInstallScriptRuns': actual_counts.get('dependencyInstallScriptRuns')},
            pi_counts.get('packageInstallScriptRuns') == 1
            and actual_counts.get('packageInstallScriptRuns') == 1
            and pi_counts.get('dependencyInstallScriptRuns') == 1
            and actual_counts.get('dependencyInstallScriptRuns') == 1,
            'npm-install'),
    }

    pi_startup = pi.get('resolved', {}).get('startup', {})
    actual_passes = actual.get('resourcePasses', [])
    actual_startup = actual_passes[0] if actual_passes else {}
    source = next((entry.get('source') for entry in pi_startup.get('skills', [])), None)

    def summarize(entries: list[dict[str, Any]]) -> list[dict[str, Any]]:
        return [{key: entry.get(key) for key in ('path', 'enabled', 'source', 'scope', 'origin')}
                | {'baseDir': '' if entry.get('baseDir') == '.' else entry.get('baseDir')}
                for entry in entries]

    pi_skills = summarize(pi_startup.get('skills', []))
    actual_skills = summarize([entry for entry in actual_startup.get('skills', [])
                               if entry.get('source') == source])
    pi_prompts = summarize(pi_startup.get('prompts', []))
    actual_prompts = summarize([entry for entry in actual_startup.get('prompts', [])
                                if entry.get('source') == source])
    pi_extensions = [{**entry, 'path': entry.get('path', '').removesuffix('.js') + '.<module>'}
                     for entry in pi_startup.get('extensions', [])]
    actual_extensions = [{'enabled': True}
                         | {key: entry.get(key) for key in ('source', 'scope', 'origin')}
                         | {'path': entry.get('path', '').removesuffix('.dll') + '.<module>',
                            'baseDir': '' if entry.get('baseDir') == '.' else entry.get('baseDir')}
                         for entry in (actual.get('extensionPasses', [[]])[0])]
    pi_themes = [{'path': entry.get('path'), 'exists': True} for entry in pi_startup.get('themes', [])]
    actual_theme = actual.get('theme', {})
    actual_themes = [{'path': actual_theme.get('path'), 'exists': actual_theme.get('exists')}] \
        if actual_theme else []
    resource_views = {
        'distribution.npm': (
            {'package': differential.get('fixture', {}).get('package'),
             'installSpec': differential.get('fixture', {}).get('installSpec'),
             'startupResources': {
                 'skills': [entry.get('path') for entry in pi_skills],
                 'prompts': [entry.get('path') for entry in pi_prompts],
                 'themes': [entry.get('path') for entry in pi_themes],
                 'extensions': [entry.get('path') for entry in pi_extensions],
             }},
            {'package': differential.get('fixture', {}).get('package'),
             'installSpec': differential.get('fixture', {}).get('installSpec'),
             'startupResources': {
                 'skills': [entry.get('path') for entry in actual_skills],
                 'prompts': [entry.get('path') for entry in actual_prompts],
                 'themes': [entry.get('path') for entry in actual_themes],
                 'extensions': [entry.get('path') for entry in actual_extensions],
             }},
            differential.get('match') is True and bool(source),
        ),
        'distribution.skill': (pi_skills, actual_skills,
                               differential.get('comparisons', {}).get('skills') is True),
        'distribution.prompt': (pi_prompts, actual_prompts,
                                differential.get('comparisons', {}).get('prompts') is True),
        'distribution.theme': (pi_themes, actual_themes,
                               differential.get('comparisons', {}).get('themes') is True),
        'distribution.native-extension': (
            {'extensions': pi_extensions, 'loads': pi_counts.get('extensionLoads')},
            {'extensions': actual_extensions, 'loads': actual_counts.get('extensionLoads')},
            differential.get('comparisons', {}).get('extensions') is True
            and pi_counts.get('extensionLoads') == actual_counts.get('extensionLoads') == 2),
        'distribution.source-metadata': (
            {'skills': [{key: entry.get(key) for key in ('source', 'scope', 'origin', 'baseDir')}
                        for entry in pi_skills],
             'prompts': [{key: entry.get(key) for key in ('source', 'scope', 'origin', 'baseDir')}
                         for entry in pi_prompts],
             'extensions': [{key: entry.get(key) for key in ('source', 'scope', 'origin', 'baseDir')}
                            for entry in pi_extensions]},
            {'skills': [{key: entry.get(key) for key in ('source', 'scope', 'origin', 'baseDir')}
                        for entry in actual_skills],
             'prompts': [{key: entry.get(key) for key in ('source', 'scope', 'origin', 'baseDir')}
                         for entry in actual_prompts],
             'extensions': [{key: entry.get(key) for key in ('source', 'scope', 'origin', 'baseDir')}
                            for entry in actual_extensions]},
            differential.get('match') is True,
        ),
    }
    for case_id, (oracle_state, actual_state, assertion) in resource_views.items():
        cases[case_id] = (oracle_state, actual_state, assertion, 'resource-distribution')

    all_passed = True
    artifact_hash = _sha256_file(report_path) if report_path.is_file() else None
    for case_id, (oracle_state, actual_state, assertion, checkpoint) in cases.items():
        passed = differential_pass and assertion and oracle_state == actual_state
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else runner.get('exitCode', 1) or 1,
                      'state': actual_state},
            passed=passed,
            command_identity='tools/parity/managed-npm-process-differential.py',
            argv=runner.get('argv', []), command_exit_code=0 if passed else runner.get('exitCode', 1) or 1,
            fixture_identity=fixture_identity if checkpoint == 'npm-install' else 'local-npm-git-resource-fixtures',
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'), 'reportSha256': artifact_hash,
            })),
        )
        all_passed = all_passed and passed
    _refresh_checkpoint_result(report, 'npm-install')
    _refresh_checkpoint_result(report, 'resource-distribution')
    return all_passed


def _record_npm_update_cases(report: dict[str, Any], output: Path,
                             runner_results: list[dict[str, Any]]) -> bool:
    """Independently validate the mutable-registry npm update process matrix."""
    report_path = output / 'managed-npm-update.json'
    differential = _load_json(report_path) if report_path.is_file() else {}
    runner = next((item for item in runner_results if item.get('runnerId') == 'managed-npm-update'), {})
    pinned_pi_sha = load_contract()['upstream']['revision']
    source_sha = report['provenance']['pisharpSourceSha']
    runner_pass = (runner.get('exitCode') == 0 and differential.get('match') is True
                   and differential.get('piSha') == pinned_pi_sha
                   and differential.get('pisharpSha') == source_sha)
    artifact_sha = _sha256_file(report_path) if report_path.is_file() else None

    strict_expected = {
        'npm.update-pinned': {
            'initialVersion': '1.0.0', 'finalVersion': '1.0.0', 'sourceConfigured': True,
            'settingsUnchanged': True, 'updateExitCode': 0,
        },
        'npm.update-range': {
            'initialVersion': '1.0.0', 'finalVersion': '1.1.0', 'sourceConfigured': True,
            'settingsUnchanged': True, 'updateExitCode': 0,
        },
        'npm.update-latest': {
            'initialVersion': '1.0.0', 'finalVersion': '1.1.0', 'sourceConfigured': True,
            'settingsUnchanged': True, 'updateExitCode': 0,
        },
        'npm.update-no-change': {
            'initialVersion': '1.0.0', 'finalVersion': '1.0.0', 'sourceConfigured': True,
            'settingsUnchanged': True, 'updateExitCode': 0,
        },
        'npm.update-failure': {
            'initialVersion': '1.0.0', 'finalVersion': '1.0.0', 'sourceConfigured': True,
            'settingsUnchanged': True, 'updateExitCode': 1,
        },
        'npm.update-repeat': {
            'initialVersion': '1.0.0', 'finalVersion': '1.1.0', 'sourceConfigured': True,
            'settingsUnchanged': True, 'updateExitCode': 0, 'repeatExitCode': 0,
            'versionAfterRepeat': '1.1.0',
        },
        'npm.update-unpinned': {
            'initialVersion': '1.0.0', 'finalVersion': '1.1.0', 'sourceConfigured': True,
            'settingsUnchanged': True, 'updateExitCode': 0,
        },
        'npm.update-prerelease': {
            'initialVersion': '2.0.0-beta.1', 'finalVersion': '2.0.0-beta.2',
            'sourceConfigured': True, 'settingsUnchanged': True, 'updateExitCode': 0,
        },
        'npm.update-registry-change': {
            'initialVersion': '1.0.0', 'finalVersion': '1.1.0', 'sourceConfigured': True,
            'settingsUnchanged': True, 'updateExitCode': 0,
        },
    }
    for case_id, expected_state in strict_expected.items():
        expected_state.update({
            'registryChanged': case_id == 'npm.update-registry-change',
            'registryUpdated': case_id not in ('npm.update-no-change', 'npm.update-failure',
                                               'npm.update-registry-change'),
            'registryFailed': case_id == 'npm.update-failure',
        })
    interrupted_base = {
        'cancelled': True, 'initialVersion': '1.0.0',
        'sourceConfigured': True, 'settingsUnchanged': True, 'downloadStarted': True,
    }
    safety_expected = {
        'npm.update-cancelled': (
            {**interrupted_base, 'finalVersion': None, 'restartExitCode': 0, 'versionAfterRestart': '1.0.0'},
            {**interrupted_base, 'finalVersion': '1.0.0', 'restartExitCode': 0, 'versionAfterRestart': '1.0.0'},
        ),
        'npm.update-interrupted': (
            {**interrupted_base, 'finalVersion': None, 'restartExitCode': 1, 'versionAfterRestart': None},
            {**interrupted_base, 'finalVersion': '1.0.0', 'restartExitCode': 0, 'versionAfterRestart': '1.0.0'},
        ),
    }
    expected_ids = set(strict_expected) | set(safety_expected)
    cases = differential.get('cases', {})
    if set(cases) != expected_ids:
        runner_pass = False

    all_passed = True
    for case_id in sorted(expected_ids):
        observed_case = cases.get(case_id, {})
        oracle_state = observed_case.get('oracle')
        actual_state = observed_case.get('actual')
        assertions_pass = False
        classification = 'strict'
        if case_id in strict_expected:
            expected_state = strict_expected[case_id]
            assertions_pass = (oracle_state == expected_state and actual_state == expected_state
                               and observed_case.get('passed') is True)
        else:
            expected_oracle, expected_actual = safety_expected[case_id]
            classification = 'intentional-safety-difference'
            assertions_pass = (
                oracle_state == expected_oracle and actual_state == expected_actual
                and observed_case.get('passed') is True
                and actual_state.get('initialVersion') == oracle_state.get('initialVersion')
                and actual_state.get('finalVersion') == actual_state.get('initialVersion')
                and oracle_state.get('restartExitCode') in (0, 1)
                and actual_state.get('restartExitCode') == 0
                and actual_state.get('versionAfterRestart') == actual_state.get('finalVersion')
                and (oracle_state.get('versionAfterRestart') is None
                     or oracle_state.get('versionAfterRestart') == oracle_state.get('initialVersion'))
                and actual_state.get('downloadStarted') is True
            )
        passed = runner_pass and assertions_pass
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if runner.get('exitCode') == 0 else runner.get('exitCode', 1),
                      'state': actual_state},
            passed=passed,
            command_identity='tools/parity/managed-npm-update-process-differential.py',
            argv=runner.get('argv', []),
            command_exit_code=runner.get('exitCode', 1),
            fixture_identity='mutable-local-npm-registry',
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'pisharpSha': differential.get('pisharpSha'), 'reportSha256': artifact_sha,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = classification if passed else 'mismatch'
        recorded['assertions'] = {case_id: bool(assertions_pass)}
        recorded['passed'] = passed
        _write_case_artifact(report, output, recorded)
        all_passed = all_passed and passed

    _refresh_checkpoint_result(report, 'npm-update')
    return all_passed


def _record_failure_recovery_cases(report: dict[str, Any], output: Path,
                                   runner_results: list[dict[str, Any]],
                                   test_group_results: dict[str, dict[str, Any]]) -> bool:
    """Record failed installs, cancellation, staged replacement and restart recovery."""
    contract = load_contract()
    checkpoint = _checkpoint_map(contract)['failure-recovery']
    source_sha = report['provenance']['pisharpSourceSha']
    pi_sha = contract['upstream']['revision']

    def load_evidence(path: Path) -> tuple[dict[str, Any], str | None]:
        value = _load_json(path) if path.is_file() else {}
        digest = _sha256_file(path) if path.is_file() else None
        if digest is not None:
            relative = path.relative_to(output).as_posix()
            report['artifacts'] = [item for item in report['artifacts'] if item['path'] != relative]
            report['artifacts'].append({'path': relative, 'sha256': digest})
        return value, digest

    failure_path = output / 'managed-package-failure.json'
    failure, failure_sha = load_evidence(failure_path)
    failure_runner = next((item for item in runner_results if item.get('runnerId') == 'failure-recovery'), {})
    failure_provenance = (failure.get('piSha') == pi_sha
                          and failure.get('pisharpSourceSha') == source_sha
                          and failure.get('fixtureVersion') == 'managed-package-install-failures-v1')
    failure_runner_pass = (failure_runner.get('exitCode') == 0 and failure_provenance
                           and failure.get('match') is True
                           and set(failure.get('cases', {})) == {
                               'failure.registry', 'failure.git-remote', 'failure.invalid-archive',
                               'failure.lifecycle-script', 'failure.dependency-install',
                               'failure.permission', 'failure.cancelled-install'})

    npm_path = output / 'managed-npm-update.json'
    npm_update, npm_sha = load_evidence(npm_path)
    npm_runner = next((item for item in runner_results if item.get('runnerId') == 'managed-npm-update'), {})
    npm_provenance = (npm_update.get('piSha') == pi_sha
                      and npm_update.get('pisharpSha') == source_sha
                      and npm_update.get('scenario') == 'mutable-local-npm-registry-update-processes')
    npm_runner_pass = (npm_runner.get('exitCode') == 0 and npm_provenance
                       and npm_update.get('match') is True)

    git_path = output / 'managed-git-update.json'
    git_update, git_sha = load_evidence(git_path)
    git_runner = next((item for item in runner_results if item.get('runnerId') == 'managed-git-update'), {})
    git_case = git_update.get('cases', {}).get('git.update-cancelled', {})
    git_provenance = (git_update.get('piSha') == pi_sha
                      and git_update.get('piSharpBaseSha') == source_sha
                      and git_update.get('fixtureVersion') == 'managed-git-update-v1')
    git_runner_pass = (git_runner.get('exitCode') == 0 and git_provenance
                       and git_update.get('match') is True
                       and git_case.get('oracle', {}).get('contractSatisfied') is True
                       and git_case.get('actual', {}).get('contractSatisfied') is True)

    config_path = output / 'managed-package-configuration.json'
    configuration, config_sha = load_evidence(config_path)
    config_runner = next((item for item in runner_results if item.get('runnerId') == 'configuration-trust'), {})
    config_case = configuration.get('cases', {}).get('config.failed-write', {})
    config_provenance = (configuration.get('piSha') == pi_sha
                         and configuration.get('piSharpBaseSha') == source_sha
                         and configuration.get('fixtureVersion') == 'managed-package-configuration-v1')
    config_runner_pass = (config_runner.get('exitCode') == 0 and config_provenance
                          and configuration.get('match') is True
                          and config_case.get('safetyDifferenceAccepted') is True
                          and config_case.get('contractSatisfied') is True)

    def test_group_passed(case_id: str, method_name: str) -> tuple[dict[str, Any], bool]:
        result = test_group_results.get(case_id, {})
        trx_path = output / result['trx'] if result.get('trx') else Path('/missing')
        outcomes = _trx_test_outcomes(trx_path)
        method_passed = any(name.split('(', 1)[0].endswith('.' + method_name) and outcome == 'Passed'
                            for name, outcome in outcomes.items())
        passed = (result.get('exitCode') == 0 and result.get('counts', {}).get('discovered', 0) > 0
                  and result.get('counts', {}).get('failed') == 0
                  and result.get('counts', {}).get('skipped') == 0 and method_passed)
        return result, passed

    npm_tests, npm_cancel_test_pass = test_group_passed(
        'baseline.managed-npm', 'CancellingNpmInstallKillsAndAwaitsLifecycleChildWithoutRetry')
    git_tests, git_cancel_test_pass = test_group_passed(
        'baseline.git', 'OfflineMissingDeltaAndFailedOrCancelledUpdatesKeepTheLastUsableCheckout')

    npm_cases = npm_update.get('cases', {})
    interrupted = npm_cases.get('npm.update-interrupted', {})
    interrupted_oracle = interrupted.get('oracle')
    interrupted_actual = interrupted.get('actual')
    interrupted_valid = (interrupted.get('passed') is True
                         and isinstance(interrupted_oracle, dict) and isinstance(interrupted_actual, dict)
                         and interrupted_oracle.get('finalVersion') is None
                         and interrupted_oracle.get('restartExitCode') == 1
                         and interrupted_oracle.get('versionAfterRestart') is None
                         and interrupted_actual.get('finalVersion') == '1.0.0'
                         and interrupted_actual.get('restartExitCode') == 0
                         and interrupted_actual.get('versionAfterRestart') == '1.0.0'
                         and interrupted_actual.get('sourceConfigured') is True
                         and interrupted_actual.get('settingsUnchanged') is True)
    cancellation = git_case.get('oracle'), git_case.get('actual')
    cancellation_valid = (git_case.get('equal') is True
                          and all(isinstance(state, dict) and state.get('contractSatisfied') is True
                                  and state.get('operationStarted') is True
                                  and state.get('processReaped') is True
                                  and state.get('packageExists') is True
                                  and state.get('contentUnchanged') is True
                                  and state.get('settingsUnchanged') is True
                                  and state.get('sourceConfigured') is True
                                  for state in cancellation))
    settings_oracle, settings_actual = config_case.get('oracle'), config_case.get('actual')
    settings_valid = (isinstance(settings_oracle, dict) and isinstance(settings_actual, dict)
                      and settings_oracle.get('installExitCode') == 0
                      and settings_actual.get('installExitCode') == 1
                      and settings_oracle.get('configured') is False
                      and settings_actual.get('configured') is False
                      and settings_oracle.get('packageInstalled') is True
                      and settings_actual.get('packageInstalled') is True
                      and settings_oracle.get('settingsPathRemainsDirectory') is True
                      and settings_actual.get('settingsPathRemainsDirectory') is True)

    direct_cases = failure.get('cases', {})
    case_specs: dict[str, tuple[dict[str, Any], dict[str, Any], bool, str, list[str], int, str, str | None]] = {}
    for case_id in ('failure.registry', 'failure.git-remote', 'failure.invalid-archive',
                    'failure.lifecycle-script', 'failure.dependency-install', 'failure.permission'):
        item = direct_cases.get(case_id, {})
        oracle_state, actual_state = item.get('pi'), item.get('pisharp')
        passed = (failure_runner_pass and item.get('contractSatisfied') is True
                  and isinstance(oracle_state, dict) and isinstance(actual_state, dict))
        case_specs[case_id] = (oracle_state or {}, actual_state or {}, passed,
                               'tools/parity/managed-package-failure-process-differential.py',
                               failure_runner.get('argv', []), failure_runner.get('exitCode', 1),
                               'deterministic-failure-injection', failure_sha)

    cancelled_install = direct_cases.get('failure.cancelled-install', {})
    cancelled_install_oracle = cancelled_install.get('pi')
    cancelled_install_actual = cancelled_install.get('pisharp')
    case_specs['failure.cancelled-install'] = (
        cancelled_install_oracle or {}, cancelled_install_actual or {},
        failure_runner_pass and cancelled_install.get('contractSatisfied') is True and npm_cancel_test_pass,
        'ManagedResourcePackageStoreTests', npm_tests.get('argv', []), npm_tests.get('exitCode', 1),
        'deterministic-failure-injection', failure_sha)

    case_specs['failure.settings-publication'] = (
        settings_oracle or {}, settings_actual or {},
        config_runner_pass and settings_valid,
        'tools/parity/managed-package-configuration-process-differential.py',
        config_runner.get('argv', []), config_runner.get('exitCode', 1),
        'disposable-package-roots', config_sha)
    case_specs['failure.cancelled-update'] = (
        cancellation[0] or {}, cancellation[1] or {},
        git_runner_pass and cancellation_valid and git_cancel_test_pass,
        'ManagedGitPackageStoreTests', git_tests.get('argv', []), git_tests.get('exitCode', 1),
        'deterministic-failure-injection', git_sha)
    case_specs['failure.interrupted-staging'] = (
        interrupted_oracle or {}, interrupted_actual or {},
        npm_runner_pass and interrupted_valid,
        'tools/parity/managed-npm-update-process-differential.py',
        npm_runner.get('argv', []), npm_runner.get('exitCode', 1),
        'disposable-package-roots', npm_sha)
    case_specs['failure.restart-recovery'] = (
        ({'restartExitCode': (interrupted_oracle or {}).get('restartExitCode'),
          'versionAfterRestart': (interrupted_oracle or {}).get('versionAfterRestart'),
          'initialVersion': (interrupted_oracle or {}).get('initialVersion') }),
        ({'restartExitCode': (interrupted_actual or {}).get('restartExitCode'),
          'versionAfterRestart': (interrupted_actual or {}).get('versionAfterRestart'),
          'initialVersion': (interrupted_actual or {}).get('initialVersion') }),
        npm_runner_pass and interrupted_valid,
        'tools/parity/managed-npm-update-process-differential.py',
        npm_runner.get('argv', []), npm_runner.get('exitCode', 1),
        'disposable-package-roots', npm_sha)

    safety_case_ids = {'failure.cancelled-install', 'failure.settings-publication',
                       'failure.interrupted-staging', 'failure.restart-recovery'}
    all_passed = True
    for case_id in checkpoint['requiredCaseIds']:
        oracle_state, actual_state, passed, command_identity, argv, command_exit_code, fixture, artifact_sha = case_specs[case_id]
        if case_id in safety_case_ids:
            classification = 'intentional-safety-difference'
        else:
            classification = 'strict'
            passed = passed and oracle_state == actual_state
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else (command_exit_code or 1), 'state': actual_state},
            passed=passed, command_identity=command_identity, argv=argv,
            command_exit_code=command_exit_code, fixture_identity=fixture,
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': pi_sha, 'pisharpSourceSha': source_sha,
                'sourceArtifactSha256': artifact_sha,
                'supportingArtifacts': {'failure': failure_sha, 'npmUpdate': npm_sha,
                                       'gitUpdate': git_sha, 'configuration': config_sha},
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = classification
        recorded['assertions'] = {case_id: bool(passed)}
        recorded['passed'] = bool(passed)
        _write_case_artifact(report, output, recorded)
        all_passed = all_passed and bool(passed)
    _refresh_checkpoint_result(report, 'failure-recovery')
    return all_passed


def _is_same_npm_install_safety_difference(observed_case: dict[str, Any]) -> bool:
    """Accept only Pi's reproduced concurrent npm target race with a safe PiSharp result."""
    observations = observed_case.get('observations', {})
    pi_detail = observations.get('pi', {})
    actual_detail = observations.get('pisharp', {})
    return (
        observed_case.get('equal') is False
        and observed_case.get('oracle') != observed_case.get('actual')
        and pi_detail.get('npmTargetInstallRace') is True
        and pi_detail.get('commandOutcomeClasses') in (['failure', 'failure'], ['failure', 'success'])
        and pi_detail.get('allProcessesReaped') is True
        and pi_detail.get('boundedSeconds') is True
        and pi_detail.get('settingsValid') is True
        and pi_detail.get('sourceOccurrences', 0) <= 1
        and not pi_detail.get('updateArtifacts')
        and actual_detail.get('allProcessesReaped') is True
        and actual_detail.get('boundedSeconds') is True
        and actual_detail.get('settingsValid') is True
        and actual_detail.get('sourceConfigured') is True
        and actual_detail.get('sourceOccurrences') == 1
        and actual_detail.get('packageRootPresent') is True
        and actual_detail.get('installedVersion') == actual_detail.get('expectedVersion')
        and actual_detail.get('packageManifestValid') is True
        and not actual_detail.get('updateArtifacts')
        and actual_detail.get('commandOutcomeClasses') == ['success', 'success']
    )


def _is_same_git_install_safety_difference(observed_case: dict[str, Any]) -> bool:
    """Accept reproduced Pi clone races only with a safe PiSharp result."""
    observations = observed_case.get('observations', {})
    pi_detail = observations.get('pi', {})
    actual_detail = observations.get('pisharp', {})
    oracle_state = observed_case.get('oracle')
    actual_state = observed_case.get('actual')
    collision_errors = pi_detail.get('cloneCollisionErrors', [])
    collision_proven = (isinstance(collision_errors, list)
                        and all(isinstance(error, str) for error in collision_errors)
                        and any('fatal: destination path ' in error
                                and 'already exists and is not an empty directory' in error
                                and 'Error: git clone ' in error for error in collision_errors)
                        and any("fatal: cannot copy '/usr/share/git-core/templates/" in error
                                and 'No such file or directory' in error and 'Error: git clone ' in error
                                for error in collision_errors))
    return (
        observed_case.get('equal') is False
        and oracle_state != actual_state
        and (pi_detail.get('originHeadCloneRace') is True
             or (pi_detail.get('cloneDestinationCollision') is True and collision_proven))
        and pi_detail.get('commandOutcomeClasses') == ['failure', 'failure']
        and pi_detail.get('allProcessesReaped') is True
        and pi_detail.get('boundedSeconds') is True
        and pi_detail.get('settingsValid') is True
        and pi_detail.get('sourceConfigured') is False
        and pi_detail.get('sourceOccurrences') == 0
        and pi_detail.get('packageRootPresent') is False
        and pi_detail.get('installedVersion') is None
        and pi_detail.get('packageManifestValid') is False
        and actual_detail.get('allProcessesReaped') is True
        and actual_detail.get('boundedSeconds') is True
        and actual_detail.get('settingsValid') is True
        and actual_detail.get('sourceConfigured') is True
        and actual_detail.get('sourceOccurrences') == 1
        and actual_detail.get('packageRootPresent') is True
        and actual_detail.get('installedVersion') == actual_detail.get('expectedVersion')
        and actual_detail.get('packageManifestValid') is True
        and 'success' in actual_detail.get('commandOutcomeClasses', [])
    )


def _record_concurrency_cases(report: dict[str, Any], output: Path,
                              runner_results: list[dict[str, Any]],
                              test_group_results: dict[str, dict[str, Any]]) -> bool:
    """Validate barrier-controlled process races, terminal integrity and the .NET regression."""
    contract = load_contract()
    checkpoint = _checkpoint_map(contract)['concurrency']
    source_sha = report['provenance']['pisharpSourceSha']
    pi_sha = contract['upstream']['revision']
    report_path = output / 'managed-package-concurrency.json'
    differential = _load_json(report_path) if report_path.is_file() else {}
    report_sha = _sha256_file(report_path) if report_path.is_file() else None
    if report_sha is not None:
        relative = report_path.relative_to(output).as_posix()
        report['artifacts'] = [item for item in report['artifacts'] if item['path'] != relative]
        report['artifacts'].append({'path': relative, 'sha256': report_sha})
    runner = next((item for item in runner_results if item.get('runnerId') == 'concurrency'), {})
    provenance_pass = (differential.get('piSha') == pi_sha
                       and differential.get('pisharpSourceSha') == source_sha
                       and differential.get('fixtureVersion') == 'managed-package-concurrency-v1'
                       and differential.get('scenario') == 'barrier-controlled-managed-package-processes')
    cases = differential.get('cases', {})
    runner_pass = (runner.get('exitCode') == 0 and provenance_pass
                   and differential.get('match') is True
                   and set(cases) == set(checkpoint['requiredCaseIds']))

    dotnet = test_group_results.get('baseline.package-cli', {})
    trx_path = output / dotnet['trx'] if dotnet.get('trx') else Path('/missing')
    outcomes = _trx_test_outcomes(trx_path)
    concurrency_test_pass = (dotnet.get('exitCode') == 0
                             and dotnet.get('counts', {}).get('discovered', 0) > 0
                             and dotnet.get('counts', {}).get('failed') == 0
                             and dotnet.get('counts', {}).get('skipped') == 0
                             and any(name.split('(', 1)[0].endswith(
                                 '.ConcurrentProcessesInstallTheSameNpmPackageWithoutDuplicatingItsDeclaration')
                                     and result == 'Passed' for name, result in outcomes.items()))

    all_passed = True
    for case_id in checkpoint['requiredCaseIds']:
        observed_case = cases.get(case_id, {})
        oracle_state, actual_state = observed_case.get('oracle'), observed_case.get('actual')
        classification = observed_case.get('classification', 'mismatch')
        states_present = isinstance(oracle_state, dict) and isinstance(actual_state, dict)
        direct_pass = (observed_case.get('contractSatisfied') is True and states_present)
        if classification == 'strict':
            assertions_pass = direct_pass and observed_case.get('equal') is True and oracle_state == actual_state
        elif classification == 'intentional-safety-difference' and case_id == 'concurrent.update-discovery':
            details = observed_case.get('observations', {})
            pi_detail, actual_detail = details.get('pi', {}), details.get('pisharp', {})
            assertions_pass = (direct_pass and oracle_state != actual_state
                and pi_detail.get('versionDuringDiscovery') is None
                and pi_detail.get('finalVersion') == '1.1.0'
                and pi_detail.get('listExitCode') == pi_detail.get('updateExitCode') == 0
                and actual_detail.get('versionDuringDiscovery') == '1.0.0'
                and actual_detail.get('finalVersion') == '1.1.0'
                and actual_detail.get('listExitCode') == actual_detail.get('updateExitCode') == 0)
        elif classification == 'intentional-safety-difference' and case_id == 'concurrent.same-npm-install':
            assertions_pass = direct_pass and _is_same_npm_install_safety_difference(observed_case)
        elif classification == 'intentional-safety-difference' and case_id == 'concurrent.same-git-install':
            assertions_pass = direct_pass and _is_same_git_install_safety_difference(observed_case)
        else:
            assertions_pass = False
        if case_id == 'concurrent.same-npm-install':
            assertions_pass = assertions_pass and concurrency_test_pass
            command_identity = 'ResourcePackageCommandProcessTests'
            command = dotnet
            command_exit = dotnet.get('exitCode', 1)
        else:
            command_identity = 'tools/parity/managed-package-concurrency-process-differential.py'
            command = runner
            command_exit = runner.get('exitCode', 1)
        fixture = ('disposable-package-roots' if case_id == 'concurrent.external-settings-change'
                   else 'barrier-synchronized-processes')
        passed = bool(provenance_pass and runner_pass and assertions_pass)
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state if states_present else {}},
            observed={'exitCode': 0 if passed else (command_exit or 1),
                      'state': actual_state if states_present else {}},
            passed=passed, command_identity=command_identity, argv=command.get('argv', []),
            command_exit_code=command_exit, fixture_identity=fixture,
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'pisharpSourceSha': differential.get('pisharpSourceSha'),
                'reportSha256': report_sha,
                'sameNpmInstallTestPassed': concurrency_test_pass if case_id == 'concurrent.same-npm-install' else None,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = classification if assertions_pass else 'mismatch'
        recorded['assertions'] = {case_id: bool(passed)}
        recorded['passed'] = passed
        _write_case_artifact(report, output, recorded)
        all_passed = all_passed and passed
    _refresh_checkpoint_result(report, 'concurrency')
    return all_passed


def _record_differential_acceptance_cases(report: dict[str, Any], output: Path,
                                          runner_results: list[dict[str, Any]]) -> bool:
    """Require independent pinned-Pi fixture agreement and a detected negative control."""
    checkpoint = _checkpoint_map(load_contract())['differential-acceptance']
    source_sha = report['provenance']['pisharpSourceSha']
    pi_sha = report['provenance']['piSha']
    report_path = output / 'managed-package-d0.json'
    differential = _load_json(report_path) if report_path.is_file() else {}
    report_sha = _sha256_file(report_path) if report_path.is_file() else None
    if report_sha is not None:
        relative = report_path.relative_to(output).as_posix()
        report['artifacts'] = [item for item in report['artifacts'] if item['path'] != relative]
        report['artifacts'].append({'path': relative, 'sha256': report_sha})
    runner = next((item for item in runner_results
                   if item.get('runnerId') == 'differential-acceptance'), {})
    provenance_pass = (differential.get('piSha') == pi_sha
                       and differential.get('pisharpSourceSha') == source_sha
                       and differential.get('fixtureVersion') == 'managed-package-d0-v1'
                       and differential.get('scenario') == 'independent-pinned-pi-calibration-and-negative-control')
    calibrations = differential.get('calibrations', {})
    negative = differential.get('negativeControl', {})
    expected_fixtures = {
        'd0.same-pi-package-cli': 'package-cli',
        'd0.same-pi-npm': 'managed-npm',
        'd0.same-pi-git': 'managed-git-install',
    }
    all_passed = True
    for case_id in checkpoint['requiredCaseIds']:
        fixture_name = expected_fixtures.get(case_id)
        if fixture_name:
            result = calibrations.get(fixture_name, {})
            case_passed = (provenance_pass and runner.get('exitCode') == 0
                           and differential.get('match') is True
                           and result.get('runnerPassed') is True
                           and result.get('normalizedOracleEqual') is True
                           and result.get('baselineCaseCount', 0) > 0
                           and result.get('baselineNormalizedOracleSha256')
                           == result.get('repeatNormalizedOracleSha256'))
            fixture_identity = 'pinned-pi-independent-runs'
            state = {
                'fixture': fixture_name,
                'baselineNormalizedOracleSha256': result.get('baselineNormalizedOracleSha256'),
                'repeatNormalizedOracleSha256': result.get('repeatNormalizedOracleSha256'),
                'caseCount': result.get('baselineCaseCount', 0),
                'normalizedOracleEqual': result.get('normalizedOracleEqual') is True,
            }
        else:
            case_passed = (provenance_pass and runner.get('exitCode') == 0
                           and negative.get('executed') is True
                           and negative.get('originalComparisonEqual') is True
                           and negative.get('mutatedComparisonEqual') is False
                           and negative.get('detectedMismatch') is True
                           and negative.get('mutatedField') == 'head')
            fixture_identity = 'negative-differential-control'
            state = {
                'sourceCase': negative.get('sourceCase'),
                'mutatedField': negative.get('mutatedField'),
                'detectedMismatch': negative.get('detectedMismatch') is True,
                'originalComparisonEqual': negative.get('originalComparisonEqual') is True,
                'mutatedComparisonEqual': negative.get('mutatedComparisonEqual') is True,
            }
        passed = bool(case_passed)
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': state},
            observed={'exitCode': 0 if passed else (runner.get('exitCode', 1) or 1), 'state': state},
            passed=passed,
            command_identity='tools/parity/managed-package-differential-acceptance.py',
            argv=runner.get('argv', []), command_exit_code=runner.get('exitCode', 1),
            fixture_identity=fixture_identity,
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': pi_sha, 'sourceSha': source_sha,
                'reportSha256': report_sha, 'state': state,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = 'strict' if passed else 'mismatch'
        recorded['assertions'] = {case_id: passed}
        recorded['passed'] = passed
        _write_case_artifact(report, output, recorded)
        all_passed = all_passed and passed
    _refresh_checkpoint_result(report, 'differential-acceptance')
    return all_passed


def _record_package_manager_cases(report: dict[str, Any], output: Path,
                                 runner_results: list[dict[str, Any]]) -> bool:
    """Validate exact manager argv, failures and process cancellation against pinned Pi."""
    report_path = output / 'package-managers.json'
    differential = _load_json(report_path) if report_path.is_file() else {}
    runner = next((item for item in runner_results if item.get('runnerId') == 'package-managers'), {})
    baseline_by_id = {item['caseId']: item for item in report['cases']}
    manager_tests_pass = all(
        baseline_by_id[case_id]['executed'] and baseline_by_id[case_id]['passed']
        for case_id in ('baseline.managed-npm', 'baseline.git')
    )
    runner_pass = (runner.get('exitCode') == 0 and differential.get('match') is True
                   and differential.get('piSha') == load_contract()['upstream']['revision']
                   and differential.get('pisharpSha') == report['provenance']['pisharpSourceSha']
                   and manager_tests_pass)
    artifact_sha = _sha256_file(report_path) if report_path.is_file() else None
    expected_cases = {
        'pm.default': ('npm', 'available-real-managers', None),
        'pm.npm': ('npm', 'available-real-managers', ['npm']),
        'pm.pnpm': ('pnpm', 'command-shims', ['pnpm']),
        'pm.yarn': ('yarn', 'command-shims', ['yarn']),
        'pm.bun': ('bun', 'command-shims', ['bun']),
        'pm.custom-arguments': ('npm', 'command-shims', None),
        'pm.missing-executable': (None, 'command-shims', ['/no/such/package-manager-with-spaces/npm-missing']),
        'pm.failure': ('npm', 'command-shims', ['npm']),
        'pm.cancellation': ('npm', 'command-shims', ['npm']),
    }
    observed_cases = differential.get('cases', {})
    expected_ids = set(expected_cases)
    if set(observed_cases) != expected_ids:
        runner_pass = False

    all_passed = True
    for case_id, (manager, fixture_family, configured_command) in expected_cases.items():
        result = observed_cases.get(case_id, {})
        oracle_state, actual_state = result.get('oracle'), result.get('actual')
        passed = False
        classification = 'strict'
        install_spec = result.get('expected', {}).get('installSpec')
        if not isinstance(oracle_state, dict) or not isinstance(actual_state, dict):
            command_exit = runner.get('exitCode', 1) or 1
            _record_baseline_case(
                report, output, case_id,
                expected={'exitCode': 0, 'state': oracle_state},
                observed={'exitCode': command_exit, 'state': actual_state}, passed=False,
                command_identity='tools/parity/package-managers-process-differential.py',
                argv=runner.get('argv', []), command_exit_code=command_exit,
                fixture_identity=fixture_family,
                input_fingerprint=_sha256_bytes(_canonical_bytes({
                    'case': case_id, 'runnerExitCode': runner.get('exitCode'),
                    'reportSha256': artifact_sha,
                })),
            )
            recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
            recorded['classification'] = 'mismatch'
            recorded['assertions'] = {case_id: False}
            recorded['passed'] = False
            _write_case_artifact(report, output, recorded)
            all_passed = False
            continue
        if case_id in ('pm.default', 'pm.npm', 'pm.pnpm', 'pm.yarn', 'pm.bun', 'pm.custom-arguments'):
            state_config_matches = (isinstance(actual_state, dict) and isinstance(oracle_state, dict)
                                    and actual_state.get('packageManagerConfig')
                                    == oracle_state.get('packageManagerConfig')
                                    and actual_state.get('npmCommandPreserved') is True
                                    and oracle_state.get('npmCommandPreserved') is True)
            prefix = ['exec', 'node@20', '--', 'npm'] if case_id == 'pm.custom-arguments' else []
            if case_id == 'pm.custom-arguments':
                custom_command = (actual_state or {}).get('packageManagerConfig')
                state_config_matches = (state_config_matches and isinstance(custom_command, list)
                                        and len(custom_command) == 5
                                        and ' ' in custom_command[0]
                                        and custom_command[1:] == prefix)
            elif configured_command is None:
                state_config_matches = (state_config_matches
                                        and actual_state.get('packageManagerConfig') is None)
            else:
                state_config_matches = (state_config_matches
                                        and actual_state.get('packageManagerConfig') == configured_command)
            if manager == 'bun':
                args = ['install', install_spec, '--cwd', '<install-root>', '--omit=peer']
            elif manager == 'pnpm':
                args = ['install', install_spec, '--prefix', '<install-root>',
                        '--config.auto-install-peers=false', '--config.strict-peer-dependencies=false',
                        '--config.strict-dep-builds=false']
            else:
                args = ['install', install_spec, '--prefix', '<install-root>', '--legacy-peer-deps']
            expected_argv = prefix + args
            real_install_valid = (case_id not in ('pm.default', 'pm.npm')
                                  or actual_state.get('packageDirectoryExists') is True)
            passed = (runner_pass and isinstance(install_spec, str) and install_spec.startswith('pm-')
                      and isinstance(oracle_state, dict) and isinstance(actual_state, dict)
                      and result.get('expected', {}).get('manager') == manager
                      and result.get('fixtureFamily') == fixture_family
                      and result.get('passed') is True and oracle_state == actual_state
                      and actual_state.get('exitCode') == 0 and actual_state.get('argv') == expected_argv
                      and actual_state.get('expectedArgv') == expected_argv
                      and actual_state.get('sourceConfigured') is True
                      and actual_state.get('managerStarted') is True and real_install_valid
                      and state_config_matches)
        elif case_id in ('pm.missing-executable', 'pm.failure'):
            expected_argv = [] if case_id == 'pm.missing-executable' else [
                'install', install_spec, '--prefix', '<install-root>', '--legacy-peer-deps']
            passed = (runner_pass and result.get('fixtureFamily') == fixture_family
                      and result.get('passed') is True and isinstance(oracle_state, dict)
                      and isinstance(actual_state, dict) and oracle_state == actual_state
                      and actual_state.get('exitCode', 0) != 0 and actual_state.get('argv') == expected_argv
                      and actual_state.get('sourceConfigured') is False
                      and actual_state.get('settingsUnchanged') is True
                      and actual_state.get('npmCommandPreserved') is True
                      and actual_state.get('packageDirectoryExists') is False
                      and actual_state.get('packageManagerConfig') == configured_command)
        else:
            classification = result.get('classification', 'mismatch')
            pi_state = oracle_state if isinstance(oracle_state, dict) else {}
            pisharp_state = actual_state if isinstance(actual_state, dict) else {}
            pisharp_safe = (pisharp_state.get('exitCode') in (1, 130)
                            and pisharp_state.get('cancelled') is True
                            and pisharp_state.get('managerStarted') is True
                            and pisharp_state.get('sourceConfigured') is False
                            and pisharp_state.get('settingsUnchanged') is True
                            and pisharp_state.get('npmCommandPreserved') is True
                            and pisharp_state.get('packageDirectoryExists') is False
                            and pisharp_state.get('packageManagerConfig') == configured_command)
            safety_difference = (classification == 'intentional-safety-difference'
                                 and _package_manager_cancellation_safety_difference(pi_state, pisharp_state))
            strict_match = (classification == 'strict' and pi_state == pisharp_state and pisharp_safe)
            passed = (runner_pass and result.get('passed') is True
                      and result.get('fixtureFamily') == fixture_family
                      and (safety_difference or strict_match))
        if not passed:
            classification = 'mismatch'
        command_exit = runner.get('exitCode', 1)
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else (command_exit or 1), 'state': actual_state},
            passed=passed,
            command_identity='tools/parity/package-managers-process-differential.py',
            argv=runner.get('argv', []), command_exit_code=command_exit,
            fixture_identity=result.get('fixtureFamily', 'command-shims'),
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'pisharpSha': differential.get('pisharpSha'), 'reportSha256': artifact_sha,
                'baselineTests': manager_tests_pass,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = classification
        recorded['assertions'] = {case_id: bool(passed)}
        recorded['passed'] = passed
        _write_case_artifact(report, output, recorded)
        all_passed = all_passed and passed
    _refresh_checkpoint_result(report, 'package-managers')
    return all_passed


def _package_manager_cancellation_safety_difference(pi_state: dict[str, Any],
                                                    pisharp_state: dict[str, Any]) -> bool:
    """Accept only the pinned cancellation state and PiSharp's bounded exit-code race."""
    pisharp_safe = (pisharp_state.get('exitCode') in (1, 130)
                    and pisharp_state.get('cancelled') is True
                    and pisharp_state.get('managerStarted') is True
                    and pisharp_state.get('sourceConfigured') is False
                    and pisharp_state.get('settingsUnchanged') is True
                    and pisharp_state.get('npmCommandPreserved') is True
                    and pisharp_state.get('packageDirectoryExists') is False
                    and pisharp_state.get('packageManagerConfig') == ['npm'])
    pi_valid = (pi_state.get('exitCode') == 130 and pi_state.get('cancelled') is True
                and pi_state.get('managerStarted') is True
                and pi_state.get('sourceConfigured') is False
                and pi_state.get('settingsUnchanged') is True
                and pi_state.get('npmCommandPreserved') is True
                and pi_state.get('packageDirectoryExists') is True
                and pi_state.get('packageManagerConfig') == ['npm'])
    same_except_known_variations = (
        {key: value for key, value in pi_state.items()
         if key not in ('exitCode', 'packageDirectoryExists')}
        == {key: value for key, value in pisharp_state.items()
            if key not in ('exitCode', 'packageDirectoryExists')}
    )
    return pisharp_safe and pi_valid and same_except_known_variations


def _record_legacy_global_cases(report: dict[str, Any], output: Path,
                                runner_results: list[dict[str, Any]]) -> bool:
    """Validate pinned global-package lookup, managed precedence and read-only removal."""
    report_path = output / 'legacy-global.json'
    differential = _load_json(report_path) if report_path.is_file() else {}
    runner = next((item for item in runner_results if item.get('runnerId') == 'legacy-global'), {})
    baseline_by_id = {item['caseId']: item for item in report['cases']}
    managed_test_run = next((item for item in runner_results
                             if item.get('runnerId') == 'package-focused .NET tests'
                             and item.get('caseId') == 'baseline.managed-npm'), {})
    managed_trx = output / managed_test_run['trx'] if managed_test_run.get('trx') else Path('/missing')
    managed_test_outcomes = _trx_test_outcomes(managed_trx)
    fallback_test_names = (
        'MissingOrMalformedLegacyLookupFallsBackToManagedAcquisition',
        'MalformedPnpmGlobalListsDoNotFallThroughToRootLookup',
    )
    fallback_tests_pass = (baseline_by_id['baseline.managed-npm']['executed']
                           and baseline_by_id['baseline.managed-npm']['passed']
                           and all(any(name.endswith('.' + required_name) and outcome == 'Passed'
                                       for name, outcome in managed_test_outcomes.items())
                                   for required_name in fallback_test_names))
    provenance_pass = (differential.get('piSha') == load_contract()['upstream']['revision']
                       and differential.get('pisharpSha') == report['provenance']['pisharpSourceSha']
                       and differential.get('scenario') == 'legacy-global-package-resolution')
    artifact_sha = _sha256_file(report_path) if report_path.is_file() else None
    if artifact_sha is not None:
        relative_report = report_path.relative_to(output).as_posix()
        report['artifacts'] = [item for item in report['artifacts'] if item['path'] != relative_report]
        report['artifacts'].append({'path': relative_report, 'sha256': artifact_sha})
    expected_cases = {
        'legacy.npm': ('npm', 'legacy-npm', 'global/npm/node_modules/legacy-npm',
                       [['root', '-g'], ['root', '-g']]),
        'legacy.pnpm': ('pnpm', 'legacy-pnpm', 'global/pnpm/20-fixture/node_modules/legacy-pnpm',
                        [['list', '-g', '--depth', '0', '--json'],
                         ['list', '-g', '--depth', '0', '--json']]),
        'legacy.yarn': ('yarn', 'legacy-yarn', 'global/yarn/node_modules/legacy-yarn',
                        [['root', '-g'], ['root', '-g']]),
        'legacy.bun': ('bun', 'legacy-bun', 'global/bun/.bun/install/global/node_modules/legacy-bun',
                       [['pm', 'bin', '-g'], ['pm', 'bin', '-g']]),
        'legacy.missing': ('npm', 'legacy-missing', 'agent/npm/node_modules/legacy-missing',
                           [['root', '-g'], ['install', 'legacy-missing', '--prefix',
                             '<product-root>/agent/npm', '--legacy-peer-deps']]),
        'legacy.malformed': ('pnpm', 'legacy-malformed', 'agent/npm/node_modules/legacy-malformed',
                             [['list', '-g', '--depth', '0', '--json'],
                              ['install', 'legacy-malformed', '--prefix', '<product-root>/agent/npm',
                               '--config.auto-install-peers=false', '--config.strict-peer-dependencies=false',
                               '--config.strict-dep-builds=false']]),
        'legacy.managed-precedence': ('npm', 'legacy-precedence',
                                      'agent/npm/node_modules/legacy-precedence', []),
        'legacy.read-only': ('npm', 'legacy-read-only', None, []),
    }
    observed_cases = differential.get('cases', {})
    if set(observed_cases) != set(expected_cases):
        provenance_pass = False

    all_passed = True
    for case_id, (manager, package_name, expected_path, expected_calls) in expected_cases.items():
        result = observed_cases.get(case_id, {})
        oracle_state = result.get('oracle')
        actual_state = result.get('actual')
        case_pass = (provenance_pass and fallback_tests_pass and isinstance(oracle_state, dict)
                     and isinstance(actual_state, dict) and oracle_state == actual_state
                     and result.get('passed') is True and result.get('expected', {}).get('manager') == manager
                     and result.get('fixtureFamilies') == ['disposable-global-roots', 'package-manager-shims'])
        if not isinstance(oracle_state, dict) or not isinstance(actual_state, dict):
            oracle_state = oracle_state if isinstance(oracle_state, dict) else {}
            actual_state = actual_state if isinstance(actual_state, dict) else {}
        expected_source = f'npm:{package_name}'
        if case_id == 'legacy.read-only':
            case_pass = (case_pass and oracle_state.get('exitCode') == 0 and actual_state.get('exitCode') == 0
                         and oracle_state.get('configuredSources') == []
                         and actual_state.get('configuredSources') == []
                         and oracle_state.get('globalPackageExists') is True
                         and actual_state.get('globalPackageExists') is True
                         and oracle_state.get('globalPackageDigestBefore') == oracle_state.get('globalPackageDigestAfter')
                         and actual_state.get('globalPackageDigestBefore') == actual_state.get('globalPackageDigestAfter')
                         and oracle_state.get('managerCalls') == [['root', '-g']]
                         and actual_state.get('managerCalls') == [['root', '-g']])
        else:
            case_pass = (case_pass and oracle_state.get('exitCode') == 0 and actual_state.get('exitCode') == 0
                         and oracle_state.get('configuredSources') == [expected_source]
                         and actual_state.get('configuredSources') == [expected_source]
                         and oracle_state.get('managerCalls') == expected_calls
                         and actual_state.get('managerCalls') == expected_calls
                         and oracle_state.get('globalPackageDigestBefore') == oracle_state.get('globalPackageDigestAfter')
                         and actual_state.get('globalPackageDigestBefore') == actual_state.get('globalPackageDigestAfter'))
            case_pass = (case_pass and oracle_state.get('selectedPath') == expected_path
                         and actual_state.get('selectedPath') == expected_path)
        if case_id in ('legacy.npm', 'legacy.pnpm', 'legacy.yarn', 'legacy.bun'):
            case_pass = (case_pass and oracle_state.get('globalPackageExists') is True
                         and actual_state.get('globalPackageExists') is True)
        elif case_id == 'legacy.missing':
            case_pass = (case_pass and oracle_state.get('globalPackageExists') is False
                         and actual_state.get('globalPackageExists') is False
                         and oracle_state.get('managedPackageExists') is True
                         and actual_state.get('managedPackageExists') is True)
        elif case_id == 'legacy.malformed':
            # Malformed pnpm output must not fall through to `pnpm root -g`; the configured
            # package is acquired into the managed root, matching pinned Pi.
            case_pass = (case_pass and oracle_state.get('globalPackageExists') is True
                         and actual_state.get('globalPackageExists') is True
                         and oracle_state.get('managedPackageExists') is True
                         and actual_state.get('managedPackageExists') is True)
        elif case_id == 'legacy.managed-precedence':
            case_pass = (case_pass and oracle_state.get('managedPackageExists') is True
                         and actual_state.get('managedPackageExists') is True)
        command_exit = 0 if (result.get('diagnostics', {}).get('pi', {}).get('exitCode') == 0
                             and result.get('diagnostics', {}).get('pisharp', {}).get('exitCode') == 0) else 1
        passed = bool(case_pass)
        classification = 'strict' if passed else 'mismatch'
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else command_exit or 1, 'state': actual_state},
            passed=passed,
            command_identity='legacy package resolution probe',
            argv=runner.get('argv', []), command_exit_code=command_exit,
            fixture_identity='disposable-global-roots',
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'pisharpSha': differential.get('pisharpSha'), 'reportSha256': artifact_sha,
                'fallbackTestsPassed': fallback_tests_pass,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = classification
        recorded['assertions'] = {case_id: passed}
        recorded['passed'] = passed
        _write_case_artifact(report, output, recorded)
        all_passed = all_passed and passed
    _refresh_checkpoint_result(report, 'legacy-global')
    return all_passed


def _record_git_install_cases(report: dict[str, Any], output: Path,
                              runner_results: list[dict[str, Any]]) -> bool:
    """Validate paired Git install state, refs, failure cleanup, dependencies and cancellation."""
    report_path = output / 'managed-git-install.json'
    differential = _load_json(report_path) if report_path.is_file() else {}
    runner = next((item for item in runner_results if item.get('runnerId') == 'managed-git-install'), {})
    git_test_run = next((item for item in runner_results
                         if item.get('runnerId') == 'package-focused .NET tests'
                         and item.get('caseId') == 'baseline.git'), {})
    git_trx = output / git_test_run['trx'] if git_test_run.get('trx') else Path('/missing')
    git_test_outcomes = _trx_test_outcomes(git_trx)
    required_test_names = (
        'SupportedGitSyntaxUsesVersionIndependentRepositoryIdentity',
        'GitDependencyInstallUsesConfiguredManagerPoliciesAndUnknownFallback',
        'ManagedGitLoadsResourcesAndNativeExtensionWithoutStartupFetchAndUpdatesConfiguredRef',
    )
    git_tests_pass = (bool(git_test_run)
                      and git_test_run.get('exitCode') == 0
                      and git_test_run.get('counts', {}).get('discovered', 0) > 0
                      and git_test_run.get('counts', {}).get('failed') == 0
                      and git_test_run.get('counts', {}).get('skipped') == 0
                      and all(any(name.split('(', 1)[0].endswith('.' + required) and outcome == 'Passed'
                                  for name, outcome in git_test_outcomes.items())
                              for required in required_test_names))
    provenance_pass = (
        differential.get('piSha') == load_contract()['upstream']['revision']
        and differential.get('piSharpBaseSha') == report['provenance']['pisharpSourceSha']
        and differential.get('scenario') == 'paired-managed-git-install-local-daemon'
        and differential.get('fixtureVersion') == 'managed-git-install-v1'
    )
    artifact_sha = _sha256_file(report_path) if report_path.is_file() else None
    if artifact_sha is not None:
        relative_report = report_path.relative_to(output).as_posix()
        report['artifacts'] = [item for item in report['artifacts'] if item['path'] != relative_report]
        report['artifacts'].append({'path': relative_report, 'sha256': artifact_sha})

    case_ids = (
        'git.install-branch', 'git.install-tag', 'git.install-commit', 'git.install-floating',
        'git.install-invalid-ref', 'git.install-failure', 'git.install-cancelled',
        'git.install-url-shorthands', 'git.install-existing', 'git.install-missing-dependencies',
    )
    observed_cases = differential.get('cases', {})
    runner_pass = runner.get('exitCode') == 0 and differential.get('match') is True
    all_passed = True
    for case_id in case_ids:
        result = observed_cases.get(case_id, {})
        oracle_state = result.get('oracle')
        actual_state = result.get('actual')
        shape_pass = (isinstance(oracle_state, dict) and isinstance(actual_state, dict)
                      and result.get('equal') is True)
        contract_pass = (shape_pass and oracle_state.get('contractSatisfied') is True
                         and actual_state.get('contractSatisfied') is True)
        passed = bool(provenance_pass and git_tests_pass and runner_pass and contract_pass)
        oracle = oracle_state if isinstance(oracle_state, dict) else {}
        actual = actual_state if isinstance(actual_state, dict) else {}
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle},
            observed={'exitCode': 0 if passed else (runner.get('exitCode', 1) or 1), 'state': actual},
            passed=passed,
            command_identity='tools/parity/managed-git-install-process-differential.py',
            argv=runner.get('argv', []), command_exit_code=0 if passed else (runner.get('exitCode', 1) or 1),
            fixture_identity='loopback-git-daemon',
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'pisharpSourceSha': differential.get('piSharpBaseSha'), 'reportSha256': artifact_sha,
                'gitStoreTestsPassed': git_tests_pass,
            })),
        )
        case = next(item for item in report['cases'] if item['caseId'] == case_id)
        case['assertions'] = {case_id: bool(provenance_pass and git_tests_pass and runner_pass and contract_pass)}
        case['passed'] = passed
        _write_case_artifact(report, output, case)
        all_passed = all_passed and passed
    _refresh_checkpoint_result(report, 'git-install')
    return all_passed


def _record_git_update_cases(report: dict[str, Any], output: Path,
                             runner_results: list[dict[str, Any]]) -> bool:
    """Validate paired Git update state and the approved staged-update safety difference."""
    report_path = output / 'managed-git-update.json'
    differential = _load_json(report_path) if report_path.is_file() else {}
    runner = next((item for item in runner_results if item.get('runnerId') == 'managed-git-update'), {})
    git_test_run = next((item for item in runner_results
                         if item.get('runnerId') == 'package-focused .NET tests'
                         and item.get('caseId') == 'baseline.git'), {})
    git_trx = output / git_test_run['trx'] if git_test_run.get('trx') else Path('/missing')
    git_test_outcomes = _trx_test_outcomes(git_trx)
    required_test_names = (
        'CachedUnpinnedGitSourceRefreshesOnlyOnExplicitUpdate',
        'OfflineMissingDeltaAndFailedOrCancelledUpdatesKeepTheLastUsableCheckout',
    )
    git_tests_pass = (bool(git_test_run)
                      and git_test_run.get('exitCode') == 0
                      and git_test_run.get('counts', {}).get('discovered', 0) > 0
                      and git_test_run.get('counts', {}).get('failed') == 0
                      and git_test_run.get('counts', {}).get('skipped') == 0
                      and all(any(name.split('(', 1)[0].endswith('.' + required) and outcome == 'Passed'
                                  for name, outcome in git_test_outcomes.items())
                              for required in required_test_names))
    contract = load_contract()
    provenance_pass = (differential.get('piSha') == contract['upstream']['revision']
                       and differential.get('piSharpBaseSha') == report['provenance']['pisharpSourceSha']
                       and differential.get('scenario') == 'paired-managed-git-update-local-daemon'
                       and differential.get('fixtureVersion') == 'managed-git-update-v1')
    report_sha = _sha256_file(report_path) if report_path.is_file() else None
    if report_sha is not None:
        relative_report = report_path.relative_to(output).as_posix()
        report['artifacts'] = [item for item in report['artifacts'] if item['path'] != relative_report]
        report['artifacts'].append({'path': relative_report, 'sha256': report_sha})

    case_ids = tuple(_checkpoint_map(contract)['git-update']['requiredCaseIds'])
    observed_cases = differential.get('cases', {})
    runner_pass = (runner.get('exitCode') == 0 and differential.get('match') is True
                   and set(observed_cases) == set(case_ids))
    all_passed = True
    for case_id in case_ids:
        result = observed_cases.get(case_id, {})
        oracle_state = result.get('oracle')
        actual_state = result.get('actual')
        states_present = isinstance(oracle_state, dict) and isinstance(actual_state, dict)
        accepted_safety_difference = (case_id == 'git.update-dependency-failure'
                                      and result.get('safetyDifferenceAccepted') is True
                                      and states_present
                                      and oracle_state.get('head') != actual_state.get('head')
                                      and oracle_state.get('head') != oracle_state.get('initialHead')
                                      and actual_state.get('head') == actual_state.get('initialHead')
                                      and oracle_state.get('updateExitCode') != 0
                                      and actual_state.get('updateExitCode') != 0
                                      and oracle_state.get('sourceConfigured') is True
                                      and actual_state.get('sourceConfigured') is True
                                      and oracle_state.get('contentUnchanged') is False
                                      and actual_state.get('contentUnchanged') is True
                                      and oracle_state.get('settingsUnchanged') is True
                                      and actual_state.get('settingsUnchanged') is True
                                      and oracle_state.get('contractSatisfied') is True
                                      and actual_state.get('contractSatisfied') is True)
        strict_match = (case_id != 'git.update-dependency-failure' and states_present
                        and result.get('equal') is True and oracle_state == actual_state
                        and oracle_state.get('contractSatisfied') is True
                        and actual_state.get('contractSatisfied') is True)
        assertions_pass = (provenance_pass and git_tests_pass and runner_pass
                           and (strict_match or accepted_safety_difference))
        passed = bool(assertions_pass)
        classification = ('intentional-safety-difference' if accepted_safety_difference else
                           'strict' if strict_match else 'mismatch')
        oracle = oracle_state if states_present else {}
        actual = actual_state if states_present else {}
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle},
            observed={'exitCode': 0 if passed else (runner.get('exitCode', 1) or 1), 'state': actual},
            passed=passed,
            command_identity='tools/parity/managed-git-update-process-differential.py',
            argv=runner.get('argv', []), command_exit_code=0 if runner_pass else (runner.get('exitCode', 1) or 1),
            fixture_identity='loopback-git-daemon',
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'pisharpSourceSha': differential.get('piSharpBaseSha'), 'reportSha256': report_sha,
                'gitStoreTestsPassed': git_tests_pass,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = classification
        recorded['assertions'] = {case_id: bool(assertions_pass)}
        recorded['passed'] = passed
        _write_case_artifact(report, output, recorded)
        all_passed = all_passed and passed
    _refresh_checkpoint_result(report, 'git-update')
    return all_passed


def _record_bulk_update_cases(report: dict[str, Any], output: Path,
                              runner_results: list[dict[str, Any]],
                              test_group_results: dict[str, dict[str, Any]]) -> bool:
    """Validate bulk target selection, mixed scopes, partial failure and cancellation."""
    report_path = output / 'managed-package-bulk-updates.json'
    differential = _load_json(report_path) if report_path.is_file() else {}
    runner = next((item for item in runner_results if item.get('runnerId') == 'bulk-updates'), {})
    cli_test = test_group_results.get('baseline.package-cli', {})
    cli_trx = output / cli_test['trx'] if cli_test.get('trx') else Path('/missing')
    cli_outcomes = _trx_test_outcomes(cli_trx)
    mixed_scope_test_pass = (cli_test.get('exitCode') == 0
                             and cli_test.get('counts', {}).get('discovered', 0) > 0
                             and cli_test.get('counts', {}).get('failed') == 0
                             and cli_test.get('counts', {}).get('skipped') == 0
                             and any(name.split('(', 1)[0].endswith(
                                 '.BulkUpdateHandlesMixedUserAndTrustedProjectPackageScopes')
                                     and outcome == 'Passed'
                                     for name, outcome in cli_outcomes.items()))
    contract = load_contract()
    provenance_pass = (differential.get('piSha') == contract['upstream']['revision']
                       and differential.get('piSharpBaseSha') == report['provenance']['pisharpSourceSha']
                       and differential.get('scenario') == 'paired-mixed-source-bulk-update-processes'
                       and differential.get('fixtureVersion') == 'managed-package-bulk-update-v1')
    report_sha = _sha256_file(report_path) if report_path.is_file() else None
    if report_sha is not None:
        relative_report = report_path.relative_to(output).as_posix()
        report['artifacts'] = [item for item in report['artifacts'] if item['path'] != relative_report]
        report['artifacts'].append({'path': relative_report, 'sha256': report_sha})

    case_ids = tuple(_checkpoint_map(contract)['bulk-updates']['requiredCaseIds'])
    observed_cases = differential.get('cases', {})
    runner_pass = (runner.get('exitCode') == 0 and differential.get('match') is True
                   and set(observed_cases) == set(case_ids))
    all_passed = True
    for case_id in case_ids:
        result = observed_cases.get(case_id, {})
        oracle_state, actual_state = result.get('oracle'), result.get('actual')
        states_present = isinstance(oracle_state, dict) and isinstance(actual_state, dict)
        safety_difference = (case_id == 'bulk.cancelled'
                             and result.get('safetyDifferenceAccepted') is True
                             and states_present
                             and oracle_state.get('updateExitCode') != 0
                             and actual_state.get('updateExitCode') != 0
                             and oracle_state.get('revisions', {}).get('a') is None
                             and actual_state.get('revisions', {}).get('a') == '1.0.0'
                             and oracle_state.get('operationStarted') is True
                             and actual_state.get('operationStarted') is True
                             and oracle_state.get('processReaped') is True
                             and actual_state.get('processReaped') is True
                             and oracle_state.get('settingsUnchanged') is True
                             and actual_state.get('settingsUnchanged') is True
                             and oracle_state.get('sources') == actual_state.get('sources')
                             and oracle_state.get('contractSatisfied') is True
                             and actual_state.get('contractSatisfied') is True)
        strict_match = (case_id != 'bulk.cancelled' and states_present
                        and result.get('equal') is True and oracle_state == actual_state
                        and oracle_state.get('contractSatisfied') is True
                        and actual_state.get('contractSatisfied') is True)
        assertions_pass = (provenance_pass and runner_pass and mixed_scope_test_pass
                           and (strict_match or safety_difference))
        passed = bool(assertions_pass)
        classification = ('intentional-safety-difference' if safety_difference else
                           'strict' if strict_match else 'mismatch')
        oracle = oracle_state if states_present else {}
        actual = actual_state if states_present else {}
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle},
            observed={'exitCode': 0 if passed else (runner.get('exitCode', 1) or 1), 'state': actual},
            passed=passed,
            command_identity='tools/parity/managed-package-bulk-update-process-differential.py',
            argv=runner.get('argv', []), command_exit_code=0 if runner_pass else (runner.get('exitCode', 1) or 1),
            fixture_identity='mixed-npm-git-fixtures',
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'pisharpSourceSha': differential.get('piSharpBaseSha'), 'reportSha256': report_sha,
                'mixedScopeTestPassed': mixed_scope_test_pass,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = classification
        recorded['assertions'] = {case_id: bool(assertions_pass)}
        recorded['passed'] = passed
        _write_case_artifact(report, output, recorded)
        all_passed = all_passed and passed
    _refresh_checkpoint_result(report, 'bulk-updates')
    return all_passed


def _record_configuration_cases(report: dict[str, Any], output: Path,
                                runner_results: list[dict[str, Any]],
                                test_group_results: dict[str, dict[str, Any]]) -> bool:
    """Validate package settings, persistence, project trust and restart behavior."""
    report_path = output / 'managed-package-configuration.json'
    differential = _load_json(report_path) if report_path.is_file() else {}
    runner = next((item for item in runner_results if item.get('runnerId') == 'configuration-trust'), {})
    settings_test = test_group_results.get('baseline.settings', {})
    settings_trx = output / settings_test['trx'] if settings_test.get('trx') else Path('/missing')
    settings_outcomes = _trx_test_outcomes(settings_trx)
    resource_test = test_group_results.get('baseline.resource-integration', {})
    resource_trx = output / resource_test['trx'] if resource_test.get('trx') else Path('/missing')
    resource_outcomes = _trx_test_outcomes(resource_trx)
    required_tests = (
        (settings_outcomes, 'PackageSettingsReaderTests.NpmCommandRemainsAnArgvArrayWithoutShellOrCharacterRestrictions'),
        (settings_outcomes, 'PackageSettingsTests.ValidPackageDeclarationsAllowOtherStartupSettingsToLoad'),
        (resource_outcomes, 'ProjectTrustTests.ProjectPromptSourcesFollowTrustAndSystemAppendPrecedence'),
    )
    tests_pass = (settings_test.get('exitCode') == 0 and settings_test.get('counts', {}).get('failed') == 0
                  and settings_test.get('counts', {}).get('skipped') == 0
                  and resource_test.get('exitCode') == 0
                  and resource_test.get('counts', {}).get('failed') == 0
                  and resource_test.get('counts', {}).get('skipped') == 0
                  and all(any(name.split('(', 1)[0].endswith('.' + required) and outcome == 'Passed'
                              for name, outcome in outcomes.items())
                          for outcomes, required in required_tests))
    contract = load_contract()
    provenance_pass = (differential.get('piSha') == contract['upstream']['revision']
                       and differential.get('piSharpBaseSha') == report['provenance']['pisharpSourceSha']
                       and differential.get('scenario') == 'paired-package-settings-trust-restart-processes'
                       and differential.get('fixtureVersion') == 'managed-package-configuration-v1')
    report_sha = _sha256_file(report_path) if report_path.is_file() else None
    if report_sha is not None:
        relative_report = report_path.relative_to(output).as_posix()
        report['artifacts'] = [item for item in report['artifacts'] if item['path'] != relative_report]
        report['artifacts'].append({'path': relative_report, 'sha256': report_sha})

    case_ids = tuple(_checkpoint_map(contract)['configuration-trust']['requiredCaseIds'])
    observed_cases = differential.get('cases', {})
    runner_pass = (runner.get('exitCode') == 0 and differential.get('match') is True
                   and set(observed_cases) == set(case_ids))
    all_passed = True
    for case_id in case_ids:
        result = observed_cases.get(case_id, {})
        oracle_state, actual_state = result.get('oracle'), result.get('actual')
        states_present = isinstance(oracle_state, dict) and isinstance(actual_state, dict)
        safety_difference = (case_id == 'config.failed-write'
                             and result.get('safetyDifferenceAccepted') is True
                             and states_present
                             and oracle_state.get('installExitCode') == 0
                             and actual_state.get('installExitCode') != 0
                             and oracle_state.get('settingsPathRemainsDirectory') is True
                             and actual_state.get('settingsPathRemainsDirectory') is True
                             and oracle_state.get('configured') is False
                             and actual_state.get('configured') is False
                             and oracle_state.get('packageInstalled') is True
                             and actual_state.get('packageInstalled') is True
                             and oracle_state.get('contractSatisfied') is True
                             and actual_state.get('contractSatisfied') is True)
        strict_match = (case_id != 'config.failed-write' and states_present
                        and result.get('equal') is True and oracle_state == actual_state
                        and oracle_state.get('contractSatisfied') is True
                        and actual_state.get('contractSatisfied') is True)
        assertions_pass = provenance_pass and tests_pass and runner_pass and (strict_match or safety_difference)
        passed = bool(assertions_pass)
        classification = ('intentional-safety-difference' if safety_difference else
                           'strict' if strict_match else 'mismatch')
        oracle = oracle_state if states_present else {}
        actual = actual_state if states_present else {}
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle},
            observed={'exitCode': 0 if passed else (runner.get('exitCode', 1) or 1), 'state': actual},
            passed=passed,
            command_identity='tools/parity/managed-package-configuration-process-differential.py',
            argv=runner.get('argv', []),
            command_exit_code=0 if runner_pass else (runner.get('exitCode', 1) or 1),
            fixture_identity='isolated-settings-roots',
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'pisharpSourceSha': differential.get('piSharpBaseSha'), 'reportSha256': report_sha,
                'settingsTestsPassed': tests_pass,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = classification
        recorded['assertions'] = {case_id: bool(assertions_pass)}
        recorded['passed'] = passed
        _write_case_artifact(report, output, recorded)
        all_passed = all_passed and passed
    _refresh_checkpoint_result(report, 'configuration-trust')
    return all_passed


def _record_startup_reload_cases(report: dict[str, Any], output: Path,
                                 runner_results: list[dict[str, Any]],
                                 test_group_results: dict[str, dict[str, Any]]) -> bool:
    """Record paired resource reload transitions and the npm reload/registration checks."""
    path = output / 'managed-package-startup-reload.json'
    differential = _load_json(path) if path.is_file() else {}
    pin = load_contract()['upstream']['revision']
    source_sha = _runtime_state()['headSha']
    local_cases = [
        'reload.local', 'reload.git', 'reload.updated-resource', 'reload.removed-resource',
        'reload.disabled-resource', 'reload.trust-revoked',
    ]
    process_pass = (differential.get('fixtureVersion') == 'managed-package-startup-reload-v1'
                    and differential.get('piSha') == pin
                    and differential.get('pisharpSourceSha') == source_sha
                    and differential.get('match') is True)
    all_passed = True
    for case_id in local_cases:
        item = differential.get('cases', {}).get(case_id, {})
        oracle_state = item.get('pi')
        actual_state = item.get('pisharp')
        passed = (process_pass and item.get('match') is True
                  and item.get('piContractSatisfied') is True
                  and item.get('pisharpContractSatisfied') is True
                  and item.get('contractSatisfied') is True
                  and isinstance(oracle_state, dict) and oracle_state == actual_state)
        commands = item.get('commands', {})
        argv = [commands[product].get('argv', []) for product in ('pi', 'pisharp') if product in commands]
        command_exits = [commands[product].get('exitCode') for product in ('pi', 'pisharp') if product in commands]
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else 1, 'state': actual_state}, passed=passed,
            command_identity='tools/parity/managed-package-startup-reload-process-differential.py',
            argv=[argument for command in argv for argument in command],
            command_exit_code=0 if passed else next((code for code in command_exits if code != 0), 1),
            fixture_identity='resource-invocation-fixtures',
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'), 'sourceSha': differential.get('pisharpSourceSha'),
                'probeSha256': differential.get('probeSourceSha256'), 'reportSha256': _sha256_file(path)
                if path.is_file() else None, 'oracle': oracle_state, 'actual': actual_state,
            })),
        )
        all_passed = all_passed and passed

    npm_path = output / 'managed-npm.json'
    npm_diff = _load_json(npm_path) if npm_path.is_file() else {}
    npm_runner = next((item for item in runner_results if item.get('runnerId') == 'managed-npm'), {})
    npm_test = test_group_results.get('baseline.managed-npm', {})
    npm_outcomes = _trx_test_outcomes(output / npm_test['trx']) if npm_test.get('trx') else {}
    npm_test_name = ('PiSharp.Tests.ManagedResourcePackageStoreTests.'
                     'LocalTarballsLoadResourcesAndNativeExtensionOncePerStartupAndReload')
    npm_test_passed = npm_outcomes.get(npm_test_name) == 'Passed'
    npm_source = next((item.get('source') for item in
                       npm_diff.get('pi', {}).get('resolved', {}).get('startup', {}).get('skills', [])), None)
    pi_startup = npm_diff.get('pi', {}).get('startupLoad', {})
    pi_reload = npm_diff.get('pi', {}).get('reloadLoad', {})
    pisharp_passes = npm_diff.get('pisharp', {}).get('resourcePasses', [])
    pisharp_package_names = [
        [skill.get('name') for skill in pass_.get('skills', []) if skill.get('source') == npm_source]
        for pass_ in pisharp_passes
    ]
    npm_pi_state = {
        'startupPackageSkills': pi_startup.get('skills', []),
        'reloadedPackageSkills': pi_reload.get('skills', []),
        'reloadStable': npm_diff.get('checks', {}).get('piReloadResolutionStable') is True,
    }
    npm_actual_state = {
        'startupPackageSkills': pisharp_package_names[0] if pisharp_package_names else [],
        'reloadedPackageSkills': pisharp_package_names[1] if len(pisharp_package_names) > 1 else [],
        'reloadStable': (len(pisharp_package_names) == 2 and pisharp_package_names[0] == pisharp_package_names[1]
                         and npm_diff.get('checks', {}).get('piSharpReloadResourcesStable') is True),
    }
    npm_passed = (npm_runner.get('exitCode') == 0 and npm_diff.get('piSha') == pin
                  and npm_diff.get('match') is True and npm_test_passed
                  and npm_pi_state['reloadStable'] and npm_actual_state['reloadStable']
                  and npm_pi_state['startupPackageSkills'] == npm_actual_state['startupPackageSkills']
                  and npm_pi_state['reloadedPackageSkills'] == npm_actual_state['reloadedPackageSkills']
                  and bool(npm_source))
    _record_baseline_case(
        report, output, 'reload.npm',
        expected={'exitCode': 0, 'state': npm_pi_state},
        observed={'exitCode': 0 if npm_passed else 1, 'state': npm_actual_state}, passed=npm_passed,
        command_identity='tools/parity/managed-npm-process-differential.py',
        argv=npm_runner.get('argv', []), command_exit_code=0 if npm_passed else npm_runner.get('exitCode', 1) or 1,
        fixture_identity='restartable-cli-processes',
        input_fingerprint=_sha256_bytes(_canonical_bytes({
            'case': 'reload.npm', 'piSha': npm_diff.get('piSha'),
            'sourceSha': npm_diff.get('pisharpSha', npm_diff.get('piSharpBaseSha')),
            'reportSha256': _sha256_file(npm_path) if npm_path.is_file() else None,
            'testOutcome': npm_outcomes.get(npm_test_name), 'oracle': npm_pi_state, 'actual': npm_actual_state,
        })),
    )
    all_passed = all_passed and npm_passed

    process_counts = npm_diff.get('processCounts', {})
    pi_extension_passes = [len(pi_startup.get('extensions', [])), len(pi_reload.get('extensions', []))]
    pisharp_extension_passes = [len(pass_) for pass_ in npm_diff.get('pisharp', {}).get('extensionPasses', [])]
    duplicate_pi_state = {
        'extensionRegistrationsPerPass': pi_extension_passes,
        'configureCallbacks': process_counts.get('pi', {}).get('extensionLoads'),
        'noWithinPassDuplicates': pi_extension_passes == [1, 1],
    }
    duplicate_actual_state = {
        'extensionRegistrationsPerPass': pisharp_extension_passes,
        'configureCallbacks': process_counts.get('pisharp', {}).get('extensionLoads'),
        'noWithinPassDuplicates': pisharp_extension_passes == [1, 1],
    }
    duplicate_passed = (npm_passed and npm_diff.get('checks', {}).get(
        'piAndPiSharpConfigureExtensionOnStartupAndReload') is True
        and duplicate_pi_state == duplicate_actual_state
        and duplicate_pi_state['configureCallbacks'] == 2
        and duplicate_actual_state['configureCallbacks'] == 2 and npm_test_passed)
    _record_baseline_case(
        report, output, 'reload.no-duplicate-registration',
        expected={'exitCode': 0, 'state': duplicate_pi_state},
        observed={'exitCode': 0 if duplicate_passed else 1, 'state': duplicate_actual_state},
        passed=duplicate_passed,
        command_identity='tools/parity/managed-npm-process-differential.py',
        argv=npm_runner.get('argv', []),
        command_exit_code=0 if duplicate_passed else npm_runner.get('exitCode', 1) or 1,
        fixture_identity='restartable-cli-processes',
        input_fingerprint=_sha256_bytes(_canonical_bytes({
            'case': 'reload.no-duplicate-registration', 'piSha': npm_diff.get('piSha'),
            'sourceSha': npm_diff.get('pisharpSha', npm_diff.get('piSharpBaseSha')),
            'reportSha256': _sha256_file(npm_path) if npm_path.is_file() else None,
            'testOutcome': npm_outcomes.get(npm_test_name),
            'oracle': duplicate_pi_state, 'actual': duplicate_actual_state,
        })),
    )
    all_passed = all_passed and duplicate_passed
    _refresh_checkpoint_result(report, 'startup-reload')
    return all_passed


def _record_identity_cases(report: dict[str, Any], output: Path,
                           runner_results: list[dict[str, Any]]) -> bool:
    runner = next((item for item in runner_results if item.get('runnerId') == 'managed-package-identity'), {})
    report_path = output / 'managed-package-identity.json'
    differential = _load_json(report_path) if report_path.is_file() else {}
    expected_pi_sha = load_contract()['upstream']['revision']
    provenance_pass = (differential.get('piSha') == expected_pi_sha
                       and differential.get('expectedPiSha') == expected_pi_sha
                       and differential.get('piShaMatches') is True)
    runner_pass = runner.get('exitCode') == 0 and differential.get('match') is True
    report_sha = _sha256_file(report_path) if report_path.is_file() else None
    if report_sha is not None:
        relative = report_path.relative_to(output).as_posix()
        report['artifacts'] = [item for item in report['artifacts'] if item['path'] != relative]
        report['artifacts'].append({'path': relative, 'sha256': report_sha})

    expected_behaviors = {
            'identity.npm': 'npm identity ignores version and install source',
            'identity.git': 'supported Git URL forms share version-independent repository identity',
            'identity.local': 'canonical local aliases resolve as one package',
            'identity.duplicate': 'duplicate package identities resolve once',
            'identity.scope-override': 'project declaration takes precedence over matching user declaration',
            'identity.autoload-delta': 'autoload-disabled project declaration acts as a delta',
            'identity.user-project-duplicates': 'matching user and project package declarations are deduplicated',
            'identity.local-symlink': 'symlinked aliases use canonical package identity',
    }
    fixture_families = {
        'identity.npm': 'npm-git-local-source-fixtures',
        'identity.git': 'npm-git-local-source-fixtures',
        'identity.local': 'user-project-settings',
        'identity.duplicate': 'npm-git-local-source-fixtures',
        'identity.scope-override': 'user-project-settings',
        'identity.autoload-delta': 'user-project-settings',
        'identity.user-project-duplicates': 'user-project-settings',
        'identity.local-symlink': 'user-project-settings',
    }
    observed_cases = differential.get('cases', {})
    all_passed = set(observed_cases) == set(expected_behaviors)
    for case_id, expected_behavior in expected_behaviors.items():
        result = observed_cases.get(case_id, {})
        passed = (expected_behavior is not None and provenance_pass and runner_pass
                  and result.get('behavior') == expected_behavior
                  and result.get('piPassed') is True and result.get('pisharpPassed') is True
                  and result.get('passed') is True)
        state = {'behavior': expected_behavior, 'testsPassed': True}
        actual_state = {'behavior': expected_behavior, 'testsPassed': bool(result.get('passed'))}
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': state},
            observed={'exitCode': 0 if passed else (runner.get('exitCode', 1) or 1), 'state': actual_state},
            passed=passed,
            command_identity='tools/parity/managed-package-identity-process-differential.py',
            argv=runner.get('argv', []), command_exit_code=0 if passed else (runner.get('exitCode', 1) or 1),
            fixture_identity=fixture_families[case_id],
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'), 'reportSha256': report_sha,
                'piTest': differential.get('piTests', {}).get(case_id),
                'pisharpTest': differential.get('pisharpTests', {}).get(case_id),
            })),
        )
        all_passed = all_passed and passed
    _refresh_checkpoint_result(report, 'identity')
    return all_passed


def _record_removal_listing_cases(report: dict[str, Any], output: Path,
                                  runner_results: list[dict[str, Any]],
                                  test_group_results: dict[str, dict[str, Any]]) -> bool:
    runner = next((item for item in runner_results if item.get('runnerId') == 'package-cli'), {})
    report_path = output / 'package-cli.json'
    differential = _load_json(report_path) if report_path.is_file() else {}
    pi_sha = load_contract()['upstream']['revision']
    provenance_pass = (differential.get('piSha') == pi_sha
                       and differential.get('piSharpBaseSha') == report['provenance']['pisharpSourceSha'])
    cli_tests = test_group_results.get('baseline.package-cli', {})
    tests_pass = (cli_tests.get('exitCode') == 0 and cli_tests.get('counts', {}).get('discovered', 0) > 0
                  and cli_tests.get('counts', {}).get('failed') == 0
                  and cli_tests.get('counts', {}).get('skipped') == 0)
    runner_pass = runner.get('exitCode') == 0 and differential.get('match') is True
    results = differential.get('results', {})
    report_sha = _sha256_file(report_path) if report_path.is_file() else None
    if report_sha is not None:
        relative = report_path.relative_to(output).as_posix()
        report['artifacts'] = [item for item in report['artifacts'] if item['path'] != relative]
        report['artifacts'].append({'path': relative, 'sha256': report_sha})

    case_specs = {
        'remove.npm': ('npmRemoveOutputs', 'npm-git-local-source-fixtures'),
        'remove.git': ('projectGitRemove', 'npm-git-local-source-fixtures'),
        'remove.local': ('spacesUnicodePathProbe', 'user-project-settings'),
        'remove.missing': ('failedRemove', 'user-project-settings'),
        'remove.scope-isolation': ('userListAfterProjectRemoval', 'user-project-settings'),
        'list.empty': ('emptyList', 'user-project-settings'),
        'list.mixed-scopes': ('bothScopesList', 'user-project-settings'),
        'list.filtered': ('listFiltered', 'user-project-settings'),
        'list.missing-install': ('listMissingInstall', 'user-project-settings'),
        'list.paths-filter-indicators': ('listFiltered', 'user-project-settings'),
    }
    case_checks = {
        'remove.npm': lambda state: isinstance(state, list) and len(state) >= 1 and all(
            item.get('exitCode') == 0 and len(item.get('statusLines', [])) == 1 for item in state),
        'remove.git': lambda state: state.get('exitCode') == 0 and len(state.get('statusLines', [])) == 1,
        'remove.local': lambda state: state.get('remove', {}).get('exitCode') == 0
            and state.get('settingsAfterRemove') == [],
        'remove.missing': lambda state: state.get('exitCode') == 1 and state.get('statusLines') == [],
        'remove.scope-isolation': lambda state: isinstance(state, list) and len(state) > 0 and all(
            entry.get('scope') == 'user' for entry in state),
        'list.empty': lambda state: state == ['No packages installed.'],
        'list.mixed-scopes': lambda state: 'User packages:' in state and 'Project packages:' in state,
        'list.filtered': lambda state: isinstance(state, list) and any(entry.get('filtered') is True for entry in state),
        'list.missing-install': lambda state: state.get('installedPath') is None,
        'list.paths-filter-indicators': lambda state: isinstance(state, list) and any(
            entry.get('filtered') is True and entry.get('installedPath') for entry in state),
    }
    fixture_families = {case_id: fixture for case_id, (_, fixture) in case_specs.items()}
    all_passed = True
    for case_id, (field, _) in case_specs.items():
        oracle_result = results.get('pi', {}).get(field)
        actual_result = results.get('pisharp', {}).get(field)
        if case_id == 'remove.local':
            oracle_state = oracle_result if isinstance(oracle_result, dict) else {}
            actual_state = actual_result if isinstance(actual_result, dict) else {}
        else:
            oracle_state = oracle_result
            actual_state = actual_result
        check = case_checks[case_id]
        shape_pass = oracle_state is not None and actual_state is not None
        assertion_pass = bool(shape_pass and check(oracle_state) and check(actual_state))
        passed = bool(provenance_pass and tests_pass and runner_pass and assertion_pass
                      and oracle_state == actual_state)
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state if oracle_state is not None else {}},
            observed={'exitCode': 0 if passed else (runner.get('exitCode', 1) or 1),
                      'state': actual_state if actual_state is not None else {}},
            passed=passed,
            command_identity='tools/parity/package-cli-process-differential.py',
            argv=runner.get('argv', []), command_exit_code=0 if passed else (runner.get('exitCode', 1) or 1),
            fixture_identity=fixture_families[case_id],
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'), 'reportSha256': report_sha,
                'cliTests': cli_tests.get('trx'), 'field': field,
            })),
        )
        all_passed = all_passed and passed
    _refresh_checkpoint_result(report, 'removal-listing')
    return all_passed


def _execute_source_inventory_cases(report: dict[str, Any], output: Path, pi_path: Path,
                                    command: list[str]) -> tuple[dict[str, Any], bool]:
    details = _validate_inventory(pi_path)
    contract = load_contract()
    inventory = _load_json(HERE / 'inventory.json')
    runtime = _runtime_state()
    required_pi_sources = sorted({source for checkpoint in contract['checkpoints']
                                  for source in checkpoint['upstreamSourceReferences']
                                  if source.startswith('packages/coding-agent/')})
    owners = {(repository, path) for repository, path in details['sourceOwnerLinks']}
    owner_links = sorted(path for repository, path in owners if repository == 'pi')
    links_match = set(required_pi_sources).issubset(set(owner_links))
    capability_case_count = sum(len(record['requiredCaseIds']) for record in inventory['capabilities'])
    coverage_match = (set(details['capabilityIds']) == set(contract['requiredPackageCapabilityIds'])
                      and capability_case_count == sum(
                          len(_checkpoint_map(contract)[capability_id]['requiredCaseIds'])
                          for capability_id in contract['requiredPackageCapabilityIds'])
                      and len(details['discoveredSelectors']) == details['executableBindings'])
    observations = {
        'inventory.pisharp': (
            {'repository': 'PhilipGB/pisharp', 'sourceSha': runtime['baseSha']},
            {'repository': details['repository'], 'sourceSha': details['pisharpSourceSha']},
            details['repository'] == 'PhilipGB/pisharp' and details['pisharpSourceSha'] == runtime['baseSha'],
            'source-inventory'),
        'inventory.pi': (
            {'revision': contract['upstream']['revision']},
            {'revision': details['piSha']},
            details['piSha'] == contract['upstream']['revision'],
            'source-inventory'),
        'inventory.source-links': (
            {'requiredPinnedSources': required_pi_sources, 'inventoryPinnedSources': required_pi_sources,
             'allLinksResolve': True},
            {'requiredPinnedSources': required_pi_sources, 'inventoryPinnedSources': owner_links,
             'allLinksResolve': links_match},
            links_match,
            'source-inventory'),
        'inventory.case-coverage': (
            {'requiredCapabilities': len(contract['requiredPackageCapabilityIds']),
             'requiredCases': capability_case_count, 'executableBindings': details['executableBindings'],
             'discoveredSelectors': details['executableBindings']},
            {'requiredCapabilities': len(details['capabilityIds']), 'requiredCases': capability_case_count,
             'executableBindings': details['executableBindings'],
             'discoveredSelectors': len(details['discoveredSelectors'])},
            coverage_match,
            'package-case-map'),
    }
    artifact = {'commandIdentity': 'source-pinned inventory validation', 'argv': command,
                'startedAt': dt.datetime.now(dt.timezone.utc).isoformat(),
                'exitCode': 0 if all(item[2] for item in observations.values()) else 1,
                'details': details, 'observations': {case_id: {
                    'oracle': oracle, 'actual': actual, 'passed': passed,
                } for case_id, (oracle, actual, passed, _) in observations.items()}}
    _persist_command_result(report, output, 'inventory-validation.json', artifact)
    all_passed = True
    for case_id, (oracle, actual, passed, fixture_identity) in observations.items():
        _record_baseline_case(
            report, output, case_id, expected={'exitCode': 0, 'state': oracle},
            observed={'exitCode': 0 if passed else 1, 'state': actual}, passed=passed,
            command_identity='source-pinned inventory validation', argv=command,
            command_exit_code=0 if passed else 1, fixture_identity=fixture_identity,
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'inventorySha256': details['inventorySha256'],
                'piSha': details['piSha'], 'pisharpSourceSha': details['pisharpSourceSha'],
                'sourceOwnerLinks': details['sourceOwnerLinks'],
            })),
        )
        all_passed = all_passed and passed
    _refresh_checkpoint_result(report, 'source-inventory')
    return artifact, all_passed


def _execute_temporary_package_cases(report: dict[str, Any], output: Path, pi_path: Path,
                                    binary_path: Path, profile: Path) -> tuple[list[dict[str, Any]], bool]:
    """Execute the temporary npm/Git package differentials and their native regressions."""
    temporary_profile = profile / 'temporary-resources'
    (temporary_profile / 'tmp').mkdir(parents=True, exist_ok=True)
    runner = ROOT / 'tools/parity/resource-invocation-process-differential.py'
    pinned_pi_sha = load_contract()['upstream']['revision']
    commands: list[dict[str, Any]] = []
    all_passed = True
    for case_id, scenario, test_name in [
        ('distribution.temporary-npm-package', 'temporary-npm-package',
         'ExplicitNpmExtensionLoadsFromTemporaryManagedCacheWithoutChangingPackageSettings'),
        ('distribution.temporary-git-package', 'temporary-git-package',
         'ExplicitGitExtensionLoadsFromTemporaryManagedCacheWithoutChangingPackageSettings'),
        ('distribution.temporary-source-order', 'temporary-mixed-source-order',
         'TemporaryNpmAndGitRootsRetainExplicitSourceOrder'),
    ]:
        result_path = output / f'{scenario}.json'
        argv = [sys.executable, str(runner), '--pi', str(pi_path.resolve()), '--pisharp', str(binary_path),
                '--profile', str(temporary_profile), '--output', str(result_path), '--scenario', scenario]
        started = dt.datetime.now(dt.timezone.utc).isoformat()
        try:
            completed = subprocess.run(argv, cwd=ROOT, capture_output=True, text=True, timeout=900)
            exit_code, stdout, stderr = completed.returncode, completed.stdout, completed.stderr
        except subprocess.TimeoutExpired as error:
            exit_code = 124
            stdout = error.stdout.decode(errors='replace') if isinstance(error.stdout, bytes) else (error.stdout or '')
            stderr = error.stderr.decode(errors='replace') if isinstance(error.stderr, bytes) else (error.stderr or '')
            stderr += '\nTemporary package process differential timed out after 900 seconds.'
        except OSError as error:
            exit_code, stdout, stderr = 127, '', str(error)
        diff_command = {'runnerId': 'pinned temporary resource process differential', 'caseId': case_id,
                        'argv': argv, 'startedAt': started, 'exitCode': exit_code,
                        'stdout': stdout, 'stderr': stderr,
                        'report': result_path.relative_to(output).as_posix() if result_path.is_file() else None}
        _persist_command_result(report, output, f'{case_id.replace(".", "-")}.command.json', diff_command)
        commands.append(diff_command)

        test_filter = f'FullyQualifiedName~PiSharp.Tests.TemporaryManagedExtensionTests.{test_name}'
        trx_name = case_id.replace('.', '-') + '.trx'
        trx_path = output / 'trx' / trx_name
        test_argv = ['dotnet', 'test', str(ROOT / 'tests/PiSharp.Tests/PiSharp.Tests.csproj'), '--no-build',
                     '--no-restore', '--filter', test_filter, '--logger', f'trx;LogFileName={trx_name}',
                     '--results-directory', str(output / 'trx')]
        test_started = dt.datetime.now(dt.timezone.utc).isoformat()
        try:
            test_result = subprocess.run(test_argv, cwd=ROOT, capture_output=True, text=True, timeout=900)
            test_exit, test_stdout, test_stderr = (test_result.returncode, test_result.stdout, test_result.stderr)
        except subprocess.TimeoutExpired as error:
            test_exit = 124
            test_stdout = error.stdout.decode(errors='replace') if isinstance(error.stdout, bytes) else (error.stdout or '')
            test_stderr = error.stderr.decode(errors='replace') if isinstance(error.stderr, bytes) else (error.stderr or '')
            test_stderr += '\nTemporary package regression test timed out after 900 seconds.'
        except OSError as error:
            test_exit, test_stdout, test_stderr = 127, '', str(error)
        counts = _trx_counts(trx_path)
        test_pass = test_exit == 0 and counts == {'discovered': 1, 'passed': 1, 'failed': 0, 'skipped': 0}
        test_command = {'runnerId': 'temporary package .NET regression', 'caseId': case_id,
                        'filter': test_filter, 'argv': test_argv, 'startedAt': test_started,
                        'exitCode': test_exit, 'counts': counts, 'stdout': test_stdout, 'stderr': test_stderr,
                        'trx': trx_path.relative_to(output).as_posix() if trx_path.is_file() else None}
        _persist_command_result(report, output, f'{case_id.replace(".", "-")}.test-command.json', test_command)
        commands.append(test_command)

        diff = _load_json(result_path) if result_path.is_file() else {}
        oracle = diff.get('results', {}).get('pi')
        actual_result = diff.get('results', {}).get('pisharp')
        differential_pass = (exit_code == 0 and diff.get('piSha') == pinned_pi_sha
                             and diff.get('match') is True and isinstance(oracle, dict)
                             and isinstance(actual_result, dict))
        passed = differential_pass and test_pass
        expected = {'exitCode': 0, 'state': oracle if isinstance(oracle, dict) else None}
        observed = {'exitCode': 0 if passed else exit_code or test_exit or 1,
                    'state': actual_result if isinstance(actual_result, dict) else None}
        _record_baseline_case(
            report, output, case_id, expected=expected, observed=observed, passed=passed,
            command_identity='tools/parity/resource-invocation-process-differential.py',
            argv=argv, command_exit_code=0 if passed else exit_code or test_exit or 1,
            fixture_identity='local-npm-git-resource-fixtures',
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': diff.get('piSha'), 'sourceSha': diff.get('piSharpBaseSha'),
                'diffSha256': _sha256_file(result_path) if result_path.is_file() else None,
                'testFilter': test_filter, 'testCounts': counts,
            })),
        )
        all_passed = all_passed and passed
        _refresh_checkpoint_result(report, 'resource-distribution')
    return commands, all_passed


def _execute_resource_process_cases(report: dict[str, Any], output: Path, pi_path: Path,
                                    binary_path: Path, profile: Path) -> tuple[list[dict[str, Any]], bool]:
    """Run pinned CLI/RPC fixtures for local, npm, Git and extension resource selection."""
    runner = ROOT / 'tools/parity/resource-invocation-process-differential.py'
    specs = [
        ('local-package', None),
        ('managed-npm-package', None),
        ('managed-git-package', None),
        ('package-extension', 'manifest'),
        ('package-extension', 'conventions'),
        ('package-extension', 'disabled'),
        ('package-extension', 'index-with-sibling'),
        ('package-extension', 'ignored-sibling'),
        ('malformed-flow', None),
    ]
    commands: list[dict[str, Any]] = []
    reports: dict[tuple[str, str | None], tuple[dict[str, Any], dict[str, Any]]] = {}
    all_passed = True
    for scenario, extension_mode in specs:
        label = scenario if extension_mode is None else f'{scenario}-{extension_mode}'
        result_path = output / f'resource-{label}.json'
        argv = [sys.executable, str(runner), '--pi', str(pi_path.resolve()), '--pisharp', str(binary_path.resolve()),
                '--scenario', scenario, '--output', str(result_path)]
        fixture_identity = 'cli-json-rpc-processes'
        if scenario in ('managed-npm-package', 'managed-git-package'):
            scenario_profile = profile / f'resource-{scenario}'
            (scenario_profile / 'tmp').mkdir(parents=True, exist_ok=True)
            argv.extend(['--profile', str(scenario_profile)])
            fixture_identity = 'local-npm-git-resource-fixtures'
        if extension_mode is not None:
            argv.extend(['--extension-mode', extension_mode])
        started = dt.datetime.now(dt.timezone.utc).isoformat()
        try:
            completed = subprocess.run(argv, cwd=ROOT, capture_output=True, text=True, timeout=900)
            exit_code, stdout, stderr = completed.returncode, completed.stdout, completed.stderr
        except subprocess.TimeoutExpired as error:
            exit_code = 124
            stdout = error.stdout.decode(errors='replace') if isinstance(error.stdout, bytes) else (error.stdout or '')
            stderr = error.stderr.decode(errors='replace') if isinstance(error.stderr, bytes) else (error.stderr or '')
            stderr += '\nResource process differential timed out after 900 seconds.'
        except OSError as error:
            exit_code, stdout, stderr = 127, '', str(error)
        command = {'runnerId': f'resource process differential: {label}',
                   'argv': argv, 'startedAt': started, 'exitCode': exit_code,
                   'stdout': stdout, 'stderr': stderr,
                   'report': result_path.relative_to(output).as_posix() if result_path.is_file() else None}
        _persist_command_result(report, output, f'resource-{label}.command.json', command)
        commands.append(command)
        differential = _load_json(result_path) if result_path.is_file() else {}
        reports[(scenario, extension_mode)] = (differential, command)
        all_passed = all_passed and exit_code == 0

    case_specs = {
        'distribution.local': (('local-package', None),
                               ['providerInput', 'providerRequests', 'promptAccepted', 'commandDescription',
                                'startupAccepted', 'packagePrompt'], 'cli-json-rpc-processes', None),
        'distribution.npm': (('managed-npm-package', None),
                             ['providerInput', 'providerRequests', 'promptAccepted', 'commandDescription',
                              'templateProviderInput', 'extensionResourceDescription', 'extensionConfiguredCount',
                              'installScripts', 'peerDependencyInstalled', 'startupAccepted', 'packagePrompt',
                              'packageSkillMetadata', 'packagePromptMetadata'],
                             'local-npm-git-resource-fixtures', None),
        'distribution.git': (('managed-git-package', None),
                             ['providerInput', 'providerRequests', 'promptAccepted', 'commandDescription',
                              'templateProviderInput', 'extensionResourceDescription', 'extensionConfiguredCount',
                              'installScripts', 'peerDependencyInstalled', 'startupAccepted', 'packagePrompt',
                              'packageSkillMetadata', 'packagePromptMetadata'],
                             'local-npm-git-resource-fixtures', None),
        'distribution.skill': (('managed-npm-package', None),
                               ['providerInput', 'commandDescription', 'packageSkillMetadata'],
                               'local-npm-git-resource-fixtures', None),
        'distribution.prompt': (('managed-npm-package', None),
                                ['templateProviderInput', 'packagePrompt', 'packagePromptMetadata'],
                                'local-npm-git-resource-fixtures', None),
        'distribution.native-extension': (('managed-npm-package', None),
                                          ['extensionResourceDescription', 'extensionConfiguredCount',
                                           'installScripts'],
                                          'local-npm-git-resource-fixtures', None),
        'distribution.manifest': (('package-extension', 'manifest'),
                                  ['providerInput', 'commandDescription', 'packageSkillMetadata',
                                   'extensionLoadTrace'], 'cli-json-rpc-processes', None),
        'distribution.conventional-directories': (('package-extension', 'conventions'),
                                                   ['providerInput', 'commandDescription', 'packageSkillMetadata',
                                                    'packagePrompt', 'packagePromptMetadata', 'extensionLoadTrace'],
                                                   'cli-json-rpc-processes', None),
        'distribution.filters': (('package-extension', 'index-with-sibling'),
                                 ['providerInput', 'commandDescription', 'extensionLoadTrace'],
                                 'cli-json-rpc-processes', 'single-extension-load'),
        'distribution.exclusions': (('package-extension', 'ignored-sibling'),
                                    ['providerInput', 'commandDescription', 'extensionLoadTrace'],
                                    'cli-json-rpc-processes', 'single-extension-load'),
        'distribution.disabled': (('package-extension', 'disabled'),
                                  ['providerInput', 'commandDescription', 'packagePrompt',
                                   'extensionLoadTrace'], 'cli-json-rpc-processes', 'disabled-extension'),
        'distribution.invalid-resource': (('malformed-flow', None),
                                          ['providerInput', 'providerRequests', 'promptAccepted',
                                           'commandDescription', 'errorBeforeResponse'],
                                          'cli-json-rpc-processes', 'invalid-resource'),
        'distribution.source-metadata': (('managed-npm-package', None),
                                         ['packageSkillMetadata', 'packagePromptMetadata'],
                                         'local-npm-git-resource-fixtures', None),
    }
    for case_id, (scenario_key, fields, fixture_identity, policy) in case_specs.items():
        differential, command = reports[scenario_key]
        pi = differential.get('results', {}).get('pi', {})
        actual = differential.get('results', {}).get('pisharp', {})
        keys_present = all(key in pi and key in actual for key in fields)
        oracle_state = {key: pi.get(key) for key in fields}
        actual_state = {key: actual.get(key) for key in fields}
        command_pass = command.get('exitCode') == 0 and differential.get('piSha') == load_contract()['upstream']['revision']
        match = differential.get('match') is True and keys_present and oracle_state == actual_state
        policy_pass = True
        if policy == 'single-extension-load':
            policy_pass = (pi.get('extensionLoadTrace', []).count('configured') == 1
                           and actual.get('extensionLoadTrace', []).count('configured') == 1)
        elif policy == 'disabled-extension':
            policy_pass = all(state.get('commandDescription') is None
                              and state.get('extensionLoadTrace') == []
                              for state in (pi, actual))
        elif policy == 'invalid-resource':
            policy_pass = all(state.get('commandDescription') is None
                              and state.get('providerInput') == '/skill:guide args'
                              and state.get('errorBeforeResponse') is None
                              for state in (pi, actual))
        passed = command_pass and match and policy_pass
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else command.get('exitCode', 1) or 1,
                      'state': actual_state},
            passed=passed,
            command_identity='tools/parity/resource-invocation-process-differential.py',
            argv=command.get('argv', []), command_exit_code=0 if passed else command.get('exitCode', 1) or 1,
            fixture_identity=fixture_identity,
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'reportSha256': _sha256_file(output / command['report']) if command.get('report') else None,
                'projection': fields, 'policy': policy,
            })),
        )
        all_passed = all_passed and passed
    _refresh_checkpoint_result(report, 'resource-distribution')
    return commands, all_passed


def execute_all(pi_path: Path, output: Path, binary_path: Path, command: list[str]) -> int:
    """Run the pinned baseline suites and emit explicit evidence for their cases."""
    output.mkdir(parents=True, exist_ok=True)
    report = _initial_report(output, pi_path, command, binary_path)
    _record_prerequisite_cases(report, output, pi_path)
    runner_results = []
    verifier_test_result = _run_verifier_tests(output)
    runner_results.append(verifier_test_result)
    _record_verifier_cases(report, output, verifier_test_result)
    profile = output / 'profile'
    profile.mkdir(exist_ok=True)
    inventory_result, inventory_passed = _execute_source_inventory_cases(report, output, pi_path, command)
    runner_results.append(inventory_result)
    runner_commands = [
        ('managed-npm', [sys.executable, str(RUNNERS['managed-npm']), '--pi', str(pi_path.resolve()),
                         '--profile', str(profile / 'npm'), '--output', str(output / 'managed-npm.json')]),
        # The npm runner builds the ResourceDiscoveryExtension fixture. Run it
        # before the package CLI baseline so both CLI calibration samples use
        # the same compiled DLL bytes.
        ('package-cli', [sys.executable, str(RUNNERS['package-cli']), '--pi', str(pi_path.resolve()),
                         '--pisharp', str(binary_path.resolve()), '--profile', str(profile / 'cli'),
                         '--output', str(output / 'package-cli.json')]),
        ('managed-npm-update', [sys.executable, str(RUNNERS['managed-npm-update']),
                                '--pi', str(pi_path.resolve()), '--pisharp', str(binary_path.resolve()),
                                '--profile', str(profile / 'npm-update'),
                                '--output', str(output / 'managed-npm-update.json')]),
        ('failure-recovery', [sys.executable, str(RUNNERS['failure-recovery']),
                              '--pi', str(pi_path.resolve()), '--pisharp', str(binary_path.resolve()),
                              '--profile', str(profile / 'failure-recovery'),
                              '--output', str(output / 'managed-package-failure.json')]),
        ('concurrency', [sys.executable, str(RUNNERS['concurrency']),
                         '--pi', str(pi_path.resolve()), '--pisharp', str(binary_path.resolve()),
                         '--profile', str(profile / 'concurrency'),
                         '--output', str(output / 'managed-package-concurrency.json')]),
        ('package-managers', [sys.executable, str(RUNNERS['package-managers']),
                              '--pi', str(pi_path.resolve()), '--pisharp', str(binary_path.resolve()),
                              '--profile', str(profile / 'package-managers'),
                              '--output', str(output / 'package-managers.json')]),
        ('legacy-global', [sys.executable, str(RUNNERS['legacy-global']),
                           '--pi', str(pi_path.resolve()), '--pisharp', str(binary_path.resolve()),
                           '--profile', str(profile / 'legacy-global'),
                           '--output', str(output / 'legacy-global.json')]),
        ('managed-git-install', [sys.executable, str(RUNNERS['managed-git-install']),
                                 '--pi', str(pi_path.resolve()), '--pisharp', str(binary_path.resolve()),
                                 '--profile', str(profile / 'managed-git-install'),
                                 '--output', str(output / 'managed-git-install.json')]),
        ('managed-git-update', [sys.executable, str(RUNNERS['managed-git-update']),
                                '--pi', str(pi_path.resolve()), '--pisharp', str(binary_path.resolve()),
                                '--profile', str(profile / 'managed-git-update'),
                                '--output', str(output / 'managed-git-update.json')]),
        ('bulk-updates', [sys.executable, str(RUNNERS['bulk-updates']),
                          '--pi', str(pi_path.resolve()), '--pisharp', str(binary_path.resolve()),
                          '--profile', str(profile / 'bulk-updates'),
                          '--output', str(output / 'managed-package-bulk-updates.json')]),
        ('configuration-trust', [sys.executable, str(RUNNERS['configuration-trust']),
                                 '--pi', str(pi_path.resolve()), '--pisharp', str(binary_path.resolve()),
                                 '--profile', str(profile / 'configuration-trust'),
                                 '--output', str(output / 'managed-package-configuration.json')]),
        ('startup-reload', [sys.executable, str(RUNNERS['startup-reload']),
                            '--pi', str(pi_path.resolve()),
                            '--profile', str(profile / 'startup-reload'),
                            '--output', str(output / 'managed-package-startup-reload.json')]),
        ('managed-package-identity', [sys.executable, str(RUNNERS['managed-package-identity']),
                                      '--pi', str(pi_path.resolve()),
                                      '--expected-pi-sha', load_contract()['upstream']['revision'],
                                      '--pisharp-tests', str(ROOT / 'tests/PiSharp.Tests/PiSharp.Tests.csproj'),
                                      '--output', str(output / 'managed-package-identity.json')]),
        ('differential-acceptance', [sys.executable, str(RUNNERS['differential-acceptance']),
                                     '--pi', str(pi_path.resolve()), '--pisharp', str(binary_path.resolve()),
                                     '--profile', str(profile / 'differential-acceptance'),
                                     '--output-root', str(output),
                                     '--baseline-profile-root', str(profile),
                                     '--baseline-package-cli', str(output / 'package-cli.json'),
                                     '--baseline-managed-npm', str(output / 'managed-npm.json'),
                                     '--baseline-managed-git', str(output / 'managed-git-install.json'),
                                     '--output', str(output / 'managed-package-d0.json')]),
    ]
    all_runner_success = all(
        next(item for item in report['checkpointResults'] if item['checkpointId'] == checkpoint_id)['status'] == 'passed'
        for checkpoint_id in ('p0-prerequisite-merge', 'p1-milestone-branch')
    ) and inventory_passed
    for runner_id, argv in runner_commands:
        log_path = output / f'{runner_id}.command.json'
        started = dt.datetime.now(dt.timezone.utc).isoformat()
        try:
            completed = subprocess.run(argv, cwd=ROOT, capture_output=True, text=True)
            result = {'runnerId': runner_id, 'argv': argv, 'startedAt': started,
                      'exitCode': completed.returncode, 'stdout': completed.stdout, 'stderr': completed.stderr}
        except OSError as error:
            result = {'runnerId': runner_id, 'argv': argv, 'startedAt': started,
                      'exitCode': 127, 'stdout': '', 'stderr': str(error)}
        log_path.write_bytes(_canonical_bytes(result))
        runner_results.append(result)
        report['artifacts'].append({'path': log_path.relative_to(output).as_posix(), 'sha256': _sha256_file(log_path)})
        all_runner_success = all_runner_success and result['exitCode'] == 0

    test_binary = ROOT / 'tests/PiSharp.Tests/bin/Debug/net10.0/PiSharp.Tests.dll'
    if not test_binary.is_file():
        raise ExecutionUnavailable(f'PiSharp test assembly is missing: {test_binary}; build tests before package verification')
    test_result_dir = output / 'trx'
    test_result_dir.mkdir(exist_ok=True)
    test_group_results: dict[str, dict[str, Any]] = {}
    for case_id, test_filter in BASELINE_DOTNET_FILTERS.items():
        result_name = case_id.replace('.', '-') + '.trx'
        trx_path = test_result_dir / result_name
        argv = ['dotnet', 'test', str(ROOT / 'tests/PiSharp.Tests/PiSharp.Tests.csproj'), '--no-build', '--no-restore',
                '--filter', test_filter, '--logger', f'trx;LogFileName={result_name}',
                '--results-directory', str(test_result_dir)]
        started = dt.datetime.now(dt.timezone.utc).isoformat()
        try:
            completed = subprocess.run(argv, cwd=ROOT, capture_output=True, text=True, timeout=900)
            exit_code = completed.returncode
            stdout, stderr = completed.stdout, completed.stderr
        except subprocess.TimeoutExpired as error:
            exit_code = 124
            stdout = error.stdout.decode(errors='replace') if isinstance(error.stdout, bytes) else (error.stdout or '')
            stderr = error.stderr.decode(errors='replace') if isinstance(error.stderr, bytes) else (error.stderr or '')
            stderr += '\nFocused package test selection timed out after 900 seconds.'
        except OSError as error:
            exit_code, stdout, stderr = 127, '', str(error)
        counts = _trx_counts(trx_path)
        passed = exit_code == 0 and counts['discovered'] > 0 and counts['failed'] == 0 and counts['skipped'] == 0
        result = {'runnerId': 'package-focused .NET tests', 'caseId': case_id, 'filter': test_filter,
                  'argv': argv, 'startedAt': started, 'exitCode': exit_code, 'counts': counts,
                  'stdout': stdout, 'stderr': stderr,
                  'trx': trx_path.relative_to(output).as_posix() if trx_path.is_file() else None}
        log_path = output / (case_id.replace('.', '-') + '.command.json')
        log_path.write_bytes(_canonical_bytes(result))
        report['artifacts'].append({'path': log_path.relative_to(output).as_posix(),
                                    'sha256': _sha256_file(log_path)})
        if trx_path.is_file():
            report['artifacts'].append({'path': trx_path.relative_to(output).as_posix(),
                                        'sha256': _sha256_file(trx_path)})
        test_group_results[case_id] = result
        runner_results.append(result)
        all_runner_success = all_runner_success and passed

    # Baseline cases have independent runtime oracles for both Pi process differentials.
    for case_id, report_file in [
        ('baseline.package-cli', output / 'package-cli.json'),
        ('baseline.managed-npm', output / 'managed-npm.json'),
    ]:
        diff = _load_json(report_file) if report_file.is_file() else {}
        pi_result = diff.get('results', {}).get('pi') if case_id == 'baseline.package-cli' else diff.get('pi')
        actual_result = diff.get('results', {}).get('pisharp') if case_id == 'baseline.package-cli' else diff.get('pisharp')
        matched = diff.get('match') is True and isinstance(pi_result, dict) and isinstance(actual_result, dict)
        scenario = diff.get('scenario', diff.get('fixture', {}).get('package'))
        expected = {'exitCode': 0, 'state': {'scenario': scenario, 'runnerMatch': True}}
        observed = {'exitCode': 0 if matched else 1,
                    'state': {'scenario': scenario, 'runnerMatch': diff.get('match') is True}}
        runner_id = 'existing package differential runners'
        runner_argv = next(item['argv'] for item in runner_results
                           if item.get('runnerId') == ('package-cli' if case_id == 'baseline.package-cli' else 'managed-npm'))
        passed = matched
        case_test = test_group_results[case_id]
        passed = passed and case_test['exitCode'] == 0 and case_test['counts']['discovered'] > 0 \
            and case_test['counts']['failed'] == 0 and case_test['counts']['skipped'] == 0
        _record_baseline_case(
            report, output, case_id, expected=expected, observed=observed, passed=passed,
            command_identity=runner_id, argv=runner_argv, command_exit_code=0 if matched else 1,
            fixture_identity='baseline-runs',
            input_fingerprint=_sha256_bytes(_canonical_bytes({'case': case_id, 'piSha': diff.get('piSha'),
                                                               'sourceSha': diff.get('pisharpSha', diff.get('piSharpBaseSha')),
                                                               'reportSha': _sha256_file(report_file) if report_file.is_file() else None})),
        )

    for case_id in ('baseline.git', 'baseline.settings', 'baseline.resource-integration'):
        result = test_group_results[case_id]
        passed = result['exitCode'] == 0 and result['counts']['discovered'] > 0 \
            and result['counts']['failed'] == 0 and result['counts']['skipped'] == 0
        expected_state = {'filter': result['filter'], 'testsDiscovered': True,
                          'allTestsPassed': True, 'noMandatorySkips': True}
        observed_state = {'filter': result['filter'],
                          'testsDiscovered': result['counts']['discovered'] > 0,
                          'allTestsPassed': result['counts']['failed'] == 0 and result['exitCode'] == 0,
                          'noMandatorySkips': result['counts']['skipped'] == 0}
        expected = {'exitCode': 0, 'state': expected_state}
        observed = {'exitCode': 0 if passed else result['exitCode'] or 1, 'state': observed_state}
        _record_baseline_case(
            report, output, case_id, expected=expected, observed=observed, passed=passed,
            command_identity='package-focused .NET tests', argv=result['argv'],
            command_exit_code=result['exitCode'], fixture_identity='disposable-package-roots',
            input_fingerprint=_sha256_bytes(_canonical_bytes({'case': case_id, 'filter': result['filter'],
                                                               'counts': result['counts']})),
        )

    for checkpoint_id in ('baseline',):
        _refresh_checkpoint_result(report, checkpoint_id)

    cli_cases_passed = _record_cli_process_cases(report, output, runner_results)
    all_runner_success = all_runner_success and cli_cases_passed

    managed_npm_cases_passed = _record_managed_npm_cases(report, output, runner_results)
    all_runner_success = all_runner_success and managed_npm_cases_passed

    startup_reload_cases_passed = _record_startup_reload_cases(
        report, output, runner_results, test_group_results)
    all_runner_success = all_runner_success and startup_reload_cases_passed

    managed_npm_update_cases_passed = _record_npm_update_cases(report, output, runner_results)
    all_runner_success = all_runner_success and managed_npm_update_cases_passed

    package_manager_cases_passed = _record_package_manager_cases(report, output, runner_results)
    all_runner_success = all_runner_success and package_manager_cases_passed

    legacy_global_cases_passed = _record_legacy_global_cases(report, output, runner_results)
    all_runner_success = all_runner_success and legacy_global_cases_passed

    git_install_cases_passed = _record_git_install_cases(report, output, runner_results)
    all_runner_success = all_runner_success and git_install_cases_passed

    git_update_cases_passed = _record_git_update_cases(report, output, runner_results)
    all_runner_success = all_runner_success and git_update_cases_passed

    bulk_update_cases_passed = _record_bulk_update_cases(report, output, runner_results, test_group_results)
    all_runner_success = all_runner_success and bulk_update_cases_passed

    configuration_cases_passed = _record_configuration_cases(
        report, output, runner_results, test_group_results)
    all_runner_success = all_runner_success and configuration_cases_passed

    failure_recovery_cases_passed = _record_failure_recovery_cases(
        report, output, runner_results, test_group_results)
    all_runner_success = all_runner_success and failure_recovery_cases_passed

    concurrency_cases_passed = _record_concurrency_cases(
        report, output, runner_results, test_group_results)
    all_runner_success = all_runner_success and concurrency_cases_passed

    identity_cases_passed = _record_identity_cases(report, output, runner_results)
    all_runner_success = all_runner_success and identity_cases_passed

    removal_cases_passed = _record_removal_listing_cases(report, output, runner_results, test_group_results)
    all_runner_success = all_runner_success and removal_cases_passed

    resource_commands, resource_cases_passed = _execute_resource_process_cases(
        report, output, pi_path, binary_path, profile)
    runner_results.extend(resource_commands)
    all_runner_success = all_runner_success and resource_cases_passed

    temporary_commands, temporary_cases_passed = _execute_temporary_package_cases(
        report, output, pi_path, binary_path, profile)
    runner_results.extend(temporary_commands)
    all_runner_success = all_runner_success and temporary_cases_passed

    differential_acceptance_passed = _record_differential_acceptance_cases(
        report, output, runner_results)
    all_runner_success = all_runner_success and differential_acceptance_passed

    all_runner_success = all_runner_success and verifier_test_result['exitCode'] == 0

    for checkpoint in load_contract()['checkpoints']:
        _refresh_checkpoint_result(report, checkpoint['id'])

    runner_path = output / 'runner-results.json'
    runner_path.write_bytes(_canonical_bytes(runner_results))
    report['artifacts'].append({'path': runner_path.relative_to(output).as_posix(), 'sha256': _sha256_file(runner_path)})
    report['provenance']['runnerCommands'] = [item['argv'] for item in runner_results]
    report['provenance']['runnerExitCodes'] = [item['exitCode'] for item in runner_results]
    report['testResults']['executed'] = sum(item['executed'] for item in report['cases'])
    report['testResults']['failed'] = sum(item['executed'] and not item['passed'] for item in report['cases'])
    report['testResults']['skipped'] = sum(item['skipped'] for item in report['cases'])
    for artifact_path in output.rglob('*'):
        if not artifact_path.is_file() or 'profile' in artifact_path.relative_to(output).parts or artifact_path.name == 'evidence.json':
            continue
        relative = artifact_path.relative_to(output).as_posix()
        if relative not in {item['path'] for item in report['artifacts']}:
            report['artifacts'].append({'path': relative, 'sha256': _sha256_file(artifact_path)})
    report['executionExitCode'] = 1
    report_path = output / 'evidence.json'
    report_path.write_bytes(_canonical_bytes(report))
    if not all_runner_success:
        print(f'One or more prerequisite process runners failed; evidence saved at {report_path}', file=sys.stderr)
        return 3 if any(item['exitCode'] == 127 for item in runner_results) else 1

    quality_exit = execute_quality(output)
    if quality_exit != 0:
        return quality_exit
    if COMMITTED_EVIDENCE_RECEIPT.is_file() and (COMMITTED_EVIDENCE_DIR / 'implementation.json').is_file():
        evidence_exit = execute_committed_evidence(output)
        if evidence_exit != 0:
            return evidence_exit

    completed = _load_json(report_path)
    local_checkpoint_ids = []
    for checkpoint in load_contract()['checkpoints']:
        local_checkpoint_ids.append(checkpoint['id'])
        if checkpoint['id'] == 'committed-evidence':
            break
    local_checkpoint_results = {item['checkpointId']: item for item in completed['checkpointResults']}
    local_case_ids = {case_id for checkpoint in load_contract()['checkpoints']
                      if checkpoint['id'] in local_checkpoint_ids for case_id in checkpoint['requiredCaseIds']}
    local_cases = [item for item in completed['cases'] if item['caseId'] in local_case_ids]
    local_complete = (all(local_checkpoint_results[item]['status'] == 'passed' for item in local_checkpoint_ids)
                      and all(item['executed'] and item['passed'] for item in local_cases))
    completed['executionExitCode'] = 0 if local_complete else 1
    report_path.write_bytes(_canonical_bytes(completed))
    if not local_complete:
        print(f'Local package corpus is incomplete; evidence saved at {report_path}', file=sys.stderr)
        return 1
    print(f'Passed locally executable managed-package checkpoints: {report_path}')
    return 0


def execute_differential_acceptance(pi_path: Path, output: Path, binary_path: Path,
                                    command: list[str]) -> int:
    """Rerun D0 after its functional dependencies have passed in saved evidence."""
    evidence_path = output / 'evidence.json'
    report = _load_json(evidence_path)
    _validate_provenance(report['provenance'], output, allow_synthetic=False)
    checkpoint = _checkpoint_map(load_contract())['differential-acceptance']
    checkpoint_results = {item['checkpointId']: item for item in report['checkpointResults']}
    for dependency in checkpoint['dependencies']:
        if checkpoint_results.get(dependency, {}).get('status') != 'passed':
            raise MandatoryFailure(f'D0 cannot run before dependency {dependency} passes')

    artifact_hashes = {item['path']: item['sha256'] for item in report['artifacts']}
    for relative in ('package-cli.json', 'managed-npm.json', 'managed-git-install.json'):
        path = output / relative
        if not path.is_file() or artifact_hashes.get(relative) != _sha256_file(path):
            raise MandatoryFailure(f'D0 baseline artifact is missing or stale: {relative}')

    profile = output / 'profile'
    profile.mkdir(exist_ok=True)
    argv = [sys.executable, str(RUNNERS['differential-acceptance']),
            '--pi', str(pi_path.resolve()), '--pisharp', str(binary_path.resolve()),
            '--profile', str(profile / 'differential-acceptance'),
            '--output-root', str(output.resolve()), '--baseline-profile-root', str(profile.resolve()),
            '--baseline-package-cli', str(output / 'package-cli.json'),
            '--baseline-managed-npm', str(output / 'managed-npm.json'),
            '--baseline-managed-git', str(output / 'managed-git-install.json'),
            '--output', str(output / 'managed-package-d0.json')]
    started = dt.datetime.now(dt.timezone.utc).isoformat()
    try:
        completed = subprocess.run(argv, cwd=ROOT, capture_output=True, text=True, timeout=2700)
        result = {'runnerId': 'differential-acceptance', 'argv': argv, 'startedAt': started,
                  'exitCode': completed.returncode, 'stdout': completed.stdout, 'stderr': completed.stderr}
    except subprocess.TimeoutExpired as error:
        stdout = error.stdout.decode(errors='replace') if isinstance(error.stdout, bytes) else (error.stdout or '')
        stderr = error.stderr.decode(errors='replace') if isinstance(error.stderr, bytes) else (error.stderr or '')
        result = {'runnerId': 'differential-acceptance', 'argv': argv, 'startedAt': started,
                  'exitCode': 124, 'stdout': stdout,
                  'stderr': stderr + '\nD0 checkpoint timed out after 2700 seconds.'}
    except OSError as error:
        result = {'runnerId': 'differential-acceptance', 'argv': argv, 'startedAt': started,
                  'exitCode': 127, 'stdout': '', 'stderr': str(error)}

    command_path = output / 'differential-acceptance.command.json'
    command_path.write_bytes(_canonical_bytes(result))
    runner_path = output / 'runner-results.json'
    runner_results = _load_json(runner_path)
    runner_results = [item for item in runner_results
                      if item.get('runnerId') != 'differential-acceptance']
    runner_results.append(result)
    runner_path.write_bytes(_canonical_bytes(runner_results))

    checkpoint_passed = _record_differential_acceptance_cases(report, output, runner_results)
    report['provenance']['runnerCommands'] = [item['argv'] for item in runner_results]
    report['provenance']['runnerExitCodes'] = [item['exitCode'] for item in runner_results]
    report['testResults']['executed'] = sum(item['executed'] for item in report['cases'])
    report['testResults']['failed'] = sum(item['executed'] and not item['passed'] for item in report['cases'])
    report['testResults']['skipped'] = sum(item['skipped'] for item in report['cases'])
    all_cases_complete = all(item['executed'] and item['passed'] for item in report['cases'])
    all_runners_succeeded = all(item['exitCode'] == 0 for item in runner_results)
    report['executionExitCode'] = 0 if all_cases_complete and all_runners_succeeded else 1

    artifacts = []
    for artifact_path in sorted(output.rglob('*')):
        relative = artifact_path.relative_to(output)
        if (not artifact_path.is_file() or 'profile' in relative.parts
                or artifact_path.name == 'evidence.json'):
            continue
        artifacts.append({'path': relative.as_posix(), 'sha256': _sha256_file(artifact_path)})
    report['artifacts'] = artifacts
    evidence_path.write_bytes(_canonical_bytes(report))
    if checkpoint_passed:
        print(f'Passed checkpoint differential-acceptance: {evidence_path}')
        return 0
    print(f'Differential acceptance failed; evidence saved at {evidence_path}', file=sys.stderr)
    return 3 if result['exitCode'] in (124, 127) else 1


def execute_quality(output: Path) -> int:
    """Run local quality gates and record machine-readable test outcomes."""
    evidence_path = output / 'evidence.json'
    report = _load_json(evidence_path)
    _validate_provenance(report['provenance'], output, allow_synthetic=False)
    checkpoint = _checkpoint_map(load_contract())['quality']
    checkpoint_results = {item['checkpointId']: item for item in report['checkpointResults']}
    for dependency in checkpoint['dependencies']:
        if checkpoint_results.get(dependency, {}).get('status') != 'passed':
            raise MandatoryFailure(f'Quality cannot run before dependency {dependency} passes')

    quality_root = output / 'quality'
    command_root = quality_root / 'commands'
    trx_root = output / 'quality-trx'
    command_root.mkdir(parents=True, exist_ok=True)
    trx_root.mkdir(parents=True, exist_ok=True)
    command_results: dict[str, dict[str, Any]] = {}
    artifacts_by_path = {item['path']: item for item in report['artifacts']}

    def update_artifact(path: Path) -> None:
        relative = path.relative_to(output).as_posix()
        if path.is_file():
            artifacts_by_path[relative] = {'path': relative, 'sha256': _sha256_file(path)}
        else:
            artifacts_by_path.pop(relative, None)
        report['artifacts'] = [artifacts_by_path[item] for item in sorted(artifacts_by_path)]
        evidence_path.write_bytes(_canonical_bytes(report))

    def run(name: str, argv: list[str], *, env_overrides: dict[str, str] | None = None,
            timeout: int = 900) -> dict[str, Any]:
        environment = dict(os.environ)
        if env_overrides:
            environment.update(env_overrides)
        started = dt.datetime.now(dt.timezone.utc).isoformat()
        try:
            completed = subprocess.run(argv, cwd=ROOT, env=environment, capture_output=True,
                                       text=True, timeout=timeout)
            exit_code, stdout, stderr = completed.returncode, completed.stdout, completed.stderr
        except subprocess.TimeoutExpired as error:
            exit_code = 124
            stdout = error.stdout.decode(errors='replace') if isinstance(error.stdout, bytes) else (error.stdout or '')
            stderr = error.stderr.decode(errors='replace') if isinstance(error.stderr, bytes) else (error.stderr or '')
            stderr += f'\nQuality command timed out after {timeout} seconds.'
        except OSError as error:
            exit_code, stdout, stderr = 127, '', str(error)
        relative_log = f'quality/commands/{name}.json'
        log_path = output / relative_log
        log_path.write_bytes(_canonical_bytes({'argv': argv, 'startedAt': started, 'exitCode': exit_code,
                                               'stdout': stdout, 'stderr': stderr}))
        update_artifact(log_path)
        result = {'argv': argv, 'startedAt': started, 'exitCode': exit_code,
                  'logPath': relative_log, 'logSha256': _sha256_file(log_path),
                  'stdout': stdout, 'stderr': stderr}
        command_results[name] = {key: value for key, value in result.items() if key not in ('stdout', 'stderr')}
        return result

    format_result = run('format', ['dotnet', 'format', 'PiSharp.slnx', '--verify-no-changes', '--no-restore'], timeout=1800)
    build_argv = ['dotnet', 'build', 'PiSharp.slnx', '--no-restore', '--warnaserror']
    build_result = run('build', build_argv, timeout=1800)
    if build_result['exitCode'] != 0:
        # A transient CLR startup/runtime failure can terminate MSBuild without
        # reporting a project diagnostic. Preserve that attempt and retry once.
        build_result = run('build-retry', build_argv, timeout=1800)
    full_trx_name = 'quality-full.trx'
    full_result = run('dotnet-suite', [
        'dotnet', 'test', 'PiSharp.slnx', '--no-build', '--no-restore',
        '--logger', f'trx;LogFileName={full_trx_name}', '--results-directory', str(trx_root.resolve()),
    ], env_overrides={'TMPDIR': '/tmp'}, timeout=3600)
    full_trx = trx_root / full_trx_name
    update_artifact(full_trx)
    full_counts = _trx_counts(full_trx)

    package_filter = '|'.join([
        'FullyQualifiedName~ResourcePackageCommandProcessTests',
        'FullyQualifiedName~ManagedResourcePackageStoreTests',
        'FullyQualifiedName~ManagedGitPackageStoreTests',
        'FullyQualifiedName~PackageSettingsReaderTests',
        'FullyQualifiedName~PackageSettingsTests',
        'FullyQualifiedName~ResourcePackageLoadingTests',
        'FullyQualifiedName~ResourceCatalogTests',
        'FullyQualifiedName~ProjectTrustTests',
        'FullyQualifiedName~PackageExtensionBootstrapTests',
    ])
    focused_results = {}
    for name, test_filter in (
        ('package', package_filter),
        ('architecture', 'FullyQualifiedName~ArchitectureTests'),
    ):
        trx_name = f'quality-focused-{name}.trx'
        test_result = run(f'focused-{name}', [
            'dotnet', 'test', str(ROOT / 'tests/PiSharp.Tests/PiSharp.Tests.csproj'),
            '--no-build', '--no-restore', '--filter', test_filter,
            '--logger', f'trx;LogFileName={trx_name}', '--results-directory', str(trx_root.resolve()),
        ], timeout=1800)
        trx_path = trx_root / trx_name
        update_artifact(trx_path)
        focused_results[name] = {'exitCode': test_result['exitCode'], 'counts': _trx_counts(trx_path),
                                 'trxPath': trx_path.relative_to(output).as_posix() if trx_path.is_file() else None,
                                 'trxSha256': _sha256_file(trx_path) if trx_path.is_file() else None}

    python_results = [
        run('python-managed-packages', [sys.executable, '-m', 'unittest', 'discover',
            '-s', 'tools/parity/managed_packages', '-p', 'test_*.py']),
        run('python-terminal', [sys.executable, '-m', 'unittest', 'discover',
            '-s', 'tools/parity/terminal', '-p', 'test_*.py']),
        run('python-builtin-differential', [sys.executable, '-m', 'unittest', 'discover',
            '-s', 'tools/parity', '-p', 'test_builtin_coding_tool_differential.py']),
    ]
    node_result = run('node-terminal', ['npm', 'test', '--prefix', 'tools/parity/terminal'], timeout=900)

    mcp_build = run('mcp-build', [
        'dotnet', 'build', 'tools/McpConformance/PiSharp.McpConformance.csproj',
        '--no-restore', '--warnaserror',
    ], timeout=1800)
    mcp_run = run('mcp-conformance', ['node', 'tools/parity/mcp-conformance/run.mjs'], timeout=1800)
    docs_result = run('docs', [sys.executable, 'tools/validate_doc_paths.py'], timeout=900)

    differential_checkpoints = [
        'source-inventory', 'baseline', 'cli', 'npm-install', 'npm-update', 'package-managers',
        'legacy-global', 'git-install', 'git-update', 'identity', 'removal-listing', 'bulk-updates',
        'configuration-trust', 'resource-distribution', 'startup-reload', 'failure-recovery',
        'concurrency', 'differential-acceptance',
    ]
    differential_results = []
    for checkpoint_id in differential_checkpoints:
        differential_results.append(run(f'differential-{checkpoint_id}', [
            sys.executable, str(HERE / 'verify.py'), 'check', '--checkpoint', checkpoint_id,
            '--evidence', str(output.resolve()),
        ], timeout=900))

    current_binary_sha = _sha256_file(ROOT / report['provenance']['binaryIdentity']['path'])
    build_output = build_result['stdout'] + '\n' + build_result['stderr']
    warning_summary = re.search(r'(\d+)\s+Warning\(s\)', build_output, re.IGNORECASE)
    build_warnings = int(warning_summary.group(1)) if warning_summary else 0

    def unittest_count(result: dict[str, Any]) -> int:
        match = re.search(r'Ran\s+(\d+)\s+tests?', result['stdout'] + '\n' + result['stderr'])
        return int(match.group(1)) if match else 0

    node_output = node_result['stdout'] + '\n' + node_result['stderr']
    node_counts = {
        label: int(count)
        for label, count in re.findall(r'^(?:#|ℹ)\s+(tests|pass|fail|skipped)\s+(\d+)\s*$',
                                       node_output, re.MULTILINE)
    }
    node_test_counts = {
        'discovered': node_counts.get('tests', 0),
        'passed': node_counts.get('pass', 0),
        'failed': node_counts.get('fail', 1),
        'skipped': node_counts.get('skipped', 1),
    }

    assertions = {
        'quality.format': format_result['exitCode'] == 0,
        'quality.build': build_result['exitCode'] == 0 and build_warnings == 0,
        'quality.dotnet-suite': (full_result['exitCode'] == 0 and full_counts['discovered'] > 0
                                 and full_counts['failed'] == 0 and full_counts['skipped'] == 0),
        'quality.focused-tests': all(
            item['exitCode'] == 0 and item['counts']['discovered'] > 0
            and item['counts']['failed'] == 0 and item['counts']['skipped'] == 0
            for item in focused_results.values()),
        'quality.python-parity': all(result['exitCode'] == 0 and unittest_count(result) > 0
                                     for result in python_results),
        'quality.node-terminal': (node_result['exitCode'] == 0 and node_test_counts['discovered'] > 0
                                  and node_test_counts['failed'] == 0
                                  and node_test_counts['skipped'] == 0
                                  and node_test_counts['passed'] == node_test_counts['discovered']),
        'quality.mcp': mcp_build['exitCode'] == 0 and mcp_run['exitCode'] == 0,
        'quality.docs': docs_result['exitCode'] == 0,
        'quality.differentials': all(result['exitCode'] == 0 for result in differential_results)
            and current_binary_sha == report['provenance']['binaryIdentity']['sha256'],
    }

    quality_report = {
        'schemaVersion': 1,
        'pisharpSourceSha': report['provenance']['pisharpSourceSha'],
        'binarySha256': current_binary_sha,
        'commands': command_results,
        'fullDotnetSuite': {'exitCode': full_result['exitCode'], 'counts': full_counts,
                            'trxPath': full_trx.relative_to(output).as_posix() if full_trx.is_file() else None,
                            'trxSha256': _sha256_file(full_trx) if full_trx.is_file() else None},
        'focusedDotnetTests': focused_results,
        'pythonTests': [{'exitCode': item['exitCode'], 'testsRun': unittest_count(item),
                         'logPath': item['logPath'], 'logSha256': item['logSha256']} for item in python_results],
        'nodeTests': {'exitCode': node_result['exitCode'], 'counts': node_test_counts,
                      'logPath': node_result['logPath'], 'logSha256': node_result['logSha256']},
        'mcp': {'buildExitCode': mcp_build['exitCode'], 'runExitCode': mcp_run['exitCode']},
        'docsExitCode': docs_result['exitCode'],
        'differentialCheckpoints': [{'checkpointId': checkpoint_id, 'exitCode': result['exitCode']}
                                    for checkpoint_id, result in zip(differential_checkpoints, differential_results)],
        'assertions': assertions,
        'match': all(assertions.values()),
    }
    quality_report_path = quality_root / 'quality-results.json'
    quality_report_path.write_bytes(_canonical_bytes(quality_report))

    quality_checkpoint = _checkpoint_map(load_contract())['quality']
    runner_results = _load_json(output / 'runner-results.json')
    aggregate_result = {
        'runnerId': 'quality',
        'argv': [command for result in command_results.values() for command in result['argv']],
        'startedAt': dt.datetime.now(dt.timezone.utc).isoformat(),
        'exitCode': 0 if all(assertions.values()) else 1,
        'stdout': json.dumps(assertions, sort_keys=True), 'stderr': '',
    }
    runner_results = [item for item in runner_results if item.get('runnerId') != 'quality']
    runner_results.append(aggregate_result)
    (output / 'runner-results.json').write_bytes(_canonical_bytes(runner_results))

    command_by_case = {
        'quality.format': ('dotnet format', [format_result], 'repository-quality-commands'),
        'quality.build': ('dotnet build', [build_result], 'repository-quality-commands'),
        'quality.dotnet-suite': ('dotnet test', [full_result], 'trx-test-results'),
        'quality.focused-tests': ('dotnet test',
                                  [command_results['focused-package'], command_results['focused-architecture']],
                                  'trx-test-results'),
        'quality.python-parity': ('python parity tests', python_results, 'repository-quality-commands'),
        'quality.node-terminal': ('Node terminal tests', [node_result], 'repository-quality-commands'),
        'quality.mcp': ('MCP conformance', [mcp_build, mcp_run], 'repository-quality-commands'),
        'quality.docs': ('docs path validation', [docs_result], 'repository-quality-commands'),
        'quality.differentials': ('tools/parity/managed_packages/verify.py check --checkpoint',
                                  differential_results, 'repository-quality-commands'),
    }
    for case_id in quality_checkpoint['requiredCaseIds']:
        command_identity, results, fixture_identity = command_by_case[case_id]
        passed = assertions[case_id]
        state = quality_report['assertions'][case_id]
        expected = {'exitCode': 0, 'state': state}
        observed = {'exitCode': 0 if passed else 1, 'state': state}
        argv = results[0]['argv'] if results else []
        _record_baseline_case(
            report, output, case_id,
            expected=expected, observed=observed, passed=passed,
            command_identity=command_identity, argv=argv,
            command_exit_code=0 if passed else 1, fixture_identity=fixture_identity,
            input_fingerprint=_sha256_bytes(_canonical_bytes({
                'case': case_id, 'binarySha256': current_binary_sha,
                'qualityReportSha256': _sha256_file(quality_report_path), 'assertion': passed,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = 'strict' if passed else 'mismatch'
        recorded['assertions'] = {case_id: bool(passed)}
        recorded['passed'] = bool(passed)
        _write_case_artifact(report, output, recorded)

    _refresh_checkpoint_result(report, 'quality')
    report['provenance']['runnerCommands'] = [item['argv'] for item in runner_results]
    report['provenance']['runnerExitCodes'] = [item['exitCode'] for item in runner_results]
    report['testResults']['executed'] = sum(item['executed'] for item in report['cases'])
    report['testResults']['failed'] = sum(item['executed'] and not item['passed'] for item in report['cases'])
    report['testResults']['skipped'] = sum(item['skipped'] for item in report['cases'])
    all_cases_complete = all(item['executed'] and item['passed'] for item in report['cases'])
    all_runners_succeeded = all(item['exitCode'] == 0 for item in runner_results)
    report['executionExitCode'] = 0 if all_cases_complete and all_runners_succeeded else 1
    artifacts = []
    for artifact_path in sorted(output.rglob('*')):
        relative = artifact_path.relative_to(output)
        if (not artifact_path.is_file() or 'profile' in relative.parts
                or artifact_path.name == 'evidence.json'):
            continue
        artifacts.append({'path': relative.as_posix(), 'sha256': _sha256_file(artifact_path)})
    report['artifacts'] = artifacts
    evidence_path.write_bytes(_canonical_bytes(report))
    if all(assertions.values()):
        print(f'Passed checkpoint quality: {evidence_path}')
        return 0
    print(f'Quality gates failed; evidence saved at {evidence_path}', file=sys.stderr)
    return 3 if any(item['exitCode'] in (124, 127) for values in command_by_case.values()
                    for item in values[1]) else 1


def _committed_file_matches_head(path: Path) -> bool:
    try:
        relative = path.resolve().relative_to(ROOT).as_posix()
        completed = subprocess.run(['git', 'show', f'HEAD:{relative}'], cwd=ROOT,
                                   capture_output=True)
    except (ValueError, OSError):
        return False
    return completed.returncode == 0 and path.is_file() and completed.stdout == path.read_bytes()


def _receipt_links_resolve(path: Path) -> bool:
    body = path.read_text(encoding='utf-8')
    links = re.findall(r'\[[^\]]+\]\(([^)]+)\)', body)
    for link in links:
        target = link.split('#', 1)[0].split('?', 1)[0]
        if not target or target.startswith(('https://', 'http://', 'mailto:')):
            continue
        if not (path.parent / target).resolve().is_file():
            return False
    return True


def _load_committed_evidence_inputs(output: Path) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any], dict[str, dict[str, Any]]]:
    evidence = _load_json(output / 'evidence.json')
    contract = load_contract()
    inventory = _load_json(HERE / 'inventory.json')
    implementation_path = COMMITTED_EVIDENCE_DIR / 'implementation.json'
    source_sha = (_load_json(implementation_path).get('sourceSha')
                  if implementation_path.is_file() else evidence['provenance']['pisharpSourceSha'])
    if not isinstance(source_sha, str) or not re.fullmatch(r'[0-9a-f]{40}', source_sha):
        raise VerificationError('Committed implementation fixture has an invalid source SHA')
    if not _source_commit_matches_current_tree(source_sha, _git('rev-parse', 'HEAD')):
        raise MandatoryFailure('Committed evidence source SHA is not the current implementation or a docs-only ancestor')
    bundle = evidence_artifacts.build_bundle(
        evidence, contract, inventory, output, Path(evidence['provenance']['piPath']), ROOT, source_sha)
    return evidence, contract, inventory, bundle


def prepare_committed_evidence(output: Path) -> int:
    """Write the compact deterministic fixture bundle from a passing local run."""
    evidence, contract, _, bundle = _load_committed_evidence_inputs(output)
    source_changes = subprocess.run(['git', 'diff', '--quiet', 'HEAD', '--', 'src', 'tests', 'tools/parity'],
                                    cwd=ROOT, capture_output=True, text=True)
    if source_changes.returncode != 0:
        raise MandatoryFailure('Commit the implementation sources before preparing reproducible evidence')
    result_map = {item['checkpointId']: item for item in evidence['checkpointResults']}
    for checkpoint in contract['checkpoints']:
        if checkpoint['id'] == 'committed-evidence':
            break
        if result_map[checkpoint['id']]['status'] != 'passed':
            raise MandatoryFailure(f'Cannot prepare a committed receipt before {checkpoint["id"]} passes')
    if any(not case['executed'] or not case['passed'] for case in evidence['cases']
           if case['checkpoint'] in {item['id'] for item in contract['checkpoints']
                                     if item['id'] != 'committed-evidence'
                                     and contract['requiredCheckpointIds'].index(item['id'])
                                     < contract['requiredCheckpointIds'].index('committed-evidence')}):
        raise MandatoryFailure('Cannot prepare a committed receipt with a missing or failed prerequisite case')
    evidence_artifacts.write_bundle(bundle, COMMITTED_EVIDENCE_DIR)
    print(f'Wrote deterministic managed-package evidence fixtures to {COMMITTED_EVIDENCE_DIR}')
    return 0


def _portable_concurrency_fixture_matches(name: str, committed: dict[str, Any],
                                          expected: dict[str, Any],
                                          committed_cases: dict[str, Any] | None = None) -> bool:
    """Compare historical receipts while permitting independently validated race outcomes."""
    variable_cases = {'concurrent.same-npm-install', 'concurrent.same-git-install'}
    if name == 'case-results.json':
        committed_cases = committed.get('cases')
        expected_cases = expected.get('cases')
        if not isinstance(committed_cases, list) or not isinstance(expected_cases, list):
            return False
        original_counts = committed.get('classificationCounts')
        if not isinstance(original_counts, dict) or original_counts != {
            classification: sum(case.get('classification') == classification for case in committed_cases
                                if case.get('executed') is True)
            for classification in ('strict', 'equivalent', 'intentional-safety-difference')
        }:
            return False
        adjusted = json.loads(json.dumps(committed))
        current = {case['caseId']: case for case in expected_cases}
        for case in adjusted['cases']:
            case_id = case.get('caseId')
            if case_id not in variable_cases or case_id not in current:
                continue
            other = current[case_id]
            if (case.get('executed') is not True or case.get('passed') is not True
                    or other.get('executed') is not True or other.get('passed') is not True
                    or case.get('classification') not in ('strict', 'intentional-safety-difference')
                    or other.get('classification') not in ('strict', 'intentional-safety-difference')):
                return False
            if case['classification'] != other['classification']:
                case['classification'] = other['classification']
                case['oracleSha256'] = other['oracleSha256']
        adjusted['classificationCounts'] = expected.get('classificationCounts')
        return adjusted == expected
    if name == 'inventory.json':
        if not isinstance(committed_cases, dict) or not isinstance(committed_cases.get('cases'), list):
            return False
        adjusted = json.loads(json.dumps(committed))
        expected_capabilities = {item['capabilityId']: item for item in expected.get('capabilities', [])}
        for capability in adjusted.get('capabilities', []):
            if capability.get('capabilityId') == 'concurrency' and 'concurrency' in expected_capabilities:
                relevant = [case for case in committed_cases['cases']
                            if case.get('caseId') in capability.get('requiredCaseIds', [])]
                counts = {classification: sum(case.get('classification') == classification for case in relevant)
                          for classification in ('strict', 'equivalent', 'intentional-safety-difference')}
                if capability.get('classifications') != counts:
                    return False
                capability['classifications'] = expected_capabilities['concurrency']['classifications']
        return adjusted == expected
    return False


def execute_committed_evidence(output: Path) -> int:
    evidence_path = output / 'evidence.json'
    report = _load_json(evidence_path)
    _validate_provenance(report['provenance'], output, allow_synthetic=False)
    checkpoint = _checkpoint_map(load_contract())['committed-evidence']
    checkpoint_results = {item['checkpointId']: item for item in report['checkpointResults']}
    for dependency in checkpoint['dependencies']:
        if checkpoint_results.get(dependency, {}).get('status') != 'passed':
            raise MandatoryFailure(f'Committed evidence cannot run before {dependency} passes')

    evidence, contract, inventory, bundle = _load_committed_evidence_inputs(output)
    fixture_paths = {name: COMMITTED_EVIDENCE_DIR / name for name in bundle}
    try:
        committed_cases = _load_json(COMMITTED_EVIDENCE_DIR / 'case-results.json')
    except VerificationError:
        committed_cases = None
    fixture_matches = {}
    for name, expected in bundle.items():
        path = fixture_paths[name]
        try:
            committed = _load_json(path)
            fixture_matches[name] = (committed == expected
                                     or _portable_concurrency_fixture_matches(name, committed, expected,
                                                                              committed_cases))
        except VerificationError:
            fixture_matches[name] = False

    receipt_text = COMMITTED_EVIDENCE_RECEIPT.read_text(encoding='utf-8') if COMMITTED_EVIDENCE_RECEIPT.is_file() else ''
    merge_sha = evidence['provenance']['baseSha']
    required_receipt_values = [
        'Actual PR #26 merge SHA', merge_sha,
        'New branch base SHA', evidence['provenance']['baseSha'],
        'Pinned Pi SHA', evidence['provenance']['piSha'],
        'Capability inventory', 'Already-correct behaviour', 'Confirmed mismatches',
        'Corrections', 'Required case inventory', 'Differential results',
        'Same-Pi calibration', 'Failure and concurrency results',
        'Intentional differences', 'Remaining limitations', 'Local quality results',
    ]
    receipt_valid = all(value in receipt_text for value in required_receipt_values)
    receipt_valid = receipt_valid and _receipt_links_resolve(COMMITTED_EVIDENCE_RECEIPT)

    active_heading = 'Complete Managed Package Lifecycle and Resource Distribution Parity'
    receipt_link = 'parity/fixtures/managed-package-lifecycle-parity.md'
    feature_text = (ROOT / 'docs/parity/feature-matrix.md').read_text(encoding='utf-8')
    continuation_text = (ROOT / 'docs/continuation.md').read_text(encoding='utf-8')
    todo_text = (ROOT / 'TODO.md').read_text(encoding='utf-8')
    feature_valid = active_heading in feature_text and receipt_link in feature_text
    continuation_valid = active_heading in continuation_text and receipt_link in continuation_text
    todo_valid = active_heading in todo_text and receipt_link in todo_text

    ledger_path = ROOT / 'docs/parity/execution-ledger.json'
    ledger_valid = False
    try:
        ledger = _load_json(ledger_path)
        entry = ledger['managed_package_lifecycle_parity_milestone']
        ledger_valid = (
            entry.get('branch') == evidence['provenance']['branch']
            and entry.get('branch_base_sha') == evidence['provenance']['baseSha']
            and entry.get('pi_reference') == evidence['provenance']['piSha']
            and entry.get('receipt') == 'docs/parity/fixtures/managed-package-lifecycle-parity.md'
            and entry.get('inventory') == 'docs/parity/fixtures/managed-package-lifecycle-parity/inventory.json'
        )
    except (VerificationError, KeyError, TypeError):
        ledger_valid = False

    tracked_paths = [COMMITTED_EVIDENCE_RECEIPT, ledger_path,
                     ROOT / 'docs/parity/feature-matrix.md', ROOT / 'docs/continuation.md',
                     ROOT / 'TODO.md', *fixture_paths.values()]
    all_committed = all(_committed_file_matches_head(path) for path in tracked_paths)
    source_links_valid = fixture_matches.get('source-links.json', False)
    inventory_valid = fixture_matches.get('inventory.json', False)
    coverage_valid = fixture_matches.get('case-coverage.json', False)
    case_data_valid = fixture_matches.get('case-results.json', False)
    quality_valid = fixture_matches.get('quality-summary.json', False)
    differential_valid = fixture_matches.get('differential-summary.json', False)

    validations = {
        'evidence.feature-matrix': feature_valid,
        'evidence.execution-ledger': ledger_valid,
        'evidence.continuation': continuation_valid,
        'evidence.todo': todo_valid,
        'evidence.receipt': receipt_valid and all_committed,
        'evidence.case-coverage': coverage_valid and case_data_valid,
        'evidence.source-links': source_links_valid and inventory_valid and quality_valid and differential_valid,
    }
    details_by_case = {
        'evidence.feature-matrix': {'activeMilestoneAndReceiptLink': feature_valid},
        'evidence.execution-ledger': {'ledgerMatchesBranchBasePinAndReceipt': ledger_valid},
        'evidence.continuation': {'activeMilestoneAndReceiptLink': continuation_valid},
        'evidence.todo': {'activeMilestoneAndReceiptLink': todo_valid},
        'evidence.receipt': {'requiredSectionsAndLinks': receipt_valid, 'fixturesCommittedAtHead': all_committed},
        'evidence.case-coverage': {'contractCoverageMatches': coverage_valid, 'regeneratedCaseResultsMatch': case_data_valid},
        'evidence.source-links': {'sourceLinksResolve': source_links_valid, 'inventoryMatches': inventory_valid,
                                  'qualitySummaryMatches': quality_valid, 'differentialSummaryMatches': differential_valid},
    }
    command = [sys.executable, str(HERE / 'verify.py'), 'execute', '--checkpoint', 'committed-evidence',
               '--pi', evidence['provenance']['piPath'], '--output', str(output.resolve())]
    command_identity = checkpoint['requiredExecutableVerification'][0]
    for case_id in checkpoint['requiredCaseIds']:
        passed = validations[case_id]
        details = details_by_case[case_id]
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': {**details, 'passed': True}},
            observed={'exitCode': 0 if passed else 1, 'state': {**details, 'passed': passed}},
            passed=passed, command_identity=command_identity, argv=command,
            command_exit_code=0 if passed else 1,
            fixture_identity=('committed-parity-receipt' if case_id in {
                'evidence.feature-matrix', 'evidence.continuation', 'evidence.todo', 'evidence.receipt'
            } else 'schema-validated-reports'),
            input_fingerprint=_sha256_bytes(_canonical_bytes({'case': case_id, 'details': details})),
        )

    _refresh_checkpoint_result(report, 'committed-evidence')
    report['testResults']['executed'] = sum(item['executed'] for item in report['cases'])
    report['testResults']['failed'] = sum(item['executed'] and not item['passed'] for item in report['cases'])
    report['testResults']['skipped'] = sum(item['skipped'] for item in report['cases'])
    runner_result = {
        'runnerId': 'committed-evidence', 'argv': command,
        'startedAt': dt.datetime.now(dt.timezone.utc).isoformat(),
        'exitCode': 0 if all(validations.values()) else 1,
        'stdout': json.dumps(validations, sort_keys=True), 'stderr': '',
    }
    runner_results_path = output / 'runner-results.json'
    runner_results = _load_json(runner_results_path) if runner_results_path.is_file() else []
    runner_results = [item for item in runner_results if item.get('runnerId') != 'committed-evidence']
    runner_results.append(runner_result)
    runner_results_path.write_bytes(_canonical_bytes(runner_results))
    report['artifacts'] = [item for item in report['artifacts'] if item['path'] != 'runner-results.json']
    report['artifacts'].append({'path': 'runner-results.json', 'sha256': _sha256_file(runner_results_path)})
    report['provenance']['runnerCommands'] = [item['argv'] for item in runner_results]
    report['provenance']['runnerExitCodes'] = [item['exitCode'] for item in runner_results]
    for artifact_path in sorted(output.rglob('*')):
        if (not artifact_path.is_file() or 'profile' in artifact_path.relative_to(output).parts
                or artifact_path.name == 'evidence.json'):
            continue
        relative = artifact_path.relative_to(output).as_posix()
        existing = next((item for item in report['artifacts'] if item['path'] == relative), None)
        digest = _sha256_file(artifact_path)
        if existing is None:
            report['artifacts'].append({'path': relative, 'sha256': digest})
        else:
            existing['sha256'] = digest
    report['artifacts'].sort(key=lambda item: item['path'])
    local_checkpoint_ids = []
    for item in load_contract()['checkpoints']:
        local_checkpoint_ids.append(item['id'])
        if item['id'] == 'committed-evidence':
            break
    local_results = {item['checkpointId']: item for item in report['checkpointResults']}
    local_case_ids = {case_id for item in load_contract()['checkpoints']
                      if item['id'] in local_checkpoint_ids for case_id in item['requiredCaseIds']}
    local_cases = [item for item in report['cases'] if item['caseId'] in local_case_ids]
    report['executionExitCode'] = 0 if (
        all(local_results[item]['status'] == 'passed' for item in local_checkpoint_ids)
        and all(item['executed'] and item['passed'] for item in local_cases)
    ) else 1
    evidence_path.write_bytes(_canonical_bytes(report))
    if all(validations.values()):
        print(f'Passed checkpoint committed-evidence: {evidence_path}')
        return 0
    print(f'Committed evidence validation failed; evidence saved at {evidence_path}', file=sys.stderr)
    return 1


def _successful_push_run(branch: str, head_sha: str) -> dict[str, Any] | None:
    runs, _ = _run_github_api(
        f'actions/runs?branch={quote(branch)}&per_page=100', label='GitHub push workflow runs query')
    candidates = [item for item in runs.get('workflow_runs', [])
                  if item.get('event') == 'push' and item.get('name') == 'Linux CI'
                  and item.get('head_branch') == branch
                  and item.get('head_sha') == head_sha and item.get('status') == 'completed'
                  and item.get('conclusion') == 'success']
    return max(candidates, key=lambda item: item.get('run_number', 0)) if candidates else None


def execute_ci_integration(output: Path) -> int:
    evidence_path = output / 'evidence.json'
    report = _load_json(evidence_path)
    _validate_provenance(report['provenance'], output, allow_synthetic=False)
    checkpoint = _checkpoint_map(load_contract())['ci-integration']
    checkpoint_results = {item['checkpointId']: item for item in report['checkpointResults']}
    for dependency in checkpoint['dependencies']:
        if checkpoint_results.get(dependency, {}).get('status') != 'passed':
            raise MandatoryFailure(f'CI integration cannot run before {dependency} passes')

    workflow_path = ROOT / '.github/workflows/ci.yml'
    workflow = workflow_path.read_text(encoding='utf-8')
    contract = load_contract()
    pi_path = Path(report['provenance']['piPath'])
    pi_sha = _git('rev-parse', 'HEAD', cwd=pi_path)
    repo_head = _git('rev-parse', 'HEAD')
    branch = report['provenance']['branch']
    event_name = os.environ.get('GITHUB_EVENT_NAME', '')
    event_path = os.environ.get('GITHUB_EVENT_PATH')
    event_payload: dict[str, Any] = {}
    if event_path and Path(event_path).is_file():
        try:
            event_payload = _load_json(Path(event_path))
        except VerificationError:
            event_payload = {}

    pinned_ok = pi_sha == contract['upstream']['revision'] == report['provenance']['piSha']
    workflow_has_required_steps = _workflow_has_required_steps(workflow, contract['upstream']['revision'])

    core_checkpoint_ids = []
    for item in contract['checkpoints']:
        if item['id'] == 'committed-evidence':
            core_checkpoint_ids.append(item['id'])
            break
        core_checkpoint_ids.append(item['id'])
    result_map = {item['checkpointId']: item for item in report['checkpointResults']}
    all_core_passed = all(result_map[item]['status'] == 'passed' for item in core_checkpoint_ids)
    core_case_ids = {case_id for item in contract['checkpoints'] if item['id'] in core_checkpoint_ids
                     for case_id in item['requiredCaseIds']}
    core_cases = [case for case in report['cases'] if case['caseId'] in core_case_ids]
    all_core_cases_passed = (len(core_cases) == len(core_case_ids)
                             and {case['caseId'] for case in core_cases} == core_case_ids
                             and all(case['executed'] and case['passed'] and not case['skipped']
                                     for case in core_cases))
    execute_all_recorded = ('--all' in report['provenance']['command']
                            and report['executionExitCode'] == 0
                            and all_core_passed and all_core_cases_passed)

    verifier_log_path = output / 'verifier-tests.command.json'
    verifier_log = _load_json(verifier_log_path) if verifier_log_path.is_file() else {}
    negative_controls_ran = (verifier_log.get('exitCode') == 0
                              and 'python3 tools/parity/managed_packages/test_verifier.py' in workflow)

    push_run = _successful_push_run(branch, repo_head) if not event_name else None

    def is_ancestor(child: str, parent: str) -> bool:
        return subprocess.run(['git', 'merge-base', '--is-ancestor', child, parent],
                              cwd=ROOT, capture_output=True).returncode == 0

    event_context = _ci_event_context(event_name, event_payload, os.environ, repo_head, branch,
                                      workflow, is_ancestor, push_run)
    push_head_ok = event_context['pushHeadValid']
    pr_head_base_ok = event_context['prHeadBaseValid']
    push_details = event_context['pushDetails']
    pr_details = event_context['prDetails']

    event_family = ('github-actions-pr' if event_name == 'pull_request' else 'github-actions-push')
    validations = {
        'ci.pinned-pi': pinned_ok and workflow_has_required_steps,
        'ci.execute-all': execute_all_recorded and workflow_has_required_steps,
        'ci.check-all': workflow_has_required_steps,
        'ci.negative-controls': negative_controls_ran,
        'ci.push-head': push_head_ok,
        'ci.pr-head-base': pr_head_base_ok,
        'ci.upload-on-failure': ('if: always()' in workflow and 'actions/upload-artifact@v4' in workflow
                                 and 'artifacts/parity/managed-packages' in workflow),
    }
    details_by_case = {
        'ci.pinned-pi': {'pinnedPiSha': contract['upstream']['revision'], 'checkedOutPiSha': pi_sha,
                         'workflowContractPresent': workflow_has_required_steps,
                         'hydrationAndOfflineBuildRequired': (
                             'Hydrate pinned Pi model data' in workflow
                             and 'Build pinned Pi offline' in workflow)},
        'ci.execute-all': {'executeAllCommandRecorded': '--all' in report['provenance']['command'],
                           'allCoreCheckpointsPassed': all_core_passed,
                           'allCoreCasesPassed': all_core_cases_passed,
                           'executionExitCode': report['executionExitCode']},
        'ci.check-all': {'checkAllCommandPresent': 'python3 tools/parity/managed_packages/verify.py check --all' in workflow},
        'ci.negative-controls': {'verifierTestExitCode': verifier_log.get('exitCode'),
                                 'negativeControlsCommandPresent': negative_controls_ran},
        'ci.push-head': push_details,
        'ci.pr-head-base': pr_details,
        'ci.upload-on-failure': {'uploadRunsAlways': 'if: always()' in workflow,
                                 'uploadActionPresent': 'actions/upload-artifact@v4' in workflow},
    }

    command = [sys.executable, str(HERE / 'verify.py'), 'execute', '--checkpoint', 'ci-integration',
               '--pi', str(pi_path), '--output', str(output.resolve())]
    workflow_identity = '.github/workflows/ci.yml'
    check_identity = 'tools/parity/managed_packages/verify.py check --all'
    run_identity = 'tools/parity/managed_packages/verify.py execute --all'
    verifier_identity = 'tools/parity/managed_packages/test_verifier.py'
    command_by_case = {
        'ci.pinned-pi': workflow_identity,
        'ci.execute-all': run_identity,
        'ci.check-all': check_identity,
        'ci.negative-controls': verifier_identity,
        'ci.push-head': workflow_identity,
        'ci.pr-head-base': workflow_identity,
        'ci.upload-on-failure': workflow_identity,
    }
    fixture_by_case = {
        case_id: ('uploaded-parity-artifacts' if case_id == 'ci.upload-on-failure' else event_family)
        for case_id in checkpoint['requiredCaseIds']
    }

    report['artifacts'] = [item for item in report['artifacts'] if item['path'] != 'ci-integration.command.json']
    command_path = output / 'ci-integration.command.json'
    command_path.write_bytes(_canonical_bytes({
        'workflowPath': workflow_path.relative_to(ROOT).as_posix(),
        'eventName': event_name or 'local-post-push-verification',
        'workflowSha256': _sha256_file(workflow_path),
        'pinnedPiSha': pi_sha,
        'headSha': repo_head,
        'checks': validations,
        'pushRun': push_details,
        'prHeadBase': pr_details,
    }))
    report['artifacts'].append({'path': 'ci-integration.command.json', 'sha256': _sha256_file(command_path)})

    # The bootstrap check validates all preceding evidence while deferring this
    # checkpoint's own result and the publication-dependent checkpoints.
    report['testResults']['executed'] = sum(item['executed'] for item in report['cases'])
    report['testResults']['failed'] = sum(item['executed'] and not item['passed'] for item in report['cases'])
    report['testResults']['skipped'] = sum(item['skipped'] for item in report['cases'])
    evidence_path.write_bytes(_canonical_bytes(report))
    bootstrap_env = dict(os.environ)
    bootstrap_env.update({'PISHARP_MANAGED_CI_SCOPE': 'local', 'PISHARP_MANAGED_CI_BOOTSTRAP': '1'})
    bootstrap_command = subprocess.run(
        [sys.executable, str(HERE / 'verify.py'), 'check', '--all', '--evidence', str(output.resolve())],
        cwd=ROOT, env=bootstrap_env, capture_output=True, text=True, timeout=300)
    validations['ci.check-all'] = bootstrap_command.returncode == 0 and workflow_has_required_steps
    details_by_case['ci.check-all'] = {
        'bootstrapCheckAllExitCode': bootstrap_command.returncode,
        'bootstrapStdout': bootstrap_command.stdout.strip(),
        'bootstrapStderr': bootstrap_command.stderr.strip(),
        'publicationChecksDeferredUntilAfterTheirExternalEvents': True,
    }

    for case_id in checkpoint['requiredCaseIds']:
        details = details_by_case[case_id]
        passed = validations[case_id]
        _record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': {**details, 'passed': True}},
            observed={'exitCode': 0 if passed else 1, 'state': {**details, 'passed': passed}},
            passed=passed, command_identity=command_by_case[case_id], argv=command,
            command_exit_code=0 if passed else 1,
            fixture_identity=fixture_by_case[case_id],
            input_fingerprint=_sha256_bytes(_canonical_bytes({'case': case_id, 'details': details})),
        )

    _refresh_checkpoint_result(report, 'ci-integration')
    report['testResults']['executed'] = sum(item['executed'] for item in report['cases'])
    report['testResults']['failed'] = sum(item['executed'] and not item['passed'] for item in report['cases'])
    report['testResults']['skipped'] = sum(item['skipped'] for item in report['cases'])
    runner_results_path = output / 'runner-results.json'
    runner_results = _load_json(runner_results_path) if runner_results_path.is_file() else []
    runner_result = {'runnerId': 'ci-integration', 'argv': command,
                     'startedAt': dt.datetime.now(dt.timezone.utc).isoformat(),
                     'exitCode': 0 if all(validations.values()) else 1,
                     'stdout': json.dumps(validations, sort_keys=True), 'stderr': ''}
    runner_results = [item for item in runner_results if item.get('runnerId') != 'ci-integration']
    runner_results.append(runner_result)
    runner_results_path.write_bytes(_canonical_bytes(runner_results))
    report['artifacts'] = [item for item in report['artifacts'] if item['path'] != 'runner-results.json']
    report['artifacts'].append({'path': 'runner-results.json', 'sha256': _sha256_file(runner_results_path)})
    report['provenance']['runnerCommands'] = [item['argv'] for item in runner_results]
    report['provenance']['runnerExitCodes'] = [item['exitCode'] for item in runner_results]
    local_checkpoint_ids = []
    for item in contract['checkpoints']:
        if item['id'] == 'draft-publication':
            break
        local_checkpoint_ids.append(item['id'])
    local_results = {item['checkpointId']: item for item in report['checkpointResults']}
    local_case_ids = {case_id for item in contract['checkpoints'] if item['id'] in local_checkpoint_ids
                      for case_id in item['requiredCaseIds']}
    local_cases = [item for item in report['cases'] if item['caseId'] in local_case_ids]
    report['executionExitCode'] = 0 if (
        all(local_results[item]['status'] == 'passed' for item in local_checkpoint_ids)
        and all(item['executed'] and item['passed'] for item in local_cases)
    ) else 1
    for artifact_path in sorted(output.rglob('*')):
        if (not artifact_path.is_file() or 'profile' in artifact_path.relative_to(output).parts
                or artifact_path.name == 'evidence.json'):
            continue
        relative = artifact_path.relative_to(output).as_posix()
        existing = next((item for item in report['artifacts'] if item['path'] == relative), None)
        digest = _sha256_file(artifact_path)
        if existing is None:
            report['artifacts'].append({'path': relative, 'sha256': digest})
        else:
            existing['sha256'] = digest
    report['artifacts'].sort(key=lambda item: item['path'])
    evidence_path.write_bytes(_canonical_bytes(report))

    final_env = dict(os.environ)
    final_env['PISHARP_MANAGED_CI_SCOPE'] = 'local'
    final_check = subprocess.run(
        [sys.executable, str(HERE / 'verify.py'), 'check', '--all', '--evidence', str(output.resolve())],
        cwd=ROOT, env=final_env, capture_output=True, text=True, timeout=300)
    if final_check.returncode != 0:
        print(f'CI all-evidence verification failed: {final_check.stderr.strip()}', file=sys.stderr)
        return final_check.returncode
    if all(validations.values()):
        print(f'Passed checkpoint ci-integration: {evidence_path}')
        return 0
    print(f'CI integration checkpoint failed; evidence saved at {evidence_path}', file=sys.stderr)
    return 1


def check_evidence(checkpoint_id: str | None, evidence_root: Path) -> int:
    evidence_path = evidence_root / 'evidence.json'
    evidence = _load_json(evidence_path)
    defer_publication = (checkpoint_id is None
                         and os.environ.get('PISHARP_MANAGED_CI_SCOPE') == 'local')
    defer_ci_integration = (checkpoint_id is None
                            and os.environ.get('PISHARP_MANAGED_CI_BOOTSTRAP') == '1')
    validate_evidence(evidence, evidence_root, checkpoint_id=checkpoint_id,
                      defer_publication_checkpoints=defer_publication,
                      defer_ci_integration=defer_ci_integration)
    scope = (f'checkpoint {checkpoint_id}' if checkpoint_id is not None else
             'all locally executable managed-package checkpoints (publication checks deferred)'
             if defer_publication else 'all required managed-package checkpoints')
    print(f'Passed {scope}: {evidence_path}')
    return 0


def _download_run_package_evidence(run: dict[str, Any], output: Path) -> tuple[Path, dict[str, Any]]:
    run_id = run.get('id')
    if not isinstance(run_id, int) or run_id <= 0:
        raise VerificationError('Exact-head workflow run has no valid run ID')
    destination = output / '_ci_downloads' / str(run_id)
    if destination.exists():
        shutil.rmtree(destination)
    destination.mkdir(parents=True, exist_ok=True)
    argv = ['gh', 'run', 'download', str(run_id), '--name', 'managed-package-parity',
            '--dir', str(destination)]
    try:
        completed = subprocess.run(argv, cwd=ROOT, capture_output=True, text=True, timeout=180)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise ExecutionUnavailable(f'Cannot download managed-package artifact from run {run_id}: {error}') from error
    command = {'argv': argv, 'exitCode': completed.returncode,
               'stdout': completed.stdout.strip(), 'stderr': completed.stderr.strip()}
    if completed.returncode != 0:
        raise ExecutionUnavailable(f'Cannot download managed-package artifact from run {run_id}: '
                                   f'{completed.stderr.strip() or completed.stdout.strip()}')
    evidence_paths = list(destination.rglob('evidence.json'))
    if len(evidence_paths) != 1:
        raise MandatoryFailure(f'Run {run_id} artifact must contain exactly one evidence.json; found {len(evidence_paths)}')
    return evidence_paths[0].parent, command


def _remote_artifact_passes(evidence_root: Path, run: dict[str, Any], *,
                            pull_request: dict[str, Any] | None) -> tuple[bool, dict[str, Any]]:
    evidence_path = evidence_root / 'evidence.json'
    evidence = _load_json(evidence_path)
    try:
        validate_evidence(evidence, evidence_root, defer_publication_checkpoints=True,
                          remote_ci_run=run, remote_pull_request=pull_request)
        checkpoint = next(item for item in evidence['checkpointResults']
                          if item['checkpointId'] == 'ci-integration')
        ci_cases = {item['caseId']: item for item in evidence['cases']
                    if item['checkpoint'] == 'ci-integration'}
        if checkpoint['status'] != 'passed' or any(
                ci_cases.get(case_id, {}).get('passed') is not True
                for case_id in _checkpoint_map(load_contract())['ci-integration']['requiredCaseIds']):
            raise MandatoryFailure('Uploaded package artifact does not contain a passed CI integration checkpoint')
    except VerificationError as error:
        return False, {'evidencePath': evidence_path.as_posix(), 'error': str(error),
                       'evidenceSha256': _sha256_file(evidence_path)}
    return True, {'evidencePath': evidence_path.as_posix(), 'evidenceSha256': _sha256_file(evidence_path),
                  'sourceSha': evidence['provenance']['checkedOutSourceSha'],
                  'caseCount': len(evidence['cases']), 'checkpointStatus': checkpoint['status']}


def _record_publication_case(report: dict[str, Any], output: Path, case_id: str, *,
                             passed: bool, details: dict[str, Any], command_identity: str,
                             argv: list[str], fixture_identity: str) -> None:
    expected = {'exitCode': 0, 'state': {**details, 'passed': True}}
    observed = {'exitCode': 0 if passed else 1, 'state': {**details, 'passed': passed}}
    _record_baseline_case(
        report, output, case_id, expected=expected, observed=observed, passed=passed,
        command_identity=command_identity, argv=argv, command_exit_code=0 if passed else 1,
        fixture_identity=fixture_identity,
        input_fingerprint=_sha256_bytes(_canonical_bytes({'case': case_id, 'details': details})),
    )


def check_publication(pr_number: int, evidence_root: Path | None = None) -> int:
    if pr_number <= 0:
        raise VerificationError('PR number must be positive')
    output = (evidence_root or ROOT / 'artifacts/parity/managed-packages').resolve()
    evidence_path = output / 'evidence.json'
    if not evidence_path.is_file():
        raise ExecutionUnavailable(f'Pre-publication evidence is missing: {evidence_path}')
    report = _load_json(evidence_path)
    validate_evidence(report, output, defer_publication_checkpoints=True)
    runtime = _runtime_state()
    branch = runtime['branch']
    head_sha = runtime['headSha']
    command_argv = [sys.executable, *sys.argv]

    repository, repository_command = _run_github_api('', label='GitHub repository metadata query')
    pr, pr_command = _run_github_api(f'pulls/{pr_number}', label=f'GitHub PR #{pr_number} query')
    branch_response, branch_command = _run_github_api(
        f'branches/{quote(branch, safe="")}', label=f'GitHub branch {branch} query')
    main_response, main_command = _run_github_api('branches/main', label='GitHub main branch query')
    if not all(isinstance(item, dict) for item in (repository, pr, branch_response, main_response)):
        raise VerificationError('GitHub publication metadata response is malformed')

    head = pr.get('head') or {}
    base = pr.get('base') or {}
    head_repo = (head.get('repo') or {}).get('full_name')
    base_repo = (base.get('repo') or {}).get('full_name')
    repository_name = repository.get('full_name')
    branch_sha = ((branch_response.get('commit') or {}).get('sha'))
    main_sha = ((main_response.get('commit') or {}).get('sha'))
    pr_state = {
        'number': pr.get('number'), 'repository': repository_name, 'headRepository': head_repo,
        'baseRepository': base_repo, 'headBranch': head.get('ref'), 'baseBranch': base.get('ref'),
        'headSha': head.get('sha'), 'baseSha': base.get('sha'), 'branchSha': branch_sha,
        'currentMainSha': main_sha, 'state': pr.get('state'), 'draft': pr.get('draft'),
        'merged': pr.get('merged'), 'mergedAt': pr.get('merged_at'), 'url': pr.get('html_url'),
    }
    publication_details = {
        'publication.repository': {'repository': repository_name, 'headRepository': head_repo,
                                   'baseRepository': base_repo},
        'publication.branch': {'expectedBranch': branch, 'observedBranch': head.get('ref'),
                               'branchSha': branch_sha, 'expectedHeadSha': head_sha},
        'publication.base': {'expectedBase': 'main', 'observedBase': base.get('ref'),
                             'baseSha': base.get('sha'), 'currentMainSha': main_sha},
        'publication.head': {'expectedHeadSha': head_sha, 'observedHeadSha': head.get('sha')},
        'publication.open': {'state': pr.get('state')},
        'publication.draft': {'draft': pr.get('draft')},
        'publication.unmerged': {'merged': pr.get('merged'), 'mergedAt': pr.get('merged_at')},
    }
    publication_pass = {
        'publication.repository': (repository_name == 'PhilipGB/pisharp' and head_repo == repository_name
                                   and base_repo == repository_name),
        'publication.branch': (head.get('ref') == branch and branch_sha == head_sha),
        'publication.base': (base.get('ref') == 'main' and isinstance(main_sha, str)
                             and base.get('sha') == main_sha),
        'publication.head': (head.get('sha') == head_sha and branch_sha == head_sha),
        'publication.open': (pr.get('number') == pr_number and pr.get('state') == 'open'),
        'publication.draft': pr.get('draft') is True,
        'publication.unmerged': pr.get('merged') is False and pr.get('merged_at') is None,
    }

    runs_response, runs_command = _run_github_api(
        f'actions/runs?branch={quote(branch, safe="")}&per_page=100', label='GitHub exact-head workflow runs query')
    if not isinstance(runs_response, dict) or not isinstance(runs_response.get('workflow_runs'), list):
        raise VerificationError('GitHub workflow run response is missing its workflow_runs array')
    push_run = _latest_exact_workflow_run(runs_response['workflow_runs'], event='push', branch=branch,
                                          head_sha=head_sha)
    pr_run = _latest_exact_workflow_run(runs_response['workflow_runs'], event='pull_request', branch=branch,
                                        head_sha=head_sha)
    workflow = (ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')
    required_jobs = _required_workflow_jobs(workflow)
    run_summaries: dict[str, Any] = {}
    artifacts_responses: dict[str, Any] = {}
    downloaded: dict[str, tuple[Path, dict[str, Any]]] = {}
    for event, run in (('push', push_run), ('pull_request', pr_run)):
        key = 'push' if event == 'push' else 'pr'
        if run is None:
            run_summaries[key] = {'run': None, 'jobs': [], 'gate': {}, 'passed': False}
            continue
        run_id = run.get('id')
        jobs_response, jobs_command = _run_github_api(
            f'actions/runs/{run_id}/jobs?per_page=100', label=f'GitHub workflow run {run_id} jobs query')
        jobs = jobs_response.get('jobs') if isinstance(jobs_response, dict) else None
        if not isinstance(jobs, list):
            raise VerificationError(f'GitHub workflow run {run_id} response is missing its job list')
        gate = _workflow_gate_state(jobs, required_jobs)
        run_pass = (run.get('status') == 'completed' and run.get('conclusion') == 'success'
                    and run.get('event') == event and run.get('head_branch') == branch
                    and run.get('head_sha') == head_sha and gate['requiredJobsPresent']
                    and gate['allJobsCompletedSuccessfully'])
        artifacts_response, artifacts_command = _run_github_api(
            f'actions/runs/{run_id}/artifacts?per_page=100', label=f'GitHub workflow run {run_id} artifacts query')
        artifacts = artifacts_response.get('artifacts') if isinstance(artifacts_response, dict) else None
        if not isinstance(artifacts, list):
            raise VerificationError(f'GitHub workflow run {run_id} response is missing its artifact list')
        package_artifacts = [item for item in artifacts
                             if item.get('name') == 'managed-package-parity' and item.get('expired') is False]
        artifact_ok = len(package_artifacts) == 1
        artifact_validation: dict[str, Any] = {'passed': False, 'error': 'workflow artifact is missing or expired'}
        download_command: dict[str, Any] | None = None
        if run_pass and artifact_ok:
            remote_root, download_command = _download_run_package_evidence(run, output)
            remote_pass, artifact_validation = _remote_artifact_passes(
                remote_root, run, pull_request=pr if event == 'pull_request' else None)
            artifact_ok = artifact_ok and remote_pass
            if remote_pass:
                downloaded[key] = (remote_root, download_command)
        run_summaries[key] = {
            'run': {field: run.get(field) for field in ('id', 'name', 'event', 'head_branch', 'head_sha',
                                                       'status', 'conclusion', 'html_url', 'run_number')},
            'jobs': [{field: job.get(field) for field in ('id', 'name', 'status', 'conclusion')}
                     for job in jobs],
            'gate': gate, 'workflowPassed': run_pass, 'artifactMetadata': package_artifacts,
            'artifactPassed': artifact_ok, 'artifactValidation': artifact_validation,
            'jobQuery': jobs_command, 'artifactQuery': artifacts_command,
            'downloadCommand': download_command,
        }
        artifacts_responses[key] = artifacts_response

    push_summary = run_summaries['push']
    pr_summary = run_summaries['pr']
    exact_details = {
        'ci1.push-exact-head': {'run': push_summary.get('run'), 'sourceHeadSha': head_sha},
        'ci1.pr-exact-head-base': {'run': pr_summary.get('run'), 'prHeadSha': head.get('sha'),
                                   'prBaseSha': base.get('sha'), 'baseBranch': base.get('ref'),
                                   'prArtifact': pr_summary.get('artifactValidation')},
        'ci1.required-jobs': {'requiredJobs': required_jobs,
                              'push': push_summary.get('gate'), 'pr': pr_summary.get('gate')},
        'ci1.package-gate': {'pushArtifactPassed': push_summary.get('artifactPassed'),
                             'prArtifactPassed': pr_summary.get('artifactPassed'),
                             'pushPackageGate': (push_summary.get('gate') or {}).get('packageGatePassed'),
                             'prPackageGate': (pr_summary.get('gate') or {}).get('packageGatePassed'),
                             'pushPinnedPiPrepared': (push_summary.get('gate') or {}).get('pinnedPiPrepared'),
                             'prPinnedPiPrepared': (pr_summary.get('gate') or {}).get('pinnedPiPrepared')},
        'ci1.tests': {'push': (push_summary.get('gate') or {}).get('fullDotnetSuitePassed'),
                      'pr': (pr_summary.get('gate') or {}).get('fullDotnetSuitePassed')},
        'ci1.mcp': {'push': (push_summary.get('gate') or {}).get('mcpConformancePassed'),
                    'pr': (pr_summary.get('gate') or {}).get('mcpConformancePassed')},
        'ci1.no-skipped-jobs': {'push': (push_summary.get('gate') or {}).get('allJobsCompletedSuccessfully'),
                                'pr': (pr_summary.get('gate') or {}).get('allJobsCompletedSuccessfully')},
        'ci1.pr-still-draft': {'number': pr.get('number'), 'state': pr.get('state'),
                               'draft': pr.get('draft'), 'merged': pr.get('merged'),
                               'headSha': head.get('sha')},
    }
    exact_pass = {
        'ci1.push-exact-head': bool(push_summary.get('workflowPassed')),
        'ci1.pr-exact-head-base': (bool(pr_summary.get('workflowPassed'))
                                  and bool(pr_summary.get('artifactPassed'))
                                  and publication_pass['publication.base']
                                  and publication_pass['publication.head']),
        'ci1.required-jobs': all((summary.get('gate') or {}).get('requiredJobsPresent') is True
                                 for summary in (push_summary, pr_summary)),
        'ci1.package-gate': all((summary.get('gate') or {}).get('packageGatePassed') is True
                                and summary.get('artifactPassed') is True
                                for summary in (push_summary, pr_summary)),
        'ci1.tests': all((summary.get('gate') or {}).get('fullDotnetSuitePassed') is True
                         for summary in (push_summary, pr_summary)),
        'ci1.mcp': all((summary.get('gate') or {}).get('mcpConformancePassed') is True
                       for summary in (push_summary, pr_summary)),
        'ci1.no-skipped-jobs': all((summary.get('gate') or {}).get('allJobsCompletedSuccessfully') is True
                                   for summary in (push_summary, pr_summary)),
        'ci1.pr-still-draft': (publication_pass['publication.open'] and publication_pass['publication.draft']
                               and publication_pass['publication.unmerged']
                               and publication_pass['publication.head']),
    }

    _persist_command_result(report, output, f'github/publication-pr-{pr_number}.json', {
        'repository': repository, 'pr': pr, 'branch': branch_response, 'main': main_response,
        'commands': [repository_command, pr_command, branch_command, main_command],
    })
    _persist_command_result(report, output, f'github/exact-head-ci-pr-{pr_number}.json', {
        'workflowRuns': runs_response, 'runDetails': run_summaries,
        'artifactResponses': artifacts_responses,
        'commands': [runs_command],
    })

    publication_checkpoint = _checkpoint_map(load_contract())['draft-publication']
    publication_command_by_case = {
        'publication.repository': repository_command['argv'],
        'publication.branch': branch_command['argv'],
        'publication.base': main_command['argv'],
        'publication.head': pr_command['argv'],
        'publication.open': pr_command['argv'],
        'publication.draft': pr_command['argv'],
        'publication.unmerged': pr_command['argv'],
    }
    for case_id in publication_checkpoint['requiredCaseIds']:
        _record_publication_case(
            report, output, case_id, passed=publication_pass[case_id],
            details=publication_details[case_id],
            command_identity='gh api repository pull request and branch metadata',
            argv=publication_command_by_case[case_id],
            fixture_identity='github-pull-request')
    _refresh_checkpoint_result(report, 'draft-publication')

    exact_checkpoint = _checkpoint_map(load_contract())['exact-head-ci']
    api_command_ids = {'ci1.push-exact-head', 'ci1.pr-exact-head-base', 'ci1.required-jobs', 'ci1.tests',
                       'ci1.mcp', 'ci1.no-skipped-jobs'}
    for case_id in exact_checkpoint['requiredCaseIds']:
        if case_id in api_command_ids:
            command_identity = 'gh api actions runs and workflow jobs'
        elif case_id == 'ci1.package-gate':
            command_identity = 'gh run download managed-package parity artifact'
        else:
            command_identity = 'verify.py check --publication --pr <number>'
        fixture_identity = ('github-pull-request' if case_id in ('ci1.pr-exact-head-base', 'ci1.pr-still-draft')
                            else 'github-workflow-runs')
        if case_id == 'ci1.package-gate':
            artifact_download = pr_summary.get('downloadCommand') or push_summary.get('downloadCommand') or {}
            case_argv = artifact_download.get('argv', command_argv)
        elif case_id == 'ci1.pr-still-draft':
            case_argv = command_argv
        else:
            case_argv = runs_command['argv']
        _record_publication_case(
            report, output, case_id, passed=exact_pass[case_id], details=exact_details[case_id],
            command_identity=command_identity, argv=case_argv, fixture_identity=fixture_identity)
    _refresh_checkpoint_result(report, 'exact-head-ci')

    result_path = output / 'publication-check.command.json'
    command_result = {
        'prNumber': pr_number, 'prUrl': pr.get('html_url'), 'branch': branch, 'headSha': head_sha,
        'publicationChecks': publication_pass, 'exactHeadCiChecks': exact_pass,
        'pushRun': push_summary, 'prRun': pr_summary,
        'pushArtifactPath': (downloaded.get('push') or (None, None))[0].as_posix()
        if downloaded.get('push') else None,
        'prArtifactPath': (downloaded.get('pr') or (None, None))[0].as_posix()
        if downloaded.get('pr') else None,
        'recordedAt': dt.datetime.now(dt.timezone.utc).isoformat(),
    }
    result_path.write_bytes(_canonical_bytes(command_result))
    report['artifacts'] = [item for item in report['artifacts']
                           if item['path'] != result_path.relative_to(output).as_posix()]
    report['artifacts'].append({'path': result_path.relative_to(output).as_posix(),
                                'sha256': _sha256_file(result_path)})

    runner_path = output / 'runner-results.json'
    runner_results = _load_json(runner_path) if runner_path.is_file() else []
    runner_results = [item for item in runner_results if item.get('runnerId') != 'publication']
    all_publication_passed = all(publication_pass.values()) and all(exact_pass.values())
    runner_results.append({
        'runnerId': 'publication', 'argv': command_argv,
        'startedAt': dt.datetime.now(dt.timezone.utc).isoformat(),
        'exitCode': 0 if all_publication_passed else 1,
        'stdout': json.dumps({'publication': publication_pass, 'exactHeadCI': exact_pass}, sort_keys=True),
        'stderr': '',
    })
    runner_path.write_bytes(_canonical_bytes(runner_results))
    report['artifacts'] = [item for item in report['artifacts'] if item['path'] != 'runner-results.json']
    report['artifacts'].append({'path': 'runner-results.json', 'sha256': _sha256_file(runner_path)})
    report['provenance']['runnerCommands'] = [item['argv'] for item in runner_results]
    report['provenance']['runnerExitCodes'] = [item['exitCode'] for item in runner_results]
    report['testResults']['executed'] = sum(item['executed'] for item in report['cases'])
    report['testResults']['failed'] = sum(item['executed'] and not item['passed'] for item in report['cases'])
    report['testResults']['skipped'] = sum(item['skipped'] for item in report['cases'])
    report['executionExitCode'] = 0 if all_publication_passed and report['testResults']['failed'] == 0 else 1
    evidence_path.write_bytes(_canonical_bytes(report))
    validate_evidence(report, output)
    _write_milestone_result(report, pr_number, pr.get('html_url'), push_summary.get('run'),
                            pr_summary.get('run'))
    print(f'Passed publication and exact-head CI checkpoints for PR #{pr_number}: {evidence_path}')
    return 0


def _write_milestone_result(report: dict[str, Any], pr_number: int, pr_url: str | None,
                            push_run: dict[str, Any] | None, pr_run: dict[str, Any] | None) -> Path:
    case_map = {item['caseId']: item for item in report['cases']}
    checkpoint_map = {item['checkpointId']: item for item in report['checkpointResults']}
    checkpoints = {item['checkpointId']: item['status'] for item in report['checkpointResults']}
    required_checkpoints = load_contract()['requiredCheckpointIds']
    unresolved = sum(not item['executed'] or not item['passed'] for item in report['cases'])
    merge_case = case_map.get('p0.pr-metadata', {})
    merge_sha = ((merge_case.get('actual') or {}).get('state') or {}).get('mergeCommit')

    def workflow_summary(run: dict[str, Any] | None) -> dict[str, Any]:
        if run is None:
            return {}
        return {key: run.get(key) for key in ('id', 'head_sha', 'event', 'status', 'conclusion', 'html_url')}

    status_result = subprocess.run(['git', 'status', '--porcelain'], cwd=ROOT,
                                   capture_output=True, text=True)
    clean = status_result.returncode == 0 and not status_result.stdout.strip()
    complete = (all(checkpoint_map[item]['status'] == 'passed' for item in required_checkpoints)
                and unresolved == 0 and report['executionExitCode'] == 0)
    result = {
        'goal': 'managed-package-lifecycle-parity', 'status': 'COMPLETE' if complete else 'INCOMPLETE',
        'pinnedPiSha': report['provenance']['piSha'], 'prerequisitePr': 26,
        'prerequisiteMergeSha': merge_sha, 'milestoneBaseSha': report['provenance']['baseSha'],
        'milestoneHeadSha': report['provenance']['checkedOutSourceSha'],
        'draftPrNumber': pr_number, 'draftPrUrl': pr_url, 'checkpoints': checkpoints,
        'strictMatches': sum(item['classification'] == 'strict' for item in report['cases']),
        'equivalentMatches': sum(item['classification'] == 'equivalent' for item in report['cases']),
        'approvedExclusions': len(report['exclusions']), 'unresolvedMandatoryDifferences': unresolved,
        'pushWorkflow': workflow_summary(push_run), 'prWorkflow': workflow_summary(pr_run),
        'workingTreeClean': clean, 'recordedAt': dt.datetime.now(dt.timezone.utc).isoformat(),
    }
    destination = ROOT / 'artifacts/parity/managed-package-milestone-result.json'
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(_canonical_bytes(result))
    if complete is not (result['status'] == 'COMPLETE'):
        raise MandatoryFailure('Final milestone report status does not match validated checkpoint evidence')
    return destination


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    execute = commands.add_parser('execute', help='Run the managed-package verification corpus')
    execution_scope = execute.add_mutually_exclusive_group(required=True)
    execution_scope.add_argument('--all', action='store_true')
    execution_scope.add_argument('--checkpoint', choices=[
        'differential-acceptance', 'quality', 'committed-evidence', 'ci-integration',
    ])
    execute.add_argument('--pi', type=Path, required=True)
    execute.add_argument('--output', type=Path, required=True)
    execute.add_argument('--pisharp', type=Path, default=ROOT / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll')
    prepare = commands.add_parser('prepare-evidence', help='Generate deterministic committed evidence fixtures')
    prepare.add_argument('--evidence', type=Path, required=True)
    check = commands.add_parser('check', help='Validate saved evidence against the committed contract')
    selection = check.add_mutually_exclusive_group(required=True)
    selection.add_argument('--all', action='store_true')
    selection.add_argument('--checkpoint')
    selection.add_argument('--publication', action='store_true')
    check.add_argument('--evidence', type=Path)
    check.add_argument('--pr', type=int)
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = _parser()
    args = parser.parse_args(argv)
    try:
        load_contract()
        if args.command == 'execute':
            if args.all:
                return execute_all(args.pi, args.output, args.pisharp, sys.argv if argv is None else argv)
            if args.checkpoint == 'differential-acceptance':
                return execute_differential_acceptance(args.pi, args.output, args.pisharp,
                                                       sys.argv if argv is None else argv)
            if args.checkpoint == 'quality':
                return execute_quality(args.output)
            if args.checkpoint == 'ci-integration':
                return execute_ci_integration(args.output)
            return execute_committed_evidence(args.output)
        if args.command == 'prepare-evidence':
            return prepare_committed_evidence(args.evidence)
        if args.publication:
            if args.pr is None:
                raise VerificationError('check --publication requires --pr')
            return check_publication(args.pr, args.evidence)
        if args.pr is not None or args.evidence is None:
            raise VerificationError('check --all/--checkpoint requires --evidence and does not accept --pr')
        return check_evidence(args.checkpoint, args.evidence)
    except VerificationError as error:
        print(f'verification failed: {error}', file=sys.stderr)
        return error.exit_code
    except Exception as error:  # unexpected failures are never successful exclusions
        print(f'verification aborted unexpectedly: {type(error).__name__}: {error}', file=sys.stderr)
        return 3


if __name__ == '__main__':
    raise SystemExit(main())
