#!/usr/bin/env python3
"""Compare pinned/native seven-tool workflows across physical local provider APIs."""
import argparse
from datetime import datetime, timezone
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

ROOT = Path(__file__).resolve().parents[2]
APIS = ['openai-responses', 'openai-completions', 'anthropic-messages']


def stream_events(api, index, item):
    text = 'workflow complete' if index == 9 else 'history retained'
    arguments = json.dumps(item['arguments']) if item else None
    events = []
    if api == 'openai-completions':
        record = dict(id=f'chatcmpl_{index}', object='chat.completion.chunk', created=1, model='fixture-model')
        delta = dict(role='assistant', tool_calls=[dict(index=0, id=item['callId'], type='function',
                     function=dict(name=item['tool'], arguments=arguments))]) if item else dict(role='assistant', content=text)
        events = [dict(record, choices=[dict(index=0, delta=delta, finish_reason=None)]),
                  dict(record, choices=[dict(index=0, delta={}, finish_reason='tool_calls' if item else 'stop')])]
    elif api == 'openai-responses':
        response = dict(id=f'resp_{index}', object='response', created_at=1, model='fixture-model', status='in_progress', output=[])
        events.append(dict(type='response.created', response=response))
        if item:
            function = dict(id=f'fc_{index}', type='function_call', call_id=item['callId'], name=item['tool'], arguments=arguments)
            events += [dict(type='response.output_item.added', output_index=0, item=dict(function, arguments='')),
                       dict(type='response.function_call_arguments.delta', output_index=0, item_id=f'fc_{index}', delta=arguments),
                       dict(type='response.function_call_arguments.done', output_index=0, item_id=f'fc_{index}', arguments=arguments),
                       dict(type='response.output_item.done', output_index=0, item=function)]
            output = [function]
        else:
            message = dict(id=f'msg_{index}', type='message', role='assistant', status='completed',
                           content=[dict(type='output_text', text=text, annotations=[])])
            events += [dict(type='response.output_item.added', output_index=0, item=dict(message, content=[])),
                       dict(type='response.output_text.delta', item_id=f'msg_{index}', output_index=0, content_index=0, delta=text),
                       dict(type='response.output_item.done', output_index=0, item=message)]
            output = [message]
        events.append(dict(type='response.completed', response=dict(response, status='completed', output=output,
                           usage=dict(input_tokens=1, output_tokens=1, total_tokens=2))))
    else:
        events.append(dict(type='message_start', message=dict(id=f'msg_{index}', type='message', role='assistant',
                      model='fixture-model', content=[], stop_reason=None, stop_sequence=None,
                      usage=dict(input_tokens=1, output_tokens=0))))
        block = dict(type='tool_use', id=item['callId'], name=item['tool'], input={}) if item else dict(type='text', text='')
        delta = dict(type='input_json_delta', partial_json=arguments) if item else dict(type='text_delta', text=text)
        events += [dict(type='content_block_start', index=0, content_block=block),
                   dict(type='content_block_delta', index=0, delta=delta),
                   dict(type='content_block_stop', index=0),
                   dict(type='message_delta', delta=dict(stop_reason='tool_use' if item else 'end_turn', stop_sequence=None),
                        usage=dict(output_tokens=1)), dict(type='message_stop')]
    body = ''.join(('event: ' + event['type'] + '\n' if api == 'anthropic-messages' else '') +
                   'data: ' + json.dumps(event) + '\n\n' for event in events)
    if api != 'anthropic-messages':
        body += 'data: [DONE]\n\n'
    return body.encode()


class Provider(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        index = len(self.server.requests)
        self.server.requests.append(dict(path=self.path, body=request))
        if index == getattr(self.server, 'abortAt', None):
            self.send_response(200)
            self.send_header('Content-Type', 'text/event-stream')
            self.end_headers()
            self.wfile.flush()
            self.server.blocked.set()
            self.server.release.wait(timeout=20)
            self.close_connection = True
            return
        item = self.server.plan[index] if index < len(self.server.plan) else None
        body = stream_events(self.server.api, index, item)
        self.send_response(200)
        self.send_header('Content-Type', 'text/event-stream')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def provider_results(api, request):
    results = []
    body = request['body']
    if api == 'openai-completions':
        for message in body.get('messages', []):
            if message.get('role') == 'tool':
                results.append(dict(callId=message['tool_call_id'], content=message.get('content')))
    elif api == 'openai-responses':
        for item in body.get('input', []):
            if item.get('type') == 'function_call_output':
                results.append(dict(callId=item['call_id'], content=item.get('output')))
    else:
        for message in body.get('messages', []):
            if isinstance(message.get('content'), list):
                for part in message['content']:
                    if part.get('type') == 'tool_result':
                        results.append(dict(callId=part['tool_use_id'], content=part.get('content'), isError=part.get('is_error', False)))
    return results


def declarations(api, request):
    answer = []
    for item in request['body'].get('tools', []):
        tool = item.get('function', item)
        answer.append(dict(name=tool['name'], description=tool.get('description'),
                           parameters=tool.get('parameters', tool.get('input_schema')),
                           protocolFields={**{key: value for key, value in item.items()
                                               if key not in ['function', 'name', 'description', 'parameters', 'input_schema']},
                                           **({'functionFields': {key: value for key, value in tool.items()
                                               if key not in ['name', 'description', 'parameters']}} if 'function' in item else {})}))
    return answer


def replay(product, api, pi, native, root, output, fixture, label=None, cancel_continuation=False):
    label = label or product
    home = root / 'home'
    agent = home / 'agent'
    sessions = root / 'sessions'
    workspace = root / 'workspace'
    agent.mkdir(parents=True)
    workspace.mkdir()
    for name, text in fixture['files'].items():
        target = workspace / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text)
    server = ThreadingHTTPServer(('127.0.0.1', 0), Provider)
    server.api, server.plan, server.requests = api, fixture['plan'], []
    server.blocked, server.release = threading.Event(), threading.Event()
    if cancel_continuation:
        server.abortAt = len(fixture['plan'])
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    endpoint = f'http://127.0.0.1:{server.server_port}' + ('' if api == 'anthropic-messages' else '/v1')
    model = dict(id='fixture-model', name='Fixture Model', api=api, reasoning=False, input=['text'],
                 contextWindow=32768, maxTokens=1024, cost=dict(input=0, output=0, cacheRead=0, cacheWrite=0))
    profile = dict(baseUrl=endpoint, api=api, models=[model])
    profile.update(dict(apiKey='fixture-only-key') if product == 'pi' else dict(apiKeyEnv='PISHARP_FIXTURE_KEY'))
    (agent / 'models.json').write_text(json.dumps(dict(providers=dict(fixture=profile))))
    env = {key: os.environ[key] for key in ['PATH', 'DOTNET_ROOT', 'LD_LIBRARY_PATH'] if key in os.environ}
    env.update(HOME=str(home), PI_CODING_AGENT_DIR=str(agent), PISHARP_AGENT_DIR=str(agent),
               PISHARP_SESSION_DIR=str(sessions), PISHARP_FIXTURE_KEY='fixture-only-key', PI_OFFLINE='1',
               DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', **thread_limit_environment())
    common = ['--mode', 'rpc', '--provider', 'fixture', '--model', 'fixture-model', '--offline',
              '--session-dir', str(sessions), '--tools', ','.join(fixture['tools']),
              '--no-extensions', '--no-skills', '--no-prompt-templates', '--no-context-files']
    command = (['node', '--import', (pi / 'packages/coding-agent/src/experimental/source-resolver.ts').as_uri(),
                str(pi / 'packages/coding-agent/src/cli.ts')] if product == 'pi' else ['dotnet', str(native)]) + common
    all_records, errors, states = [], [], []
    process = None
    session_file = None
    try:
        for restart in [False, True]:
            error_path = output / f'{label}-{api}-{int(restart)}.stderr.log'
            errors.append(error_path)
            received = queue.Queue()
            records = []
            with error_path.open('w') as stderr:
                process = subprocess.Popen(command + (['--session', str(session_file)] if restart else []),
                    cwd=workspace, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=stderr, text=True)
                def read_lines(child=process):
                    try:
                        for line in child.stdout:
                            received.put(json.loads(line))
                    except Exception as error:
                        received.put(error)
                    finally:
                        received.put(None)
                reader = threading.Thread(target=read_lines, daemon=True)
                reader.start()

                def send(value):
                    process.stdin.write(json.dumps(value) + '\n')
                    process.stdin.flush()

                def until(predicate):
                    while True:
                        value = received.get(timeout=20)
                        if isinstance(value, Exception):
                            raise value
                        if value is None:
                            raise RuntimeError(f'{product} {api} exited: ' + error_path.read_text())
                        records.append(value)
                        if predicate(value):
                            return value

                send(dict(id='workflow', type='prompt', message='Inspect saved outcomes without repeating tools.' if restart
                          else 'Complete the fixture coding workflow.'))
                response = until(lambda value: value.get('id') == 'workflow' and value.get('type') == 'response')
                assert response['success'], response
                if cancel_continuation and not restart:
                    assert server.blocked.wait(timeout=20), 'Provider continuation did not reach HTTP barrier'
                    send(dict(id='abort-workflow', type='abort'))
                    abort = until(lambda value: value.get('id') == 'abort-workflow' and value.get('type') == 'response')
                    assert abort['success'], abort
                    if not any(value.get('type') == 'agent_settled' for value in records):
                        until(lambda value: value.get('type') == 'agent_settled')
                    server.release.set()
                else:
                    until(lambda value: value.get('type') == 'agent_settled')
                send(dict(id='state', type='get_state'))
                state = until(lambda value: value.get('id') == 'state')['data']
                assert not state['isStreaming'] and state['pendingMessageCount'] == 0, state
                session_file = Path(state['sessionFile'])
                assert session_file.is_relative_to(sessions) and session_file.is_file()
                send(dict(id='history', type='get_messages'))
                history = until(lambda value: value.get('id') == 'history')['data']
                states.append(dict(state=state, history=history))
                process.stdin.close()
                assert process.wait(timeout=10) == 0, error_path.read_text()
                reader.join(timeout=5)
                all_records.extend(records)
                process.stdout.close()
                process = None
        final_files = {name: dict(text=(workspace / name).read_text(), mode=(workspace / name).stat().st_mode & 0o777)
                       for name in fixture['finalFiles']}
        projected = []
        for request in server.requests:
            projected.append(dict(path=request['path'], declarations=declarations(api, request),
                                   results=provider_results(api, request)))
        return dict(providerRequests=len(server.requests), requests=projected,
                    finalFiles=final_files, states=states,
                    workspaceEntries=sorted([dict(path=entry.relative_to(workspace).as_posix(),
                        kind='symlink' if entry.is_symlink() else 'directory' if entry.is_dir() else 'file')
                        for entry in workspace.rglob('*')], key=lambda entry: entry['path']),
                    toolEvents=[value for value in all_records if value.get('type', '').startswith('tool_execution_')],
                    stderr=[path.read_text() for path in errors])
    finally:
        if process is not None:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)
            process.stdout.close()
            if process.stdin is not None and not process.stdin.closed:
                process.stdin.close()
        server.release.set()
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)
        replacements = {str(workspace): '<workspace>', str(root): '<fixture>', endpoint: '<endpoint>'}
        def normalized(value):
            text = json.dumps(value)
            for original, replacement in replacements.items():
                text = text.replace(original, replacement)
            return json.loads(text)
        (output / f'{label}-{api}-records.json').write_text(json.dumps(normalized(all_records), indent=2) + '\n')
        (output / f'{label}-{api}-http.json').write_text(json.dumps(normalized(server.requests), indent=2) + '\n')


def canonical_contract(state):
    messages = state['history']['messages']
    calls = [part for message in messages if message['role'] == 'assistant'
             for part in message.get('content', []) if isinstance(part, dict) and part.get('type') == 'toolCall']
    results = [message for message in messages if message['role'] == 'toolResult']
    projected = [dict(tool=message['toolName'], content=message['content'], isError=message['isError'],
                      details=message.get('details')) for message in results]
    return calls, results, projected


def validate_observation(api, observation, fixture):
    errors = []
    plan = fixture['plan']
    outcomes = fixture['outcomes']
    expected_path = {'openai-responses': '/v1/responses', 'openai-completions': '/v1/chat/completions',
                     'anthropic-messages': '/v1/messages?beta=true'}[api]
    if observation['providerRequests'] != len(plan) + 2:
        errors.append('provider request count')
    for index, request in enumerate(observation['requests']):
        if request['path'] != expected_path:
            errors.append(f'requests/{index}/path')
        if [item['name'] for item in request['declarations']] != fixture['tools']:
            errors.append(f'requests/{index}/tool names/order')
        count = min(index, len(plan))
        expected = [dict(callId=item['callId'], content=''.join(part['text'] for part in outcome['result']['content']))
                    for item, outcome in zip(plan[:count], outcomes[:count])]
        if api == 'anthropic-messages':
            for item, outcome in zip(expected, outcomes[:count]):
                item['isError'] = outcome['isError']
        if request['results'] != expected:
            errors.append(f'requests/{index}/provider results')
    if len(observation['states']) != 2:
        errors.append('canonical restart count')
    contracts = []
    for index, state in enumerate(observation['states']):
        calls, results, projected = canonical_contract(state)
        call_ids = [call['id'] for call in calls]
        if len(call_ids) != len(plan) or len(set(call_ids)) != len(plan):
            errors.append(f'states/{index}/authoritative call IDs')
        if [result['toolCallId'] for result in results] != call_ids:
            errors.append(f'states/{index}/call result correlation')
        if [(call['name'], call['arguments']) for call in calls] != [(item['tool'], item['arguments']) for item in plan]:
            errors.append(f'states/{index}/call names or arguments')
        expected = [dict(tool=item['tool'], content=outcome['result']['content'], isError=outcome['isError'],
                         details=outcome['result'].get('details')) for item, outcome in zip(plan, outcomes)]
        if projected != expected:
            errors.append(f'states/{index}/result text names errors or metadata')
        contracts.append(dict(calls=calls, results=projected))
    if len(contracts) == 2 and contracts[0] != contracts[1]:
        errors.append('canonical call IDs or outcomes changed on restart')
    events = observation['toolEvents']
    ends = [event for event in events if event['type'] == 'tool_execution_end']
    if len(ends) != len(plan) or [event['toolName'] for event in ends] != [item['tool'] for item in plan]:
        errors.append('tool lifecycle result count or names')
    for end, outcome in zip(ends, outcomes):
        starts = [index for index, event in enumerate(events) if event['type'] == 'tool_execution_start'
                  and event['toolCallId'] == end['toolCallId']]
        if len(starts) != 1 or starts[0] >= events.index(end):
            errors.append('tool lifecycle start/end ordering')
        if end['isError'] != outcome['isError'] or end['result']['content'] != outcome['result']['content']:
            errors.append('tool lifecycle error or text')
        for index, event in enumerate(events):
            if event['type'] == 'tool_execution_update' and event['toolCallId'] == end['toolCallId']:
                if not starts or not starts[0] < index < events.index(end):
                    errors.append('tool update outside invocation')
    if set(observation['finalFiles']) != set(fixture['finalFiles']):
        errors.append('filesystem inventory')
    expected_entries = {name: 'file' for name in fixture['finalFiles']}
    for name in fixture['finalFiles']:
        for parent in Path(name).parents:
            if parent != Path('.'):
                expected_entries[parent.as_posix()] = 'directory'
    if observation.get('workspaceEntries') != [dict(path=name, kind=kind) for name, kind in sorted(expected_entries.items())]:
        errors.append('workspace entries')
    for name, expected in fixture['finalFiles'].items():
        if observation['finalFiles'].get(name, {}).get('text') != expected['text']:
            errors.append('filesystem contents/' + name)
    if any(observation['stderr']):
        errors.append('unexpected CLI stderr')
    return errors


def compare_observations(api, pinned, native, fixture):
    errors = (['pi/' + error for error in validate_observation(api, pinned, fixture)] +
              ['pisharp/' + error for error in validate_observation(api, native, fixture)])
    for index, (expected, actual) in enumerate(zip(pinned['requests'], native['requests'])):
        if expected['declarations'] != actual['declarations']:
            errors.append(f'requests/{index}/declarations')
        if expected['results'] != actual['results']:
            errors.append(f'requests/{index}/results')
    strict_filesystem = pinned['finalFiles'] == native['finalFiles']
    private_creation = (set(pinned['finalFiles']) == set(native['finalFiles']) and
        all(actual == expected if name != 'generated/result.txt' else
            expected == dict(text='created\n', mode=0o644) and actual == dict(text='created\n', mode=0o600)
            for name, expected in pinned['finalFiles'].items() for actual in [native['finalFiles'][name]]))
    if not strict_filesystem and not private_creation:
        errors.append('unclassified filesystem difference')
    return dict(supportedContractPass=not errors, strictPass=not errors and strict_filesystem,
                intentionalPrivateCreationExclusion=not strict_filesystem and private_creation, differences=errors,
                strictDifferences=errors + ([] if strict_filesystem else ['finalFiles/generated/result.txt/mode']))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pi', type=Path, required=True)
    parser.add_argument('--pisharp', type=Path, default=ROOT / 'src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll')
    parser.add_argument('--apis', nargs='+', choices=APIS, default=APIS)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--calibrate', action='store_true')
    parser.add_argument('--cancel-continuation', action='store_true')
    args = parser.parse_args()
    apply_test_thread_limit()
    pin = verify_pi_checkout(args.pi)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    fixture = json.loads((ROOT / 'docs/parity/fixtures/builtin-coding-tool-agent-workflow-2026-10-22.json').read_text())
    if args.cancel_continuation:
        fixture['plan'] = fixture['plan'][:7]
        fixture['outcomes'] = fixture['outcomes'][:7]
    report = dict(schema=1, pinnedPiSha=pin,
                  piSharpSha=None if args.calibrate else subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
                  piSharpUsed=not args.calibrate, mode='same-pi-control' if args.calibrate else 'pi-vs-pisharp',
                  cancelContinuation=args.cancel_continuation,
                  dirtyPaths=subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT, text=True).splitlines(),
                  startedAt=datetime.now(timezone.utc).isoformat(), observations={})
    for api in args.apis:
        report['observations'][api] = {}
        other = 'controlPi' if args.calibrate else 'pisharp'
        for label in ['pi', other]:
            product = 'pi' if args.calibrate else label
            with tempfile.TemporaryDirectory(prefix='pisharp-provider-tools-') as temporary:
                root = Path(temporary)
                result = replay(product, api, args.pi.resolve(), args.pisharp.resolve(), root, output, fixture,
                                label=label, cancel_continuation=args.cancel_continuation)
                serialized = json.dumps(result).replace(str(root / 'workspace'), '<workspace>').replace(str(root), '<fixture>')
                report['observations'][api][label] = json.loads(serialized)
                print(api, label, result['providerRequests'], 'requests', flush=True)
            report['finishedAt'] = datetime.now(timezone.utc).isoformat()
            (output / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
        pair = report['observations'][api]
        report.setdefault('comparisons', {})[api] = compare_observations(api, pair['pi'], pair[other], fixture)
        print(api, report['comparisons'][api], flush=True)
        (output / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
    if not all(comparison['supportedContractPass'] for comparison in report['comparisons'].values()):
        raise SystemExit(1)


if __name__ == '__main__':
    main()
