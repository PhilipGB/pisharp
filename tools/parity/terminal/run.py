#!/usr/bin/env python3
"""Run paired real CLI/PTY scenarios and compare complete terminal state."""
import argparse
import base64
import fcntl
import gzip
import itertools
import json
import os
from pathlib import Path
import subprocess
import shutil
import time

from compare import differences
from fixture_http import FixtureServer
from pty_process import TerminalProcess

ROOT = Path(__file__).resolve().parents[3]


def commands(pi, pisharp, scenario):
    common = ['--provider', 'fixture', '--model', 'fixture-model', '--no-session', '--offline',
              '--no-extensions', '--no-skills', '--no-prompt-templates', '--no-tools']
    resolver = pi / 'packages/coding-agent/src/experimental/source-resolver.ts'
    result = dict(pi=['node', '--import', resolver.as_uri(), str(pi / 'packages/coding-agent/src/experimental/cli.ts'), *common],
                pisharp=['dotnet', str(pisharp), *common, '--no-approve', '--no-context-files'])
    for product, arguments in scenario.get('arguments', {}).items():
        result[product] = result[product][:-len(common) - (2 if product == 'pisharp' else 0)] + arguments
    return result


def environment(agent):
    names = ['PATH', 'HOME', 'DOTNET_ROOT', 'LD_LIBRARY_PATH']
    result = {name: os.environ[name] for name in names if name in os.environ}
    result.update(TERM='xterm-256color', COLORTERM='truecolor', PI_TRUE_COLOR='1', LANG='C.UTF-8', LC_ALL='C.UTF-8',
                  PI_OFFLINE='1', PI_CODING_AGENT_DIR=str(agent), PISHARP_AGENT_DIR=str(agent),
                  DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1')
    return result


def run_product(label, command, scenario, dimensions, theme, mode, server):
    agent = Path('/tmp/pisharp-terminal-fixture-agent')
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
    settings = dict(scenario.get('settings', {}), theme=theme)
    if label == 'pi' or mode != 'fullscreen':
        settings['tuiMode'] = mode
    (agent / 'settings.json').write_text(json.dumps(settings))
    model = dict(id='fixture-model', name='Fixture Model', api='openai-completions', reasoning=False,
                 input=['text'], contextWindow=8192, maxTokens=1024,
                 cost=dict(input=0, output=0, cacheRead=0, cacheWrite=0))
    (agent / 'models.json').write_text(json.dumps(dict(providers=dict(fixture=dict(baseUrl=server.url + '/v1',
                                                                                 apiKey='fixture-key', api='openai-completions', models=[model])))))
    for name, content in scenario.get('files', {}).get(label, {}).items():
        path = (agent / name).resolve()
        if not path.is_relative_to(agent.resolve()):
            raise ValueError(f'Fixture file leaves its agent directory: {name}')
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content if isinstance(content, str) else json.dumps(content))
    options = dict(dimensions, foreground='#dddddd' if theme == 'dark' else '#222222',
                   background='#111111' if theme == 'dark' else '#ffffff')
    options.update(scenario.get('terminal', {}))
    command = [argument.replace('{agent}', str(agent)) for argument in command]
    child_environment = environment(agent)
    child_environment.update(scenario.get('environment', {}).get(label, {}))
    terminal = TerminalProcess(command, Path('/tmp/pisharp-terminal-fixture-workspace'), child_environment, options)
    frames = []
    request_start = len(server.requests)
    try:
        for action in scenario['actions']:
            mark, control_mark = len(terminal.raw), len(terminal.trace.events)
            if 'send' in action:
                terminal.send(action['send'])
            if 'resize' in action:
                terminal.resize(**action['resize'])
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
                frame = terminal.settle(expect, timeout=action.get('timeout', 20), after=0 if 'resize' in action else mark)
            frames.append(dict(id=action['id'], state=frame, controls=terminal.trace.events[control_mark:], controlPending=terminal.trace.pending.hex()))
        return dict(frames=frames, raw=base64.b64encode(terminal.raw).decode(),
                    http=server.requests[request_start:], command=command, scenarioError=None)
    except (TimeoutError, RuntimeError) as error:
        frames.append(dict(id=action['id'], state=terminal.snapshot(), controls=terminal.trace.events[control_mark:], controlPending=terminal.trace.pending.hex()))
        return dict(frames=frames, raw=base64.b64encode(terminal.raw).decode(),
                    http=server.requests[request_start:], command=command, scenarioError=str(error))
    finally:
        terminal.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--pisharp', type=Path, default=ROOT / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll')
    parser.add_argument('--fixture', type=Path, default=Path(__file__).with_name('fixtures') / 'startup-editor.json')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--calibrate', action='store_true', help='Compare two independent Pi runs before trusting cross-product evidence')
    parser.add_argument('--case', help='Run just the named dimension/theme/mode case')
    args = parser.parse_args()
    scenario = json.loads(args.fixture.read_text())
    args.output.mkdir(parents=True, exist_ok=True)
    product_commands = commands(args.pi.resolve(), args.pisharp.resolve(), scenario)
    report = dict(piSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=args.pi, text=True).strip(),
                  pisharpSha=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
                  fixture=scenario['id'], calibration=args.calibrate, normalization='None. Full cells, styles, spacing, cursor, modes, buffers and control sequences remain exact.', cases=[])
    cwd = Path('/tmp/pisharp-terminal-fixture-workspace')
    owner = cwd / '.pisharp-terminal-harness'
    if cwd.exists() and not owner.exists():
        raise RuntimeError(f'Refusing to use unowned fixture directory: {cwd}')
    cwd.mkdir(exist_ok=True, mode=0o700)
    owner.touch()
    lock = owner.open('r+')
    fcntl.flock(lock, fcntl.LOCK_EX)
    server = FixtureServer()
    try:
        for dimensions, theme, mode in itertools.product(scenario['dimensions'], scenario['themes'], scenario['modes']):
            case_id = f"{dimensions['columns']}x{dimensions['rows']}-{theme}-{mode}"
            if args.case and args.case != case_id:
                continue
            products = {}
            for label, command in product_commands.items():
                products[label] = run_product('pi' if args.calibrate else label, product_commands['pi'] if args.calibrate else command,
                                              scenario, dimensions, theme, mode, server)
            delta = list(differences(products['pi']['frames'], products['pisharp']['frames']))
            errors = {label: product['scenarioError'] for label, product in products.items() if product['scenarioError']}
            if errors:
                delta.insert(0, dict(path='$.scenarioError', **errors))
            evidence = dict(dimensions=dimensions, theme=theme, mode=mode, **products)
            artifact = case_id + '.json.gz'
            (args.output / artifact).write_bytes(gzip.compress(json.dumps(evidence, ensure_ascii=False).encode(), mtime=0))
            report['cases'].append(dict(id=case_id, match=not delta, differences=len(delta),
                                        firstDifferences=delta[:50], scenarioErrors=errors, evidence=artifact))
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
