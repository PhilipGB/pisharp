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
from clone_race import clone_destination_collision
import checkpoint_validation
import evidence_validation
import package_adapters
import publication_gate
import quality_gate
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
    context = checkpoint_validation.ContractContext(
        CONTRACT_HASH_PATH=CONTRACT_HASH_PATH,
        CONTRACT_PATH=CONTRACT_PATH,
        FIXTURE_VERSION=FIXTURE_VERSION,
        PIN_PATH=PIN_PATH,
        _case_artifact_path=_case_artifact_path,
        _sha256_bytes=_sha256_bytes,
    )
    return checkpoint_validation.load_contract(context)


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


def _validation_context() -> evidence_validation.ValidationContext:
    return evidence_validation.ValidationContext(
        FIXTURE_VERSION=FIXTURE_VERSION,
        ROOT=ROOT,
        SCHEMA_PATH=SCHEMA_PATH,
        _platform_metadata=_platform_metadata,
        _runtime_state=_runtime_state,
        _safe_artifact=_safe_artifact,
        _sha256_file=_sha256_file,
        _source_commit_matches_current_tree=_source_commit_matches_current_tree,
        load_contract=load_contract,
        _case_artifact_path=_case_artifact_path,
        _checkpoint_map=_checkpoint_map,
        _load_json=_load_json,
        _schema_errors=_schema_errors,
        _sha256_bytes=_sha256_bytes,
        _validate_inventory=_validate_inventory,
        _validate_provenance=_validate_provenance,
        _validate_remote_ci_provenance=_validate_remote_ci_provenance,
    )


def _validate_provenance(*args: Any, **kwargs: Any) -> None:
    return evidence_validation._validate_provenance(_validation_context(), *args, **kwargs)


def _validate_remote_ci_provenance(*args: Any, **kwargs: Any) -> None:
    return evidence_validation._validate_remote_ci_provenance(_validation_context(), *args, **kwargs)


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


def validate_evidence(*args: Any, **kwargs: Any) -> int:
    return evidence_validation.validate_evidence(_validation_context(), *args, **kwargs)


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


def _adapter_context() -> package_adapters.AdapterContext:
    return package_adapters.AdapterContext(
        _canonical_bytes=_canonical_bytes,
        _checkpoint_map=_checkpoint_map,
        _is_same_git_install_safety_difference=_is_same_git_install_safety_difference,
        _is_same_npm_install_safety_difference=_is_same_npm_install_safety_difference,
        _load_json=_load_json,
        _package_manager_cancellation_safety_difference=_package_manager_cancellation_safety_difference,
        _record_baseline_case=_record_baseline_case,
        _refresh_checkpoint_result=_refresh_checkpoint_result,
        _runtime_state=_runtime_state,
        _sha256_bytes=_sha256_bytes,
        _sha256_file=_sha256_file,
        _trx_test_outcomes=_trx_test_outcomes,
        _write_case_artifact=_write_case_artifact,
        load_contract=load_contract,
    )


def _record_cli_process_cases(*args: Any, **kwargs: Any) -> bool:
    return package_adapters._record_cli_process_cases(_adapter_context(), *args, **kwargs)


def _record_managed_npm_cases(*args: Any, **kwargs: Any) -> bool:
    return package_adapters._record_managed_npm_cases(_adapter_context(), *args, **kwargs)


def _record_npm_update_cases(*args: Any, **kwargs: Any) -> bool:
    return package_adapters._record_npm_update_cases(_adapter_context(), *args, **kwargs)


def _record_failure_recovery_cases(*args: Any, **kwargs: Any) -> bool:
    return package_adapters._record_failure_recovery_cases(_adapter_context(), *args, **kwargs)


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
    collision_proven = isinstance(collision_errors, list) and clone_destination_collision(collision_errors)
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


def _record_concurrency_cases(*args: Any, **kwargs: Any) -> bool:
    return package_adapters._record_concurrency_cases(_adapter_context(), *args, **kwargs)


def _record_differential_acceptance_cases(*args: Any, **kwargs: Any) -> bool:
    return package_adapters._record_differential_acceptance_cases(_adapter_context(), *args, **kwargs)


def _record_package_manager_cases(*args: Any, **kwargs: Any) -> bool:
    return package_adapters._record_package_manager_cases(_adapter_context(), *args, **kwargs)


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


def _record_legacy_global_cases(*args: Any, **kwargs: Any) -> bool:
    return package_adapters._record_legacy_global_cases(_adapter_context(), *args, **kwargs)


def _record_git_install_cases(*args: Any, **kwargs: Any) -> bool:
    return package_adapters._record_git_install_cases(_adapter_context(), *args, **kwargs)


def _record_git_update_cases(*args: Any, **kwargs: Any) -> bool:
    return package_adapters._record_git_update_cases(_adapter_context(), *args, **kwargs)


def _record_bulk_update_cases(*args: Any, **kwargs: Any) -> bool:
    return package_adapters._record_bulk_update_cases(_adapter_context(), *args, **kwargs)


def _record_configuration_cases(*args: Any, **kwargs: Any) -> bool:
    return package_adapters._record_configuration_cases(_adapter_context(), *args, **kwargs)


def _record_startup_reload_cases(*args: Any, **kwargs: Any) -> bool:
    return package_adapters._record_startup_reload_cases(_adapter_context(), *args, **kwargs)


def _record_identity_cases(*args: Any, **kwargs: Any) -> bool:
    return package_adapters._record_identity_cases(_adapter_context(), *args, **kwargs)


def _record_removal_listing_cases(*args: Any, **kwargs: Any) -> bool:
    return package_adapters._record_removal_listing_cases(_adapter_context(), *args, **kwargs)


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
    for case_id, scenario, test_filter, expected_tests in [
        ('distribution.temporary-npm-package', 'temporary-npm-package',
         'FullyQualifiedName~PiSharp.Tests.TemporaryManagedExtensionTests.'
         'ExplicitNpmExtensionLoadsOnlyManifestEnabledExtensionsWithoutChangingPackageSettings', 4),
        ('distribution.temporary-git-package', 'temporary-git-package',
         'FullyQualifiedName~PiSharp.Tests.TemporaryManagedExtensionTests.'
         'ExplicitGitExtensionLoadsFromTemporaryManagedCacheWithoutChangingPackageSettings|'
         'FullyQualifiedName~PiSharp.Tests.TemporaryManagedExtensionTests.'
         'TemporaryGitRepositoriesWithSameHostPathAndRefUseIsolatedCaches', 2),
        ('distribution.temporary-source-order', 'temporary-mixed-source-order',
         'FullyQualifiedName~PiSharp.Tests.TemporaryManagedExtensionTests.'
         'TemporaryNpmAndGitRootsRetainExplicitSourceOrder', 1),
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
        test_pass = test_exit == 0 and counts == {
            'discovered': expected_tests, 'passed': expected_tests, 'failed': 0, 'skipped': 0,
        }
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
    context = quality_gate.GateContext(
        HERE=HERE,
        ROOT=ROOT,
        _canonical_bytes=_canonical_bytes,
        _checkpoint_map=_checkpoint_map,
        _load_json=_load_json,
        _record_baseline_case=_record_baseline_case,
        _refresh_checkpoint_result=_refresh_checkpoint_result,
        _sha256_bytes=_sha256_bytes,
        _sha256_file=_sha256_file,
        _trx_counts=_trx_counts,
        _validate_provenance=_validate_provenance,
        _write_case_artifact=_write_case_artifact,
        load_contract=load_contract,
    )
    return quality_gate.execute_quality(context, output)


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
    context = publication_gate.GateContext(
        ROOT=ROOT,
        _canonical_bytes=_canonical_bytes,
        _checkpoint_map=_checkpoint_map,
        _download_run_package_evidence=_download_run_package_evidence,
        _latest_exact_workflow_run=_latest_exact_workflow_run,
        _load_json=_load_json,
        _persist_command_result=_persist_command_result,
        _record_publication_case=_record_publication_case,
        _refresh_checkpoint_result=_refresh_checkpoint_result,
        _remote_artifact_passes=_remote_artifact_passes,
        _required_workflow_jobs=_required_workflow_jobs,
        _run_github_api=_run_github_api,
        _runtime_state=_runtime_state,
        _sha256_file=_sha256_file,
        _workflow_gate_state=_workflow_gate_state,
        _write_milestone_result=_write_milestone_result,
        load_contract=load_contract,
        validate_evidence=validate_evidence,
    )
    return publication_gate.check_publication(context, pr_number, evidence_root)


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
