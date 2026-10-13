#!/usr/bin/env python3
"""Compare text/JSON print exit behavior against current Pi with a real loopback provider."""
import argparse
import http.server
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading

from pi_reference import verify_pi_checkout
from terminal.thread_limits import apply_test_thread_limit, thread_limit_environment

apply_test_thread_limit()
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--pi', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
sha = verify_pi_checkout(args.pi)
repo = Path(__file__).resolve().parents[2]
finish_reason = 'stop'
requests = []

class Provider(http.server.BaseHTTPRequestHandler):
    def log_message(self, *unused):
        pass

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        requests.append(body)
        self.send_response(200)
        self.send_header('Content-Type', 'text/event-stream')
        self.end_headers()
        for delta, reason in [({'role': 'assistant', 'content': 'partial response'}, None), ({}, finish_reason)]:
            chunk = dict(id='length-fixture', object='chat.completion.chunk', created=1, model='fixture-model',
                         choices=[dict(index=0, delta=delta, finish_reason=reason)])
            self.wfile.write(('data: ' + json.dumps(chunk) + '\n\n').encode())
        self.wfile.write(b'data: [DONE]\n\n')
        self.wfile.flush()

server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Provider)
thread = threading.Thread(target=server.serve_forever, daemon=True)
thread.start()
results = []
try:
    with tempfile.TemporaryDirectory(prefix='pisharp-print-truncation-') as directory:
        root = Path(directory)
        for mode in ['text', 'json']:
            for reason in ['stop', 'length']:
                finish_reason = reason
                pair = {}
                for product in ['pi', 'pisharp']:
                    work = root / f'{product}-{mode}-{reason}'
                    agent = work / 'agent'
                    agent.mkdir(parents=True)
                    (agent / 'settings.json').write_text(json.dumps({'compaction': {'enabled': False}, 'retry': {'enabled': False}}))
                    (agent / 'models.json').write_text(json.dumps(dict(providers=dict(fixture=dict(
                        baseUrl=f'http://127.0.0.1:{server.server_port}/v1', apiKey='fixture-key',
                        api='openai-completions', models=[dict(id='fixture-model', contextWindow=8192, maxTokens=128)])))))
                    env = dict(PATH=os.environ['PATH'], HOME=str(work), TERM='xterm-256color',
                               NO_PROXY='127.0.0.1,localhost', no_proxy='127.0.0.1,localhost',
                               PI_CODING_AGENT_DIR=str(agent), PISHARP_AGENT_DIR=str(agent),
                               PISHARP_SESSION_DIR=str(agent / 'sessions'), **thread_limit_environment())
                    if product == 'pi': command = ['node', str(args.pi.resolve() / 'packages/coding-agent/dist/cli.js')]
                    else: command = ['dotnet', str(repo / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll')]
                    command += ['--mode', mode, '--provider', 'fixture', '--model', 'fixture-model', '--offline',
                                '--no-tools', '--no-session', '--no-extensions', 'hello']
                    requests.clear()
                    run = subprocess.run(command, cwd=work, env=env, input='', text=True, capture_output=True, timeout=25)
                    if mode == 'text': stdout = run.stdout
                    else:
                        events = [json.loads(line) for line in run.stdout.splitlines()]
                        if product == 'pi':
                            messages = [event['message'] for event in events if event.get('type') == 'message_end'
                                        and event.get('message', {}).get('role') == 'assistant']
                            stdout = ''.join(block.get('text', '') for message in messages
                                             for block in message.get('content', []) if block.get('type') == 'text')
                        else:
                            stdout = ''.join(event.get('data', {}).get('Text', '') or '' for event in events
                                             if event.get('data', {}).get('Type') == 'model_text_delta')
                    pair[product] = dict(exitCode=run.returncode, stdout=stdout, stderr=run.stderr,
                                         requests=len(requests))
                assert pair['pi'] == pair['pisharp'], json.dumps(dict(mode=mode, reason=reason, pair=pair), indent=2)
                assert pair['pi']['requests'] == 1
                assert pair['pi']['exitCode'] == (1 if mode == 'text' and reason == 'length' else 0)
                results.append(dict(mode=mode, finishReason=reason, match=True, **pair))
finally:
    server.shutdown()
    server.server_close()
    thread.join(timeout=5)
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(dict(piSha=sha, cases=results,
    comparison='Real isolated CLI processes. Text-mode stdout/stderr/exit status and provider request count match exactly. JSON mode compares assistant text, stderr/exit status and request count only. The different PiSharp event envelope and missing public stop reason remain open protocol requirements; this probe does not claim JSON format compatibility. Compaction and retry are disabled in both processes to isolate the final output-limit response.'), indent=2) + '\n')
print(f'Matched {len(results)} CLI truncation cases.')
