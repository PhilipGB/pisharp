#!/usr/bin/env python3
"""Replay a deleted skill after RPC startup and compare provider input/error ordering."""
import argparse
import hashlib
from http.server import ThreadingHTTPServer
import importlib.util
import json
import os
from pathlib import Path
import queue
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


def replay(product, pi, native, root, output, scenario):
    agent = root / product / 'agent'
    home = root / product / 'home'
    skill = root / product / 'guide/SKILL.md'
    agent.mkdir(parents=True)
    home.mkdir()
    skill.parent.mkdir()
    description = '!!int word' if scenario == 'invalid-tag' else 'Guide'
    skill.write_text(f'---\nname: guide\ndescription: {description}\n---\nbody')
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
               PISHARP_FIXTURE_KEY='fixture-only-key', PI_OFFLINE='1', TERM='xterm-256color',
               DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1', **thread_limit_environment())
    common = ['--mode', 'rpc', '--provider', 'fixture', '--model', 'fixture-model', '--offline', '--no-session',
              '--no-extensions', '--no-skills', '--no-prompt-templates', '--no-tools', '--skill', str(skill)]
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
                    assert record is not None, 'RPC ended before the checkpoint'
                    records.append(record)
                    if predicate(record):
                        return record

            send(dict(id='commands', type='get_commands'))
            commands = until(lambda r: r.get('id') == 'commands')
            command_info = next((c for c in commands['data']['commands'] if c['name'] == 'skill:guide'), None)
            if scenario == 'malformed-flow':
                assert command_info is None, commands
            else:
                assert command_info and command_info['description'] == ('word' if scenario == 'invalid-tag' else 'Guide')
            if scenario == 'deleted-skill':
                skill.unlink()
            send(dict(id='prompt', type='prompt', message='/skill:guide args'))
            until(lambda r: r.get('type') == 'agent_settled')
            response_index = next(i for i, r in enumerate(records) if r.get('id') == 'prompt')
            assert records[response_index]['success'], records
            failure = None
            if scenario == 'deleted-skill':
                error_index = next(i for i, r in enumerate(records) if r.get('type') == 'extension_error')
                assert error_index < response_index, records
                failure = records[error_index]
                assert failure['event'] == 'skill_expansion' and failure['extensionPath'] == str(skill), failure
            else:
                assert not any(r.get('type') == 'extension_error' for r in records), records
            assert len(server.requests) == 1
            users = [m['content'] for m in server.requests[0]['messages'] if m['role'] == 'user']
            content = users[-1]
            text = content if isinstance(content, str) else ''.join(p.get('text', '') for p in content if p.get('type') == 'text')
            expected = '/skill:guide args' if failure or scenario == 'malformed-flow' else (
                f'<skill name="guide" location="{skill}">\nReferences are relative to {skill.parent}.\n\nbody\n</skill>\n\nargs')
            assert text == expected, text
            process.stdin.close()
            assert process.wait(timeout=10) == 0
            reader.join(timeout=5)
            return dict(errorBeforeResponse=True if failure else None, event=failure['event'] if failure else None,
                        error=failure['error'].replace(str(root / product), '<workspace>') if failure else None,
                        providerInput=text.replace(str(root / product), '<workspace>'), providerRequests=1,
                        promptAccepted=True, commandDescription=command_info['description'] if command_info else None)
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
    parser.add_argument('--scenario', choices=['deleted-skill', 'invalid-tag', 'malformed-flow'], default='deleted-skill')
    args = parser.parse_args()
    sha = verify_pi_checkout(args.pi)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='pisharp-resource-invocation-process-') as directory:
        results = {product: replay(product, args.pi.resolve(), args.pisharp.resolve(), Path(directory), args.output, args.scenario)
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
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    assert result['match'], result
    print(f'Matched {args.scenario} RPC/provider input, metadata and error boundary.')


if __name__ == '__main__':
    main()
