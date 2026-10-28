"""Load and validate the pinned checkpoint contract."""

from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable
import json
import re

from errors import VerificationError


@dataclass(frozen=True)
class ContractContext:
    CONTRACT_HASH_PATH: Path
    CONTRACT_PATH: Path
    FIXTURE_VERSION: str
    PIN_PATH: Path
    _case_artifact_path: Callable[..., Any]
    _sha256_bytes: Callable[..., Any]


def load_contract(context: ContractContext) -> dict[str, Any]:
    raw = context.CONTRACT_PATH.read_bytes()
    expected_hash = context.CONTRACT_HASH_PATH.read_text(encoding='ascii').strip()
    actual_hash = context._sha256_bytes(raw)
    if not re.fullmatch(r'[0-9a-f]{64}', expected_hash) or expected_hash != actual_hash:
        raise VerificationError('checkpoint_contract.json does not match its committed SHA-256 pin')
    try:
        contract = json.loads(raw)
    except json.JSONDecodeError as error:
        raise VerificationError(f'checkpoint contract is malformed JSON: {error}') from error
    if contract.get('schemaVersion') != 1:
        raise VerificationError('Unsupported checkpoint contract schema version')
    if contract.get('fixtureVersion') != context.FIXTURE_VERSION:
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
    if not isinstance(upstream, dict) or upstream.get('revision') != context.PIN_PATH.read_text(encoding='ascii').strip():
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
        if checkpoint['requiredEvidenceFiles'] != [context._case_artifact_path(case_id) for case_id in cases]:
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
