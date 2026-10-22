#!/usr/bin/env python3
"""Run paired real CLI/PTY scenarios and compare complete terminal state."""
import argparse
import base64
import fcntl
import gzip
import io
import itertools
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from pi_reference import verify_pi_checkout

from compare import compare_products, osc52_clipboard_writes
from fixture_http import FixtureServer
from pty_process import RenderFrameSequence, RenderFrameStore, TerminalProcess
from thread_limits import apply_test_thread_limit, thread_limit_environment

apply_test_thread_limit()

ROOT = Path(__file__).resolve().parents[3]
WORKSPACE = Path('/tmp/pisharp-terminal-fixture-workspace')
AGENT_DIRECTORY = Path('/tmp/pisharp-terminal-fixture-agent')


def write_scenario_files(files, base):
    root = base.resolve()
    for name, content in files.items():
        path = (base / name).resolve()
        if not path.is_relative_to(root):
            raise ValueError(f'Fixture file leaves its owned directory: {name}')
        path.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(content, dict) and set(content) == {'$base64'} and isinstance(content['$base64'], str):
            path.write_bytes(base64.b64decode(content['$base64'], validate=True))
        else:
            text = content if isinstance(content, str) else json.dumps(content)
            path.write_text(text.replace('{workspace}', str(WORKSPACE)))


def prepare_workspace(scenario, workspace=None):
    workspace = WORKSPACE if workspace is None else workspace
    marker = workspace / '.pisharp-terminal-harness'
    if workspace.exists() and not marker.exists():
        raise RuntimeError(f'Refusing to reset unowned fixture directory: {workspace}')
    workspace.mkdir(exist_ok=True, mode=0o700)
    marker.touch()
    for entry in workspace.iterdir():
        if entry == marker:
            continue
        if entry.is_dir() and not entry.is_symlink():
            shutil.rmtree(entry)
        else:
            entry.unlink()

    workspace_root = workspace.resolve()
    write_scenario_files(scenario.get('projectFiles', {}), workspace)

    working_relative = scenario.get('workingDirectory', '.')
    working_directory = (workspace / working_relative).resolve()
    if not working_directory.is_relative_to(workspace_root):
        raise ValueError(f'Fixture working directory leaves its workspace: {working_relative}')
    working_directory.mkdir(parents=True, exist_ok=True)
    return working_directory


def commands(pi, pisharp, scenario, pisharp_extension=None):
    common = ['--provider', 'fixture', '--model', 'fixture-model', '--no-session', '--offline',
              '--no-extensions', '--no-skills', '--no-prompt-templates', '--no-tools', '--no-context-files']
    resolver = pi / 'packages/coding-agent/src/experimental/source-resolver.ts'
    pi_command = ['node', '--import', resolver.as_uri()]
    if scenario.get('mockHuggingFace'):
        pi_command.extend(['--import', (ROOT / 'tools/parity/terminal/huggingface-preload.mjs').as_uri()])
    pi_prefix = [*pi_command, str(pi / 'packages/coding-agent/src/cli.ts')]
    pisharp_prefix = ['dotnet', str(pisharp)]
    result = dict(pi=[*pi_prefix, *common],
                pisharp=[*pisharp_prefix, *common, '--no-approve'])
    for product, arguments in scenario.get('arguments', {}).items():
        if product not in result:
            raise ValueError(f'Unknown product in fixture arguments: {product}')
        prefix = pi_prefix if product == 'pi' else pisharp_prefix
        result[product] = [*prefix, *arguments]
    if scenario.get('pisharpExtension'):
        if pisharp_extension is None or not pisharp_extension.is_file():
            raise ValueError('This fixture requires an existing --pisharp-extension assembly')
        if not scenario.get('pisharpNativeExtensionPackage'):
            result['pisharp'].extend(['--extension', str(pisharp_extension.resolve())])
    terminal_prelude = scenario.get('terminalPrelude')
    if terminal_prelude is not None:
        if not isinstance(terminal_prelude, str):
            raise ValueError('terminalPrelude must be a string')
        for product, command in result.items():
            result[product] = ['sh', '-c', 'printf %s "$1"; shift; exec "$@"',
                               'pisharp-terminal-fixture', terminal_prelude, *command]
    return result


def environment(agent, *, persist_sessions=False):
    names = ['PATH', 'HOME', 'DOTNET_ROOT', 'LD_LIBRARY_PATH']
    result = {name: os.environ[name] for name in names if name in os.environ}
    result.update(TERM='xterm-256color', COLORTERM='truecolor', PI_TRUE_COLOR='1', LANG='C.UTF-8', LC_ALL='C.UTF-8',
                  PI_OFFLINE='1', PI_CODING_AGENT_DIR=str(agent), PISHARP_AGENT_DIR=str(agent),
                  DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1')
    if persist_sessions:
        result['PISHARP_SESSION_DIR'] = str(agent / 'sessions')
    result.update(thread_limit_environment())
    return result


def start_terminal_process(command, cwd, child_environment, options):
    """Start a runner-owned PTY with compressed render spooling enabled."""
    return TerminalProcess(command, cwd, child_environment, options, spool_render_frames=True)


def expand_environment(values, agent, router_url):
    return {name: value.replace('{router}', router_url).replace('{agent}', str(agent))
            if isinstance(value, str) else value
            for name, value in values.items()}


def record_clipboard_checkpoint(action, raw):
    writes = osc52_clipboard_writes(raw)
    if action.get('expectClipboardWrite') and not writes:
        raise RuntimeError(f"Action {action['id']} expected an OSC 52 clipboard write but captured none")
    expected_texts = action.get('expectClipboardTexts')
    if expected_texts is not None and [write['text'] for write in writes] != expected_texts:
        raise RuntimeError(f"Action {action['id']} clipboard text was "
                           f"{[write['text'] for write in writes]!r}, expected {expected_texts!r}")
    return dict(id=action['id'], writes=writes)


def contains_render_frame_sequence(value):
    if isinstance(value, RenderFrameSequence):
        return True
    if isinstance(value, dict):
        return any(contains_render_frame_sequence(item) for item in value.values())
    if isinstance(value, (list, tuple)):
        return any(contains_render_frame_sequence(item) for item in value)
    return False


def write_json_stream(stream, value):
    """Write JSON normally, streaming only the large render sequences from disk."""
    if isinstance(value, RenderFrameSequence):
        stream.write('[')
        for index, frame in enumerate(value):
            if index:
                stream.write(',')
            stream.write(json.dumps(frame, ensure_ascii=False, separators=(',', ':')))
        stream.write(']')
    elif not contains_render_frame_sequence(value):
        json.dump(value, stream, ensure_ascii=False, separators=(',', ':'))
    elif isinstance(value, dict):
        stream.write('{')
        for index, (key, item) in enumerate(value.items()):
            if index:
                stream.write(',')
            json.dump(key, stream, ensure_ascii=False, separators=(',', ':'))
            stream.write(':')
            write_json_stream(stream, item)
        stream.write('}')
    else:
        stream.write('[')
        for index, item in enumerate(value):
            if index:
                stream.write(',')
            write_json_stream(stream, item)
        stream.write(']')


def working_indicator_summary(render_states):
    count = 0
    premature_usage = []
    for state in render_states:
        if state.get('activeScreen') != 'alternate':
            continue
        lines = state.get('buffers', {}).get('alternate', {}).get('lines', [])
        status_row = state.get('rows', 0) - 5
        footer_row = state.get('rows', 0) - 1
        if status_row < 0 or footer_row < 0 or len(lines) <= max(status_row, footer_row):
            continue
        status = ''.join(cell.get('chars', '') if cell.get('width', 1) else ''
                         for cell in lines[status_row].get('cells', [])).rstrip()
        footer = ''.join(cell.get('chars', '') if cell.get('width', 1) else ''
                         for cell in lines[footer_row].get('cells', [])).rstrip()
        if 'Working' not in status:
            continue
        count += 1
        if '↑10 ↓3' in footer and len(premature_usage) < 3:
            premature_usage.append(footer)
    return dict(workingRenderFrames=count, prematureUsage=premature_usage)


def write_assertion_evidence(path, products, dimensions, theme, mode, frame_ids=(),
                            include_working_indicator=False):
    """Write named checkpoints and request traces without the large render/raw streams."""
    requested_frames = set(frame_ids)
    evidence = dict(dimensions=dimensions, theme=theme, mode=mode,
                    assertionFrames=sorted(requested_frames))
    for name, product in products.items():
        frames = list(product.get('assertionFrames', []))
        evidence[name] = dict(frames=frames, http=product['http'], scenarioError=product['scenarioError'])
        if include_working_indicator:
            evidence[name]['workingIndicator'] = working_indicator_summary(product['renders'])

    with path.open('wb') as output:
        compressed = gzip.GzipFile(fileobj=output, mode='wb', compresslevel=1, mtime=0)
        writer = io.TextIOWrapper(compressed, encoding='utf-8')
        try:
            json.dump(evidence, writer, ensure_ascii=False, separators=(',', ':'))
            writer.flush()
        finally:
            writer.detach()
            compressed.close()


def stage_native_extension_package(agent, scenario, extension):
    package = scenario.get('pisharpNativeExtensionPackage')
    if not isinstance(package, dict):
        raise ValueError('Native extension package fixtures require pisharpNativeExtensionPackage metadata')
    package_id = package.get('id')
    version = package.get('version')
    entry_type = package.get('type')
    if not all(isinstance(value, str) and value.strip() for value in (package_id, version, entry_type)):
        raise ValueError('pisharpNativeExtensionPackage requires non-empty id, version, and type values')
    if (len(package_id) > 128 or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]*', package_id) or
            '..' in package_id):
        raise ValueError(f'Invalid native extension package id: {package_id!r}')
    version_pattern = r'\d+(?:\.\d+){1,3}(?:-[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*)?(?:\+[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*)?'
    if len(version) > 128 or not re.fullmatch(version_pattern, version):
        raise ValueError(f'Invalid native extension package version: {version!r}')
    if (len(entry_type) > 1024 or '.' not in entry_type or '/' in entry_type or '\\' in entry_type or
            any(character.isspace() or ord(character) < 32 for character in entry_type)):
        raise ValueError(f'Invalid native extension entry type: {entry_type!r}')
    deps = extension.with_suffix('.deps.json')
    if not deps.is_file():
        raise ValueError(f'Native extension assembly has no adjacent dependency manifest: {deps}')

    package_root = agent / 'native-extensions' / package_id.lower() / version
    assembly_directory = package_root / 'lib/net10.0'
    assembly_directory.mkdir(parents=True, exist_ok=True)
    shutil.copy2(extension, assembly_directory / extension.name)
    shutil.copy2(deps, assembly_directory / deps.name)
    manifest = {
        'id': package_id,
        'version': version,
        'apiVersion': 1,
        'extensions': [{
            'assembly': f'lib/net10.0/{extension.name}',
            'type': entry_type,
        }],
    }
    (package_root / 'pisharp-extension.json').write_text(json.dumps(manifest))
    archive_path = agent / f'{package_id}-{version}.nupkg'
    with zipfile.ZipFile(archive_path, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
        for path in package_root.rglob('*'):
            if path.is_file():
                archive.write(path, path.relative_to(package_root).as_posix())

    settings_path = agent / 'settings.json'
    settings = json.loads(settings_path.read_text())
    configured = settings.setdefault('nativeExtensions', [])
    configured.append({'id': package_id, 'version': version, 'source': str(archive_path.resolve())})
    settings_path.write_text(json.dumps(settings))


def run_product(label, command, scenario, dimensions, theme, mode, server, pisharp_extension=None):
    agent = AGENT_DIRECTORY
    marker = agent / '.pisharp-terminal-harness'
    if agent.exists() and not marker.exists():
        raise RuntimeError(f'Refusing to reset unowned fixture configuration: {agent}')
    agent.mkdir(exist_ok=True, mode=0o700)
    marker.touch()
    for path in agent.iterdir():
        if path == marker:
            continue
        if path.is_dir() and not path.is_symlink():
            shutil.rmtree(path)
        else:
            path.unlink()
    bin_directory = agent / 'bin'
    bin_directory.mkdir()
    fd = bin_directory / 'fd'
    fd.write_text('#!/bin/sh\nprintf "fd 10.2.0\\n"\n')
    fd.chmod(0o700)
    settings = dict(scenario.get('settings', {}), theme=theme)
    if label == 'pi' or mode != 'fullscreen':
        settings['tuiMode'] = mode
    (agent / 'settings.json').write_text(json.dumps(settings))
    model = dict(id='fixture-model', name='Fixture Model', api='openai-completions',
                 reasoning=bool(scenario.get('modelReasoning', False)),
                 input=scenario.get('modelInput', ['text']),
                 contextWindow=scenario.get('modelContextWindow', 8192), maxTokens=1024,
                 cost=dict(input=0, output=0, cacheRead=0, cacheWrite=0))
    (agent / 'models.json').write_text(json.dumps(dict(providers=dict(fixture=dict(baseUrl=server.url + '/v1',
                                                                                 apiKey='fixture-key', api='openai-completions', models=[model])))))
    write_scenario_files(scenario.get('files', {}).get(label, {}), agent)
    if label == 'pisharp' and scenario.get('pisharpExtension') and scenario.get('pisharpNativeExtensionPackage'):
        if pisharp_extension is None or not pisharp_extension.is_file():
            raise ValueError('This fixture requires an existing --pisharp-extension assembly')
        stage_native_extension_package(agent, scenario, pisharp_extension.resolve())
    options = dict(dimensions, foreground='#dddddd' if theme == 'dark' else '#222222',
                   background='#111111' if theme == 'dark' else '#ffffff')
    options.update(scenario.get('terminal', {}))
    command = [argument.replace('{agent}', str(agent)) for argument in command]
    child_environment = environment(agent, persist_sessions='--no-session' not in command)
    child_environment['PATH'] = str(bin_directory) + os.pathsep + child_environment.get('PATH', '')
    child_environment.update(expand_environment(
        scenario.get('environment', {}).get(label, {}), agent, server.url))
    working_directory = prepare_workspace(scenario)
    terminal = start_terminal_process(command, working_directory, child_environment, options)
    frames = RenderFrameStore()
    assertion_frames = RenderFrameStore()
    assertion_frame_ids = set(scenario.get('assertionFrames', []))

    def record_frame(frame):
        frames.append(frame)
        if frame.get('id') in assertion_frame_ids:
            assertion_frames.append(frame)

    http_by_frame = []
    clipboard_by_frame = []
    prior_raw = bytearray()
    prior_render_frames = []
    request_start = len(server.requests)
    try:
        for action in scenario['actions']:
            request_mark = len(server.requests)
            cursor_before = terminal.snapshot().get('cursor') if action.get('expectCursorChange') else None
            mark, control_mark = len(terminal.raw), len(terminal.trace.events)
            wait_gates = action.get('waitGates', [])
            if isinstance(wait_gates, str):
                wait_gates = [wait_gates]
            for gate in wait_gates:
                if not server.wait_for_gate(gate, timeout=action.get('gateTimeout', 20)):
                    raise TimeoutError(f'Fixture gate was not signaled: {gate}')
            release_gates = action.get('releaseGates', [])
            if isinstance(release_gates, str):
                release_gates = [release_gates]
            for gate in release_gates:
                server.release_gate(gate)
            for scope, base in (('workspace', WORKSPACE), ('agent', agent)):
                write_scenario_files(action.get('writeFiles', {}).get(scope, {}), base)
            if action.get('restart'):
                prior_raw.extend(terminal.raw)
                prior_render_frames.append(terminal.render_frames)
                terminal.close()
                restart_environment = dict(child_environment)
                restart_environment.update(expand_environment(
                    action.get('restartEnvironment', {}), agent, server.url))
                working_directory = prepare_workspace(scenario)
                terminal = start_terminal_process(command, working_directory, restart_environment, options)
                mark = 0
                control_mark = len(terminal.trace.events)
                frame = terminal.settle(action.get('expect'), timeout=action.get('timeout', 20),
                                        render_match=action.get('renderMatch', False))
                record_frame(dict(id=action['id'], state=frame, controls=terminal.trace.events[control_mark:],
                                  controlPending=terminal.trace.pending.hex()))
                http_by_frame.append(dict(id=action['id'], requests=server.requests[request_mark:]))
                clipboard_by_frame.append(record_clipboard_checkpoint(action, terminal.raw[mark:]))
                continue
            mark, control_mark = len(terminal.raw), len(terminal.trace.events)
            if 'send' in action:
                value = action['send']
                if isinstance(value, str):
                    value = value.replace('{router}', server.url).replace('{agent}', str(agent))
                terminal.send(value)
            if 'resize' in action:
                terminal.resize(**action['resize'])
            wait_gates_after = action.get('waitGatesAfter', [])
            if isinstance(wait_gates_after, str):
                wait_gates_after = [wait_gates_after]
            for gate in wait_gates_after:
                if not server.wait_for_gate(gate, timeout=action.get('gateTimeout', 20)):
                    raise TimeoutError(f'Fixture gate was not signaled after action: {gate}')
            poll_model = action.get('waitForModelPoll')
            if poll_model and not server.wait_for_model_poll(poll_model, timeout=action.get('gateTimeout', 20)):
                raise TimeoutError(f'Fixture model poll did not reach its gate: {poll_model}')
            if action.get('exit'):
                deadline = time.monotonic() + 10
                while not terminal.closed and time.monotonic() < deadline:
                    terminal.pump()
                if not terminal.closed:
                    raise TimeoutError('CLI did not exit')
                frame = terminal.snapshot()
                terminal.process.wait(timeout=3)
                frame['exitCode'] = terminal.process.returncode
            else:
                expect = action.get('expect')
                if isinstance(expect, dict):
                    expect = expect[label]
                frame = terminal.settle(expect, timeout=action.get('timeout', 20),
                                        quiet_ms=action.get('quietMs', 200),
                                        absent=action.get('notExpect'),
                                        after=0 if 'resize' in action or action.get('allowStaticFrame') else mark,
                                        render_match=action.get('renderMatch', False),
                                        allow_static_frame=action.get('allowStaticFrame', False),
                                        cursor_before=cursor_before,
                                        clipboard_texts=action.get('expectClipboardTexts'), clipboard_after=mark)
            expected_model_status = action.get('expectModelStatus')
            if expected_model_status:
                model = next((value for value in server.models
                              if value.get('id') == expected_model_status['id']), None)
                actual_status = model.get('status', {}).get('value') if model else None
                if actual_status != expected_model_status['value']:
                    raise RuntimeError(f"Fixture model {expected_model_status['id']} status was {actual_status!r}, "
                                       f"expected {expected_model_status['value']!r}.")
            record_frame(dict(id=action['id'], state=frame, controls=terminal.trace.events[control_mark:],
                              controlPending=terminal.trace.pending.hex()))
            http_by_frame.append(dict(id=action['id'], requests=server.requests[request_mark:]))
            clipboard_by_frame.append(record_clipboard_checkpoint(action, terminal.raw[mark:]))
        prior_raw.extend(terminal.raw)
        return dict(frames=frames, assertionFrames=assertion_frames,
                    renders=RenderFrameSequence(*prior_render_frames, terminal.render_frames),
                    httpByFrame=http_by_frame, clipboardByFrame=clipboard_by_frame,
                    raw=base64.b64encode(prior_raw).decode(),
                    http=server.requests[request_start:], command=command, scenarioError=None)
    except (TimeoutError, RuntimeError) as error:
        record_frame(dict(id=action['id'], state=terminal.snapshot(), controls=terminal.trace.events[control_mark:],
                          controlPending=terminal.trace.pending.hex()))
        http_by_frame.append(dict(id=action['id'], requests=server.requests[request_mark:]))
        combined_raw = prior_raw + terminal.raw
        return dict(frames=frames, assertionFrames=assertion_frames,
                    renders=RenderFrameSequence(*prior_render_frames, terminal.render_frames),
                    httpByFrame=http_by_frame, clipboardByFrame=clipboard_by_frame,
                    raw=base64.b64encode(combined_raw).decode(),
                    http=server.requests[request_start:], command=command, scenarioError=str(error))
    finally:
        terminal.close()


def main():
    global WORKSPACE, AGENT_DIRECTORY
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--pisharp', type=Path, default=ROOT / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll')
    parser.add_argument('--pisharp-extension', type=Path,
                        help='Native extension assembly required by a fixture')
    parser.add_argument('--fixture', type=Path, default=Path(__file__).with_name('fixtures') / 'startup-editor.json')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--calibrate', action='store_true', help='Compare two independent Pi runs before trusting cross-product evidence')
    parser.add_argument('--fixture-root', type=Path,
                        help='Isolate owned workspace and agent directories beneath this root')
    parser.add_argument('--case', help='Run just the named dimension/theme/mode case')
    args = parser.parse_args()
    if args.fixture_root is not None:
        root = args.fixture_root.resolve()
        root.mkdir(parents=True, exist_ok=True)
        WORKSPACE, AGENT_DIRECTORY = root / 'workspace', root / 'agent'
    verify_pi_checkout(args.pi)
    scenario = json.loads(args.fixture.read_text())
    args.output.mkdir(parents=True, exist_ok=True)
    product_commands = commands(args.pi.resolve(), args.pisharp.resolve(), scenario, args.pisharp_extension)
    report = dict(piSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=args.pi, text=True).strip(),
                  pisharpSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
                  fixture=scenario['id'], calibration=args.calibrate,
                  concurrentHttpGroups=scenario.get('concurrentHttpGroups', []),
                  normalization=('Named checkpoints and render streams are compared exactly. Render streams align identical '
                                 'snapshots in order and report every changed or unmatched frame, collapsing adjacent identical '
                                 'states, hidden cursor coordinates, transient loader glyphs, and in-flight '
                                 'Working-indicator frames. Empty cells with the same width and style use one canonical glyph so '
                                 'xterm backends that serialize a blank as an empty string or a space compare visually; colors, '
                                 'attributes, width, cursor, and modes remain exact. Same-Pi calibration shows fast scripted responses can finish before '
                                 'that animated frame is captured, making its presence and partial usage footer PTY-scheduling dependent. '
                                 'Raw bytes remain captured and exact-match status is reported separately. '
                                 'HTTP identity/count/order remain exact except within declared concurrent batches, '
                                 'where arrival order is unspecified. Only generated documentation package-root lines in '
                                 'system messages are canonicalized in HTTP comparisons. Per-checkpoint control buckets are diagnostic.'),
                  cases=[])
    cwd = WORKSPACE
    owner = cwd / '.pisharp-terminal-harness'
    if cwd.exists() and not owner.exists():
        raise RuntimeError(f'Refusing to use unowned fixture directory: {cwd}')
    cwd.mkdir(exist_ok=True, mode=0o700)
    owner.touch()
    lock = owner.open('r+')
    fcntl.flock(lock, fcntl.LOCK_EX)
    fixture_behavior = dict(scenario.get('fixtureBehavior', {}))
    fixture_behavior.update(scenario.get('llamaBehavior', {}))
    server = FixtureServer(scenario.get('llamaModels'), scenario.get('llamaProps'),
                           fixture_behavior, scenario.get('huggingFace'))
    try:
        for dimensions, theme, mode in itertools.product(scenario['dimensions'], scenario['themes'], scenario['modes']):
            case_id = f"{dimensions['columns']}x{dimensions['rows']}-{theme}-{mode}"
            if args.case and args.case != case_id:
                continue
            products = {}
            for label, command in product_commands.items():
                server.reset()
                products[label] = run_product('pi' if args.calibrate else label, product_commands['pi'] if args.calibrate else command,
                                              scenario, dimensions, theme, mode, server, args.pisharp_extension)
            comparison = compare_products(products['pi'], products['pisharp'],
                                          scenario.get('concurrentHttpGroups', []))
            delta = comparison['differences']
            errors = {label: product['scenarioError'] for label, product in products.items() if product['scenarioError']}
            if errors:
                delta.insert(0, dict(path='$.scenarioError', **errors))
            difference_count = comparison['differenceCount'] + int(bool(errors))
            artifact = case_id + '.json.gz'
            assertion_artifact = case_id + '.assertions.json.gz'
            artifact_path = args.output / artifact
            assertion_artifact_path = args.output / assertion_artifact
            render_artifacts = {}
            frame_artifacts = {}
            evidence_products = {}
            for name, product in products.items():
                frame_artifact = case_id + f'.{name}.frames.jsonl.gz'
                frame_path = args.output / frame_artifact
                with frame_path.open('wb') as frame_output:
                    product['frames'].write_compressed_to(frame_output)
                frame_artifacts[name] = frame_artifact
                render_artifact = case_id + f'.{name}.renders.jsonl.gz'
                render_path = args.output / render_artifact
                with render_path.open('wb') as render_output:
                    product['renders'].write_compressed_to(render_output)
                render_artifacts[name] = render_artifact
                evidence_products[name] = {
                    **{key: value for key, value in product.items()
                       if key not in ('frames', 'assertionFrames', 'renders')},
                    'frames': dict(format='gzip-jsonl', path=frame_artifact,
                                   frameCount=len(product['frames'])),
                    'renders': dict(format='gzip-jsonl', path=render_artifact,
                                    frameCount=len(product['renders'])),
                }
            evidence = dict(dimensions=dimensions, theme=theme, mode=mode, **evidence_products)
            print(f'{case_id}: captured {comparison["comparedRenderFrameCount"]} canonical render frames; '
                  'streaming full evidence', flush=True)
            try:
                with artifact_path.open('wb') as output:
                    compressed = gzip.GzipFile(fileobj=output, mode='wb', compresslevel=1, mtime=0)
                    writer = io.TextIOWrapper(compressed, encoding='utf-8')
                    write_json_stream(writer, evidence)
                    writer.flush()
                    writer.detach()
                    compressed.close()
                write_assertion_evidence(assertion_artifact_path, products, dimensions, theme, mode,
                                         frame_ids=scenario.get('assertionFrames', []),
                                         include_working_indicator=scenario['id'] == 'project-trust-extension-ui')
            finally:
                for product in products.values():
                    product['frames'].close()
                    product['assertionFrames'].close()
                    product['renders'].close()
            report['cases'].append(dict(id=case_id, match=difference_count == 0, differences=difference_count,
                                        firstDifferences=delta[:50], scenarioErrors=errors,
                                        terminalMatch=comparison['terminalMatch'], rawMatch=comparison['rawMatch'],
                                        clipboardMatch=comparison['clipboardMatch'],
                                        clipboardDifferences=comparison['clipboardDifferences'],
                                        rawDifference=comparison['rawDifference'],
                                        comparedRenderFrameCount=comparison['comparedRenderFrameCount'],
                                        renderDifferences=comparison['renderDifferenceCount'],
                                        renderAlignment=comparison['renderAlignment'],
                                        httpMatch=comparison['httpMatch'],
                                        rawHttpMatch=comparison['rawHttpMatch'],
                                        rawHttpDifferenceCount=comparison['rawHttpDifferenceCount'],
                                        rawHttpFirstDifferences=comparison['rawHttpFirstDifferences'],
                                        httpNormalization=comparison['httpNormalization'],
                                        controlBoundaryDifferenceCount=comparison['controlBoundaryDifferenceCount'],
                                        controlBoundaryFirstDifferences=comparison['controlBoundaryFirstDifferences'],
                                        evidence=artifact, assertionEvidence=assertion_artifact,
                                        frameEvidence=frame_artifacts, renderEvidence=render_artifacts))
            print(f'{case_id}: {len(delta)} differences', flush=True)
    finally:
        server.close()
        lock.close()
    if not report['cases']:
        raise ValueError('No terminal cases selected')
    report['match'] = all(case['match'] for case in report['cases'])
    (args.output / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
    return 0 if report['match'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
