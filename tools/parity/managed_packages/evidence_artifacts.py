"""Build compact, deterministic committed evidence from a verification run."""

from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path
from typing import Any


def canonical_bytes(value: Any) -> bytes:
    return (json.dumps(value, sort_keys=True, indent=2, ensure_ascii=False) + '\n').encode()


def _sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _stable_value(value: Any, *, repository: str, evidence_root: str,
                  case_id: str = '', git_ids: dict[str, str] | None = None) -> Any:
    if git_ids is None:
        git_ids = {}
    if isinstance(value, dict):
        return {
            key: ('<runtime-identity>' if case_id == 'v0.runtime-provenance' and key in {
                'checkedOutSourceSha', 'pisharpSourceSha', 'binarySha256',
            } else '<loopback-port>' if key == 'gitDaemonPort' and isinstance(item, int)
            else _stable_value(item, repository=repository, evidence_root=evidence_root,
                               case_id=case_id, git_ids=git_ids))
            for key, item in sorted(value.items())
            if key.lower() not in {
                'argv', 'generatedat', 'startedat', 'timestamp', 'durationms',
                'executiontime', 'logpath', 'profilepath', 'temproot',
            }
        }
    if isinstance(value, list):
        if (case_id == 'cli.update' and len(value) == 2 and value[0] == 'extensions/index.dll'
                and isinstance(value[1], str) and re.fullmatch(r'[0-9a-f]{64}', value[1])):
            return [value[0], '<built-fixture-digest>']
        return [_stable_value(item, repository=repository, evidence_root=evidence_root,
                              case_id=case_id, git_ids=git_ids) for item in value]
    if isinstance(value, str):
        normalized = value.replace(evidence_root, '<EVIDENCE>')
        try:
            relative = Path(evidence_root).relative_to(repository)
            obfuscated = str(Path(repository).parent / '<app>' / relative)
            normalized = normalized.replace(obfuscated, '<EVIDENCE>')
        except ValueError:
            pass
        normalized = normalized.replace(repository, '<REPOSITORY>')
        normalized = re.sub(r'/tmp/[^/\s]+', '/tmp/<TEMP>', normalized)
        normalized = re.sub(r'(?<=127\.0\.0\.1):\d+', ':<PORT>', normalized)
        if (case_id.startswith('git.') or case_id in {
                'cli.update', 'concurrent.same-git-install', 'failure.cancelled-update',
        }) \
                and re.fullmatch(r'[0-9a-f]{40}', normalized):
            if normalized not in git_ids:
                git_ids[normalized] = f'<fixture-git-{len(git_ids) + 1}>'
            return git_ids[normalized]
        return normalized
    return value


def _case_digest(value: Any, *, repository: str, evidence_root: str, case_id: str = '') -> str | None:
    if value is None:
        return None
    normalized = _stable_value(value, repository=repository, evidence_root=evidence_root, case_id=case_id)
    return _sha256(canonical_bytes(normalized))


def _case_side_digest(case_id: str, side: str, value: Any, *, repository: str,
                     evidence_root: str) -> str | None:
    if (case_id == 'pm.cancellation' and side == 'actual' and isinstance(value, dict)
            and isinstance(value.get('state'), dict) and value['state'].get('exitCode') in (1, 130)):
        value = {**value, 'state': {**value['state'], 'exitCode': 'bounded-cancellation-exit'}}
    if case_id.startswith('d0.same-pi-') and isinstance(value, dict) and isinstance(value.get('state'), dict):
        state = value['state']
        baseline = state.get('baselineNormalizedOracleSha256')
        repeat = state.get('repeatNormalizedOracleSha256')
        if isinstance(baseline, str) and re.fullmatch(r'[0-9a-f]{64}', baseline) and baseline == repeat:
            value = {**value, 'state': {**state, 'baselineNormalizedOracleSha256': '<matching-digests>',
                                        'repeatNormalizedOracleSha256': '<matching-digests>'}}
    return _case_digest(value, repository=repository, evidence_root=evidence_root, case_id=case_id)


def build_case_results(evidence: dict[str, Any], contract: dict[str, Any],
                       evidence_root: Path, repository_root: Path,
                       source_sha: str) -> dict[str, Any]:
    completed_ids = []
    for checkpoint in contract['checkpoints']:
        if checkpoint['id'] == 'committed-evidence':
            break
        completed_ids.append(checkpoint['id'])
    repository = str(repository_root.resolve())
    checkpoint_results = {item['checkpointId']: item for item in evidence['checkpointResults']}
    cases = []
    for case in evidence['cases']:
        if case['checkpoint'] not in completed_ids:
            continue
        cases.append({
            'caseId': case['caseId'],
            'checkpoint': case['checkpoint'],
            'executed': case['executed'],
            'passed': case['passed'],
            'classification': case['classification'],
            'assertions': case['assertions'],
            'commandIdentity': case['execution']['commandIdentity'],
            'commandExitCode': case['execution']['commandExitCode'],
            'fixtureIdentity': case['execution']['fixtureIdentity'],
            'oracleSha256': _case_side_digest(case['caseId'], 'oracle', case['oracle'], repository=repository,
                                              evidence_root=str(evidence_root.resolve())),
            'actualSha256': _case_side_digest(case['caseId'], 'actual', case['actual'], repository=repository,
                                              evidence_root=str(evidence_root.resolve())),
        })
    return {
        'schemaVersion': 1,
        'goal': 'managed-package-lifecycle-parity',
        'pinnedPiSha': evidence['provenance']['piSha'],
        'sourceImplementationSha': source_sha,
        'branchBaseSha': evidence['provenance']['baseSha'],
        'branch': evidence['provenance']['branch'],
        'checkpoints': [
            {
                'checkpointId': checkpoint_id,
                'status': checkpoint_results[checkpoint_id]['status'],
                'discovered': checkpoint_results[checkpoint_id]['discovered'],
                'executed': checkpoint_results[checkpoint_id]['executed'],
                'failed': checkpoint_results[checkpoint_id]['failed'],
                'skipped': checkpoint_results[checkpoint_id]['skipped'],
            }
            for checkpoint_id in completed_ids
        ],
        'cases': cases,
        'classificationCounts': {
            classification: sum(item['classification'] == classification for item in cases if item['executed'])
            for classification in ('strict', 'equivalent', 'intentional-safety-difference')
        },
    }


def build_case_coverage(contract: dict[str, Any]) -> dict[str, Any]:
    return {
        'schemaVersion': 1,
        'requiredCheckpointIds': contract['requiredCheckpointIds'],
        'checkpoints': [
            {'checkpointId': item['id'], 'requiredCaseIds': item['requiredCaseIds']}
            for item in contract['checkpoints']
        ],
        'requiredCaseCount': sum(len(item['requiredCaseIds']) for item in contract['checkpoints']),
    }


def build_inventory(inventory: dict[str, Any], case_results: dict[str, Any]) -> dict[str, Any]:
    observed = {item['caseId']: item for item in case_results['cases']}
    capabilities = []
    for capability in inventory['capabilities']:
        cases = [observed[item] for item in capability['requiredCaseIds'] if item in observed]
        capabilities.append({
            'capabilityId': capability['capabilityId'],
            'description': capability['description'],
            'baselineStatus': capability['currentImplementationStatus'],
            'requiredCaseIds': capability['requiredCaseIds'],
            'verifiedCases': len(cases),
            'passedCases': sum(item['passed'] for item in cases),
            'classifications': {
                classification: sum(item['classification'] == classification for item in cases)
                for classification in ('strict', 'equivalent', 'intentional-safety-difference')
            },
            'sourceOwners': [
                {'repository': owner['repository'], 'path': owner['path'], 'symbols': owner['symbols']}
                for owner in capability['sourceOwners']
            ],
            'confirmedBaselineDifferences': [item['differenceId'] for item in capability['confirmedDifferences']],
            'baselineCorrectionsRequired': capability['correctiveWorkRequired'],
        })
    return {
        'schemaVersion': 1,
        'piSha': inventory['piSha'],
        'inventoryBaseSha': inventory['pisharpSourceSha'],
        'repository': inventory['repository'],
        'capabilities': capabilities,
    }


def build_quality_summary(quality: dict[str, Any]) -> dict[str, Any]:
    return {
        'schemaVersion': 1,
        'match': quality['match'],
        'assertions': quality['assertions'],
        'fullDotnetSuite': quality['fullDotnetSuite']['counts'],
        'focusedDotnetTests': {
            name: item['counts'] for name, item in sorted(quality['focusedDotnetTests'].items())
        },
        'pythonTests': [{'testsRun': item['testsRun'], 'exitCode': item['exitCode']}
                        for item in quality['pythonTests']],
        'nodeTests': quality['nodeTests']['counts'],
        'mcp': quality['mcp'],
        'docsExitCode': quality['docsExitCode'],
        'differentialCheckpoints': quality['differentialCheckpoints'],
    }


def build_differential_summary(differential: dict[str, Any], cases: dict[str, Any]) -> dict[str, Any]:
    calibrations = {
        name: {
            'fixture': item['fixture'],
            'baselineCaseCount': item['baselineCaseCount'],
            'repeatCaseCount': item['repeatCaseCount'],
            'normalizedOracleEqual': item['normalizedOracleEqual'],
            'oracleDigestsMatched': (isinstance(item['baselineNormalizedOracleSha256'], str)
                                     and re.fullmatch(r'[0-9a-f]{64}', item['baselineNormalizedOracleSha256'])
                                     is not None and item['baselineNormalizedOracleSha256']
                                     == item['repeatNormalizedOracleSha256']),
        }
        for name, item in sorted(differential['calibrations'].items())
    }
    return {
        'schemaVersion': 1,
        'piSha': differential['piSha'],
        'match': differential['match'],
        'caseCount': sum(item['executed'] and item['passed'] for item in cases['cases']
                         if item['checkpoint'] == 'differential-acceptance'),
        'calibrations': calibrations,
        'negativeControl': {
            'executed': differential['negativeControl']['executed'],
            'detectedMismatch': differential['negativeControl']['detectedMismatch'],
            'mutatedField': differential['negativeControl']['mutatedField'],
            'originalComparisonEqual': differential['negativeControl']['originalComparisonEqual'],
            'mutatedComparisonEqual': differential['negativeControl']['mutatedComparisonEqual'],
        },
    }


def build_source_links(inventory: dict[str, Any], contract: dict[str, Any],
                       pi_root: Path, repository_root: Path) -> dict[str, Any]:
    links: set[tuple[str, str]] = set()
    for capability in inventory['capabilities']:
        links.update((owner['repository'], owner['path']) for owner in capability['sourceOwners'])
    for checkpoint in contract['checkpoints']:
        links.update(('pi', path) for path in checkpoint['upstreamSourceReferences'])
    sources = []
    for repository, path in sorted(links):
        root = pi_root if repository == 'pi' else repository_root
        source_path = root / path
        if repository not in ('pi', 'pisharp'):
            raise ValueError(f'Unknown source repository: {repository}')
        if not source_path.is_file():
            raise FileNotFoundError(f'{repository} source does not exist: {path}')
        sources.append({
            'repository': repository,
            'path': path,
            'exists': True,
            'sha256': _sha256(source_path.read_bytes()),
        })
    return {'schemaVersion': 1, 'sources': sources}


def build_bundle(evidence: dict[str, Any], contract: dict[str, Any], inventory: dict[str, Any],
                 evidence_root: Path, pi_root: Path, repository_root: Path,
                 source_sha: str) -> dict[str, dict[str, Any]]:
    quality = json.loads((evidence_root / 'quality/quality-results.json').read_text(encoding='utf-8'))
    differential = json.loads((evidence_root / 'managed-package-d0.json').read_text(encoding='utf-8'))
    cases = build_case_results(evidence, contract, evidence_root, repository_root, source_sha)
    return {
        'implementation.json': {
            'schemaVersion': 1,
            'sourceSha': source_sha,
            'branchBaseSha': evidence['provenance']['baseSha'],
            'pinnedPiSha': evidence['provenance']['piSha'],
            'repository': evidence['provenance']['repository'],
        },
        'case-results.json': cases,
        'case-coverage.json': build_case_coverage(contract),
        'inventory.json': build_inventory(inventory, cases),
        'quality-summary.json': build_quality_summary(quality),
        'differential-summary.json': build_differential_summary(differential, cases),
        'source-links.json': build_source_links(inventory, contract, pi_root, repository_root),
    }


def write_bundle(bundle: dict[str, dict[str, Any]], target: Path) -> None:
    target.mkdir(parents=True, exist_ok=True)
    for name, value in bundle.items():
        (target / name).write_bytes(canonical_bytes(value))
