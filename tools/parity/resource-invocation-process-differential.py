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
import re
import shutil
import signal
import socket
import subprocess
import tempfile
import threading
import time

from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit, thread_limit_environment

apply_test_thread_limit()
ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('rpc_settlement', Path(__file__).with_name('rpc-settlement-differential.py'))
shared = importlib.util.module_from_spec(spec)
spec.loader.exec_module(shared)


class RpcEndedBeforeCheckpoint(Exception):
    pass


def normalize_temporary_git_cache_path(value):
    return re.sub(r'(/tmp/extensions/git-[^/]+/)[0-9a-f]{8,64}(/)',
                  lambda match: match.group(1) + '<source-cache>' + match.group(2), value)


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
        "const fs=require('node:fs'); fs.appendFileSync(process.env.PISHARP_MANAGED_NPM_MARKER_DIR + '/skill-package.log', 'run\\n'); " +
        "if(process.env.PISHARP_MANAGED_SOURCE_ORDER_LOG) fs.appendFileSync(process.env.PISHARP_MANAGED_SOURCE_ORDER_LOG, 'npm\\n');\n")
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


def create_managed_git_repository(root, pi, environment):
    remote_root = root / 'managed-git-remote'
    repository = remote_root / 'fixture/repo.git'
    source_tree = root / 'managed-git-source'
    dependency = source_tree / 'vendor/managed-extension-dependency'
    skills = source_tree / 'skills/guide'
    prompts = source_tree / 'prompts'
    themes = source_tree / 'themes'
    extensions = source_tree / 'extensions'
    for directory in [repository.parent, dependency, skills, prompts, themes, extensions]:
        directory.mkdir(parents=True, exist_ok=True)
    (dependency / 'package.json').write_text(json.dumps(dict(name='managed-extension-dependency', version='1.0.0',
        scripts=dict(postinstall='node postinstall.cjs'))))
    (dependency / 'postinstall.cjs').write_text(
        "require('node:fs').appendFileSync(process.env.PISHARP_MANAGED_GIT_MARKER_DIR + '/dependency.log', 'run\\n');\n")
    (source_tree / 'package.json').write_text(json.dumps(dict(name='managed-git-package', version='1.0.0', type='module',
        pi=dict(skills=['skills'], prompts=['prompts'], themes=['themes'], extensions=['extensions']),
        dependencies={'managed-extension-dependency': 'file:vendor/managed-extension-dependency'},
        peerDependencies={'pisharp-host-api-must-not-install': '^1.0.0'},
        scripts=dict(postinstall='node postinstall.cjs'))))
    (source_tree / 'postinstall.cjs').write_text(
        "const fs=require('node:fs'); fs.appendFileSync(process.env.PISHARP_MANAGED_GIT_MARKER_DIR + '/package.log', 'run\\n'); " +
        "if(process.env.PISHARP_MANAGED_SOURCE_ORDER_LOG) fs.appendFileSync(process.env.PISHARP_MANAGED_SOURCE_ORDER_LOG, 'git\\n');\n")
    (skills / 'SKILL.md').write_text(
        '---\nname: guide\ndescription: Installed managed Git skill\n---\nInstalled managed Git skill body')
    (prompts / 'review.md').write_text('---\ndescription: Installed managed Git prompt\n---\nReview topic')
    dark_theme = json.loads((pi / 'packages/coding-agent/src/modes/interactive/theme/dark.json').read_text())
    dark_theme['name'] = 'managed-git-theme'
    (themes / 'managed.json').write_text(json.dumps(dark_theme))
    (extensions / 'index.ts').write_text("""import {appendFileSync, readFileSync} from 'node:fs';
import {join} from 'node:path';
import {pathToFileURL} from 'node:url';
export default function(pi) {
 appendFileSync(process.env.PISHARP_TEST_EXTENSION_LOAD_LOG, 'configured\\n');
 pi.on('project_trust', () => ({trusted:'undecided',remember:true}));
 pi.on('project_trust', ({cwd}) => {
  try { const state=JSON.parse(readFileSync(join(cwd,'.pi','project-trust-extension.json'),'utf8'));
   return {trusted:state.decision,remember:state.remember}; } catch { return null; }
 });
 pi.on('resources_discover', ({cwd,reason}) => {
  const phase=reason==='reload'?'reloaded':'startup';
  const base=join(cwd,'.pi','extension-resources',phase);
  return {skillPaths:[pathToFileURL(join(base,'skills')).href],promptPaths:[join(base,'prompts')],themePaths:[join(base,'themes')]};
 });
}
""")
    native_extension = ROOT / 'tests/ResourceDiscoveryExtension/bin/Debug/net10.0/dynamic-resources.dll'
    if not native_extension.is_file():
        raise RuntimeError('ResourceDiscoveryExtension output is missing; build the focused test project first')
    shutil.copyfile(native_extension, extensions / 'index.dll')

    git_environment = dict(environment)
    git_environment.update(GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=str(root / 'gitconfig'),
                           GIT_TERMINAL_PROMPT='0')
    subprocess.run(['git', 'init', '--initial-branch=main'], cwd=source_tree, env=git_environment,
                   check=True, capture_output=True, text=True, timeout=30)
    subprocess.run(['git', 'add', '.'], cwd=source_tree, env=git_environment,
                   check=True, capture_output=True, text=True, timeout=30)
    subprocess.run(['git', '-c', 'user.name=Parity Fixture', '-c', 'user.email=fixture@example.invalid',
                    'commit', '-m', 'managed Git fixture'], cwd=source_tree, env=git_environment,
                   check=True, capture_output=True, text=True, timeout=30)
    subprocess.run(['git', 'tag', 'v1'], cwd=source_tree, env=git_environment,
                   check=True, capture_output=True, text=True, timeout=30)
    (source_tree / 'revision.txt').write_text('v2\n')
    subprocess.run(['git', 'add', 'revision.txt'], cwd=source_tree, env=git_environment,
                   check=True, capture_output=True, text=True, timeout=30)
    subprocess.run(['git', '-c', 'user.name=Parity Fixture', '-c', 'user.email=fixture@example.invalid',
                    'commit', '-m', 'advance default branch'], cwd=source_tree, env=git_environment,
                   check=True, capture_output=True, text=True, timeout=30)
    v1_head = subprocess.check_output(['git', 'rev-parse', 'v1'], cwd=source_tree, env=git_environment,
                                      text=True).strip()
    v2_head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=source_tree, env=git_environment,
                                      text=True).strip()
    subprocess.run(['git', 'clone', '--bare', str(source_tree), str(repository)], cwd=root, env=git_environment,
                   check=True, capture_output=True, text=True, timeout=30)
    subprocess.run(['git', '--git-dir', str(repository), 'update-ref', 'refs/heads/main', v1_head], cwd=root,
                   env=git_environment, check=True, capture_output=True, text=True, timeout=30)

    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    daemon_log = (root / 'git-daemon.log').open('w')
    daemon = subprocess.Popen(['git', 'daemon', '--reuseaddr', '--export-all', f'--base-path={remote_root}',
        '--listen=127.0.0.1', f'--port={port}'], cwd=remote_root, env=git_environment,
        stdout=daemon_log, stderr=subprocess.STDOUT, start_new_session=True)
    daemon_log.close()
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        if daemon.poll() is not None:
            raise RuntimeError(f'Local Git daemon exited with status {daemon.returncode}: {(root / "git-daemon.log").read_text()}')
        try:
            with socket.create_connection(('127.0.0.1', port), timeout=0.2):
                break
        except OSError:
            time.sleep(0.05)
    else:
        daemon.kill()
        daemon.wait(timeout=5)
        raise TimeoutError('Local Git daemon did not start listening')
    return dict(pinned_source=f'git:git://127.0.0.1:{port}/fixture/repo.git@v1',
                unpinned_source=f'git:git://127.0.0.1:{port}/fixture/repo.git',
                daemon=daemon, repository=repository, v1_head=v1_head, v2_head=v2_head)


def stop_git_daemon(daemon):
    if daemon.poll() is None:
        os.killpg(daemon.pid, signal.SIGTERM)
        try:
            daemon.wait(timeout=5)
        except subprocess.TimeoutExpired:
            os.killpg(daemon.pid, signal.SIGKILL)
            daemon.wait(timeout=5)


def probe_pi_git_update(pi, root, profile, git_fixture, environment, output):
    probe = root / 'pi-update-probe'
    work = probe / 'project'
    agent = probe / 'agent'
    markers = probe / 'install-markers'
    npm_cache = probe / 'npm-cache'
    for directory in [work, agent, markers, npm_cache]:
        directory.mkdir(parents=True, exist_ok=True)
    script = probe / 'probe.mjs'
    script.write_text("""import {execFileSync} from 'node:child_process';
import {existsSync, readFileSync} from 'node:fs';
import {pathToFileURL} from 'node:url';
const [piRoot, source, cwd, agentDir, bareRepo, v1, v2] = process.argv.slice(2);
const {DefaultPackageManager} = await import(pathToFileURL(`${piRoot}/packages/coding-agent/src/core/package-manager.ts`).href);
const {SettingsManager} = await import(pathToFileURL(`${piRoot}/packages/coding-agent/src/core/settings-manager.ts`).href);
const settingsManager = SettingsManager.inMemory();
settingsManager.setPackages([source]);
const manager = new DefaultPackageManager({cwd, agentDir, settingsManager});
await manager.install(source);
const installPath = manager.getInstalledPath(source, 'user');
const before = execFileSync('git', ['-C', installPath, 'rev-parse', 'HEAD'], {encoding: 'utf8'}).trim();
execFileSync('git', ['--git-dir', bareRepo, 'update-ref', 'refs/heads/main', v2]);
await manager.update(source);
const after = execFileSync('git', ['-C', installPath, 'rev-parse', 'HEAD'], {encoding: 'utf8'}).trim();
console.log(JSON.stringify({source, before, after, expectedBefore: v1, expectedAfter: v2,
  installPath: installPath.replace(cwd, '<probe>'), updated: before !== after && after === v2,
  installScripts: Object.fromEntries(['package', 'dependency'].map(name => [name,
    existsSync(`${process.env.PISHARP_MANAGED_GIT_MARKER_DIR}/${name}.log`)
      ? readFileSync(`${process.env.PISHARP_MANAGED_GIT_MARKER_DIR}/${name}.log`, 'utf8').trim().split('\\n').length : 0])),
  peerDependencyInstalled: existsSync(`${installPath}/node_modules/pisharp-host-api-must-not-install`)}));
""")
    probe_env = dict(environment)
    probe_env.update(HOME=str(profile / 'home'), TMPDIR=str(profile / 'tmp'),
        XDG_CONFIG_HOME=str(profile / 'config'), XDG_CACHE_HOME=str(profile / 'cache'),
        PI_CODING_AGENT_DIR=str(agent), npm_config_cache=str(npm_cache), npm_config_offline='true',
        PISHARP_MANAGED_GIT_MARKER_DIR=str(markers), GIT_CONFIG_NOSYSTEM='1',
        GIT_CONFIG_GLOBAL=str(root / 'gitconfig'), GIT_TERMINAL_PROMPT='0')
    probe_env.pop('PI_OFFLINE', None)
    resolver = (pi / 'packages/coding-agent/src/experimental/source-resolver.ts').as_uri()
    command = ['node', '--import', resolver, str(script), str(pi), git_fixture['unpinned_source'], str(work),
               str(agent), str(git_fixture['repository']), git_fixture['v1_head'], git_fixture['v2_head']]
    completed = subprocess.run(command, cwd=work, env=probe_env, capture_output=True, text=True, timeout=180)
    output.write_text(completed.stdout + completed.stderr)
    if completed.returncode != 0:
        raise RuntimeError(f'Pi update probe failed ({completed.returncode}): {completed.stderr}')
    result = json.loads(completed.stdout.splitlines()[-1])
    if not result['updated'] or result['before'] != git_fixture['v1_head']:
        raise AssertionError(f'Pi did not refresh the unpinned Git source: {result}')
    return result


def replay(product, pi, native, root, output, scenario, extension_mode='manifest', managed_sources=None,
           profile=None):
    package_scenario = scenario in ['local-package', 'padded-local-package', 'file-url-package', 'package-extension',
                                    'managed-npm-package', 'managed-git-package',
                                    'temporary-npm-package', 'temporary-git-package', 'temporary-mixed-source-order']
    agent = root / product / 'agent'
    home = (profile / 'home') if profile else root / product / 'home'
    skill = root / product / ('package/skills/guide/SKILL.md' if package_scenario else 'guide/SKILL.md')
    if scenario == 'managed-npm-package':
        skill = agent / 'npm/node_modules/managed-skill/skills/guide/SKILL.md'
    if scenario == 'managed-git-package':
        skill = agent / 'git/127.0.0.1/fixture/repo/skills/guide/SKILL.md'
    if scenario == 'temporary-npm-package':
        skill = agent / 'tmp/extensions/npm/managed-skill/skills/guide/SKILL.md'
    if scenario == 'temporary-git-package':
        skill = agent / 'tmp/extensions/git-127.0.0.1/fixture/repo/skills/guide/SKILL.md'
    if scenario == 'temporary-mixed-source-order':
        skill = agent / 'tmp/extensions/git-127.0.0.1/fixture/repo/skills/guide/SKILL.md'
    if scenario == 'package-extension':
        skill = root / product / '.pi/extension-resources/startup/skills/guide/SKILL.md'
    agent.mkdir(parents=True)
    home.mkdir(parents=True, exist_ok=True)
    if scenario not in ['managed-npm-package', 'managed-git-package', 'temporary-npm-package',
                        'temporary-git-package', 'temporary-mixed-source-order']:
        skill.parent.mkdir(parents=True)
        description = '!!int word' if scenario == 'invalid-tag' else 'Guide'
        skill.write_text(f'---\nname: guide\ndescription: {description}\n---\nbody')
    if package_scenario:
        if scenario in ['managed-npm-package', 'managed-git-package']:
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
        elif scenario in ['temporary-npm-package', 'temporary-git-package', 'temporary-mixed-source-order']:
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
    managed_scenario = scenario in ['managed-npm-package', 'managed-git-package',
                                    'temporary-npm-package', 'temporary-git-package', 'temporary-mixed-source-order']
    if managed_scenario:
        env.update(npm_config_cache=str(profile / 'cache/npm'), npm_config_offline='true',
                   PISHARP_AGENT_DIR=str(agent), PISHARP_SESSION_DIR=str(profile / 'sessions'),
                   PISHARP_MANAGED_NPM_MARKER_DIR=str(root / product / 'install-markers'),
                   PISHARP_TEST_RESOURCE_DISCOVERY='true',
                   PISHARP_TEST_EXTENSION_LOAD_LOG=str(root / product / 'extension-load.log'))
        (root / product / 'install-markers').mkdir(parents=True)
    if scenario in ['managed-git-package', 'temporary-git-package', 'temporary-mixed-source-order']:
        # Keep npm install's cache in the isolated CLI fixture directory rather than the ambient profile.
        env.update(npm_config_cache=str(root / product / 'npm-cache'),
                   PISHARP_MANAGED_GIT_MARKER_DIR=str(root / product / 'install-markers'))
    if scenario == 'temporary-mixed-source-order':
        env['PISHARP_MANAGED_SOURCE_ORDER_LOG'] = str(root / product / 'install-markers/source-order.log')
    elif not managed_scenario:
        env['PI_OFFLINE'] = '1'
    if scenario in ['package-extension', 'managed-npm-package', 'managed-git-package',
                    'temporary-npm-package', 'temporary-git-package', 'temporary-mixed-source-order']:
        env.update(PISHARP_TEST_RESOURCE_DISCOVERY='true',
                   PISHARP_TEST_EXTENSION_LOAD_LOG=str(root / product / 'extension-load.log'))
        if extension_mode == 'throwing-sibling':
            env['PISHARP_TEST_EXTENSION_PARTIAL_FAILURE'] = 'true'
    common = ['--mode', 'rpc', '--provider', 'fixture', '--model', 'fixture-model', '--no-session',
              '--no-extensions', '--no-skills', '--no-prompt-templates', '--no-tools', '--skill', str(skill)]
    if not managed_scenario:
        common.insert(6, '--offline')
    if package_scenario:
        common = common[:-2]
        common.remove('--no-skills')
        common.remove('--no-prompt-templates')
    if scenario in ['package-extension', 'managed-npm-package', 'managed-git-package',
                    'temporary-npm-package', 'temporary-git-package', 'temporary-mixed-source-order'] and extension_mode != 'no-extensions':
        common.remove('--no-extensions')
    if scenario in ['temporary-npm-package', 'temporary-git-package', 'temporary-mixed-source-order']:
        for source in managed_sources or []:
            common.extend(['--extension', source])
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
            temporary_resource_metadata = None
            if scenario in ['temporary-npm-package', 'temporary-git-package', 'temporary-mixed-source-order']:
                skill_metadata = (command_info or {}).get('sourceInfo', {})
                prompt_metadata = (review_command or {}).get('sourceInfo', {})
                skill_fields = {key: skill_metadata.get(key) for key in ['source', 'scope', 'origin']}
                prompt_fields = {key: prompt_metadata.get(key) for key in ['source', 'scope', 'origin']}
                assert skill_fields == {'source': 'cli', 'scope': 'temporary', 'origin': 'top-level'}, commands
                if scenario == 'temporary-mixed-source-order':
                    prompt_source = managed_sources[0] if review_command['description'] == 'Installed managed Git prompt' else managed_sources[1]
                else:
                    prompt_source = managed_sources[0]
                assert prompt_fields == {'source': prompt_source, 'scope': 'temporary', 'origin': 'package'}, commands
                temporary_resource_metadata = {'skill': skill_fields, 'prompt': prompt_fields}
            if scenario in ['managed-npm-package', 'managed-git-package',
                            'temporary-npm-package', 'temporary-git-package', 'temporary-mixed-source-order']:
                if scenario == 'temporary-mixed-source-order':
                    assert command_info and command_info['description'] in [
                        'Installed managed Git skill', 'Installed managed skill'], commands
                    assert review_command and review_command['description'] in [
                        'Installed managed Git prompt', 'Installed managed prompt'], commands
                else:
                    expected_skill_description = ('Installed managed Git skill'
                                                  if scenario in ['managed-git-package', 'temporary-git-package']
                                                  else 'Installed managed skill')
                    expected_prompt_description = ('Installed managed Git prompt'
                                                  if scenario in ['managed-git-package', 'temporary-git-package']
                                                   else 'Installed managed prompt')
                    assert command_info and command_info['description'] == expected_skill_description, commands
                    assert review_command and review_command['description'] == expected_prompt_description, commands
                assert startup_skill and startup_skill['description'] == 'managed-startup extension resource', commands
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
            prompts = [('skill', '/skill:guide args'), ('template', '/review topic')] \
                if scenario in ['managed-npm-package', 'managed-git-package',
                                'temporary-npm-package', 'temporary-git-package', 'temporary-mixed-source-order'] \
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
            expected_requests = 2 if scenario in ['managed-npm-package', 'managed-git-package',
                                                   'temporary-npm-package', 'temporary-git-package',
                                                   'temporary-mixed-source-order'] else 1
            assert len(server.requests) == expected_requests
            provider_inputs = []
            for request in server.requests:
                users = [m['content'] for m in request['messages'] if m['role'] == 'user']
                content = users[-1]
                provider_inputs.append(content if isinstance(content, str) else ''.join(
                    p.get('text', '') for p in content if p.get('type') == 'text'))
            if scenario in ['managed-npm-package', 'managed-git-package',
                            'temporary-npm-package', 'temporary-git-package', 'temporary-mixed-source-order']:
                expected_skill_body = ('Installed managed Git skill body'
                                       if scenario in ['managed-git-package', 'temporary-git-package']
                                       else 'Installed managed skill body')
                if scenario == 'temporary-mixed-source-order':
                    assert any(body in provider_inputs[0] for body in [
                        'Installed managed Git skill body', 'Installed managed skill body']), provider_inputs
                else:
                    assert expected_skill_body in provider_inputs[0], provider_inputs
                assert provider_inputs[0].endswith('args'), provider_inputs
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
                        providerInput=normalize_temporary_git_cache_path(
                            provider_inputs[0].replace(str(root / product), '<workspace>')),
                        providerRequests=expected_requests,
                        promptAccepted=True, commandDescription=command_info['description'] if command_info else None)
            if package_scenario:
                result['packageSkillMetadata'] = {
                    key: (command_info or {}).get('sourceInfo', {}).get(key)
                    for key in ['source', 'scope', 'origin']
                }
                result['packagePromptMetadata'] = {
                    key: (review_command or {}).get('sourceInfo', {}).get(key)
                    for key in ['source', 'scope', 'origin']
                }
            if temporary_resource_metadata is not None:
                result['temporaryResourceMetadata'] = temporary_resource_metadata
            if scenario in ['managed-npm-package', 'managed-git-package',
                            'temporary-npm-package', 'temporary-git-package', 'temporary-mixed-source-order']:
                result['templateProviderInput'] = normalize_temporary_git_cache_path(
                    provider_inputs[1].replace(str(root / product), '<workspace>'))
                result['extensionResourceDescription'] = startup_skill['description']
                result['extensionConfiguredCount'] = sum(line == 'configured' for line in
                    (root / product / 'extension-load.log').read_text().splitlines())
                git_scenario = scenario in ['managed-git-package', 'temporary-git-package', 'temporary-mixed-source-order']
                if scenario == 'temporary-mixed-source-order':
                    marker_names = ['package', 'skill-package', 'extension-package', 'dependency']
                else:
                    marker_names = ['package', 'dependency'] if git_scenario else [
                        'skill-package', 'extension-package', 'dependency']
                result['installScripts'] = {name: len((root / product / 'install-markers' / f'{name}.log').read_text().splitlines())
                    for name in marker_names}
                if scenario == 'managed-git-package':
                    managed_root = agent / 'git/127.0.0.1/fixture/repo'
                elif scenario == 'temporary-git-package':
                    roots = list((agent / 'tmp/extensions/git-127.0.0.1').glob('*/fixture/repo'))
                    managed_root = roots[0] if roots else agent / 'tmp/extensions/git-127.0.0.1/missing/fixture/repo'
                elif scenario == 'temporary-npm-package':
                    roots = list((agent / 'tmp/extensions/npm').glob('*/node_modules/managed-native-extension'))
                    managed_root = roots[0] if roots else agent / 'tmp/extensions/npm/missing/node_modules/managed-native-extension'
                elif scenario == 'temporary-mixed-source-order':
                    roots = list((agent / 'tmp/extensions/git-127.0.0.1').glob('*/fixture/repo'))
                    roots.extend((agent / 'tmp/extensions/npm').glob('*/node_modules/managed-native-extension'))
                    managed_root = roots[0] if roots else agent / 'tmp/extensions/missing'
                else:
                    managed_root = agent / 'npm/node_modules/managed-native-extension'
                roots = roots if scenario in ['temporary-git-package', 'temporary-npm-package',
                                               'temporary-mixed-source-order'] else [managed_root]
                result['peerDependencyInstalled'] = any(
                    (package_root / 'node_modules/pisharp-host-api-must-not-install').exists() for package_root in roots)
                expected_configured = 2 if scenario == 'temporary-mixed-source-order' else 1
                assert result['extensionConfiguredCount'] == expected_configured, result
                if scenario == 'temporary-mixed-source-order':
                    expected_scripts = {'package': 1, 'skill-package': 1, 'extension-package': 1, 'dependency': 2}
                else:
                    expected_scripts = {'package': 1, 'dependency': 1} if git_scenario else {
                        'skill-package': 1, 'extension-package': 1, 'dependency': 1}
                assert result['installScripts'] == expected_scripts, result
                assert not result['peerDependencyInstalled'], result
                if scenario == 'temporary-mixed-source-order':
                    order_path = root / product / 'install-markers/source-order.log'
                    source_order = order_path.read_text().splitlines() if order_path.exists() else []
                    collapsed_order = [value for index, value in enumerate(source_order)
                                       if index == 0 or value != source_order[index - 1]]
                    assert collapsed_order == ['git', 'npm'], source_order
                    result['temporarySourceOrder'] = collapsed_order
                if scenario in ['temporary-npm-package', 'temporary-git-package', 'temporary-mixed-source-order']:
                    user_settings = json.loads((agent / 'settings.json').read_text()) if (agent / 'settings.json').is_file() else {}
                    project_settings_path = root / product / '.pi/settings.json'
                    project_settings = json.loads(project_settings_path.read_text()) if project_settings_path.is_file() else {}
                    assert not user_settings.get('packages'), user_settings
                    assert not project_settings.get('packages'), project_settings
                    assert (agent / 'tmp/extensions').is_dir()
                    assert not (agent / 'npm').exists() and not (agent / 'git').exists()
                    result['temporaryScope'] = True
                    result['packageSettingsUnchanged'] = True
                    result['persistentPackageRootsAbsent'] = True
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
    parser.add_argument('--scenario', choices=['deleted-skill', 'invalid-tag', 'malformed-flow', 'local-package', 'padded-local-package', 'file-url-package', 'package-extension', 'managed-npm-package', 'managed-git-package', 'temporary-npm-package', 'temporary-git-package', 'temporary-mixed-source-order'], default='deleted-skill')
    parser.add_argument('--extension-mode', choices=['manifest', 'conventions', 'disabled', 'no-extensions', 'file', 'root', 'index-with-sibling', 'nested-manifest', 'ignored-sibling', 'hidden-sibling', 'node-modules-sibling', 'throwing-sibling', 'missing-explicit', 'padded-explicit', 'file-url-explicit', 'canonical-explicit-alias', 'project-disabled', 'project-enabled'], default='manifest')
    args = parser.parse_args()
    sha = verify_pi_checkout(args.pi)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    profile = args.profile.resolve() if args.profile else None
    if args.scenario in ['managed-npm-package', 'managed-git-package',
                         'temporary-npm-package', 'temporary-git-package', 'temporary-mixed-source-order']:
        if profile is None:
            parser.error('--profile is required for managed package scenarios')
        if not (profile / 'tmp').is_dir():
            parser.error('--profile/tmp must already exist as the isolated temporary directory')
    with tempfile.TemporaryDirectory(prefix='pisharp-resource-invocation-process-',
                                     dir=str(profile / 'tmp') if profile else None) as directory:
        work = Path(directory)
        fixture_environment = os.environ.copy()
        if profile:
            npm_cache = profile / 'cache/npm'
            npm_cache.mkdir(parents=True, exist_ok=True)
            fixture_environment['npm_config_cache'] = str(npm_cache)
        daemon = None
        git_fixture = None
        pi_update_probe = None
        try:
            if args.scenario in ['managed-npm-package', 'temporary-npm-package', 'temporary-mixed-source-order']:
                npm_sources = create_managed_npm_tarballs(work, args.pi.resolve(), fixture_environment)
            else:
                npm_sources = None
            if args.scenario in ['managed-git-package', 'temporary-git-package', 'temporary-mixed-source-order']:
                git_fixture = create_managed_git_repository(work, args.pi.resolve(), fixture_environment)
                daemon = git_fixture['daemon']
                managed_sources = [git_fixture['pinned_source']]
                if args.scenario == 'temporary-mixed-source-order':
                    managed_sources.extend(npm_sources or [])
            elif npm_sources is not None:
                managed_sources = npm_sources
            else:
                managed_sources = None
            results = {product: replay(product, args.pi.resolve(), args.pisharp.resolve(), work, args.output,
                                       args.scenario, args.extension_mode, managed_sources, profile)
                       for product in ['pi', 'pisharp']}
            if git_fixture is not None and args.scenario == 'managed-git-package':
                probe_log = args.output.with_name(args.output.stem + '-pi-update-probe.log')
                pi_update_probe = probe_pi_git_update(args.pi.resolve(), work, profile, git_fixture,
                    os.environ.copy(), probe_log)
        finally:
            if daemon is not None:
                stop_git_daemon(daemon)
    if args.scenario == 'invalid-tag':
        for product in results:
            assert 'YAMLWarning' not in args.output.with_name(args.output.stem + f'-{product}.stderr.log').read_text()
    result = dict(piSha=sha, scenario=args.scenario, piSharpBaseSha=subprocess.check_output(
        ['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
        assemblySha256=hashlib.sha256(args.pisharp.read_bytes()).hexdigest(),
        dependencyAssemblySha256={name: hashlib.sha256(args.pisharp.with_name(name).read_bytes()).hexdigest()
                                  for name in ['PiSharp.Runtime.dll', 'PiSharp.Core.dll']},
        results=results, match=results['pi'] == results['pisharp'])
    if pi_update_probe is not None:
        result['piUpdateProbe'] = pi_update_probe
    if args.scenario == 'package-extension':
        result['extensionMode'] = args.extension_mode
    if args.scenario in ['managed-npm-package', 'managed-git-package',
                         'temporary-npm-package', 'temporary-git-package', 'temporary-mixed-source-order']:
        result['scope'] = ('Real Pi/PiSharp RPC startup after local managed npm acquisition: declared skill and prompt template '
                           'appear in commands, expanded provider input contains both package resources, one startup extension '
                           'configuration, lifecycle script policy and peer suppression. Reload is paired separately by the '
                           'native ProjectRuntimeContext reload-owner test; this RPC flow does not claim a reload command.')
        if args.scenario in ['managed-git-package', 'temporary-git-package', 'temporary-mixed-source-order']:
            result['scope'] = ('Real Pi/PiSharp RPC startup after managed Git acquisition from a local loopback git daemon: '
                               'declared skill and prompt template appear in commands, expanded provider input contains both '
                               'package resources, one startup extension configuration, dependency lifecycle policy and peer '
                               'suppression. A paired Pi DefaultPackageManager update probe advances the unpinned source from '
                               'v1 to v2; PiSharp explicit update and resource reload are covered by native store/context tests. '
                               'This task does not add a CLI update or RPC reload command.')
        if args.scenario == 'temporary-mixed-source-order':
            result['scope'] = ('Real Pi/PiSharp RPC startup with explicit Git then npm extension sources: package lifecycle '
                               'markers, provider-visible skill/prompt precedence, temporary source metadata, settings '
                               'isolation and extension initialization order are compared.')
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    assert result['match'], result
    print(f'Matched {args.scenario} RPC/provider input, metadata and error boundary.')


if __name__ == '__main__':
    main()
