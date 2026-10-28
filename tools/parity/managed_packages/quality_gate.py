"""Quality gate for managed-package verification."""

from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable
import datetime as dt
import json
import os
import re
import subprocess
import sys
from errors import MandatoryFailure


@dataclass(frozen=True)
class GateContext:
    HERE: Path
    ROOT: Path
    _canonical_bytes: Callable[..., Any]
    _checkpoint_map: Callable[..., Any]
    _load_json: Callable[..., Any]
    _record_baseline_case: Callable[..., Any]
    _refresh_checkpoint_result: Callable[..., Any]
    _sha256_bytes: Callable[..., Any]
    _sha256_file: Callable[..., Any]
    _trx_counts: Callable[..., Any]
    _validate_provenance: Callable[..., Any]
    _write_case_artifact: Callable[..., Any]
    load_contract: Callable[..., Any]


def execute_quality(context: GateContext, output: Path) -> int:
    """Run local quality gates and record machine-readable test outcomes."""
    evidence_path = output / 'evidence.json'
    report = context._load_json(evidence_path)
    context._validate_provenance(report['provenance'], output, allow_synthetic=False)
    checkpoint = context._checkpoint_map(context.load_contract())['quality']
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
            artifacts_by_path[relative] = {'path': relative, 'sha256': context._sha256_file(path)}
        else:
            artifacts_by_path.pop(relative, None)
        report['artifacts'] = [artifacts_by_path[item] for item in sorted(artifacts_by_path)]
        evidence_path.write_bytes(context._canonical_bytes(report))

    def run(name: str, argv: list[str], *, env_overrides: dict[str, str] | None = None,
            timeout: int = 900) -> dict[str, Any]:
        environment = dict(os.environ)
        if env_overrides:
            environment.update(env_overrides)
        started = dt.datetime.now(dt.timezone.utc).isoformat()
        try:
            completed = subprocess.run(argv, cwd=context.ROOT, env=environment, capture_output=True,
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
        log_path.write_bytes(context._canonical_bytes({'argv': argv, 'startedAt': started, 'exitCode': exit_code,
                                               'stdout': stdout, 'stderr': stderr}))
        update_artifact(log_path)
        result = {'argv': argv, 'startedAt': started, 'exitCode': exit_code,
                  'logPath': relative_log, 'logSha256': context._sha256_file(log_path),
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
    full_counts = context._trx_counts(full_trx)

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
            'dotnet', 'test', str(context.ROOT / 'tests/PiSharp.Tests/PiSharp.Tests.csproj'),
            '--no-build', '--no-restore', '--filter', test_filter,
            '--logger', f'trx;LogFileName={trx_name}', '--results-directory', str(trx_root.resolve()),
        ], timeout=1800)
        trx_path = trx_root / trx_name
        update_artifact(trx_path)
        focused_results[name] = {'exitCode': test_result['exitCode'], 'counts': context._trx_counts(trx_path),
                                 'trxPath': trx_path.relative_to(output).as_posix() if trx_path.is_file() else None,
                                 'trxSha256': context._sha256_file(trx_path) if trx_path.is_file() else None}

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
            sys.executable, str(context.HERE / 'verify.py'), 'check', '--checkpoint', checkpoint_id,
            '--evidence', str(output.resolve()),
        ], timeout=900))

    current_binary_sha = context._sha256_file(context.ROOT / report['provenance']['binaryIdentity']['path'])
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
                            'trxSha256': context._sha256_file(full_trx) if full_trx.is_file() else None},
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
    quality_report_path.write_bytes(context._canonical_bytes(quality_report))

    quality_checkpoint = context._checkpoint_map(context.load_contract())['quality']
    runner_results = context._load_json(output / 'runner-results.json')
    aggregate_result = {
        'runnerId': 'quality',
        'argv': [command for result in command_results.values() for command in result['argv']],
        'startedAt': dt.datetime.now(dt.timezone.utc).isoformat(),
        'exitCode': 0 if all(assertions.values()) else 1,
        'stdout': json.dumps(assertions, sort_keys=True), 'stderr': '',
    }
    runner_results = [item for item in runner_results if item.get('runnerId') != 'quality']
    runner_results.append(aggregate_result)
    (output / 'runner-results.json').write_bytes(context._canonical_bytes(runner_results))

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
        context._record_baseline_case(
            report, output, case_id,
            expected=expected, observed=observed, passed=passed,
            command_identity=command_identity, argv=argv,
            command_exit_code=0 if passed else 1, fixture_identity=fixture_identity,
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                'case': case_id, 'binarySha256': current_binary_sha,
                'qualityReportSha256': context._sha256_file(quality_report_path), 'assertion': passed,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = 'strict' if passed else 'mismatch'
        recorded['assertions'] = {case_id: bool(passed)}
        recorded['passed'] = bool(passed)
        context._write_case_artifact(report, output, recorded)

    context._refresh_checkpoint_result(report, 'quality')
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
        artifacts.append({'path': relative.as_posix(), 'sha256': context._sha256_file(artifact_path)})
    report['artifacts'] = artifacts
    evidence_path.write_bytes(context._canonical_bytes(report))
    if all(assertions.values()):
        print(f'Passed checkpoint quality: {evidence_path}')
        return 0
    print(f'Quality gates failed; evidence saved at {evidence_path}', file=sys.stderr)
    return 3 if any(item['exitCode'] in (124, 127) for values in command_by_case.values()
                    for item in values[1]) else 1
