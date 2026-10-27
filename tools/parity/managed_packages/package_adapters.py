"""Translate package differential and test runner results into checkpoint evidence.

The context explicitly supplies verifier services so adapters stay independent of
the CLI entry point and remain testable with substituted implementations.
"""

from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable


@dataclass(frozen=True)
class AdapterContext:
    _canonical_bytes: Callable[..., Any]
    _checkpoint_map: Callable[..., Any]
    _is_same_git_install_safety_difference: Callable[..., Any]
    _is_same_npm_install_safety_difference: Callable[..., Any]
    _load_json: Callable[..., Any]
    _package_manager_cancellation_safety_difference: Callable[..., Any]
    _record_baseline_case: Callable[..., Any]
    _refresh_checkpoint_result: Callable[..., Any]
    _runtime_state: Callable[..., Any]
    _sha256_bytes: Callable[..., Any]
    _sha256_file: Callable[..., Any]
    _trx_test_outcomes: Callable[..., Any]
    _write_case_artifact: Callable[..., Any]
    load_contract: Callable[..., Any]


def _record_cli_process_cases(context: AdapterContext, report: dict[str, Any], output: Path,
                             runner_results: list[dict[str, Any]]) -> bool:
    report_path = output / 'package-cli.json'
    differential = context._load_json(report_path) if report_path.is_file() else {}
    pi_result = differential.get('results', {}).get('pi', {})
    actual_result = differential.get('results', {}).get('pisharp', {})
    runner = next((item for item in runner_results if item.get('runnerId') == 'package-cli'), {})
    differential_pass = (runner.get('exitCode') == 0 and differential.get('match') is True
                         and differential.get('piSha') == context.load_contract()['upstream']['revision'])
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
        context._record_baseline_case(
            report, output, case_id, expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else runner.get('exitCode', 1) or 1, 'state': actual_state},
            passed=passed, command_identity='tools/parity/package-cli-process-differential.py',
            argv=runner.get('argv', []), command_exit_code=0 if passed else runner.get('exitCode', 1) or 1,
            fixture_identity='cli-process',
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'reportSha256': context._sha256_file(report_path) if report_path.is_file() else None,
                'fields': fields,
            })),
        )
        if case_id == 'cli.help' or (case_id == 'cli.update' and equivalent_projection and passed):
            case = next(item for item in report['cases'] if item['caseId'] == case_id)
            case['classification'] = 'equivalent'
            context._write_case_artifact(report, output, case)
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
    context._record_baseline_case(
        report, output, idempotent_id,
        expected={'exitCode': 0, 'state': oracle_idempotent},
        observed={'exitCode': 0 if idempotent_pass else runner.get('exitCode', 1) or 1,
                  'state': actual_idempotent},
        passed=idempotent_pass,
        command_identity='tools/parity/package-cli-process-differential.py',
        argv=runner.get('argv', []),
        command_exit_code=0 if idempotent_pass else runner.get('exitCode', 1) or 1,
        fixture_identity='real-npm-processes',
        input_fingerprint=context._sha256_bytes(context._canonical_bytes({
            'case': idempotent_id, 'piSha': differential.get('piSha'),
            'reportSha256': context._sha256_file(report_path) if report_path.is_file() else None,
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
        context._record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else runner.get('exitCode', 1) or 1,
                      'state': actual_state},
            passed=passed,
            command_identity='tools/parity/package-cli-process-differential.py',
            argv=runner.get('argv', []),
            command_exit_code=0 if passed else runner.get('exitCode', 1) or 1,
            fixture_identity='real-npm-processes',
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'reportSha256': context._sha256_file(report_path) if report_path.is_file() else None,
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
        context._record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else runner.get('exitCode', 1) or 1,
                      'state': actual_state},
            passed=passed,
            command_identity='tools/parity/package-cli-process-differential.py',
            argv=runner.get('argv', []),
            command_exit_code=0 if passed else runner.get('exitCode', 1) or 1,
            fixture_identity='local-npm-registry',
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'reportSha256': context._sha256_file(report_path) if report_path.is_file() else None,
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
    context._record_baseline_case(
        report, output, script_failure_id,
        expected={'exitCode': 0, 'state': oracle_script_failure},
        observed={'exitCode': 0 if script_failure_pass else runner.get('exitCode', 1) or 1,
                  'state': actual_script_failure},
        passed=script_failure_pass,
        command_identity='tools/parity/package-cli-process-differential.py',
        argv=runner.get('argv', []),
        command_exit_code=0 if script_failure_pass else runner.get('exitCode', 1) or 1,
        fixture_identity='real-npm-processes',
        input_fingerprint=context._sha256_bytes(context._canonical_bytes({
            'case': script_failure_id, 'piSha': differential.get('piSha'),
            'reportSha256': context._sha256_file(report_path) if report_path.is_file() else None,
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
    context._record_baseline_case(
        report, output, cancelled_id,
        expected={'exitCode': 0, 'state': oracle_cancelled},
        observed={'exitCode': 0 if cancelled_pass else runner.get('exitCode', 1) or 1,
                  'state': actual_cancelled},
        passed=cancelled_pass,
        command_identity='tools/parity/package-cli-process-differential.py',
        argv=runner.get('argv', []),
        command_exit_code=0 if cancelled_pass else runner.get('exitCode', 1) or 1,
        fixture_identity='local-npm-registry',
        input_fingerprint=context._sha256_bytes(context._canonical_bytes({
            'case': cancelled_id, 'piSha': differential.get('piSha'),
            'reportSha256': context._sha256_file(report_path) if report_path.is_file() else None,
        })),
    )
    all_passed = all_passed and cancelled_pass
    context._refresh_checkpoint_result(report, 'cli')
    return all_passed


def _record_managed_npm_cases(context: AdapterContext, report: dict[str, Any], output: Path,
                              runner_results: list[dict[str, Any]]) -> bool:
    """Record distinct install and resource assertions from the pinned npm process differential."""
    report_path = output / 'managed-npm.json'
    differential = context._load_json(report_path) if report_path.is_file() else {}
    pi = differential.get('pi', {})
    actual = differential.get('pisharp', {})
    counts = differential.get('processCounts', {})
    pi_counts = counts.get('pi', {})
    actual_counts = counts.get('pisharp', {})
    runner = next((item for item in runner_results if item.get('runnerId') == 'managed-npm'), {})
    differential_pass = (runner.get('exitCode') == 0 and differential.get('match') is True
                         and differential.get('piSha') == context.load_contract()['upstream']['revision'])
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
    artifact_hash = context._sha256_file(report_path) if report_path.is_file() else None
    for case_id, (oracle_state, actual_state, assertion, checkpoint) in cases.items():
        passed = differential_pass and assertion and oracle_state == actual_state
        context._record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else runner.get('exitCode', 1) or 1,
                      'state': actual_state},
            passed=passed,
            command_identity='tools/parity/managed-npm-process-differential.py',
            argv=runner.get('argv', []), command_exit_code=0 if passed else runner.get('exitCode', 1) or 1,
            fixture_identity=fixture_identity if checkpoint == 'npm-install' else 'local-npm-git-resource-fixtures',
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'), 'reportSha256': artifact_hash,
            })),
        )
        all_passed = all_passed and passed
    context._refresh_checkpoint_result(report, 'npm-install')
    context._refresh_checkpoint_result(report, 'resource-distribution')
    return all_passed


def _record_npm_update_cases(context: AdapterContext, report: dict[str, Any], output: Path,
                             runner_results: list[dict[str, Any]]) -> bool:
    """Independently validate the mutable-registry npm update process matrix."""
    report_path = output / 'managed-npm-update.json'
    differential = context._load_json(report_path) if report_path.is_file() else {}
    runner = next((item for item in runner_results if item.get('runnerId') == 'managed-npm-update'), {})
    pinned_pi_sha = context.load_contract()['upstream']['revision']
    source_sha = report['provenance']['pisharpSourceSha']
    runner_pass = (runner.get('exitCode') == 0 and differential.get('match') is True
                   and differential.get('piSha') == pinned_pi_sha
                   and differential.get('pisharpSha') == source_sha)
    artifact_sha = context._sha256_file(report_path) if report_path.is_file() else None

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
        context._record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if runner.get('exitCode') == 0 else runner.get('exitCode', 1),
                      'state': actual_state},
            passed=passed,
            command_identity='tools/parity/managed-npm-update-process-differential.py',
            argv=runner.get('argv', []),
            command_exit_code=runner.get('exitCode', 1),
            fixture_identity='mutable-local-npm-registry',
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'pisharpSha': differential.get('pisharpSha'), 'reportSha256': artifact_sha,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = classification if passed else 'mismatch'
        recorded['assertions'] = {case_id: bool(assertions_pass)}
        recorded['passed'] = passed
        context._write_case_artifact(report, output, recorded)
        all_passed = all_passed and passed

    context._refresh_checkpoint_result(report, 'npm-update')
    return all_passed


def _record_failure_recovery_cases(context: AdapterContext, report: dict[str, Any], output: Path,
                                   runner_results: list[dict[str, Any]],
                                   test_group_results: dict[str, dict[str, Any]]) -> bool:
    """Record failed installs, cancellation, staged replacement and restart recovery."""
    contract = context.load_contract()
    checkpoint = context._checkpoint_map(contract)['failure-recovery']
    source_sha = report['provenance']['pisharpSourceSha']
    pi_sha = contract['upstream']['revision']

    def load_evidence(path: Path) -> tuple[dict[str, Any], str | None]:
        value = context._load_json(path) if path.is_file() else {}
        digest = context._sha256_file(path) if path.is_file() else None
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
        outcomes = context._trx_test_outcomes(trx_path)
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
        context._record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else (command_exit_code or 1), 'state': actual_state},
            passed=passed, command_identity=command_identity, argv=argv,
            command_exit_code=command_exit_code, fixture_identity=fixture,
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
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
        context._write_case_artifact(report, output, recorded)
        all_passed = all_passed and bool(passed)
    context._refresh_checkpoint_result(report, 'failure-recovery')
    return all_passed


def _record_concurrency_cases(context: AdapterContext, report: dict[str, Any], output: Path,
                              runner_results: list[dict[str, Any]],
                              test_group_results: dict[str, dict[str, Any]]) -> bool:
    """Validate barrier-controlled process races, terminal integrity and the .NET regression."""
    contract = context.load_contract()
    checkpoint = context._checkpoint_map(contract)['concurrency']
    source_sha = report['provenance']['pisharpSourceSha']
    pi_sha = contract['upstream']['revision']
    report_path = output / 'managed-package-concurrency.json'
    differential = context._load_json(report_path) if report_path.is_file() else {}
    report_sha = context._sha256_file(report_path) if report_path.is_file() else None
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
    outcomes = context._trx_test_outcomes(trx_path)
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
            assertions_pass = direct_pass and context._is_same_npm_install_safety_difference(observed_case)
        elif classification == 'intentional-safety-difference' and case_id == 'concurrent.same-git-install':
            assertions_pass = direct_pass and context._is_same_git_install_safety_difference(observed_case)
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
        context._record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state if states_present else {}},
            observed={'exitCode': 0 if passed else (command_exit or 1),
                      'state': actual_state if states_present else {}},
            passed=passed, command_identity=command_identity, argv=command.get('argv', []),
            command_exit_code=command_exit, fixture_identity=fixture,
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
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
        context._write_case_artifact(report, output, recorded)
        all_passed = all_passed and passed
    context._refresh_checkpoint_result(report, 'concurrency')
    return all_passed


def _record_differential_acceptance_cases(context: AdapterContext, report: dict[str, Any], output: Path,
                                          runner_results: list[dict[str, Any]]) -> bool:
    """Require independent pinned-Pi fixture agreement and a detected negative control."""
    checkpoint = context._checkpoint_map(context.load_contract())['differential-acceptance']
    source_sha = report['provenance']['pisharpSourceSha']
    pi_sha = report['provenance']['piSha']
    report_path = output / 'managed-package-d0.json'
    differential = context._load_json(report_path) if report_path.is_file() else {}
    report_sha = context._sha256_file(report_path) if report_path.is_file() else None
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
        context._record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': state},
            observed={'exitCode': 0 if passed else (runner.get('exitCode', 1) or 1), 'state': state},
            passed=passed,
            command_identity='tools/parity/managed-package-differential-acceptance.py',
            argv=runner.get('argv', []), command_exit_code=runner.get('exitCode', 1),
            fixture_identity=fixture_identity,
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                'case': case_id, 'piSha': pi_sha, 'sourceSha': source_sha,
                'reportSha256': report_sha, 'state': state,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = 'strict' if passed else 'mismatch'
        recorded['assertions'] = {case_id: passed}
        recorded['passed'] = passed
        context._write_case_artifact(report, output, recorded)
        all_passed = all_passed and passed
    context._refresh_checkpoint_result(report, 'differential-acceptance')
    return all_passed


def _record_package_manager_cases(context: AdapterContext, report: dict[str, Any], output: Path,
                                 runner_results: list[dict[str, Any]]) -> bool:
    """Validate exact manager argv, failures and process cancellation against pinned Pi."""
    report_path = output / 'package-managers.json'
    differential = context._load_json(report_path) if report_path.is_file() else {}
    runner = next((item for item in runner_results if item.get('runnerId') == 'package-managers'), {})
    baseline_by_id = {item['caseId']: item for item in report['cases']}
    manager_tests_pass = all(
        baseline_by_id[case_id]['executed'] and baseline_by_id[case_id]['passed']
        for case_id in ('baseline.managed-npm', 'baseline.git')
    )
    runner_pass = (runner.get('exitCode') == 0 and differential.get('match') is True
                   and differential.get('piSha') == context.load_contract()['upstream']['revision']
                   and differential.get('pisharpSha') == report['provenance']['pisharpSourceSha']
                   and manager_tests_pass)
    artifact_sha = context._sha256_file(report_path) if report_path.is_file() else None
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
            context._record_baseline_case(
                report, output, case_id,
                expected={'exitCode': 0, 'state': oracle_state},
                observed={'exitCode': command_exit, 'state': actual_state}, passed=False,
                command_identity='tools/parity/package-managers-process-differential.py',
                argv=runner.get('argv', []), command_exit_code=command_exit,
                fixture_identity=fixture_family,
                input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                    'case': case_id, 'runnerExitCode': runner.get('exitCode'),
                    'reportSha256': artifact_sha,
                })),
            )
            recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
            recorded['classification'] = 'mismatch'
            recorded['assertions'] = {case_id: False}
            recorded['passed'] = False
            context._write_case_artifact(report, output, recorded)
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
                                 and context._package_manager_cancellation_safety_difference(pi_state, pisharp_state))
            strict_match = (classification == 'strict' and pi_state == pisharp_state and pisharp_safe)
            passed = (runner_pass and result.get('passed') is True
                      and result.get('fixtureFamily') == fixture_family
                      and (safety_difference or strict_match))
        if not passed:
            classification = 'mismatch'
        command_exit = runner.get('exitCode', 1)
        context._record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else (command_exit or 1), 'state': actual_state},
            passed=passed,
            command_identity='tools/parity/package-managers-process-differential.py',
            argv=runner.get('argv', []), command_exit_code=command_exit,
            fixture_identity=result.get('fixtureFamily', 'command-shims'),
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'pisharpSha': differential.get('pisharpSha'), 'reportSha256': artifact_sha,
                'baselineTests': manager_tests_pass,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = classification
        recorded['assertions'] = {case_id: bool(passed)}
        recorded['passed'] = passed
        context._write_case_artifact(report, output, recorded)
        all_passed = all_passed and passed
    context._refresh_checkpoint_result(report, 'package-managers')
    return all_passed


def _record_legacy_global_cases(context: AdapterContext, report: dict[str, Any], output: Path,
                                runner_results: list[dict[str, Any]]) -> bool:
    """Validate pinned global-package lookup, managed precedence and read-only removal."""
    report_path = output / 'legacy-global.json'
    differential = context._load_json(report_path) if report_path.is_file() else {}
    runner = next((item for item in runner_results if item.get('runnerId') == 'legacy-global'), {})
    baseline_by_id = {item['caseId']: item for item in report['cases']}
    managed_test_run = next((item for item in runner_results
                             if item.get('runnerId') == 'package-focused .NET tests'
                             and item.get('caseId') == 'baseline.managed-npm'), {})
    managed_trx = output / managed_test_run['trx'] if managed_test_run.get('trx') else Path('/missing')
    managed_test_outcomes = context._trx_test_outcomes(managed_trx)
    fallback_test_names = (
        'MissingOrMalformedLegacyLookupFallsBackToManagedAcquisition',
        'MalformedPnpmGlobalListsDoNotFallThroughToRootLookup',
    )
    fallback_tests_pass = (baseline_by_id['baseline.managed-npm']['executed']
                           and baseline_by_id['baseline.managed-npm']['passed']
                           and all(any(name.endswith('.' + required_name) and outcome == 'Passed'
                                       for name, outcome in managed_test_outcomes.items())
                                   for required_name in fallback_test_names))
    provenance_pass = (differential.get('piSha') == context.load_contract()['upstream']['revision']
                       and differential.get('pisharpSha') == report['provenance']['pisharpSourceSha']
                       and differential.get('scenario') == 'legacy-global-package-resolution')
    artifact_sha = context._sha256_file(report_path) if report_path.is_file() else None
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
        context._record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else command_exit or 1, 'state': actual_state},
            passed=passed,
            command_identity='legacy package resolution probe',
            argv=runner.get('argv', []), command_exit_code=command_exit,
            fixture_identity='disposable-global-roots',
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'pisharpSha': differential.get('pisharpSha'), 'reportSha256': artifact_sha,
                'fallbackTestsPassed': fallback_tests_pass,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = classification
        recorded['assertions'] = {case_id: passed}
        recorded['passed'] = passed
        context._write_case_artifact(report, output, recorded)
        all_passed = all_passed and passed
    context._refresh_checkpoint_result(report, 'legacy-global')
    return all_passed


def _record_git_install_cases(context: AdapterContext, report: dict[str, Any], output: Path,
                              runner_results: list[dict[str, Any]]) -> bool:
    """Validate paired Git install state, refs, failure cleanup, dependencies and cancellation."""
    report_path = output / 'managed-git-install.json'
    differential = context._load_json(report_path) if report_path.is_file() else {}
    runner = next((item for item in runner_results if item.get('runnerId') == 'managed-git-install'), {})
    git_test_run = next((item for item in runner_results
                         if item.get('runnerId') == 'package-focused .NET tests'
                         and item.get('caseId') == 'baseline.git'), {})
    git_trx = output / git_test_run['trx'] if git_test_run.get('trx') else Path('/missing')
    git_test_outcomes = context._trx_test_outcomes(git_trx)
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
        differential.get('piSha') == context.load_contract()['upstream']['revision']
        and differential.get('piSharpBaseSha') == report['provenance']['pisharpSourceSha']
        and differential.get('scenario') == 'paired-managed-git-install-local-daemon'
        and differential.get('fixtureVersion') == 'managed-git-install-v1'
    )
    artifact_sha = context._sha256_file(report_path) if report_path.is_file() else None
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
        context._record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle},
            observed={'exitCode': 0 if passed else (runner.get('exitCode', 1) or 1), 'state': actual},
            passed=passed,
            command_identity='tools/parity/managed-git-install-process-differential.py',
            argv=runner.get('argv', []), command_exit_code=0 if passed else (runner.get('exitCode', 1) or 1),
            fixture_identity='loopback-git-daemon',
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'pisharpSourceSha': differential.get('piSharpBaseSha'), 'reportSha256': artifact_sha,
                'gitStoreTestsPassed': git_tests_pass,
            })),
        )
        case = next(item for item in report['cases'] if item['caseId'] == case_id)
        case['assertions'] = {case_id: bool(provenance_pass and git_tests_pass and runner_pass and contract_pass)}
        case['passed'] = passed
        context._write_case_artifact(report, output, case)
        all_passed = all_passed and passed
    context._refresh_checkpoint_result(report, 'git-install')
    return all_passed


def _record_git_update_cases(context: AdapterContext, report: dict[str, Any], output: Path,
                             runner_results: list[dict[str, Any]]) -> bool:
    """Validate paired Git update state and the approved staged-update safety difference."""
    report_path = output / 'managed-git-update.json'
    differential = context._load_json(report_path) if report_path.is_file() else {}
    runner = next((item for item in runner_results if item.get('runnerId') == 'managed-git-update'), {})
    git_test_run = next((item for item in runner_results
                         if item.get('runnerId') == 'package-focused .NET tests'
                         and item.get('caseId') == 'baseline.git'), {})
    git_trx = output / git_test_run['trx'] if git_test_run.get('trx') else Path('/missing')
    git_test_outcomes = context._trx_test_outcomes(git_trx)
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
    contract = context.load_contract()
    provenance_pass = (differential.get('piSha') == contract['upstream']['revision']
                       and differential.get('piSharpBaseSha') == report['provenance']['pisharpSourceSha']
                       and differential.get('scenario') == 'paired-managed-git-update-local-daemon'
                       and differential.get('fixtureVersion') == 'managed-git-update-v1')
    report_sha = context._sha256_file(report_path) if report_path.is_file() else None
    if report_sha is not None:
        relative_report = report_path.relative_to(output).as_posix()
        report['artifacts'] = [item for item in report['artifacts'] if item['path'] != relative_report]
        report['artifacts'].append({'path': relative_report, 'sha256': report_sha})

    case_ids = tuple(context._checkpoint_map(contract)['git-update']['requiredCaseIds'])
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
        context._record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle},
            observed={'exitCode': 0 if passed else (runner.get('exitCode', 1) or 1), 'state': actual},
            passed=passed,
            command_identity='tools/parity/managed-git-update-process-differential.py',
            argv=runner.get('argv', []), command_exit_code=0 if runner_pass else (runner.get('exitCode', 1) or 1),
            fixture_identity='loopback-git-daemon',
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'pisharpSourceSha': differential.get('piSharpBaseSha'), 'reportSha256': report_sha,
                'gitStoreTestsPassed': git_tests_pass,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = classification
        recorded['assertions'] = {case_id: bool(assertions_pass)}
        recorded['passed'] = passed
        context._write_case_artifact(report, output, recorded)
        all_passed = all_passed and passed
    context._refresh_checkpoint_result(report, 'git-update')
    return all_passed


def _record_bulk_update_cases(context: AdapterContext, report: dict[str, Any], output: Path,
                              runner_results: list[dict[str, Any]],
                              test_group_results: dict[str, dict[str, Any]]) -> bool:
    """Validate bulk target selection, mixed scopes, partial failure and cancellation."""
    report_path = output / 'managed-package-bulk-updates.json'
    differential = context._load_json(report_path) if report_path.is_file() else {}
    runner = next((item for item in runner_results if item.get('runnerId') == 'bulk-updates'), {})
    cli_test = test_group_results.get('baseline.package-cli', {})
    cli_trx = output / cli_test['trx'] if cli_test.get('trx') else Path('/missing')
    cli_outcomes = context._trx_test_outcomes(cli_trx)
    mixed_scope_test_pass = (cli_test.get('exitCode') == 0
                             and cli_test.get('counts', {}).get('discovered', 0) > 0
                             and cli_test.get('counts', {}).get('failed') == 0
                             and cli_test.get('counts', {}).get('skipped') == 0
                             and any(name.split('(', 1)[0].endswith(
                                 '.BulkUpdateHandlesMixedUserAndTrustedProjectPackageScopes')
                                     and outcome == 'Passed'
                                     for name, outcome in cli_outcomes.items()))
    contract = context.load_contract()
    provenance_pass = (differential.get('piSha') == contract['upstream']['revision']
                       and differential.get('piSharpBaseSha') == report['provenance']['pisharpSourceSha']
                       and differential.get('scenario') == 'paired-mixed-source-bulk-update-processes'
                       and differential.get('fixtureVersion') == 'managed-package-bulk-update-v1')
    report_sha = context._sha256_file(report_path) if report_path.is_file() else None
    if report_sha is not None:
        relative_report = report_path.relative_to(output).as_posix()
        report['artifacts'] = [item for item in report['artifacts'] if item['path'] != relative_report]
        report['artifacts'].append({'path': relative_report, 'sha256': report_sha})

    case_ids = tuple(context._checkpoint_map(contract)['bulk-updates']['requiredCaseIds'])
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
        context._record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle},
            observed={'exitCode': 0 if passed else (runner.get('exitCode', 1) or 1), 'state': actual},
            passed=passed,
            command_identity='tools/parity/managed-package-bulk-update-process-differential.py',
            argv=runner.get('argv', []), command_exit_code=0 if runner_pass else (runner.get('exitCode', 1) or 1),
            fixture_identity='mixed-npm-git-fixtures',
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'pisharpSourceSha': differential.get('piSharpBaseSha'), 'reportSha256': report_sha,
                'mixedScopeTestPassed': mixed_scope_test_pass,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = classification
        recorded['assertions'] = {case_id: bool(assertions_pass)}
        recorded['passed'] = passed
        context._write_case_artifact(report, output, recorded)
        all_passed = all_passed and passed
    context._refresh_checkpoint_result(report, 'bulk-updates')
    return all_passed


def _record_configuration_cases(context: AdapterContext, report: dict[str, Any], output: Path,
                                runner_results: list[dict[str, Any]],
                                test_group_results: dict[str, dict[str, Any]]) -> bool:
    """Validate package settings, persistence, project trust and restart behavior."""
    report_path = output / 'managed-package-configuration.json'
    differential = context._load_json(report_path) if report_path.is_file() else {}
    runner = next((item for item in runner_results if item.get('runnerId') == 'configuration-trust'), {})
    settings_test = test_group_results.get('baseline.settings', {})
    settings_trx = output / settings_test['trx'] if settings_test.get('trx') else Path('/missing')
    settings_outcomes = context._trx_test_outcomes(settings_trx)
    resource_test = test_group_results.get('baseline.resource-integration', {})
    resource_trx = output / resource_test['trx'] if resource_test.get('trx') else Path('/missing')
    resource_outcomes = context._trx_test_outcomes(resource_trx)
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
    contract = context.load_contract()
    provenance_pass = (differential.get('piSha') == contract['upstream']['revision']
                       and differential.get('piSharpBaseSha') == report['provenance']['pisharpSourceSha']
                       and differential.get('scenario') == 'paired-package-settings-trust-restart-processes'
                       and differential.get('fixtureVersion') == 'managed-package-configuration-v1')
    report_sha = context._sha256_file(report_path) if report_path.is_file() else None
    if report_sha is not None:
        relative_report = report_path.relative_to(output).as_posix()
        report['artifacts'] = [item for item in report['artifacts'] if item['path'] != relative_report]
        report['artifacts'].append({'path': relative_report, 'sha256': report_sha})

    case_ids = tuple(context._checkpoint_map(contract)['configuration-trust']['requiredCaseIds'])
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
        context._record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle},
            observed={'exitCode': 0 if passed else (runner.get('exitCode', 1) or 1), 'state': actual},
            passed=passed,
            command_identity='tools/parity/managed-package-configuration-process-differential.py',
            argv=runner.get('argv', []),
            command_exit_code=0 if runner_pass else (runner.get('exitCode', 1) or 1),
            fixture_identity='isolated-settings-roots',
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'),
                'pisharpSourceSha': differential.get('piSharpBaseSha'), 'reportSha256': report_sha,
                'settingsTestsPassed': tests_pass,
            })),
        )
        recorded = next(item for item in report['cases'] if item['caseId'] == case_id)
        recorded['classification'] = classification
        recorded['assertions'] = {case_id: bool(assertions_pass)}
        recorded['passed'] = passed
        context._write_case_artifact(report, output, recorded)
        all_passed = all_passed and passed
    context._refresh_checkpoint_result(report, 'configuration-trust')
    return all_passed


def _record_startup_reload_cases(context: AdapterContext, report: dict[str, Any], output: Path,
                                 runner_results: list[dict[str, Any]],
                                 test_group_results: dict[str, dict[str, Any]]) -> bool:
    """Record paired resource reload transitions and the npm reload/registration checks."""
    path = output / 'managed-package-startup-reload.json'
    differential = context._load_json(path) if path.is_file() else {}
    pin = context.load_contract()['upstream']['revision']
    source_sha = context._runtime_state()['headSha']
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
        context._record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state},
            observed={'exitCode': 0 if passed else 1, 'state': actual_state}, passed=passed,
            command_identity='tools/parity/managed-package-startup-reload-process-differential.py',
            argv=[argument for command in argv for argument in command],
            command_exit_code=0 if passed else next((code for code in command_exits if code != 0), 1),
            fixture_identity='resource-invocation-fixtures',
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'), 'sourceSha': differential.get('pisharpSourceSha'),
                'probeSha256': differential.get('probeSourceSha256'), 'reportSha256': context._sha256_file(path)
                if path.is_file() else None, 'oracle': oracle_state, 'actual': actual_state,
            })),
        )
        all_passed = all_passed and passed

    npm_path = output / 'managed-npm.json'
    npm_diff = context._load_json(npm_path) if npm_path.is_file() else {}
    npm_runner = next((item for item in runner_results if item.get('runnerId') == 'managed-npm'), {})
    npm_test = test_group_results.get('baseline.managed-npm', {})
    npm_outcomes = context._trx_test_outcomes(output / npm_test['trx']) if npm_test.get('trx') else {}
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
    context._record_baseline_case(
        report, output, 'reload.npm',
        expected={'exitCode': 0, 'state': npm_pi_state},
        observed={'exitCode': 0 if npm_passed else 1, 'state': npm_actual_state}, passed=npm_passed,
        command_identity='tools/parity/managed-npm-process-differential.py',
        argv=npm_runner.get('argv', []), command_exit_code=0 if npm_passed else npm_runner.get('exitCode', 1) or 1,
        fixture_identity='restartable-cli-processes',
        input_fingerprint=context._sha256_bytes(context._canonical_bytes({
            'case': 'reload.npm', 'piSha': npm_diff.get('piSha'),
            'sourceSha': npm_diff.get('pisharpSha', npm_diff.get('piSharpBaseSha')),
            'reportSha256': context._sha256_file(npm_path) if npm_path.is_file() else None,
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
    context._record_baseline_case(
        report, output, 'reload.no-duplicate-registration',
        expected={'exitCode': 0, 'state': duplicate_pi_state},
        observed={'exitCode': 0 if duplicate_passed else 1, 'state': duplicate_actual_state},
        passed=duplicate_passed,
        command_identity='tools/parity/managed-npm-process-differential.py',
        argv=npm_runner.get('argv', []),
        command_exit_code=0 if duplicate_passed else npm_runner.get('exitCode', 1) or 1,
        fixture_identity='restartable-cli-processes',
        input_fingerprint=context._sha256_bytes(context._canonical_bytes({
            'case': 'reload.no-duplicate-registration', 'piSha': npm_diff.get('piSha'),
            'sourceSha': npm_diff.get('pisharpSha', npm_diff.get('piSharpBaseSha')),
            'reportSha256': context._sha256_file(npm_path) if npm_path.is_file() else None,
            'testOutcome': npm_outcomes.get(npm_test_name),
            'oracle': duplicate_pi_state, 'actual': duplicate_actual_state,
        })),
    )
    all_passed = all_passed and duplicate_passed
    context._refresh_checkpoint_result(report, 'startup-reload')
    return all_passed


def _record_identity_cases(context: AdapterContext, report: dict[str, Any], output: Path,
                           runner_results: list[dict[str, Any]]) -> bool:
    runner = next((item for item in runner_results if item.get('runnerId') == 'managed-package-identity'), {})
    report_path = output / 'managed-package-identity.json'
    differential = context._load_json(report_path) if report_path.is_file() else {}
    expected_pi_sha = context.load_contract()['upstream']['revision']
    provenance_pass = (differential.get('piSha') == expected_pi_sha
                       and differential.get('expectedPiSha') == expected_pi_sha
                       and differential.get('piShaMatches') is True)
    runner_pass = runner.get('exitCode') == 0 and differential.get('match') is True
    report_sha = context._sha256_file(report_path) if report_path.is_file() else None
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
        context._record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': state},
            observed={'exitCode': 0 if passed else (runner.get('exitCode', 1) or 1), 'state': actual_state},
            passed=passed,
            command_identity='tools/parity/managed-package-identity-process-differential.py',
            argv=runner.get('argv', []), command_exit_code=0 if passed else (runner.get('exitCode', 1) or 1),
            fixture_identity=fixture_families[case_id],
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'), 'reportSha256': report_sha,
                'piTest': differential.get('piTests', {}).get(case_id),
                'pisharpTest': differential.get('pisharpTests', {}).get(case_id),
            })),
        )
        all_passed = all_passed and passed
    context._refresh_checkpoint_result(report, 'identity')
    return all_passed


def _record_removal_listing_cases(context: AdapterContext, report: dict[str, Any], output: Path,
                                  runner_results: list[dict[str, Any]],
                                  test_group_results: dict[str, dict[str, Any]]) -> bool:
    runner = next((item for item in runner_results if item.get('runnerId') == 'package-cli'), {})
    report_path = output / 'package-cli.json'
    differential = context._load_json(report_path) if report_path.is_file() else {}
    pi_sha = context.load_contract()['upstream']['revision']
    provenance_pass = (differential.get('piSha') == pi_sha
                       and differential.get('piSharpBaseSha') == report['provenance']['pisharpSourceSha'])
    cli_tests = test_group_results.get('baseline.package-cli', {})
    tests_pass = (cli_tests.get('exitCode') == 0 and cli_tests.get('counts', {}).get('discovered', 0) > 0
                  and cli_tests.get('counts', {}).get('failed') == 0
                  and cli_tests.get('counts', {}).get('skipped') == 0)
    runner_pass = runner.get('exitCode') == 0 and differential.get('match') is True
    results = differential.get('results', {})
    report_sha = context._sha256_file(report_path) if report_path.is_file() else None
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
        context._record_baseline_case(
            report, output, case_id,
            expected={'exitCode': 0, 'state': oracle_state if oracle_state is not None else {}},
            observed={'exitCode': 0 if passed else (runner.get('exitCode', 1) or 1),
                      'state': actual_state if actual_state is not None else {}},
            passed=passed,
            command_identity='tools/parity/package-cli-process-differential.py',
            argv=runner.get('argv', []), command_exit_code=0 if passed else (runner.get('exitCode', 1) or 1),
            fixture_identity=fixture_families[case_id],
            input_fingerprint=context._sha256_bytes(context._canonical_bytes({
                'case': case_id, 'piSha': differential.get('piSha'), 'reportSha256': report_sha,
                'cliTests': cli_tests.get('trx'), 'field': field,
            })),
        )
        all_passed = all_passed and passed
    context._refresh_checkpoint_result(report, 'removal-listing')
    return all_passed
