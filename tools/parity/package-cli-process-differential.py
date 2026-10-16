#!/usr/bin/env python3
"""Compare real Pi and PiSharp package CLI workflows using local npm and Git fixtures."""
import argparse
import atexit
import hashlib
from http.server import ThreadingHTTPServer
import importlib.util
import json
import os
from pathlib import Path
import queue
import re
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/parity'))
from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit, thread_limit_environment

apply_test_thread_limit()
resource_path = Path(__file__).with_name('resource-invocation-process-differential.py')
resource_spec = importlib.util.spec_from_file_location('resource_process', resource_path)
resource = importlib.util.module_from_spec(resource_spec)
resource_spec.loader.exec_module(resource)
rpc_spec = importlib.util.spec_from_file_location('rpc_settlement', Path(__file__).with_name('rpc-settlement-differential.py'))
rpc = importlib.util.module_from_spec(rpc_spec)
rpc_spec.loader.exec_module(rpc)


def command_for(product, pi, native, args):
    if product == 'pi':
        resolver = (pi / 'packages/coding-agent/src/experimental/source-resolver.ts').as_uri()
        return ['node', '--import', resolver, str(pi / 'packages/coding-agent/src/cli.ts'), *args]
    return ['dotnet', str(native), *args]


def base_environment(profile, product, agent, cwd, markers, trace):
    env = {key: os.environ[key] for key in ['PATH', 'DOTNET_ROOT', 'LD_LIBRARY_PATH', 'LANG', 'LC_ALL']
           if key in os.environ}
    local = profile / product
    home = local / 'home'
    paths = {
        'home': home,
        'tmp': profile / 'tmp',
        'xdg_config': local / 'xdg-config',
        'xdg_cache': local / 'xdg-cache',
        'npm_cache': local / 'npm-cache',
        'dotnet': profile / 'dotnet',
        'nuget': profile / 'nuget',
        'nuget_http': profile / 'nuget-http',
        'sessions': local / 'sessions',
    }
    for path in paths.values():
        path.mkdir(parents=True, exist_ok=True)
    git_config = local / 'gitconfig.ini'
    git_config.touch(exist_ok=True)
    npm_markers = markers / 'npm'
    git_markers = markers / 'git'
    npm_markers.mkdir(parents=True, exist_ok=True)
    git_markers.mkdir(parents=True, exist_ok=True)
    env.update(HOME=str(home), USERPROFILE=str(home), TMPDIR=str(paths['tmp']),
        XDG_CONFIG_HOME=str(paths['xdg_config']), XDG_CACHE_HOME=str(paths['xdg_cache']),
        PI_CODING_AGENT_DIR=str(agent), PISHARP_AGENT_DIR=str(agent), PISHARP_SESSION_DIR=str(paths['sessions']),
        DOTNET_CLI_HOME=str(paths['dotnet']), NUGET_PACKAGES=str(paths['nuget']),
        NUGET_HTTP_CACHE_PATH=str(paths['nuget_http']), DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1',
        npm_config_cache=str(paths['npm_cache']), npm_config_offline='true',
        GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=str(git_config), GIT_TERMINAL_PROMPT='0',
        PISHARP_MANAGED_NPM_MARKER_DIR=str(npm_markers), PISHARP_MANAGED_GIT_MARKER_DIR=str(git_markers),
        PISHARP_TEST_RESOURCE_DISCOVERY='true', PISHARP_TEST_EXTENSION_LOAD_LOG=str(trace),
        PISHARP_TEST_EXTENSION_PARTIAL_FAILURE='true', TERM='xterm-256color')
    env.update(thread_limit_environment())
    env.pop('PI_OFFLINE', None)
    markers.mkdir(parents=True, exist_ok=True)
    return env


def run_cli(product, pi, native, cwd, env, args):
    completed = subprocess.run(command_for(product, pi, native, args), cwd=cwd, env=env,
        capture_output=True, text=True, timeout=180)
    return dict(exitCode=completed.returncode, stdout=completed.stdout, stderr=completed.stderr)


def strip_ansi(value):
    return re.sub(r'\x1b\[[0-?]*[ -/]*[@-~]', '', value)


def normalized_command(result, product_root, agent):
    def normalize(value):
        value = strip_ansi(value).replace(str(product_root), '<project-root>').replace(str(agent), '<agent>')
        return re.sub(r'\b\d+(?:\.\d+)?(?:ms|s)\b', '<duration>', value)
    stdout = normalize(result['stdout']).strip()
    stderr = normalize(result['stderr']).strip()
    status_lines = [line.strip() for line in stdout.splitlines()
                    if re.match(r'^(Installed|Removed|Updated|No packages installed\.)', line.strip())]
    stdout_lines = [line.strip() for line in stdout.splitlines() if line.strip()]
    diagnostics = [line for line in stderr.splitlines() if line.strip()]
    return dict(exitCode=result['exitCode'], statusLines=status_lines, stdoutLines=stdout_lines,
                diagnostics=diagnostics)


def command_outcome(result, product_root, agent):
    normalized = normalized_command(result, product_root, agent)
    return dict(exitCode=normalized['exitCode'], statusLines=normalized['statusLines'])


def read_settings(path):
    if not path.exists():
        return {}
    return json.loads(path.read_text())


def package_sources(settings):
    values = settings.get('packages', []) if isinstance(settings, dict) else []
    return [item if isinstance(item, str) else item.get('source') for item in values]


def setting_snapshot(agent, cwd):
    paths = [agent / 'settings.json', cwd / '.pi/settings.json']
    return {str(path): path.read_bytes().hex() if path.exists() else None for path in paths}


def file_tree_digest(directory):
    if not directory.exists():
        return None
    files = []
    for path in sorted(directory.rglob('*')):
        relative = str(path.relative_to(directory))
        if path.is_symlink():
            content = b'link:' + os.readlink(path).encode()
        elif path.is_file():
            content = b'file:' + path.read_bytes()
        else:
            continue
        files.append((relative, hashlib.sha256(content).hexdigest()))
    return files


def content_tree_digest(directory):
    return [(name, digest) for name, digest in file_tree_digest(directory) if not name.startswith('.git/')]


def changed_tree_paths(before, after):
    before_map = dict(before or [])
    after_map = dict(after or [])
    return sorted(name for name in before_map.keys() | after_map.keys() if before_map.get(name) != after_map.get(name))


def find_git_package(product_root, name):
    roots = [product_root / 'agent', product_root / 'work/.pi']
    for root in roots:
        if not root.exists():
            continue
        for manifest in root.rglob('package.json'):
            try:
                if json.loads(manifest.read_text()).get('name') == name and (manifest.parent / '.git').exists():
                    return manifest.parent
            except (OSError, json.JSONDecodeError):
                continue
    raise AssertionError(f'Could not find installed Git package {name} below {product_root}')


def git_head(path, environment):
    return subprocess.check_output(['git', '-C', str(path), 'rev-parse', 'HEAD'], env=environment, text=True).strip()


def lifecycle_counts(directory, names):
    counts = {}
    for name in names:
        marker = directory / f'{name}.log'
        counts[name] = len(marker.read_text().splitlines()) if marker.exists() else 0
    return counts


def list_semantics(result):
    selected = []
    for raw in result['stdout'].splitlines():
        line = strip_ansi(raw).rstrip()
        value = line.strip()
        if value in ['User packages:', 'Project packages:', 'No packages installed.']:
            selected.append(value)
        elif line.startswith('  ') and not line.startswith('    ') and value:
            selected.append(value)
    return selected


def provider_content(request):
    users = [message['content'] for message in request['messages'] if message['role'] == 'user']
    content = users[-1]
    if isinstance(content, str):
        return content
    return ''.join(part.get('text', '') for part in content if part.get('type') == 'text')


def rpc_replay(product, pi, native, product_root, env, output, expected_kind):
    cwd = product_root / 'work'
    agent = product_root / 'agent'
    env = dict(env)
    env['PISHARP_FIXTURE_KEY'] = 'fixture-only-key'
    trace = Path(env['PISHARP_TEST_EXTENSION_LOAD_LOG'])
    trace.unlink(missing_ok=True)
    server = ThreadingHTTPServer(('127.0.0.1', 0), rpc.Provider)
    server.requests = []
    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    env['PISHARP_FIXTURE_BASE_URL'] = f'http://127.0.0.1:{server.server_port}/v1'
    # Re-write the model file after the ephemeral provider port is allocated.
    (agent / 'models.json').write_text(json.dumps(dict(providers=dict(fixture=dict(
        baseUrl=env['PISHARP_FIXTURE_BASE_URL'], models=[dict(id='fixture-model', name='fixture-model',
            api='openai-completions', reasoning=False, input=['text'], contextWindow=4096, maxTokens=128,
            cost=dict(input=0, output=0, cacheRead=0, cacheWrite=0))],
        **(dict(apiKey='fixture-only-key') if product == 'pi' else dict(apiKeyEnv='PISHARP_FIXTURE_KEY')))))))
    command = command_for(product, pi, native, ['--mode', 'rpc', '--provider', 'fixture', '--model',
        'fixture-model', '--no-session', '--no-tools'])
    records, received = [], queue.Queue()
    process = None
    stderr_path = output.with_name(output.stem + f'-{product}-rpc.stderr.log')
    try:
        with stderr_path.open('w') as error:
            process = subprocess.Popen(command, cwd=cwd, env=env, stdin=subprocess.PIPE,
                stdout=subprocess.PIPE, stderr=error, text=True)
            def read_records():
                try:
                    for line in process.stdout:
                        received.put(json.loads(line))
                except Exception as failure:
                    received.put(failure)
                finally:
                    received.put(None)
            reader = threading.Thread(target=read_records, daemon=True)
            reader.start()
            def send(record):
                process.stdin.write(json.dumps(record) + '\n')
                process.stdin.flush()
            def until(predicate):
                while True:
                    record = received.get(timeout=45)
                    if isinstance(record, Exception):
                        raise record
                    if record is None:
                        raise RuntimeError('RPC process ended before the requested checkpoint')
                    records.append(record)
                    if predicate(record):
                        return record
            send(dict(id='commands', type='get_commands'))
            command_result = until(lambda record: record.get('id') == 'commands')
            commands = command_result['data']['commands']
            guide = next((item for item in commands if item['name'] == 'skill:guide'), None)
            review = next((item for item in commands if item['name'] == 'review'), None)
            startup = next((item for item in commands if item['name'] == 'skill:managed-startup'), None)
            expected_skill = 'Installed managed Git skill' if expected_kind == 'project-git' else 'Installed managed skill'
            expected_prompt = 'Installed managed Git prompt' if expected_kind == 'project-git' else 'Installed managed prompt'
            assert guide and guide['description'] == expected_skill, commands
            assert review and review['description'] == expected_prompt, commands
            assert startup and startup['description'] == 'managed-startup extension resource', commands
            prompts = [('guide', '/skill:guide args'), ('review', '/review topic'),
                       ('startup', '/skill:managed-startup extra')]
            response_ids = []
            for identifier, message in prompts:
                send(dict(id=identifier, type='prompt', message=message))
                until(lambda record: record.get('type') == 'agent_settled')
                response = next(record for record in records if record.get('id') == identifier)
                assert response['success'], records
                response_ids.append(identifier)
            assert len(server.requests) == len(prompts), server.requests
            inputs = [provider_content(request) for request in server.requests]
            expected_body = 'Installed managed Git skill body' if expected_kind == 'project-git' else 'Installed managed skill body'
            expected_review = 'Review topic'
            assert expected_body in inputs[0] and inputs[0].endswith('args'), inputs
            assert expected_review in inputs[1], inputs
            assert 'managed-startup body' in inputs[2] and inputs[2].endswith('extra'), inputs
            process.stdin.close()
            code = process.wait(timeout=20)
            reader.join(timeout=5)
            assert code == 0, (code, stderr_path.read_text())
            normalized_inputs = [value.replace(str(product_root), '<project-root>').replace(str(agent), '<agent>')
                                for value in inputs]
            result = dict(exitCode=code, skillDescription=guide['description'], promptDescription=review['description'],
                extensionSkillDescription=startup['description'], providerInputs=normalized_inputs,
                providerRequests=len(server.requests), extensionConfigurationCount=sum(
                    line == 'configured' for line in trace.read_text().splitlines()) if trace.exists() else 0)
            assert result['extensionConfigurationCount'] > 0, result
            output.with_name(output.stem + f'-{product}-rpc.records.json').write_text(json.dumps(records, indent=2) + '\n')
            output.with_name(output.stem + f'-{product}-rpc.http.json').write_text(json.dumps(server.requests, indent=2) + '\n')
            return result
    finally:
        if process is not None:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=5)
            if process.stdout:
                process.stdout.close()
            if process.stdin and not process.stdin.closed:
                process.stdin.close()
        server.shutdown()
        server.server_close()
        worker.join(timeout=5)


def assert_pair(label, results):
    if results['pi'] != results['pisharp']:
        raise AssertionError(f'{label} differs: {json.dumps(results, indent=2)}')


def start_git_daemon(remote_root, port, environment):
    log_path = remote_root.parent / 'git-daemon-restarted.log'
    with log_path.open('w') as log:
        daemon = subprocess.Popen(['git', 'daemon', '--reuseaddr', '--export-all', f'--base-path={remote_root}',
            '--listen=127.0.0.1', f'--port={port}'], cwd=remote_root, env=environment,
            stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        if daemon.poll() is not None:
            raise RuntimeError(f'Restarted Git daemon exited: {log_path.read_text()}')
        try:
            with socket.create_connection(('127.0.0.1', port), timeout=0.2):
                return daemon
        except OSError:
            time.sleep(0.05)
    daemon.kill()
    daemon.wait(timeout=5)
    raise TimeoutError('Restarted local Git daemon did not listen')


def stop_daemon(daemon):
    if daemon.poll() is None:
        os.killpg(daemon.pid, signal.SIGTERM)
        try:
            daemon.wait(timeout=5)
        except subprocess.TimeoutExpired:
            os.killpg(daemon.pid, signal.SIGKILL)
            daemon.wait(timeout=5)
    ACTIVE_DAEMONS.discard(daemon)


ACTIVE_DAEMONS = set()


def register_daemon(daemon):
    ACTIVE_DAEMONS.add(daemon)
    return daemon


def cleanup_daemons():
    for daemon in list(ACTIVE_DAEMONS):
        stop_daemon(daemon)


atexit.register(cleanup_daemons)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--pisharp', type=Path, default=ROOT / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll')
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.pi = args.pi.resolve()
    args.pisharp = args.pisharp.resolve()
    profile = args.profile.resolve()
    profile.mkdir(parents=True, exist_ok=True)
    (profile / 'tmp').mkdir(parents=True, exist_ok=True)
    pi_sha = verify_pi_checkout(args.pi)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    results = {product: {} for product in ['pi', 'pisharp']}
    daemon = None
    with tempfile.TemporaryDirectory(prefix='package-cli-', dir=str(profile / 'tmp')) as scratch:
        root = Path(scratch)
        products = {}
        for product in results:
            product_root = root / product
            cwd, agent = product_root / 'work', product_root / 'agent'
            markers, trace = product_root / 'install-markers', product_root / 'extension-load.log'
            cwd.mkdir(parents=True)
            (cwd / '.pi').mkdir()
            agent.mkdir()
            (agent / 'settings.json').write_text('{}\n')
            (cwd / '.pi/settings.json').write_text('{}\n')
            (cwd / '.pi/project-trust-extension.json').write_text('{"decision":"yes","remember":true}\n')
            for resource_name, description, body in [
                ('managed-startup', 'managed-startup extension resource', 'managed-startup body')]:
                skill = cwd / '.pi/extension-resources/startup/skills' / resource_name / 'SKILL.md'
                skill.parent.mkdir(parents=True, exist_ok=True)
                skill.write_text(f'---\nname: {resource_name}\ndescription: {description}\n---\n{body}')
            products[product] = dict(root=product_root, cwd=cwd, agent=agent, markers=markers, trace=trace,
                env=base_environment(profile, product, agent, cwd, markers, trace))
        fixtures = resource.create_managed_npm_tarballs(root, args.pi, products['pi']['env'])
        git_fixture = resource.create_managed_git_repository(root, args.pi, products['pi']['env'])
        daemon = register_daemon(git_fixture['daemon'])
        git_source = git_fixture['unpinned_source']
        port = int(git_source.rsplit(':', 1)[1].split('/')[0])
        npm_sources = fixtures

        # Failed local install must leave settings and managed package roots untouched.
        failure_results = {}
        for product, item in products.items():
            before_settings = setting_snapshot(item['agent'], item['cwd'])
            roots = [item['agent'] / 'npm', item['agent'] / 'git', item['cwd'] / '.pi']
            before_roots = {str(path): file_tree_digest(path) for path in roots}
            failure_results[product] = run_cli(product, args.pi, args.pisharp, item['cwd'], item['env'],
                ['install', './missing-local-package'])
            assert failure_results[product]['exitCode'] != 0, failure_results[product]
            assert setting_snapshot(item['agent'], item['cwd']) == before_settings
            assert {str(path): file_tree_digest(path) for path in roots} == before_roots
            results[product]['failedInstall'] = normalized_command(failure_results[product], item['root'], item['agent'])
        assert_pair('failed local install exit status', {product: results[product]['failedInstall']['exitCode']
            for product in results})

        # Install two user npm tarballs, then prove resources arrive through normal RPC/provider input.
        for source in npm_sources:
            command_results = {}
            for product, item in products.items():
                command_results[product] = run_cli(product, args.pi, args.pisharp, item['cwd'], item['env'],
                    ['install', source])
                assert command_results[product]['exitCode'] == 0, command_results[product]
            assert_pair('npm install', {product: command_outcome(command_results[product], item['root'], item['agent'])
                for product, item in products.items()})
            for product, item in products.items():
                results[product].setdefault('npmInstallDiagnostics', []).append(
                    normalized_command(command_results[product], item['root'], item['agent'])['diagnostics'])
                results[product].setdefault('npmInstallStdout', []).append(
                    normalized_command(command_results[product], item['root'], item['agent'])['stdoutLines'])
        for product, item in products.items():
            settings = read_settings(item['agent'] / 'settings.json')
            assert package_sources(settings) == npm_sources, settings
            assert (item['agent'] / 'npm/node_modules/managed-skill').is_dir()
            assert (item['agent'] / 'npm/node_modules/managed-native-extension').is_dir()
            results[product]['userSettingsAfterNpm'] = package_sources(settings)
            results[product]['npmInstalled'] = True
            results[product]['npmLifecycleCounts'] = lifecycle_counts(item['markers'] / 'npm',
                ['skill-package', 'extension-package', 'dependency'])
        assert_pair('user npm settings/cache', {product: dict(sources=results[product]['userSettingsAfterNpm'],
            installed=results[product]['npmInstalled']) for product in results})
        assert_pair('user npm lifecycle script effects', {product: results[product]['npmLifecycleCounts']
            for product in results})
        assert_pair('successful npm install stderr diagnostics', {product: results[product]['npmInstallDiagnostics']
            for product in results})
        assert_pair('successful npm install stdout', {product: results[product]['npmInstallStdout']
            for product in results})

        list_results = {}
        for product, item in products.items():
            list_results[product] = run_cli(product, args.pi, args.pisharp, item['cwd'], item['env'], ['list'])
            assert list_results[product]['exitCode'] == 0
            results[product]['userList'] = list_semantics(list_results[product])
        assert_pair('user list', {product: results[product]['userList'] for product in results})

        user_replays = {product: rpc_replay(product, args.pi, args.pisharp, item['root'], item['env'],
            args.output, 'user-npm') for product, item in products.items()}
        assert_pair('installed user resources through RPC/provider', user_replays)
        for product in results:
            results[product]['userRpc'] = user_replays[product]

        # Install a project-scoped Git fixture at v1 and verify its persisted scope.
        project_installs = {}
        for product, item in products.items():
            project_installs[product] = run_cli(product, args.pi, args.pisharp, item['cwd'], item['env'],
                ['install', git_source, '--local', '--approve'])
            assert project_installs[product]['exitCode'] == 0, project_installs[product]
            results[product]['projectGitInstallOutput'] = normalized_command(
                project_installs[product], item['root'], item['agent'])
            results[product]['projectSettingsAfterGit'] = package_sources(read_settings(item['cwd'] / '.pi/settings.json'))
            git_path = find_git_package(item['root'], 'managed-git-package')
            results[product]['gitHeadBeforeUpdate'] = git_head(git_path, item['env'])
            results[product]['gitLifecycleCountsAfterInstall'] = lifecycle_counts(item['markers'] / 'git',
                ['package', 'dependency'])
        assert_pair('project Git install settings and pinned starting revision', {product: dict(
            packages=results[product]['projectSettingsAfterGit'], head=results[product]['gitHeadBeforeUpdate'])
            for product in results})
        assert_pair('project Git lifecycle script effects', {product: results[product]['gitLifecycleCountsAfterInstall']
            for product in results})
        assert_pair('successful project Git install output', {product: results[product]['projectGitInstallOutput']
            for product in results})
        assert all(results[product]['gitHeadBeforeUpdate'] == git_fixture['v1_head'] for product in results)

        both_lists = {}
        for product, item in products.items():
            command_result = run_cli(product, args.pi, args.pisharp, item['cwd'], item['env'], ['list', '--approve'])
            assert command_result['exitCode'] == 0
            both_lists[product] = list_semantics(command_result)
        assert_pair('list both scopes', both_lists)
        for product in results:
            results[product]['bothScopesList'] = both_lists[product]

        # Update outage: status is paired and the configured source and cached checkout remain intact.
        stop_daemon(daemon)
        daemon = None
        failed_updates = {}
        for product, item in products.items():
            git_path = find_git_package(item['root'], 'managed-git-package')
            before_settings = setting_snapshot(item['agent'], item['cwd'])
            before_cache = file_tree_digest(git_path)
            before_content = content_tree_digest(git_path)
            failed_updates[product] = run_cli(product, args.pi, args.pisharp, item['cwd'], item['env'],
                ['update', '--extension', git_source, '--approve'])
            after_cache = file_tree_digest(git_path)
            after_content = content_tree_digest(git_path)
            assert failed_updates[product]['exitCode'] != 0, failed_updates[product]
            assert setting_snapshot(item['agent'], item['cwd']) == before_settings
            assert after_content == before_content, (before_content, after_content)
            results[product]['failedUpdate'] = normalized_command(failed_updates[product], item['root'], item['agent'])
            results[product]['gitHeadAfterFailedUpdate'] = git_head(git_path, item['env'])
            results[product]['gitContentAfterFailedUpdate'] = after_content
            results[product]['failedUpdateCacheChangedPaths'] = changed_tree_paths(before_cache, after_cache)
        assert_pair('failed update exit status', {product: results[product]['failedUpdate']['exitCode']
            for product in results})
        assert_pair('failed update preserves Git cache', {product: results[product]['gitHeadAfterFailedUpdate']
            for product in results})
        assert_pair('failed update preserves package content', {product: results[product]['gitContentAfterFailedUpdate']
            for product in results})
        assert all(results[product]['gitHeadAfterFailedUpdate'] == git_fixture['v1_head'] for product in results)

        daemon = register_daemon(start_git_daemon(git_fixture['repository'].parent.parent, port, products['pi']['env']))
        subprocess.run(['git', '--git-dir', str(git_fixture['repository']), 'update-ref', 'refs/heads/main',
            git_fixture['v2_head']], cwd=root, env=products['pi']['env'], check=True, capture_output=True, text=True)
        successful_updates = {}
        for product, item in products.items():
            successful_updates[product] = run_cli(product, args.pi, args.pisharp, item['cwd'], item['env'],
                ['update', '--extension', git_source, '--approve'])
            assert successful_updates[product]['exitCode'] == 0, successful_updates[product]
            results[product]['successfulGitUpdateOutput'] = normalized_command(
                successful_updates[product], item['root'], item['agent'])
            git_path = find_git_package(item['root'], 'managed-git-package')
            results[product]['gitHeadAfterUpdate'] = git_head(git_path, item['env'])
            results[product]['gitLifecycleCountsAfterUpdate'] = lifecycle_counts(item['markers'] / 'git',
                ['package', 'dependency'])
            assert results[product]['gitHeadAfterUpdate'] == git_fixture['v2_head']
        assert_pair('successful Git update', {product: dict(exitCode=successful_updates[product]['exitCode'],
            head=results[product]['gitHeadAfterUpdate']) for product in results})
        assert_pair('Git lifecycle script effects after update', {product: results[product]['gitLifecycleCountsAfterUpdate']
            for product in results})

        project_replays = {product: rpc_replay(product, args.pi, args.pisharp, item['root'], item['env'],
            args.output, 'project-git') for product, item in products.items()}
        assert_pair('installed project resources through RPC/provider', project_replays)
        for product in results:
            results[product]['projectRpc'] = project_replays[product]

        # Remove the project package, then user packages; the list must return to empty.
        project_removes, user_removes, npm_removes = {}, {}, {}
        for product, item in products.items():
            project_removes[product] = run_cli(product, args.pi, args.pisharp, item['cwd'], item['env'],
                ['remove', git_source, '--local', '--approve'])
            assert project_removes[product]['exitCode'] == 0
            assert package_sources(read_settings(item['cwd'] / '.pi/settings.json')) == []
            npm_removes[product] = []
            for source in reversed(npm_sources):
                removal = run_cli(product, args.pi, args.pisharp, item['cwd'], item['env'], ['remove', source])
                assert removal['exitCode'] == 0, removal
                npm_removes[product].append(normalized_command(removal, item['root'], item['agent']))
            assert package_sources(read_settings(item['agent'] / 'settings.json')) == []
            user_removes[product] = run_cli(product, args.pi, args.pisharp, item['cwd'], item['env'], ['list'])
            assert user_removes[product]['exitCode'] == 0
            results[product]['emptyList'] = list_semantics(user_removes[product])
            results[product]['npmRemoveOutputs'] = npm_removes[product]
            results[product]['projectGitRemove'] = command_outcome(project_removes[product], item['root'], item['agent'])
        assert_pair('npm uninstall command output', {product: results[product]['npmRemoveOutputs'] for product in results})
        assert_pair('project Git removal command output', {product: results[product]['projectGitRemove'] for product in results})
        assert_pair('remove and empty list', {product: results[product]['emptyList'] for product in results})
        assert all(results[product]['emptyList'] == ['No packages installed.'] for product in results)

        # A missing local package removal fails with paired status and keeps persisted state/cache.
        failed_removes = {}
        for product, item in products.items():
            before_settings = setting_snapshot(item['agent'], item['cwd'])
            before_roots = {str(path): file_tree_digest(path) for path in [item['agent'] / 'npm', item['agent'] / 'git',
                item['cwd'] / '.pi/packages']}
            failed_removes[product] = run_cli(product, args.pi, args.pisharp, item['cwd'], item['env'],
                ['remove', './never-installed-package'])
            assert failed_removes[product]['exitCode'] != 0
            assert setting_snapshot(item['agent'], item['cwd']) == before_settings
            assert {str(path): file_tree_digest(path) for path in [item['agent'] / 'npm', item['agent'] / 'git',
                item['cwd'] / '.pi/packages']} == before_roots
            results[product]['failedRemove'] = normalized_command(failed_removes[product], item['root'], item['agent'])
        assert_pair('failed remove exit status', {product: results[product]['failedRemove']['exitCode']
            for product in results})

        stop_daemon(daemon)
        daemon = None
    result = dict(piSha=pi_sha, piSharpBaseSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT,
        text=True).strip(), assemblySha256=hashlib.sha256(args.pisharp.read_bytes()).hexdigest(),
        scenario='real-package-cli-local-npm-and-git', match=True, results=results,
        cases=['failed local install preserves settings/cache and paired exit status',
            'user-scope npm tarball install/persistence/list/remove', 'normal RPC skill/prompt/extension resource input after npm CLI install',
            'project-scope loopback Git install/persistence/list/remove', 'failed Git update preserves settings/cache and paired exit status',
            'successful Git update moves unpinned checkout v1 to v2',
            'normal RPC skill/prompt/extension resource input after project Git CLI install',
            'empty list after all successful removals', 'failed removal preserves settings/cache and paired exit status'])
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print('Matched real Pi/PiSharp package CLI install/remove/update/list, scoped persistence, failure preservation, and RPC/provider resources.')


if __name__ == '__main__':
    main()
