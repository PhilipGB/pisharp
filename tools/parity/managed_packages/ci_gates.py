"""Inspect required CI workflow jobs and exact-head results."""

import re
from typing import Any, Callable, Mapping

from errors import VerificationError


def _ci_event_context(event: str, payload: dict[str, Any], environment: Mapping[str, str],
                      checkout_sha: str, branch: str, workflow: str,
                      is_ancestor: Callable[[str, str], bool],
                      push_run: dict[str, Any] | None) -> dict[str, Any]:
    """Evaluate actual event identity and the workflow's PR-merge safeguards."""
    valid_sha = lambda value: isinstance(value, str) and re.fullmatch(r'[0-9a-f]{40}', value) is not None
    contract = all(marker in workflow for marker in (
        'PULL_REQUEST_HEAD_SHA:', 'PULL_REQUEST_BASE_SHA:', 'git merge-base --is-ancestor'))
    milestone = branch == 'milestone/managed-package-lifecycle-parity'
    if event == 'push':
        branch_match = milestone and environment.get('GITHUB_REF_NAME') == branch
        push_ok = valid_sha(checkout_sha) and environment.get('GITHUB_SHA') == checkout_sha and branch_match
        return {'pushHeadValid': push_ok, 'prHeadBaseValid': contract,
                'pushDetails': {'event': 'push', 'eventSha': environment.get('GITHUB_SHA'),
                                'checkedOutSha': checkout_sha, 'branch': environment.get('GITHUB_REF_NAME'),
                                'matchesMilestoneBranch': branch_match},
                'prDetails': {'syntheticMergeIdentityContractPresent': contract,
                              'actualPullRequestWillBeValidatedByExactHeadCi': True}}
    if event == 'pull_request':
        pr = payload.get('pull_request') or {}
        pr_head = (pr.get('head') or {}).get('sha')
        pr_base = (pr.get('base') or {}).get('sha')
        base_ref = (pr.get('base') or {}).get('ref')
        head_branch = (pr.get('head') or {}).get('ref')
        identity = (valid_sha(checkout_sha) and environment.get('GITHUB_SHA') == checkout_sha
                    and milestone and environment.get('GITHUB_HEAD_REF') == branch and head_branch == branch)
        contains_head = bool(identity and valid_sha(pr_head) and is_ancestor(pr_head, checkout_sha))
        contains_base = bool(identity and valid_sha(pr_base) and is_ancestor(pr_base, checkout_sha))
        return {'pushHeadValid': contains_head, 'prHeadBaseValid': bool(contains_head and contains_base
                                                                         and base_ref == 'main' and contract),
                'pushDetails': {'event': event, 'prHeadSha': pr_head, 'mergeSha': checkout_sha,
                                'mergeContainsHead': contains_head},
                'prDetails': {'headSha': pr_head, 'baseSha': pr_base, 'baseRef': base_ref,
                              'mergeSha': checkout_sha, 'mergeContainsHead': contains_head,
                              'mergeContainsBase': contains_base}}
    if event:
        return {'pushHeadValid': False, 'prHeadBaseValid': False,
                'pushDetails': {'event': event}, 'prDetails': {'unsupportedEvent': event}}
    valid_run = bool(push_run and milestone and valid_sha(checkout_sha)
                     and push_run.get('event') == 'push' and push_run.get('name') == 'Linux CI'
                     and push_run.get('head_branch') == branch
                     and push_run.get('head_sha') == checkout_sha and push_run.get('status') == 'completed'
                     and push_run.get('conclusion') == 'success')
    return {'pushHeadValid': valid_run, 'prHeadBaseValid': contract,
            'pushDetails': ({'runId': push_run.get('id'), 'headSha': push_run.get('head_sha'),
                             'conclusion': push_run.get('conclusion')} if valid_run else
                            {'headSha': checkout_sha, 'completedSuccessfulPushRun': False}),
            'prDetails': {'syntheticMergeIdentityContractPresent': contract,
                          'actualPullRequestWillBeValidatedByExactHeadCi': True}}


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


