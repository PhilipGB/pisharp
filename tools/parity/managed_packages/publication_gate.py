"""Publication gate for managed-package verification."""

from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable
import datetime as dt
import json
import sys
from urllib.parse import quote
from errors import ExecutionUnavailable, VerificationError


@dataclass(frozen=True)
class GateContext:
    ROOT: Path
    _canonical_bytes: Callable[..., Any]
    _checkpoint_map: Callable[..., Any]
    _download_run_package_evidence: Callable[..., Any]
    _latest_exact_workflow_run: Callable[..., Any]
    _load_json: Callable[..., Any]
    _persist_command_result: Callable[..., Any]
    _record_publication_case: Callable[..., Any]
    _refresh_checkpoint_result: Callable[..., Any]
    _remote_artifact_passes: Callable[..., Any]
    _required_workflow_jobs: Callable[..., Any]
    _run_github_api: Callable[..., Any]
    _runtime_state: Callable[..., Any]
    _sha256_file: Callable[..., Any]
    _workflow_gate_state: Callable[..., Any]
    _write_milestone_result: Callable[..., Any]
    load_contract: Callable[..., Any]
    validate_evidence: Callable[..., Any]


def check_publication(context: GateContext, pr_number: int, evidence_root: Path | None = None) -> int:
    if pr_number <= 0:
        raise VerificationError('PR number must be positive')
    output = (evidence_root or context.ROOT / 'artifacts/parity/managed-packages').resolve()
    evidence_path = output / 'evidence.json'
    if not evidence_path.is_file():
        raise ExecutionUnavailable(f'Pre-publication evidence is missing: {evidence_path}')
    report = context._load_json(evidence_path)
    context.validate_evidence(report, output, defer_publication_checkpoints=True)
    runtime = context._runtime_state()
    branch = runtime['branch']
    head_sha = runtime['headSha']
    command_argv = [sys.executable, *sys.argv]

    repository, repository_command = context._run_github_api('', label='GitHub repository metadata query')
    pr, pr_command = context._run_github_api(f'pulls/{pr_number}', label=f'GitHub PR #{pr_number} query')
    branch_response, branch_command = context._run_github_api(
        f'branches/{quote(branch, safe="")}', label=f'GitHub branch {branch} query')
    main_response, main_command = context._run_github_api('branches/main', label='GitHub main branch query')
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

    runs_response, runs_command = context._run_github_api(
        f'actions/runs?branch={quote(branch, safe="")}&per_page=100', label='GitHub exact-head workflow runs query')
    if not isinstance(runs_response, dict) or not isinstance(runs_response.get('workflow_runs'), list):
        raise VerificationError('GitHub workflow run response is missing its workflow_runs array')
    push_run = context._latest_exact_workflow_run(runs_response['workflow_runs'], event='push', branch=branch,
                                          head_sha=head_sha)
    pr_run = context._latest_exact_workflow_run(runs_response['workflow_runs'], event='pull_request', branch=branch,
                                        head_sha=head_sha)
    workflow = (context.ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')
    required_jobs = context._required_workflow_jobs(workflow)
    run_summaries: dict[str, Any] = {}
    artifacts_responses: dict[str, Any] = {}
    downloaded: dict[str, tuple[Path, dict[str, Any]]] = {}
    for event, run in (('push', push_run), ('pull_request', pr_run)):
        key = 'push' if event == 'push' else 'pr'
        if run is None:
            run_summaries[key] = {'run': None, 'jobs': [], 'gate': {}, 'passed': False}
            continue
        run_id = run.get('id')
        jobs_response, jobs_command = context._run_github_api(
            f'actions/runs/{run_id}/jobs?per_page=100', label=f'GitHub workflow run {run_id} jobs query')
        jobs = jobs_response.get('jobs') if isinstance(jobs_response, dict) else None
        if not isinstance(jobs, list):
            raise VerificationError(f'GitHub workflow run {run_id} response is missing its job list')
        gate = context._workflow_gate_state(jobs, required_jobs)
        run_pass = (run.get('status') == 'completed' and run.get('conclusion') == 'success'
                    and run.get('event') == event and run.get('head_branch') == branch
                    and run.get('head_sha') == head_sha and gate['requiredJobsPresent']
                    and gate['allJobsCompletedSuccessfully'])
        artifacts_response, artifacts_command = context._run_github_api(
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
            remote_root, download_command = context._download_run_package_evidence(run, output)
            remote_pass, artifact_validation = context._remote_artifact_passes(
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

    context._persist_command_result(report, output, f'github/publication-pr-{pr_number}.json', {
        'repository': repository, 'pr': pr, 'branch': branch_response, 'main': main_response,
        'commands': [repository_command, pr_command, branch_command, main_command],
    })
    context._persist_command_result(report, output, f'github/exact-head-ci-pr-{pr_number}.json', {
        'workflowRuns': runs_response, 'runDetails': run_summaries,
        'artifactResponses': artifacts_responses,
        'commands': [runs_command],
    })

    publication_checkpoint = context._checkpoint_map(context.load_contract())['draft-publication']
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
        context._record_publication_case(
            report, output, case_id, passed=publication_pass[case_id],
            details=publication_details[case_id],
            command_identity='gh api repository pull request and branch metadata',
            argv=publication_command_by_case[case_id],
            fixture_identity='github-pull-request')
    context._refresh_checkpoint_result(report, 'draft-publication')

    exact_checkpoint = context._checkpoint_map(context.load_contract())['exact-head-ci']
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
        context._record_publication_case(
            report, output, case_id, passed=exact_pass[case_id], details=exact_details[case_id],
            command_identity=command_identity, argv=case_argv, fixture_identity=fixture_identity)
    context._refresh_checkpoint_result(report, 'exact-head-ci')

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
    result_path.write_bytes(context._canonical_bytes(command_result))
    report['artifacts'] = [item for item in report['artifacts']
                           if item['path'] != result_path.relative_to(output).as_posix()]
    report['artifacts'].append({'path': result_path.relative_to(output).as_posix(),
                                'sha256': context._sha256_file(result_path)})

    runner_path = output / 'runner-results.json'
    runner_results = context._load_json(runner_path) if runner_path.is_file() else []
    runner_results = [item for item in runner_results if item.get('runnerId') != 'publication']
    all_publication_passed = all(publication_pass.values()) and all(exact_pass.values())
    runner_results.append({
        'runnerId': 'publication', 'argv': command_argv,
        'startedAt': dt.datetime.now(dt.timezone.utc).isoformat(),
        'exitCode': 0 if all_publication_passed else 1,
        'stdout': json.dumps({'publication': publication_pass, 'exactHeadCI': exact_pass}, sort_keys=True),
        'stderr': '',
    })
    runner_path.write_bytes(context._canonical_bytes(runner_results))
    report['artifacts'] = [item for item in report['artifacts'] if item['path'] != 'runner-results.json']
    report['artifacts'].append({'path': 'runner-results.json', 'sha256': context._sha256_file(runner_path)})
    report['provenance']['runnerCommands'] = [item['argv'] for item in runner_results]
    report['provenance']['runnerExitCodes'] = [item['exitCode'] for item in runner_results]
    report['testResults']['executed'] = sum(item['executed'] for item in report['cases'])
    report['testResults']['failed'] = sum(item['executed'] and not item['passed'] for item in report['cases'])
    report['testResults']['skipped'] = sum(item['skipped'] for item in report['cases'])
    report['executionExitCode'] = 0 if all_publication_passed and report['testResults']['failed'] == 0 else 1
    evidence_path.write_bytes(context._canonical_bytes(report))
    context.validate_evidence(report, output)
    context._write_milestone_result(report, pr_number, pr.get('html_url'), push_summary.get('run'),
                            pr_summary.get('run'))
    print(f'Passed publication and exact-head CI checkpoints for PR #{pr_number}: {evidence_path}')
    return 0
