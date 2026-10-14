#!/usr/bin/env python3
"""Compare immediate post-settlement prompts through real, persisted Pi/Native RPC CLIs."""
import argparse
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
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


class Provider(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        self.server.requests.append(request)
        record = dict(id='fixture', object='chat.completion.chunk', created=1, model='fixture-model',
                      choices=[dict(index=0, delta=dict(role='assistant', content='SETTLED_REPLY'), finish_reason=None)])
        finish = dict(record, choices=[dict(index=0, delta={}, finish_reason='stop')])
        body = ('data: ' + json.dumps(record) + '\n\ndata: ' + json.dumps(finish) + '\n\ndata: [DONE]\n\n').encode()
        self.send_response(200)
        self.send_header('Content-Type', 'text/event-stream')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def replay(product, pi, native, work, output, turns):
    agent = work / product / 'agent'
    sessions = work / product / 'sessions'
    agent.mkdir(parents=True)
    server = ThreadingHTTPServer(('127.0.0.1', 0), Provider)
    server.requests = []
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    provider = dict(baseUrl=f'http://127.0.0.1:{server.server_port}/v1',
                    models=[dict(id='fixture-model', name='fixture-model', api='openai-completions',
                                 reasoning=False, input=['text'], contextWindow=4096, maxTokens=128,
                                 cost=dict(input=0, output=0, cacheRead=0, cacheWrite=0))])
    provider.update(dict(apiKey='fixture-only-key') if product == 'pi' else dict(apiKeyEnv='PISHARP_FIXTURE_KEY'))
    (agent / 'models.json').write_text(json.dumps(dict(providers=dict(fixture=provider))))
    env = {key: os.environ[key] for key in ['PATH', 'DOTNET_ROOT', 'LD_LIBRARY_PATH'] if key in os.environ}
    env.update(HOME=str(agent), PI_CODING_AGENT_DIR=str(agent), PISHARP_AGENT_DIR=str(agent),
               PISHARP_FIXTURE_KEY='fixture-only-key', PI_OFFLINE='1', TERM='xterm-256color',
               DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_NOLOGO='1', **thread_limit_environment())
    common = ['--mode', 'rpc', '--provider', 'fixture', '--model', 'fixture-model', '--offline',
              '--session-dir', str(sessions), '--no-extensions', '--no-skills', '--no-prompt-templates', '--no-tools']
    command = (['node', '--import', (pi / 'packages/coding-agent/src/experimental/source-resolver.ts').as_uri(),
                str(pi / 'packages/coding-agent/src/cli.ts')] if product == 'pi' else ['dotnet', str(native)]) + common
    records = []
    received = queue.Queue()
    errors = output.with_name(output.stem + f'-{product}.stderr.log')
    process = None
    try:
        with errors.open('w') as stderr:
            process = subprocess.Popen(command, cwd=work, env=env, stdin=subprocess.PIPE,
                                       stdout=subprocess.PIPE, stderr=stderr, text=True)

            def read_lines():
                try:
                    for line in process.stdout:
                        received.put(json.loads(line))
                except Exception as error:
                    received.put(error)
                finally:
                    received.put(None)

            reader = threading.Thread(target=read_lines, daemon=True)
            reader.start()

            def send(command):
                process.stdin.write(json.dumps(command) + '\n')
                process.stdin.flush()

            def read_until(predicate):
                while True:
                    record = received.get(timeout=30)
                    if isinstance(record, Exception):
                        raise record
                    assert record is not None, errors.read_text()
                    records.append(record)
                    if predicate(record):
                        return record

            responses = []
            for turn in range(turns):
                send(dict(id=str(turn), type='prompt', message=f'turn {turn}'))
                response = read_until(lambda r: r.get('id') == str(turn) and r.get('type') == 'response')
                assert response['success'], response
                responses.append(dict(command=response['command'], success=response['success']))
                read_until(lambda r: r.get('type') == 'agent_settled')
                # No sleep or get_state round trip before the next prompt.
            send(dict(id='state', type='get_state'))
            state = read_until(lambda r: r.get('id') == 'state')['data']
            assert state['isStreaming'] is False and state['pendingMessageCount'] == 0, state
            assert len(server.requests) == turns, len(server.requests)
            def message_text(message):
                content = message.get('content')
                if isinstance(content, str):
                    return content
                return ''.join(part.get('text', '') for part in content or [] if part.get('type') == 'text')

            assert all(any(message.get('role') == 'user' and message_text(message) == f'turn {index}'
                           for message in request['messages']) for index, request in enumerate(server.requests))
            saved = Path(state['sessionFile'])
            assert saved.is_relative_to(sessions) and saved.is_file() and saved.stat().st_size > 0, saved
            process.stdin.close()
            assert process.wait(timeout=10) == 0, errors.read_text()
            reader.join(timeout=5)
            return dict(promptResponses=responses, settledCount=sum(r.get('type') == 'agent_settled' for r in records),
                        providerRequests=len(server.requests), isStreaming=state['isStreaming'],
                        pendingMessageCount=state['pendingMessageCount'])
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
        thread.join(timeout=5)
        output.with_name(output.stem + f'-{product}.records.json').write_text(json.dumps(records, indent=2) + '\n')
        output.with_name(output.stem + f'-{product}.http.json').write_text(json.dumps(server.requests, indent=2) + '\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--pisharp', type=Path, default=ROOT / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll')
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--turns', type=int, default=5)
    args = parser.parse_args()
    assert 2 <= args.turns <= 20
    pi = args.pi.resolve()
    sha = verify_pi_checkout(pi)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='pisharp-rpc-settlement-') as directory:
        work = Path(directory)
        results = {product: replay(product, pi, args.pisharp.resolve(), work, args.output, args.turns)
                   for product in ['pi', 'pisharp']}
    result = dict(piSha=sha,
                  pisharpSha=subprocess.check_output(['git', '-C', str(ROOT), 'rev-parse', 'HEAD'], text=True).strip(),
                  pisharpAssemblySha256=hashlib.sha256(args.pisharp.read_bytes()).hexdigest(),
                  results=results, match=results['pi'] == results['pisharp'],
                  scope='Prompt success, one provider request/settlement per turn, real session save and final idle state. '
                        'Each next prompt is sent immediately after external settlement with no sleep. '
                        'Full wire events, JSONL formats, errors/abort and indefinitely blocked saves remain outside this comparison.')
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    assert result['match'], result
    print(f'Matched {args.turns} persisted immediate post-settlement RPC turns.')


if __name__ == '__main__':
    main()
