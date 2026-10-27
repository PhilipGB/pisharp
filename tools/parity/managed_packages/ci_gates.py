"""Inspect required CI workflow jobs and exact-head results."""

from typing import Any

from errors import VerificationError


def _latest_exact_workflow_run(runs: list[dict[str, Any]], *, event: str, branch: str,
                               head_sha: str) -> dict[str, Any] | None:
    candidates = [item for item in runs
                  if item.get('event') == event and item.get('head_branch') == branch
                  and item.get('head_sha') == head_sha and item.get('name') == 'Linux CI']
    return max(candidates, key=lambda item: (item.get('run_number', 0), item.get('id', 0))) if candidates else None


def _required_workflow_jobs(workflow: str) -> list[str]:
    in_jobs = False
    names: list[str] = []
    for line in workflow.splitlines():
        if line == 'jobs:':
            in_jobs = True
            continue
        if in_jobs and line and not line.startswith(' '):
            break
        if in_jobs and line.startswith('  ') and not line.startswith('    '):
            candidate = line.strip()
            if candidate.endswith(':'):
                names.append(candidate[:-1])
    if not names:
        raise VerificationError('CI workflow has no discoverable required jobs')
    return names


def _workflow_step_block(workflow: str, step_name: str) -> str:
    lines = workflow.splitlines()
    marker = f'      - name: {step_name}'
    matches = [index for index, line in enumerate(lines) if line == marker]
    if len(matches) != 1:
        return ''
    start = matches[0]
    end = next((index for index in range(start + 1, len(lines))
                if lines[index].startswith('      - ')), len(lines))
    return '\n'.join(lines[start:end])


def _workflow_has_required_steps(workflow: str, pinned_pi_sha: str) -> bool:
    corpus_step = _workflow_step_block(workflow, 'Run managed-package parity corpus')
    return all(marker in workflow for marker in (
        'python3 tools/parity/managed_packages/verify.py execute --all',
        'python3 tools/parity/managed_packages/verify.py check --all',
        'python3 tools/parity/managed_packages/test_verifier.py',
        'npm --prefix artifacts/parity/pi-ownership-oracle run hydrate:model-data',
        'npm --prefix artifacts/parity/pi-ownership-oracle run build:offline',
        'PULL_REQUEST_HEAD_SHA:', 'PULL_REQUEST_BASE_SHA:',
        'git merge-base --is-ancestor', 'if: always()', 'actions/upload-artifact@v4',
        pinned_pi_sha,
    )) and ('PISHARP_TEST_OWNERSHIP_ORACLE: ${{ github.workspace }}/artifacts/parity/builtin-ownership/pinned.json'
            in corpus_step) and 'continue-on-error:' not in workflow and '|| true' not in workflow


def _workflow_gate_state(jobs: list[dict[str, Any]], required_jobs: list[str]) -> dict[str, Any]:
    by_name = {item.get('name'): item for item in jobs if isinstance(item, dict)}
    required_present = all(name in by_name for name in required_jobs)
    all_successful = bool(jobs) and all(item.get('status') == 'completed'
                                        and item.get('conclusion') == 'success' for item in jobs)
    package_job = by_name.get('managed-package-verifier', {})
    package_steps = {item.get('name'): item for item in package_job.get('steps', [])
                     if isinstance(item, dict)}
    package_step_names = (
        'Verify managed-package evidence controls',
        'Hydrate pinned Pi model data',
        'Build pinned Pi offline',
        'Run managed-package parity corpus',
        'Record managed-package CI integration',
        'Independently check managed-package evidence',
    )
    package_gate = all(package_steps.get(name, {}).get('conclusion') == 'success'
                       for name in package_step_names)
    full_tests = package_steps.get('Run dotnet test PiSharp.slnx --no-build --no-restore', {})
    mcp_steps = {item.get('name'): item for item in by_name.get('mcp-conformance', {}).get('steps', [])
                 if isinstance(item, dict)}
    mcp_step = mcp_steps.get('Run node tools/parity/mcp-conformance/run.mjs', {})
    return {
        'requiredJobsPresent': required_present,
        'requiredJobNames': required_jobs,
        'observedJobNames': sorted(name for name in by_name if isinstance(name, str)),
        'allJobsCompletedSuccessfully': all_successful,
        'packageGatePassed': package_gate,
        'pinnedPiPrepared': (package_steps.get('Hydrate pinned Pi model data', {}).get('conclusion') == 'success'
                             and package_steps.get('Build pinned Pi offline', {}).get('conclusion') == 'success'),
        'packageGateSteps': {name: package_steps.get(name, {}).get('conclusion')
                             for name in package_step_names},
        'fullDotnetSuitePassed': full_tests.get('conclusion') == 'success',
        'mcpConformancePassed': mcp_step.get('conclusion') == 'success'
        and by_name.get('mcp-conformance', {}).get('conclusion') == 'success',
    }


