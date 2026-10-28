"""Builders for deterministic verifier control fixtures; never used as product evidence."""

from __future__ import annotations

import copy
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
from typing import Any

import verify

sys.path.insert(0, str(verify.ROOT / 'tools/parity'))


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _pi_checkout() -> Path:
    candidates = [
        os.environ.get('PISHARP_MANAGED_PACKAGES_PI'),
        str(verify.ROOT / 'artifacts/parity/pi-ownership-oracle'),
        '/tmp/pi-multimodal-pinned',
    ]
    for candidate in candidates:
        if candidate and Path(candidate).is_dir():
            return Path(candidate).resolve()
    raise verify.ExecutionUnavailable('No pinned Pi checkout is available for the valid verifier control fixture')


def make_valid_evidence(output: Path) -> dict[str, Any]:
    output.mkdir(parents=True, exist_ok=True)
    contract = verify.load_contract()
    runtime = verify._runtime_state()
    binary = verify.HERE / 'verify.py'
    pi_path = _pi_checkout()
    from pi_reference import verify_pi_checkout
    pi_sha = verify_pi_checkout(pi_path)
    cases: list[dict[str, Any]] = []
    artifacts: list[dict[str, str]] = []
    checkpoint_results: list[dict[str, Any]] = []
    for checkpoint in contract['checkpoints']:
        required_case_ids = checkpoint['requiredCaseIds']
        checkpoint_results.append({
            'checkpointId': checkpoint['id'], 'status': 'passed', 'discovered': len(required_case_ids),
            'executed': len(required_case_ids), 'failed': 0, 'skipped': 0, 'commandExitCode': 0,
            'runnerIds': checkpoint['requiredExecutableVerification'],
        })
        for case_id in required_case_ids:
            state = {
                'packageVersion': '1.2.3', 'scope': 'user',
                'resourceInvocation': {'exitCode': 0, 'value': f'control-{case_id}'},
            }
            case = {
                'caseId': case_id, 'checkpoint': checkpoint['id'], 'executed': True, 'skipped': False,
                'classification': 'strict',
                'oracle': {'exitCode': 0, 'state': copy.deepcopy(state)},
                'actual': {'exitCode': 0, 'state': copy.deepcopy(state)},
                'assertions': {assertion: True for assertion in checkpoint['requiredAssertions'].get(case_id, [case_id])},
                'passed': True,
                'execution': {
                    'commandIdentity': 'synthetic-verifier-control', 'argv': [sys.executable, 'synthetic-case'],
                    'commandExitCode': 0, 'fixtureIdentity': checkpoint['requiredFixtureFamilies'][0],
                    'inputFingerprint': _sha256(verify._canonical_bytes({'caseId': case_id, 'fixture': 'valid-control'})),
                    'timestamp': dt.datetime.now(dt.timezone.utc).isoformat(),
                },
            }
            cases.append(case)
            result_path = output / verify._case_artifact_path(case_id)
            result_path.parent.mkdir(parents=True, exist_ok=True)
            projection = {key: case[key] for key in ('caseId', 'checkpoint', 'executed', 'classification',
                                                      'oracle', 'actual', 'assertions', 'execution')}
            raw = verify._canonical_bytes(projection)
            result_path.write_bytes(raw)
            artifacts.append({'path': result_path.relative_to(output).as_posix(), 'sha256': _sha256(raw)})

    environment = verify._platform_metadata()
    evidence = {
        'schemaVersion': 1,
        'runId': 'valid-control-' + runtime['headSha'][:12],
        'generatedAt': dt.datetime.now(dt.timezone.utc).isoformat(),
        'provenance': {
            'repository': runtime['remote'], 'piSha': pi_sha, 'piPath': str(pi_path),
            'pisharpSourceSha': runtime['headSha'], 'checkedOutSourceSha': runtime['headSha'],
            'branch': runtime['branch'], 'baseSha': runtime['baseSha'],
            'fixtureVersion': verify.FIXTURE_VERSION,
            'binaryIdentity': {'scope': 'repository', 'path': binary.relative_to(verify.ROOT).as_posix(),
                               'sha256': verify._sha256_file(binary)},
            'command': [sys.executable, 'test_verifier.py', 'valid-control'],
            'environment': environment, 'testControl': True,
        },
        'checkpointResults': checkpoint_results, 'cases': cases, 'artifacts': artifacts, 'exclusions': [],
        'testResults': {'discovered': len(cases), 'executed': len(cases), 'failed': 0, 'skipped': 0},
        'executionExitCode': 0,
    }
    return evidence


def write_evidence(root: Path, evidence: dict[str, Any]) -> None:
    (root / 'evidence.json').write_bytes(verify._canonical_bytes(evidence))


def copied_fixture(base_root: Path, target_root: Path) -> dict[str, Any]:
    shutil.copytree(base_root, target_root)
    return json.loads((target_root / 'evidence.json').read_text(encoding='utf-8'))
