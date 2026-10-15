#!/usr/bin/env python3
"""Compare normal RPC resource loading, expansion, recovery and provider input."""
import argparse
import hashlib
from http.server import ThreadingHTTPServer
import importlib.util
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import tempfile
import threading

from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit, thread_limit_environment

apply_test_thread_limit()
ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('rpc_settlement', Path(__file__).with_name('rpc-settlement-differential.py'))
shared = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shared)


class RpcEndedBeforeCheckpoint(Exception):
    pass


def create_managed_npm_tarballs(root, pi, environment):
    pack_root = root / 'managed-npm-input'
    tarballs = pack_root / 'tarballs'
    dependency = pack_root / 'dependency'
    skill_package = pack_root / 'managed-skill'
    extension_package = pack_root / 'managed-native-extension'
    for directory in [tarballs, dependency, skill_package, extension_package]:
        directory.mkdir(parents=True, exist_ok=True)
    (dependency / 'package.json').write_text(json.dumps(dict(name='managed-extension-dependency', version='1.0.0',
        scripts=dict(postinstall='node postinstall.cjs'))))
    (dependency / 'postinstall.cjs').write_text(
        "require('node:fs').appendFileSync(process.env.PISHARP_MANAGED_NPM_MARKER_DIR + '/dependency.log', 'run\\n');\n")
    subprocess.run(['npm', 'pack', '--ignore-scripts', '--pack-destination', str(tarballs)], cwd=dependency,
                   env=environment, check=True, capture_output=True, text=True, timeout=30)
    dependency_tarball = tarballs / 'managed-extension-dependency-1.0.0.tgz'
    (skill_package / 'package.json').write_text(json.dumps(dict(name='managed-skill', version='1.0.0', type='module',
        pi=dict(skills=['skills'], prompts=['prompts'], themes=['themes']),
        scripts=dict(postinstall='node postinstall.cjs'))))
    (skill_package / 'postinstall.cjs').write_text(
        "require('node:fs').appendFileSync(process.env.PISHARP_MANAGED_NPM_MARKER_DIR + '/skill-package.log', 'run\\n');\n")
    (skill_package / 'skills/guide').mkdir(parents=True)
    (skill_package / 'prompts').mkdir()
    (skill_package / 'themes').mkdir()
    (skill_package / 'skills/guide/SKILL.md').write_text(
        '---\nname: guide\ndescription: Installed managed skill\n---\nInstalled managed skill body')
    (skill_package / 'prompts/review.md').write_text('---\ndescription: Installed managed prompt\n---\nReview $1')
    dark_theme = json.loads((pi / 'packages/coding-agent/src/modes/interactive/theme/dark.json').read_text())
    dark_theme['name'] = 'managed-npm-theme'
    (skill_package / 'themes/managed.json').write_text(json.dumps(dark_theme))
    subprocess.run(['npm', 'pack', '--ignore-scripts', '--pack-destination', str(tarballs)], cwd=skill_package,
                   env=environment, check=True, capture_output=True, text=True, timeout=30)
    skill_tarball = tarballs / 'managed-skill-1.0.0.tgz'

    (extension_package / 'package.json').write_text(json.dumps(dict(name='managed-native-extension', version='1.0.0',
        type='module', pi=dict(extensions=['extensions']),
        dependencies={'managed-extension-dependency': 'file:' + str(dependency_tarball)},
        peerDependencies={'pisharp-host-api-must-not-install': '^1.0.0'},
        scripts=dict(postinstall='node postinstall.cjs'))))
    (extension_package / 'postinstall.cjs').write_text(
        "require('node:fs').appendFileSync(process.env.PISHARP_MANAGED_NPM_MARKER_DIR + '/extension-package.log', 'run\\n');\n")
    (extension_package / 'extensions').mkdir()
    (extension_package / 'extensions/index.ts').write_text("""import {appendFileSync, readFileSync} from 'node:fs';
import {join} from 'node:path';
import {pathToFileURL} from 'node:url';
export default function(pi) {
 const trace = value => appendFileSync(process.env.PISHARP_TEST_EXTENSION_LOAD_LOG, value+'\\n');
 trace('configured');
 pi.on('project_trust', () => ({trusted:'undecided',remember:true}));
 pi.on('project_trust', ({cwd}) => {
  try { const state=JSON.parse(readFileSync(join(cwd,'.pi','project-trust-extension.json'),'utf8'));
   return {trusted:state.decision,remember:state.remember}; } catch { return null; }
 });
 pi.on('resources_discover', ({cwd,reason}) => {
  const phase=reason==='reload'?'reloaded':'startup';
  const base=join(cwd,'.pi','extension-resources',phase,'skills');
  return {skillPaths:[pathToFileURL(base).href]};
 });
}
""")
    native_extension = ROOT / 'tests/ResourceDiscoveryExtension/bin/Debug/net10.0/dynamic-resources.dll'
    if not native_extension.is_file():
        raise RuntimeError('ResourceDiscoveryExtension output is missing; build the focused test project first')
    shutil.copyfile(native_extension, extension_package / 'extensions/index.dll')
    subprocess.run(['npm', 'pack', '--ignore-scripts', '--pack-destination', str(tarballs)], cwd=extension_package,
                   env=environment, check=True, capture_output=True, text=True, timeout=30)
    extension_tarball = tarballs / 'managed-native-extension-1.0.0.tgz'
    return [f'npm:managed-skill@file:{skill_tarball}',
            f'npm:managed-native-extension@file:{extension_tarball}']


def replay(product, pi, native, root, output, scenario, extension_mode='manifest', managed_sources=None,
           profile=None):
    package_scenario = scenario in ['local-package', 'padded-local-package', 'file-url-package', 'package-extension',
                                    'managed-npm-package']
    agent = root / product / 'agent'
    home = (profile / 'home') if profile else root / product / 'home'
    skill = root / product / ('package/skills/guide/SKILL.md' if package_scenario else 'guide/SKILL.md')
    if scenario == 'managed-npm-package':
        skill = agent / 'npm/node_modules/managed-skill/skills/guide/SKILL.md'
    if scenario == 'package-extension':
        skill = root / product / '.pi/extension-resources/startup/skills/guide/SKILL.md'
    agent.mkdir(parents=True)
    home.mkdir(parents=True, exist_ok=True)
    if scenario != 'managed-npm-package':
        skill.parent.mkdir(parents=True)
        description = '!!int word' if scenario == 'invalid-tag' else 'Guide'
        skill.write_text(f'---\nname: guide\ndescription: {description}\n---\nbody')
    if package_scenario:
        if scenario == 'managed-npm-package':
            (agent / 'settings.json').write_text(json.dumps(dict(packages=managed_sources)))
            project = root / product / '.pi'
            (project / 'extension-resources/startup/skills/managed-startup').mkdir(parents=True)
            (project / 'extension-resources/reloaded/skills/managed-reloaded').mkdir(parents=True)
            for name in ['managed-startup', 'managed-reloaded']:
                phase = 'reloaded' if name == 'managed-reloaded' else 'startup'
                (project / f'extension-resources/{phase}/skills/{name}/SKILL.md').write_text(
                    f'---\nname: {name}\ndescription: {name} extension resource\n---\n{name} body')
            (project / 'extension-resources/startup/prompts').mkdir(parents=True)
            (project / 'extension-resources/reloaded/prompts').mkdir(parents=True)
            (project / 'extension-resources/startup/themes').mkdir(parents=True)
            (project / 'extension-resources/reloaded/themes').mkdir(parents=True)
            (project / 'settings.json').write_text('{}')
            (project / 'project-trust-extension.json').write_text('{"decision":"yes","remember":true}')
        else:
            package = root / product / 'package'
            (package / 'prompts').mkdir(parents=True)
            (package / 'prompts/review.md').write_text('---\ndescription: Package review\n---\nReview $1')
            (package / 'package.json').write_text(json.dumps(dict(pi=dict(skills=['skills'], prompts=['prompts/review.md']))))
            source = '\uFEFF \t../package\r\n\uFEFF' if scenario == 'padded-local-package' else '../package'
            if scenario == 'file-url-package':
                source = package.as_uri() + '?ignored#fragment'
            (agent / 'settings.json').write_text(json.dumps(dict(packages=[source])))
        if scenario == 'package-extension':
            (root / product / '.pi/settings.json').write_text('{}')
            (root / product / '.pi/project-trust-extension.json').write_text('{"decision":"yes","remember":true}')
            extension = package / 'extensions'
            extension.mkdir()
            if product == 'pi':
                (extension / 'index.ts').write_text("""import {appendFileSync} from 'node:fs';
import {join} from 'node:path';
import {pathToFileURL} from 'node:url';
export default function(pi) {
 const trace = value => appendFileSync(process.env.PISHARP_TEST_EXTENSION_LOAD_LOG, value+'\\n');
 trace('configured');
 pi.on('project_trust', (_event,ctx) => {trace(`trust-undecided:${ctx.hasUI}`);return {trusted:'undecided'};});
 pi.on('project_trust', (_event,ctx) => {trace(`trust-decision:${ctx.hasUI}`);return {trusted:'yes',remember:true};});
 pi.on('resources_discover', ({cwd}) => {
  const base=join(cwd,'.pi','extension-resources','startup');
  return {skillPaths:[pathToFileURL(join(base,'skills')).href],promptPaths:[join(base,'prompts')],themePaths:[join(base,'themes')]};
 });
}
""")
            else:
                shutil.copyfile(ROOT / 'tests/ResourceDiscoveryExtension/bin/Debug/net10.0/dynamic-resources.dll', extension / 'index.dll')
            (package / 'package.json').write_text(json.dumps(dict(pi=dict(extensions=['extensions']))))
            if extension_mode == 'index-with-sibling':
                if product == 'pi':
                    (extension / 'sibling.ts').write_text("export default function(){throw new Error('A sibling beside index must not load');}\n")
                else:
                    shutil.copyfile(extension / 'index.dll', extension / 'sibling.dll')
            if extension_mode in ['nested-manifest', 'ignored-sibling', 'hidden-sibling', 'node-modules-sibling']:
                suffix = 'ts' if product == 'pi' else 'dll'
                entry = extension / f'guide.{suffix}'
                (extension / f'index.{suffix}').rename(entry)
                sibling = extension / ('.hidden' if extension_mode == 'hidden-sibling' else 'blocked')
                sibling = sibling.with_suffix('.' + suffix)
                if extension_mode == 'node-modules-sibling':
                    sibling = extension / 'node_modules' / f'index.{suffix}'
                    sibling.parent.mkdir()
                if product == 'pi':
                    sibling.write_text("export default function(){throw new Error('Unselected extension must not load');}\n")
                else:
                    shutil.copyfile(entry, sibling)
                if extension_mode == 'nested-manifest':
                    (extension / 'package.json').write_text(json.dumps(dict(pi=dict(extensions=[f'guide.{suffix}']))))
                if extension_mode == 'ignored-sibling':
                    (extension / '.ignore').write_text('blocked.*\n')
            if extension_mode == 'throwing-sibling':
                suffix = 'ts' if product == 'pi' else 'dll'
                entry = extension / f'guide.{suffix}'
                (extension / f'index.{suffix}').rename(entry)
                if product == 'pi':
                    (extension / 'bad.ts').write_text("""import {appendFileSync} from 'node:fs';
export default function(pi) {
 appendFileSync(process.env.PISHARP_TEST_EXTENSION_LOAD_LOG,'configured\\n');
 pi.registerCommand('partial_command',{description:'Uncommitted extension command',handler:async()=>{}});
 throw new Error('fixture extension configuration failure');
}
""")
                else:
                    shutil.copyfile(entry, extension / 'bad.dll')
            if extension_mode == 'conventions':
                (package / 'package.json').unlink()
            if extension_mode in ['project-disabled', 'project-enabled']:
                (root / product / '.pi/settings.json').write_text(json.dumps(dict(packages=[
                    dict(source='../package', extensions=[]) if extension_mode == 'project-disabled' else '../package'])))
            if extension_mode == 'disabled':
                (agent / 'settings.json').write_text(json.dumps(dict(packages=[dict(source='../package', extensions=[])])))
            if extension_mode == 'file':
                suffix = 'ts' if product == 'pi' else 'dll'
                (agent / 'settings.json').write_text(json.dumps(dict(packages=[f'../package/extensions/index.{suffix}'])))
            if extension_mode == 'root':
                suffix = 'ts' if product == 'pi' else 'dll'
                (extension / f'index.{suffix}').rename(package / f'index.{suffix}')
                extension.rmdir()
                (package / 'prompts/review.md').unlink()
                (package / 'prompts').rmdir()
                (package / 'package.json').unlink()
    if scenario == 'malformed-flow':
        skill.write_text('---\nname: guide\nunused: [bad\ndescription: *missing\n---\nbody')
    server = ThreadingHTTPServer(('127.0.0.1', 0), shared.Provider)
    server.requests = []
    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    provider = dict(baseUrl=f'http://127.0.0.1:{server.server_port}/v1',
                    models=[dict(id='fixture-model', name='fixture-model', api='openai-completions',
                        reasoning=False, input=['text'], contextWindow=4096, maxTokens=128,
                        cost=dict(input=0, output=0, cacheRead=0, cacheWrite=0))])
    provider.update(dict(apiKey='fixture-only-key') if product == 'pi' else dict(apiKeyEnv='PISHARP_FIXTURE_KEY'))
    (agent / 'models.json').write_text(json.dumps(dict(providers=dict(fixture=provider))))
    env = {key: os.environ[key] for key in ['PATH', 'DOTNET_ROOT', 'LD_LIBRARY_PATH'] if key in os.environ}
    env.update(HOME=str(home), PI_CODING_AGENT_DIR=str(agent), PISHARP_AGENT_DIR=str(agent),
               PISHARP_FIXTURE_KEY='fixture-only-key', TERM='xterm-256color',
               DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1', **thread_limit_environment())
    if profile:
        env.update(TMPDIR=str(profile / 'tmp'), XDG_CONFIG_HOME=str(profile / 'config'),
                   XDG_CACHE_HOME=str(profile / 'cache'), DOTNET_CLI_HOME=str(profile / 'dotnet'),
                   NUGET_PACKAGES=str(profile / 'nuget'), NUGET_HTTP_CACHE_PATH=str(profile / 'nuget-http'),
                   PISHARP_SESSION_DIR=str(profile / 'sessions'), npm_config_cache=str(profile / 'cache/npm'))
    if scenario == 'managed-npm-package':
        env.update(npm_config_cache=str(profile / 'cache/npm'), npm_config_offline='true',
                   PISHARP_AGENT_DIR=str(agent), PISHARP_SESSION_DIR=str(profile / 'sessions'),
                   PISHARP_MANAGED_NPM_MARKER_DIR=str(root / product / 'install-markers'),
                   PISHARP_TEST_RESOURCE_DISCOVERY='true',
                   PISHARP_TEST_EXTENSION_LOAD_LOG=str(root / product / 'extension-load.log'))
        (root / product / 'install-markers').mkdir(parents=True)
    else:
        env['PI_OFFLINE'] = '1'
    if scenario in ['package-extension', 'managed-npm-package']:
        env.update(PISHARP_TEST_RESOURCE_DISCOVERY='true',
                   PISHARP_TEST_EXTENSION_LOAD_LOG=str(root / product / 'extension-load.log'))
        if extension_mode == 'throwing-sibling':
            env['PISHARP_TEST_EXTENSION_PARTIAL_FAILURE'] = 'true'
    common = ['--mode', 'rpc', '--provider', 'fixture', '--model', 'fixture-model', '--no-session',
              '--no-extensions', '--no-skills', '--no-prompt-templates', '--no-tools', '--skill', str(skill)]
    if scenario != 'managed-npm-package':
        common.insert(6, '--offline')
    if package_scenario:
        common = common[:-2]
        common.remove('--no-skills')
        common.remove('--no-prompt-templates')
    if scenario in ['package-extension', 'managed-npm-package'] and extension_mode != 'no-extensions':
        common.remove('--no-extensions')
    if scenario == 'package-extension' and extension_mode == 'missing-explicit':
        suffix = 'ts' if product == 'pi' else 'dll'
        common.extend(['--extension', str(root / product / f'missing.{suffix}')])
    if scenario == 'package-extension' and extension_mode in ['padded-explicit', 'file-url-explicit', 'canonical-explicit-alias']:
        suffix = 'ts' if product == 'pi' else 'dll'
        selected = root / product / 'package/extensions' / f'index.{suffix}'
        if extension_mode == 'canonical-explicit-alias':
            alias = root / product / f'alias.{suffix}'
            alias.symlink_to(selected)
            value = str(alias)
        elif extension_mode == 'file-url-explicit':
            value = selected.as_uri()
        else:
            value = '\uFEFF \t' + str(selected) + '\r\n\uFEFF'
        common.extend(['--extension', value])
    command = (['node', '--import', (pi / 'packages/coding-agent/src/experimental/source-resolver.ts').as_uri(),
                str(pi / 'packages/coding-agent/src/cli.ts')] if product == 'pi' else ['dotnet', str(native)]) + common
    records = []
    incoming = queue.Queue()
    process = None
    try:
        with output.with_name(output.stem + f'-{product}.stderr.log').open('w') as error:
            process = subprocess.Popen(command, cwd=root / product, env=env, stdin=subprocess.PIPE,
                stdout=subprocess.PIPE, stderr=error, text=True)

            def read_lines():
                try:
                    for line in process.stdout:
                        incoming.put(json.loads(line))
                except Exception as failure:
                    incoming.put(failure)
                finally:
                    incoming.put(None)

            reader = threading.Thread(target=read_lines, daemon=True)
            reader.start()

            def send(record):
                process.stdin.write(json.dumps(record) + '\n')
                process.stdin.flush()

            def until(predicate):
                while True:
                    record = incoming.get(timeout=30)
                    if isinstance(record, Exception):
                        raise record
                    if record is None:
                        raise RpcEndedBeforeCheckpoint('RPC ended before the checkpoint')
                    records.append(record)
                    if predicate(record):
                        return record

            send(dict(id='commands', type='get_commands'))
            commands = until(lambda r: r.get('id') == 'commands')
            command_info = next((c for c in commands['data']['commands'] if c['name'] == 'skill:guide'), None)
            startup_skill = next((c for c in commands['data']['commands']
                                  if c['name'] == 'skill:managed-startup'), None)
            review_command = next((c for c in commands['data']['commands'] if c['name'] == 'review'), None)
            if scenario == 'managed-npm-package':
                assert command_info and command_info['description'] == 'Installed managed skill', commands
                assert startup_skill and startup_skill['description'] == 'managed-startup extension resource', commands
                assert review_command and review_command['description'] == 'Installed managed prompt', commands
            elif scenario == 'malformed-flow':
                assert command_info is None, commands
            elif scenario == 'package-extension' and extension_mode in ['disabled', 'no-extensions']:
                assert command_info is None, commands
            elif scenario == 'package-extension' and extension_mode == 'project-disabled':
                if product == 'pi':
                    assert command_info is None, commands
            elif not package_scenario or product == 'pi':
                assert command_info and command_info['description'] == ('word' if scenario == 'invalid-tag' else 'Guide')
            if scenario == 'deleted-skill':
                skill.unlink()
            prompts = [('skill', '/skill:guide args'), ('template', '/review topic')] if scenario == 'managed-npm-package' \
                else [('prompt', '/skill:guide args')]
            response_records = []
            for prompt_id, prompt_text in prompts:
                send(dict(id=prompt_id, type='prompt', message=prompt_text))
                until(lambda r: r.get('type') == 'agent_settled')
                response = next(r for r in records if r.get('id') == prompt_id)
                assert response['success'], records
                response_records.append(response)
            response_index = records.index(response_records[0])
            failure = None
            if scenario == 'deleted-skill':
                error_index = next(i for i, r in enumerate(records) if r.get('type') == 'extension_error')
                assert error_index < response_index, records
                failure = records[error_index]
                assert failure['event'] == 'skill_expansion' and failure['extensionPath'] == str(skill), failure
            else:
                assert not any(r.get('type') == 'extension_error' for r in records), records
            expected_requests = 2 if scenario == 'managed-npm-package' else 1
            assert len(server.requests) == expected_requests
            provider_inputs = []
            for request in server.requests:
                users = [m['content'] for m in request['messages'] if m['role'] == 'user']
                content = users[-1]
                provider_inputs.append(content if isinstance(content, str) else ''.join(
                    p.get('text', '') for p in content if p.get('type') == 'text'))
            if scenario == 'managed-npm-package':
                assert 'Installed managed skill body' in provider_inputs[0] and provider_inputs[0].endswith('args'), provider_inputs
                assert 'Review topic' in provider_inputs[1], provider_inputs
            else:
                text = provider_inputs[0]
                expected = '/skill:guide args' if failure or scenario == 'malformed-flow' or command_info is None else (
                    f'<skill name="guide" location="{skill}">\nReferences are relative to {skill.parent}.\n\nbody\n</skill>\n\nargs')
                assert text == expected, text
            process.stdin.close()
            assert process.wait(timeout=10) == 0
            reader.join(timeout=5)
            result = dict(errorBeforeResponse=True if failure else None, event=failure['event'] if failure else None,
                        error=failure['error'].replace(str(root / product), '<workspace>') if failure else None,
                        providerInput=provider_inputs[0].replace(str(root / product), '<workspace>'),
                        providerRequests=expected_requests,
                        promptAccepted=True, commandDescription=command_info['description'] if command_info else None)
            if scenario == 'managed-npm-package':
                result['templateProviderInput'] = provider_inputs[1].replace(str(root / product), '<workspace>')
                result['extensionResourceDescription'] = startup_skill['description']
                result['extensionConfiguredCount'] = sum(line == 'configured' for line in
                    (root / product / 'extension-load.log').read_text().splitlines())
                result['installScripts'] = {name: len((root / product / 'install-markers' / f'{name}.log').read_text().splitlines())
                    for name in ['skill-package', 'extension-package', 'dependency']}
                result['peerDependencyInstalled'] = (agent / 'npm/node_modules/pisharp-host-api-must-not-install').exists()
                assert result['extensionConfiguredCount'] == 1, result
                assert result['installScripts'] == {'skill-package': 1, 'extension-package': 1, 'dependency': 1}, result
                assert not result['peerDependencyInstalled'], result
            if package_scenario:
                result['startupAccepted'] = True
                result['packagePrompt'] = review_command['description'] if review_command else None
            if scenario == 'package-extension':
                log = root / product / 'extension-load.log'
                result['extensionLoadTrace'] = log.read_text().splitlines() if log.exists() else []
                if extension_mode == 'throwing-sibling':
                    result['partialCommandPresent'] = any(c['name'] == 'partial_command' for c in commands['data']['commands'])
            return result
    except RpcEndedBeforeCheckpoint:
        if not package_scenario or records or product != 'pisharp' and extension_mode not in ['throwing-sibling', 'missing-explicit', 'padded-explicit']:
            raise
        if extension_mode in ['throwing-sibling', 'missing-explicit', 'padded-explicit']:
            stderr = output.with_name(output.stem + f'-{product}.stderr.log').read_text()
            return dict(startupAccepted=False, exitCode=process.wait(timeout=10), providerRequests=len(server.requests),
                        configurationFailureReported='fixture extension configuration failure' in stderr,
                        recoveryHint='-ne' in stderr,
                        diagnostics=[line.replace(str(root / product), '<workspace>')
                            .replace('/bad.ts', '/bad.<module>').replace('/bad.dll', '/bad.<module>')
                            .replace('/missing.ts', '/missing.<module>').replace('/missing.dll', '/missing.<module>')
                            .replace('/index.ts', '/index.<module>').replace('/index.dll', '/index.<module>')
                            .replace('using "pi -ne"', 'using "<app> -ne"')
                            .replace('using "pisharp -ne"', 'using "<app> -ne"')
                            for line in stderr.splitlines() if line])
        return dict(startupAccepted=False, exitCode=process.wait(timeout=10),
                    error=output.with_name(output.stem + f'-{product}.stderr.log').read_text().strip(),
                    providerRequests=len(server.requests))
    finally:
        if process is not None:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=5)
            process.stdout.close()
            if not process.stdin.closed:
                process.stdin.close()
        server.shutdown()
        server.server_close()
        worker.join(timeout=5)
        output.with_name(output.stem + f'-{product}.records.json').write_text(json.dumps(records, indent=2) + '\n')
        output.with_name(output.stem + f'-{product}.http.json').write_text(json.dumps(server.requests, indent=2) + '\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--pisharp', type=Path, default=ROOT / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--profile', type=Path)
    parser.add_argument('--scenario', choices=['deleted-skill', 'invalid-tag', 'malformed-flow', 'local-package', 'padded-local-package', 'file-url-package', 'package-extension', 'managed-npm-package'], default='deleted-skill')
    parser.add_argument('--extension-mode', choices=['manifest', 'conventions', 'disabled', 'no-extensions', 'file', 'root', 'index-with-sibling', 'nested-manifest', 'ignored-sibling', 'hidden-sibling', 'node-modules-sibling', 'throwing-sibling', 'missing-explicit', 'padded-explicit', 'file-url-explicit', 'canonical-explicit-alias', 'project-disabled', 'project-enabled'], default='manifest')
    args = parser.parse_args()
    sha = verify_pi_checkout(args.pi)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    profile = args.profile.resolve() if args.profile else None
    if args.scenario == 'managed-npm-package':
        if profile is None:
            parser.error('--profile is required for managed-npm-package')
        if not (profile / 'tmp').is_dir():
            parser.error('--profile/tmp must already exist as the isolated temporary directory')
    with tempfile.TemporaryDirectory(prefix='pisharp-resource-invocation-process-',
                                     dir=str(profile / 'tmp') if profile else None) as directory:
        work = Path(directory)
        managed_sources = create_managed_npm_tarballs(work, args.pi.resolve(), os.environ.copy()) \
            if args.scenario == 'managed-npm-package' else None
        results = {product: replay(product, args.pi.resolve(), args.pisharp.resolve(), work, args.output,
                                   args.scenario, args.extension_mode, managed_sources, profile)
                   for product in ['pi', 'pisharp']}
    if args.scenario == 'invalid-tag':
        for product in results:
            assert 'YAMLWarning' not in args.output.with_name(args.output.stem + f'-{product}.stderr.log').read_text()
    result = dict(piSha=sha, scenario=args.scenario, piSharpBaseSha=subprocess.check_output(
        ['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
        assemblySha256=hashlib.sha256(args.pisharp.read_bytes()).hexdigest(),
        dependencyAssemblySha256={name: hashlib.sha256(args.pisharp.with_name(name).read_bytes()).hexdigest()
                                  for name in ['PiSharp.Runtime.dll', 'PiSharp.Core.dll']},
        results=results, match=results['pi'] == results['pisharp'])
    if args.scenario == 'package-extension':
        result['extensionMode'] = args.extension_mode
    if args.scenario == 'managed-npm-package':
        result['scope'] = ('Real Pi/PiSharp RPC startup after local managed npm acquisition: declared skill and prompt template '
                           'appear in commands, expanded provider input contains both package resources, one startup extension '
                           'configuration, lifecycle script policy and peer suppression. Reload is paired separately by the '
                           'native ProjectRuntimeContext reload-owner test; this RPC flow does not claim a reload command.')
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    assert result['match'], result
    print(f'Matched {args.scenario} RPC/provider input, metadata and error boundary.')


if __name__ == '__main__':
    main()
