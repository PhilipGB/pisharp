#!/usr/bin/env python3
"""Compare Pi and PiSharp managed npm install, package loading, reload, and native extension behavior."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.sax.saxutils

from pi_reference import verify_pi_checkout

ROOT = Path(__file__).resolve().parents[2]


def run(command, *, cwd, env, log):
    result = subprocess.run(command, cwd=cwd, env=env, text=True, capture_output=True, timeout=180)
    log.write_text(result.stdout + result.stderr)
    if result.returncode != 0:
        raise RuntimeError(f"Command failed ({result.returncode}): {' '.join(map(str, command))}\n{result.stderr}")
    return result.stdout


def parse_json_result(output):
    for line in reversed(output.splitlines()):
        try:
            return json.loads(line)
        except json.JSONDecodeError:
            continue
    raise ValueError('Process output did not contain its final JSON result.')


def package_fixture(pi_root, work, env, logs):
    tarballs = work / 'tarballs'
    dependency = work / 'package-input' / 'dependency'
    main = work / 'package-input' / 'managed-parity'
    tarballs.mkdir(parents=True)
    dependency.mkdir(parents=True)
    (dependency / 'package.json').write_text(json.dumps({
        'name': 'managed-parity-dependency', 'version': '1.0.0',
        'scripts': {'postinstall': 'node postinstall.cjs'},
    }))
    (dependency / 'postinstall.cjs').write_text(
        "const fs = require('node:fs');\n" +
        "fs.appendFileSync(`${process.env.PISHARP_MANAGED_PARITY_MARKER_DIR}/dependency.log`, 'run\\n');\n")
    run(['npm', 'pack', '--ignore-scripts', '--pack-destination', str(tarballs)],
        cwd=dependency, env=env, log=logs / 'dependency-pack.log')
    dependency_tarball = tarballs / 'managed-parity-dependency-1.0.0.tgz'
    if not dependency_tarball.is_file():
        raise RuntimeError('npm pack did not create the dependency tarball')

    main.mkdir(parents=True)
    main_manifest = {
        'name': 'managed-parity', 'version': '1.0.0', 'type': 'module',
        'pi': {
            'skills': ['skills'], 'prompts': ['prompts'], 'themes': ['themes'],
            'extensions': ['extensions'],
        },
        'dependencies': {'managed-parity-dependency': f'file:{dependency_tarball}'},
        'peerDependencies': {'pisharp-host-api-must-not-install': '^1.0.0'},
        'scripts': {'postinstall': 'node postinstall.cjs'},
    }
    (main / 'package.json').write_text(json.dumps(main_manifest))
    (main / 'postinstall.cjs').write_text(
        "const fs = require('node:fs');\n" +
        "fs.appendFileSync(`${process.env.PISHARP_MANAGED_PARITY_MARKER_DIR}/package.log`, 'run\\n');\n")
    skills = main / 'skills' / 'guide'
    prompts = main / 'prompts'
    themes = main / 'themes'
    extensions = main / 'extensions'
    for directory in [skills, prompts, themes, extensions]:
        directory.mkdir(parents=True, exist_ok=True)
    (skills / 'SKILL.md').write_text(
        '---\nname: managed-parity-skill\ndescription: Installed parity skill\n---\nUse the installed skill.\n')
    (prompts / 'review.md').write_text('Review the managed npm package.\n')
    dark_theme = json.loads((pi_root / 'packages/coding-agent/src/modes/interactive/theme/dark.json').read_text())
    dark_theme['name'] = 'managed-parity-theme'
    (themes / 'managed.json').write_text(json.dumps(dark_theme))
    (extensions / 'index.js').write_text(
        "import fs from 'node:fs';\n" +
        "export default function () {\n" +
        "  fs.appendFileSync(process.env.PISHARP_MANAGED_PARITY_EXTENSION_LOG, 'configured\\n');\n" +
        "}\n")
    csharp_extension = ROOT / 'tests/ResourceDiscoveryExtension/bin/Debug/net10.0/dynamic-resources.dll'
    if not csharp_extension.is_file():
        raise RuntimeError('ResourceDiscoveryExtension output missing after probe build')
    shutil.copy2(csharp_extension, extensions / 'index.dll')
    (main / 'package.json').write_text(json.dumps(main_manifest))
    run(['npm', 'pack', '--ignore-scripts', '--pack-destination', str(tarballs)],
        cwd=main, env=env, log=logs / 'package-pack.log')
    package_tarball = tarballs / 'managed-parity-1.0.0.tgz'
    if not package_tarball.is_file():
        raise RuntimeError('npm pack did not create the managed package tarball')
    return package_tarball


def summarize_resource_entries(entries):
    return [{**{key: entry[key] for key in ('path', 'enabled', 'source', 'scope', 'origin', 'baseDir')},
             'baseDir': '' if entry['baseDir'] == '.' else entry['baseDir']}
            for entry in entries]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    pi_sha = verify_pi_checkout(args.pi)
    profile = args.profile.resolve()
    directories = {
        'home': profile / 'home', 'tmp': profile / 'tmp', 'config': profile / 'config',
        'cache': profile / 'cache', 'agent': profile / 'agent', 'sessions': profile / 'sessions',
        'nuget': profile / 'nuget', 'nuget_http': profile / 'nuget-http',
        'dotnet': profile / 'dotnet', 'npm_cache': profile / 'cache/npm',
    }
    for directory in directories.values():
        directory.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ)
    env.update({
        'HOME': str(directories['home']), 'TMPDIR': str(directories['tmp']),
        'XDG_CONFIG_HOME': str(directories['config']), 'XDG_CACHE_HOME': str(directories['cache']),
        'PISHARP_AGENT_DIR': str(directories['agent']), 'PISHARP_SESSION_DIR': str(directories['sessions']),
        'NUGET_PACKAGES': str(directories['nuget']), 'NUGET_HTTP_CACHE_PATH': str(directories['nuget_http']),
        'DOTNET_CLI_HOME': str(directories['dotnet']), 'npm_config_cache': str(directories['npm_cache']),
        'DOTNET_PROCESSOR_COUNT': '2', 'MSBUILDDISABLENODEREUSE': '1',
        'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
        'npm_config_offline': 'true', 'npm_config_update_notifier': 'false',
    })
    env.pop('NPM_CONFIG_UPDATE_NOTIFIER', None)
    env.pop('PI_OFFLINE', None)
    args.output.parent.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix='managed-npm-', dir=directories['tmp']) as temporary:
        work = Path(temporary)
        logs = work / 'logs'
        logs.mkdir()
        probe = work / 'pisharp-probe'
        probe.mkdir()
        (probe / 'Program.cs').write_text((ROOT / 'tools/parity/managed-npm-probe.cs').read_text())
        references = [ROOT / 'src/PiSharp.Runtime/PiSharp.Runtime.csproj',
                      ROOT / 'tests/ResourceDiscoveryExtension/ResourceDiscoveryExtension.csproj']
        project_refs = ''.join(
            f'<ProjectReference Include="{xml.sax.saxutils.escape(str(reference))}" />'
            for reference in references)
        (probe / 'probe.csproj').write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
            '<OutputType>Exe</OutputType><AssemblyName>ManagedNpmProbe</AssemblyName>'
            '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>'
            + project_refs + '</ItemGroup></Project>')
        build_command = ['dotnet', 'build', str(probe / 'probe.csproj'), '--warnaserror',
                         '--disable-build-servers', '-m:1', '-p:UseSharedCompilation=false']
        run(build_command, cwd=ROOT, env=env, log=logs / 'dotnet-build.log')
        package_tarball = package_fixture(args.pi.resolve(), work, env, logs)
        source = f'npm:managed-parity@file:{package_tarball}'
        package_source_file = work / 'package-source.txt'
        package_source_file.write_text(source)

        pi_root = work / 'pi'
        pi_markers = work / 'pi-markers'
        pi_root.mkdir()
        pi_markers.mkdir()
        pi_env = dict(env, PISHARP_MANAGED_PARITY_MARKER_DIR=str(pi_markers),
                      PISHARP_MANAGED_PARITY_EXTENSION_LOG=str(pi_markers / 'extension.log'))
        pi_stdout = run(['node', '--experimental-transform-types',
                         str(ROOT / 'tools/parity/managed-npm-probe.mjs'),
                         str(args.pi.resolve()), str(pi_root), source], cwd=ROOT, env=pi_env,
                        log=logs / 'pi-process.log')

        pisharp_root = work / 'pisharp'
        pisharp_agent = pisharp_root / 'agent'
        pisharp_cwd = pisharp_root / 'project'
        pisharp_markers = work / 'pisharp-markers'
        pisharp_root.mkdir()
        pisharp_markers.mkdir()
        pisharp_env = dict(env, PISHARP_MANAGED_PARITY_MARKER_DIR=str(pisharp_markers),
                           PISHARP_MANAGED_PARITY_EXTENSION_LOG=str(pisharp_markers / 'extension.log'),
                           PISHARP_TEST_EXTENSION_LOAD_LOG=str(pisharp_markers / 'extension.log'),
                           PISHARP_TEST_RESOURCE_DISCOVERY='true')
        probe_dll = probe / 'bin/Debug/net10.0/ManagedNpmProbe.dll'
        pisharp_stdout = run(['dotnet', str(probe_dll), str(pisharp_root), str(pisharp_agent),
                              str(pisharp_cwd), source], cwd=ROOT, env=pisharp_env,
                             log=logs / 'pisharp-process.log')

        pi_result = parse_json_result(pi_stdout)
        pisharp_result = parse_json_result(pisharp_stdout)
        pi_manifest = json.loads((pi_root / 'agent/npm/node_modules/managed-parity/package.json').read_text())
        pisharp_manifest = json.loads((pisharp_agent / 'npm/node_modules/managed-parity/package.json').read_text())
        pi_result['installedPackage'] = {'name': pi_manifest.get('name'), 'version': pi_manifest.get('version')}
        pisharp_result['installedPackage'] = {
            'name': pisharp_manifest.get('name'), 'version': pisharp_manifest.get('version')}
        pi_counts = {
            'packageInstallScriptRuns': len((pi_markers / 'package.log').read_text().splitlines()),
            'dependencyInstallScriptRuns': len((pi_markers / 'dependency.log').read_text().splitlines()),
            'extensionLoads': len((pi_markers / 'extension.log').read_text().splitlines()),
        }
        pisharp_counts = {
            'packageInstallScriptRuns': len((pisharp_markers / 'package.log').read_text().splitlines()),
            'dependencyInstallScriptRuns': len((pisharp_markers / 'dependency.log').read_text().splitlines()),
            'extensionLoads': len((pisharp_markers / 'extension.log').read_text().splitlines()),
        }

        pi_resolved = pi_result['resolved']['startup']
        pisharp_pass = pisharp_result['resourcePasses'][0]
        pisharp_package_skills = summarize_resource_entries(
            [entry for entry in pisharp_pass['skills'] if entry['source'] == source])
        pisharp_package_prompts = summarize_resource_entries(
            [entry for entry in pisharp_pass['prompts'] if entry['source'] == source])
        pisharp_extensions = [{
            'path': entry['path'].removesuffix('.dll') + '.<module>', 'enabled': True,
            'source': entry['source'], 'scope': entry['scope'], 'origin': entry['origin'],
            'baseDir': '' if entry['baseDir'] == '.' else entry['baseDir'],
        } for entry in pisharp_result['extensionPasses'][0]]
        pi_extensions = [{**entry, 'path': entry['path'].removesuffix('.js') + '.<module>'}
                         for entry in pi_resolved['extensions']]
        comparisons = {
            'installedPackage': pi_result['installedPackage'] == pisharp_result['installedPackage']
                == {'name': 'managed-parity', 'version': '1.0.0'},
            'skills': summarize_resource_entries(pi_resolved['skills']) == pisharp_package_skills,
            'prompts': summarize_resource_entries(pi_resolved['prompts']) == pisharp_package_prompts,
            'extensions': pi_extensions == pisharp_extensions,
            'themes': [entry['path'] for entry in pi_resolved['themes']] ==
                      [pisharp_result['theme']['path']],
        }
        checks = {
            'piReloadResolutionStable': pi_result['resolved']['startup'] == pi_result['resolved']['reloaded'],
            'piSharpReloadResourcesStable': [
                [skill['name'] for skill in pass_['skills'] if skill['source'] == source]
                for pass_ in pisharp_result['resourcePasses']
            ] == [['managed-parity-skill'], ['managed-parity-skill']],
            'piPackageAndDependencyScriptsRunOnce': pi_counts['packageInstallScriptRuns'] == 1 and
                pi_counts['dependencyInstallScriptRuns'] == 1,
            'piSharpPackageAndDependencyScriptsRunOnce': pisharp_counts['packageInstallScriptRuns'] == 1 and
                pisharp_counts['dependencyInstallScriptRuns'] == 1,
            'piAndPiSharpDoNotInstallPeer': not pi_result['peerInstalled'] and not pisharp_result['peerInstalled'],
            'piAndPiSharpConfigureExtensionOnStartupAndReload': pi_counts['extensionLoads'] == 2 and
                pisharp_counts['extensionLoads'] == 2,
            'piSharpExtensionResourcesReload': [
                [skill['name'] for skill in pass_['skills'] if skill['name'].startswith('managed-') and
                 skill['source'] != source]
                for pass_ in pisharp_result['resourcePasses']
            ] == [['managed-startup-extension-skill'], ['managed-reloaded-extension-skill']],
        }
        evidence = {
            'piSha': pi_sha,
            'pisharpSha': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
            'fixture': {'package': 'managed-parity@1.0.0', 'installSpec': 'npm:<name>@file:<local-tarball>',
                        'peerPolicy': 'npm --legacy-peer-deps', 'lifecycleScripts': 'enabled'},
            'pi': pi_result,
            'pisharp': pisharp_result,
            'processCounts': {'pi': pi_counts, 'pisharp': pisharp_counts},
            'comparisons': comparisons,
            'checks': checks,
            'match': all(comparisons.values()) and all(checks.values()),
            'limitations': [
                'The comparison uses one local named npm tarball and does not exercise registry access.',
                'Pi loads a JavaScript extension entry while PiSharp loads the same package’s native C# DLL entry.',
                'Cancellation is verified separately by ManagedResourcePackageStoreTests.',
            ],
        }
        artifacts = args.output.parent
        (artifacts / 'managed-npm-pi.json').write_text(json.dumps(pi_result, indent=2) + '\n')
        (artifacts / 'managed-npm-pisharp.json').write_text(json.dumps(pisharp_result, indent=2) + '\n')
        (artifacts / 'managed-npm-process-counts.json').write_text(json.dumps(evidence['processCounts'], indent=2) + '\n')
        shutil.copy2(logs / 'dotnet-build.log', artifacts / 'managed-npm-dotnet-build.log')
        shutil.copy2(logs / 'pi-process.log', artifacts / 'managed-npm-pi-process.log')
        shutil.copy2(logs / 'pisharp-process.log', artifacts / 'managed-npm-pisharp-process.log')
        args.output.write_text(json.dumps(evidence, indent=2) + '\n')
        if not evidence['match']:
            raise AssertionError(json.dumps({'comparisons': comparisons, 'checks': checks}, indent=2))
        print(f"Matched local npm install and reload behavior. Raw evidence: {args.output}")


if __name__ == '__main__':
    main()
