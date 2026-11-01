#!/usr/bin/env python3
"""Shared harness for the core-loop active-queue parity differentials.

Runs one product (pinned Pi or PiSharp) in RPC mode against a deterministic
gated OpenAI Chat Completions provider and the deterministic MCP tool server
(``mcp_server.py``). Case scripts are lists of actions executed identically
against both products; observations (provider requests, RPC records, state,
session entries, side-effect files) are captured and normalised for the
pairwise comparison.
"""
import hashlib
import json
import os
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
PARITY = HERE.parent
sys.path.insert(0, str(PARITY / 'terminal'))
sys.path.insert(0, str(PARITY))
from pi_reference import verify_pi_checkout  # noqa: E402
from thread_limits import apply_test_thread_limit, thread_limit_environment  # noqa: E402

apply_test_thread_limit()
ROOT = HERE.parents[2]
MODEL = 'fixture-model'
PROVIDER_NAME = 'fixture'
MCP_SERVER = str(HERE / 'mcp_server.py')
EXCLUDE_BUILTIN_TOOLS = 'read,bash,edit,write'
WAIT_TIMEOUT = 45


# ============================================================================
# Gated provider
# ============================================================================

def _sse(data):
    return ('data: ' + json.dumps(data) + '\n\n').encode()


class GatedProvider(BaseHTTPRequestHandler):
    """Scripted OpenAI Chat Completions provider with file-based gates."""

    def log_message(self, *_):
        pass

    def _begin_sse(self, full_body):
        self.send_response(200)
        self.send_header('Content-Type', 'text/event-stream')
        self.send_header('Content-Length', str(len(full_body)))
        self.end_headers()

    def _send_sse(self, record, ops, finish):
        """Write a precomputed SSE body; ops interleaves writes with gate waits."""
        try:
            body = b''.join(payload for kind, payload in ops if kind == 'write')
            body += _sse(dict(record, choices=[dict(index=0, delta={}, finish_reason=finish)]))
            body += b'data: [DONE]\n\n'
            self._begin_sse(body)
            for kind, payload in ops:
                if kind == 'write':
                    self.wfile.write(payload)
                    self.wfile.flush()
                else:
                    self.server.note_gate_waiting(payload)
                    while not self.server.gate_released(payload):
                        time.sleep(0.005)
            self.wfile.write(_sse(dict(record, choices=[dict(index=0, delta={}, finish_reason=finish)])))
            self.wfile.write(b'data: [DONE]\n\n')
            self.wfile.flush()
            return True
        except (BrokenPipeError, ConnectionResetError, OSError):
            self.server.note_aborted('mid')
            return False

    def _send_sse_error(self, record, ops, error_event):
        """Stream chunks then terminate the SSE body with an error event (no [DONE])."""
        try:
            body = b''.join(payload for kind, payload in ops if kind == 'write')
            body += _sse(error_event)
            self._begin_sse(body)
            for kind, payload in ops:
                if kind == 'write':
                    self.wfile.write(payload)
                    self.wfile.flush()
                else:
                    self.server.note_gate_waiting(payload)
                    while not self.server.gate_released(payload):
                        time.sleep(0.005)
            self.wfile.write(_sse(error_event))
            self.wfile.flush()
            return True
        except (BrokenPipeError, ConnectionResetError, OSError):
            self.server.note_aborted('mid')
            return False

    def _text_ops(self, record, parts):
        ops = []
        for part in parts:
            if isinstance(part, str):
                ops.append(('gate', part))
            else:
                for chunk in part:
                    ops.append(('write', _sse(dict(record, choices=[dict(
                        index=0, delta=dict(role='assistant', content=chunk), finish_reason=None)]))))
        return ops

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        server = self.server
        index = len(server.requests)
        entry = dict(index=index, path=self.path, body=body, outcome='pending')
        server.requests.append(entry)
        step = server.script[index] if index < len(server.script) else dict(t='text', text='UNSCRIPTED')
        if step.get('gate'):
            server.note_gate_waiting(step['gate'])
            while not server.gate_released(step['gate']):
                time.sleep(0.005)
        kind = step['t']
        if kind == 'text':
            record = dict(id=f'fixture-{index}', object='chat.completion.chunk', created=1, model=MODEL)
            ops = self._text_ops(record, step.get('parts') or [[step.get('text', 'OK')]])
            if not self._send_sse(record, ops, step.get('finish', 'stop')):
                entry['outcome'] = 'aborted_mid'
                return
            entry['outcome'] = 'completed'
        elif kind == 'tool':
            record = dict(id=f'fixture-{index}', object='chat.completion.chunk', created=1, model=MODEL)
            ops = self._text_ops(record, step.get('parts', []))
            for i, call in enumerate(step['calls']):
                delta = dict(role='assistant', tool_calls=[dict(
                    index=i, id=f'call_{index}_{i}', type='function',
                    function=dict(name=call['name'], arguments=json.dumps(call['args'], sort_keys=True)))])
                ops.append(('write', _sse(dict(record, choices=[dict(index=0, delta=delta, finish_reason=None)]))))
            if not self._send_sse(record, ops, 'tool_calls'):
                entry['outcome'] = 'aborted_mid'
                return
            entry['outcome'] = 'completed'
        elif kind == 'error_pre':
            self.send_response(step.get('status', 400))
            self.send_header('Content-Type', 'application/json')
            payload = json.dumps({'error': step.get('error', {'message': 'fixture pre-content failure',
                                                             'type': 'invalid_request_error'})}).encode()
            self.send_header('Content-Length', str(len(payload)))
            self.end_headers()
            try:
                self.wfile.write(payload)
                self.wfile.flush()
                entry['outcome'] = 'completed'
            except (BrokenPipeError, ConnectionResetError, OSError):
                entry['outcome'] = 'aborted_mid'
        elif kind == 'error_post':
            record = dict(id=f'fixture-{index}', object='chat.completion.chunk', created=1, model=MODEL)
            ops = self._text_ops(record, step.get('parts', []))
            error_event = {'error': step.get('error', {'message': 'fixture mid-stream failure',
                                                       'type': 'server_error'})}
            if not self._send_sse_error(record, ops, error_event):
                entry['outcome'] = 'aborted_mid'
                return
            entry['outcome'] = 'error_sse'
        else:
            raise ValueError(f'unknown provider step kind {kind!r}')
        server.note_step_done(index, entry['outcome'])


class ProviderServer(ThreadingHTTPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, script, marker_dir):
        super().__init__(('127.0.0.1', 0), GatedProvider)
        self.script = script
        self.requests = []
        self.marker_dir = Path(marker_dir)
        self.gate_waiters = []
        self.step_log = []
        self.lock = threading.Lock()

    def gate_released(self, token):
        return (self.marker_dir / (token + '.released')).exists()

    def note_gate_waiting(self, token):
        with self.lock:
            self.gate_waiters.append(token)

    def note_step_done(self, index, outcome):
        with self.lock:
            self.step_log.append(dict(index=index, outcome=outcome))

    def note_aborted(self, phase):
        with self.lock:
            self.step_log.append(dict(index=None, outcome=f'connection_closed_{phase}'))


class Provider:
    def __init__(self, script, marker_dir):
        self.server = ProviderServer(script, marker_dir)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    @property
    def url(self):
        return f'http://127.0.0.1:{self.server.server_port}/v1'

    @property
    def requests(self):
        return self.server.requests

    @property
    def request_count(self):
        return len(self.server.requests)

    @property
    def step_log(self):
        with self.server.lock:
            return list(self.server.step_log)

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)


# ============================================================================
# Product process driver
# ============================================================================

class ProductRun:
    def __init__(self, product, pi_root, pisharp_dll, work, provider, marker_dir, timeout=WAIT_TIMEOUT):
        self.product = product
        self.work = Path(work)
        self.agent = self.work / product / 'agent'
        self.sessions = self.work / product / 'sessions'
        self.agent.mkdir(parents=True, exist_ok=True)
        self.sessions.mkdir(parents=True, exist_ok=True)
        self.marker_dir = Path(marker_dir)
        self.marker_dir.mkdir(parents=True, exist_ok=True)
        self.records = []
        self.received = queue.Queue()
        self.timeout = timeout
        provider_dict = dict(
            baseUrl=provider.url,
            models=[dict(id=MODEL, name=MODEL, api='openai-completions', reasoning=False, input=['text'],
                         contextWindow=4096, maxTokens=1024,
                         cost=dict(input=0, output=0, cacheRead=0, cacheWrite=0))])
        if product == 'pi':
            provider_dict['apiKey'] = 'fixture-only-key'
            command = ['node', '--import',
                       (pi_root / 'packages/coding-agent/src/experimental/source-resolver.ts').as_uri(),
                       str(pi_root / 'packages/coding-agent/src/cli.ts')]
        else:
            provider_dict['apiKeyEnv'] = 'CL_FIXTURE_KEY'
            command = ['dotnet', str(pisharp_dll)]
        (self.agent / 'models.json').write_text(json.dumps(dict(providers=dict(fixture=provider_dict))))
        mcp_env = dict(CL_MARKERS=str(self.marker_dir), CL_EFFECTS=str(self.work / 'effects.txt'),
                       CL_REQUEST_LOG=str(self.work / 'mcp-calls.jsonl'))
        (self.agent / 'mcp.json').write_text(json.dumps(dict(mcpServers=dict(core=dict(
            type='stdio', command=sys.executable, args=[MCP_SERVER], exposure='direct', env=mcp_env)))))
        base_env = {key: os.environ[key] for key in ('PATH', 'DOTNET_ROOT', 'LD_LIBRARY_PATH') if key in os.environ}
        self.env = dict(base_env, HOME=str(self.agent), PI_CODING_AGENT_DIR=str(self.agent),
                        PISHARP_AGENT_DIR=str(self.agent), CL_FIXTURE_KEY='fixture-only-key',
                        PI_OFFLINE='1', TERM='xterm-256color', DOTNET_CLI_TELEMETRY_OPTOUT='1',
                        DOTNET_NOLOGO='1', **thread_limit_environment())
        self.command = command + ['--mode', 'rpc', '--provider', PROVIDER_NAME, '--model', MODEL,
                                  '--offline', '--session-dir', str(self.sessions),
                                  '--no-skills', '--no-prompt-templates',
                                  '--exclude-tools', EXCLUDE_BUILTIN_TOOLS]
        self.stderr_log = self.work / (product + '.stderr.log')
        self._next_id = 0

    def start(self):
        if getattr(self, '_stderr', None) is not None:
            self._stderr.close()
        self._stderr = open(self.stderr_log, 'a', encoding='utf-8')
        self.process = subprocess.Popen(self.command, cwd=self.work, env=self.env, stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=self._stderr, text=True,
                                        start_new_session=True)
        self._reader = threading.Thread(target=self._read_loop, daemon=True)
        self._reader.start()

    def _read_loop(self):
        try:
            for line in self.process.stdout:
                line = line.strip()
                if not line:
                    continue
                try:
                    self.received.put(json.loads(line))
                except json.JSONDecodeError:
                    self.received.put({'__unparsed__': line})
        finally:
            self.received.put(None)

    def marker(self, name):
        return self.marker_dir / name

    def release(self, token):
        self.marker(token + '.released').write_text('1')

    def marker_exists(self, name):
        return self.marker(name).exists()

    def _send_raw(self, payload):
        self.process.stdin.write(json.dumps(payload) + '\n')
        self.process.stdin.flush()

    def command_id(self, label):
        self._next_id += 1
        return f'{label}-{self._next_id}'

    def send(self, command_type, **fields):
        command_id = self.command_id(command_type)
        self._send_raw(dict(id=command_id, type=command_type, **fields))
        return command_id

    def pump(self, limit=None):
        """Drain currently buffered records (non-blocking)."""
        drained = []
        while limit is None or len(drained) < limit:
            try:
                record = self.received.get_nowait()
            except queue.Empty:
                return drained
            if record is None:
                self._eof = True
                return drained
            self.records.append(record)
            drained.append(record)
        return drained

    def wait_for(self, predicate, what):
        """Deterministic barrier: block until a buffered record satisfies the predicate."""
        deadline = time.monotonic() + self.timeout
        while True:
            self.pump()
            if predicate(self.records, self):
                return
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                tail = '\n'.join(str(line) for line in self.stderr_log.read_text().splitlines()[-25:])
                raise TimeoutError(f'{self.product}: timeout waiting for {what}; last stderr:\n{tail}')
            try:
                record = self.received.get(timeout=min(0.05, remaining))
            except queue.Empty:
                continue
            if record is None:
                self.pump()
                raise TimeoutError(f'{self.product}: process exited while waiting for {what}; '
                                   f'stderr:\n{self.stderr_log.read_text()[-4000:]}')
            self.records.append(record)

    def wait_response(self, command_id, command_type):
        self.wait_for(lambda records, run: any(r.get('type') == 'response' and r.get('id') == command_id
                                               and r.get('command') == command_type for r in records),
                      f'response {command_type} {command_id}')
        return next(r for r in self.records if r.get('type') == 'response' and r.get('id') == command_id)

    def events(self, event_type, **fields):
        return [r for r in self.records if r.get('type') == event_type
                and all(r.get(key) == value for key, value in fields.items())]

    def state(self):
        command_id = self.send('get_state')
        response = self.wait_response(command_id, 'get_state')
        return response['data']

    def restart(self, extra_args=()):
        """Start a new process for the same product/work directory (e.g. with --continue)."""
        self.command = self.command + list(extra_args)
        self.start()
        self._next_id = 0

    def exit_clean(self):
        if not self.process.stdin.closed:
            self.process.stdin.close()
        code = self.process.wait(timeout=20)
        self._kill_group()
        self.pump()
        return code

    def _kill_group(self):
        """Kill the product's process group so MCP/provider children cannot leak."""
        try:
            os.killpg(os.getpgid(self.process.pid), 9)
        except (ProcessLookupError, PermissionError, OSError):
            pass

    def kill(self):
        if self.process.poll() is None:
            self.process.kill()
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            pass
        self._kill_group()
        self.pump()

    def effects(self):
        path = self.work / 'effects.txt'
        return path.read_text().splitlines() if path.exists() else []

    def mcp_calls(self):
        path = self.work / 'mcp-calls.jsonl'
        if not path.exists():
            return []
        return [json.loads(line) for line in path.read_text().splitlines() if line.strip()]

    def session_entries(self):
        """Raw persisted session entries (Pi JSONL lines / PiSharp document Entries)."""
        if self.product == 'pi':
            entries = []
            for path in sorted(self.sessions.glob('**/*.jsonl')):
                for line in path.read_text().splitlines():
                    if not line.strip():
                        continue
                    entry = json.loads(line)
                    entry['__sessionFile'] = path.name
                    entries.append(entry)
            return entries
        entries = []
        for path in sorted(self.sessions.glob('**/*.session.json')):
            document = json.loads(path.read_text())
            for entry in document.get('Entries', []):
                entry['__sessionFile'] = path.name
                entries.append(entry)
        return entries


# ============================================================================
# Normalisation
# ============================================================================

def _message_text(message):
    content = message.get('content')
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return ''.join(part.get('text', '') for part in content
                       if isinstance(part, dict) and part.get('type') == 'text')
    return ''


def normalize_provider_request(request):
    body = request['body']
    messages = []
    for message in body.get('messages', []):
        role = message.get('role')
        if role == 'system':
            messages.append({'role': 'system'})
            continue
        normalized = {'role': role}
        text = _message_text(message)
        if text:
            normalized['text'] = text
        if message.get('tool_calls'):
            normalized['toolCalls'] = [
                dict(name=call['function']['name'],
                     args=json.loads(call['function'].get('arguments') or '{}'))
                for call in message['tool_calls']]
        if role == 'tool':
            normalized['toolCall'] = True
        if role == 'assistant':
            stop = message.get('stop_reason')
            if stop:
                normalized['stopReason'] = stop
        messages.append(normalized)
    tools = [tool['function']['name'] for tool in body.get('tools', [])]
    return dict(index=request['index'], outcome=request['outcome'], messages=messages, tools=tools)


_STOP_CANONICAL = {'stop': 'stop', 'toolUse': 'toolCall', 'tool_calls': 'toolCall',
                   'aborted': 'aborted', 'error': 'error', 'length': 'length'}


def _canonical_stop(stop):
    return _STOP_CANONICAL.get(stop, stop)


def _pisharp_message_projection(message):
    role = message.get('role')
    contents = message.get('contents') or []
    extra = message.get('additionalProperties') or {}
    text_parts, calls, result = [], [], None
    for part in contents:
        if not isinstance(part, dict):
            continue
        part_type = part.get('$type')
        if part_type == 'text':
            text_parts.append(part.get('text', ''))
        elif part_type == 'functionCall':
            calls.append(dict(name=part.get('name'), args=part.get('arguments')))
        elif part_type == 'functionResult':
            result = part.get('result') or {}
    text = '\n'.join(text_parts)
    if role == 'system':
        return ['system']
    if role == 'user':
        return ['user', text]
    if role == 'assistant':
        stop = _canonical_stop(extra.get('pisharp.stopReason') or extra.get('pisharp.rawStopReason'))
        # Pi records a provider failure via stopReason and keeps isError false on the assistant
        # message; mirror that so the projection matches (the failure is captured in `stop`).
        is_error = bool(extra.get('pisharp.isError'))
        return ['assistant', stop, is_error, text, calls]
    if role == 'tool':
        is_error = bool(result.get('isError')) if result is not None else False
        return ['toolResult', is_error, (result or {}).get('text', '')]
    return [role]


def _pi_message_projection(message):
    role = message.get('role')
    if role == 'system':
        return ['system']
    content = message.get('content')
    text_parts, calls = [], []
    if isinstance(content, str):
        text_parts.append(content)
    elif isinstance(content, list):
        for block in content:
            if not isinstance(block, dict):
                continue
            if block.get('type') == 'text':
                text_parts.append(block.get('text', ''))
            elif block.get('type') == 'toolCall':
                calls.append(dict(name=block.get('name'), args=block.get('arguments')))
    text = '\n'.join(text_parts)
    if role == 'user':
        return ['user', text]
    if role == 'assistant':
        stop = _canonical_stop(message.get('stopReason'))
        is_error = bool(message.get('isError'))
        return ['assistant', stop, is_error, text, calls]
    if role == 'toolResult':
        return ['toolResult', bool(message.get('isError')), text]
    return [role]


def project_session_entries(product, raw_entries):
    """Common persisted-history projection (bookkeeping entries excluded)."""
    projected = []
    for entry in raw_entries:
        if product == 'pi':
            entry_type = entry.get('type')
            if entry_type == 'message':
                projected.append(_pi_message_projection(entry.get('message') or {}))
            elif entry_type == 'model_change':
                projected.append(['model_change', entry.get('provider'), entry.get('modelId')])
            elif entry_type == 'thinking_level_change':
                projected.append(['thinking_level_change', entry.get('thinkingLevel')])
            elif entry_type in ('label', 'session_info', 'compaction'):
                projected.append([entry_type])
            # 'session' header and other bookkeeping entries are out of scope
            continue
        entry_type = entry.get('Type')
        payload = entry.get('Payload') or {}
        if entry_type == 'chat':
            projected.append(_pisharp_message_projection(payload.get('Message') or {}))
        elif entry_type == 'tool_transcript':
            items = payload if isinstance(payload, list) else [payload]
            for item in items:
                message = (item or {}).get('message') or {}
                if message.get('role') == 'system':
                    projected.append(['system'])
        elif entry_type == 'model_change':
            projected.append(['model_change', payload.get('provider'), payload.get('model')])
        elif entry_type == 'thinking_level_change':
            projected.append(['thinking_level_change', payload.get('thinkingLevel')])
        # run_started/run_finished bookkeeping is out of scope
    return projected


# ============================================================================
# Case execution
# ============================================================================

class CaseContext:
    """Action vocabulary shared by the case scripts."""

    def __init__(self, run: ProductRun, provider: Provider):
        self.run = run
        self.provider = provider

    # -- commands -----------------------------------------------------------
    def prompt(self, message, behavior=None):
        command_id = self.run.send('prompt', message=message, **({'streamingBehavior': behavior} if behavior else {}))
        self.run.wait_response(command_id, 'prompt')

    def steer(self, message):
        command_id = self.run.send('steer', message=message)
        self.run.wait_response(command_id, 'steer')

    def follow_up(self, message):
        command_id = self.run.send('follow_up', message=message)
        self.run.wait_response(command_id, 'follow_up')

    def abort(self):
        command_id = self.run.send('abort')
        self.run.wait_response(command_id, 'abort')

    def clear_queue(self):
        command_id = self.run.send('clear_queue')
        self.run.wait_response(command_id, 'clear_queue')

    def set_steering_mode(self, mode):
        command_id = self.run.send('set_steering_mode', mode=mode)
        self.run.wait_response(command_id, 'set_steering_mode')

    def set_follow_up_mode(self, mode):
        command_id = self.run.send('set_follow_up_mode', mode=mode)
        self.run.wait_response(command_id, 'set_follow_up_mode')

    def get_state(self):
        return self.run.state()

    def get_entries(self):
        command_id = self.run.send('get_entries')
        self.run.wait_response(command_id, 'get_entries')
        return next(r for r in self.run.records if r.get('type') == 'response'
                    and r.get('id') == command_id)['data']

    def restart(self, extra_args=()):
        """Exit the process cleanly and restart it with extra arguments (e.g. --continue).

        The mcp-ready marker is cleared first so the post-restart warm-up barrier
        only passes once the new process has started its MCP server.
        """
        marker = self.run.marker('mcp-ready')
        if marker.exists():
            marker.unlink()
        code = self.run.exit_clean()
        if code != 0:
            raise RuntimeError(f'restart: previous process exited {code}')
        self.run.restart(extra_args=extra_args)
        self.run.wait_for(lambda records, run: run.marker_exists('mcp-ready'), 'mcp server ready after restart')

    # -- deterministic barriers ----------------------------------------------
    def wait_events(self, event_type, minimum=1, **fields):
        self.run.wait_for(lambda records, run: len(run.events(event_type, **fields)) >= minimum,
                          f'{minimum} x {event_type} {fields}')

    def wait_response_count(self, command_type, minimum=1):
        self.run.wait_for(lambda records, run: len([r for r in records
                                                    if r.get('type') == 'response' and r.get('command') == command_type]) >= minimum,
                          f'{minimum} x response {command_type}')

    def wait_provider_requests(self, minimum):
        self.run.wait_for(lambda records, run: self.provider.request_count >= minimum,
                          f'{minimum} provider requests')

    def wait_marker(self, name, description=None):
        self.run.wait_for(lambda records, run: run.marker_exists(name), description or f'marker {name}')

    def release(self, token):
        self.run.release(token)

    def settled(self):
        self.wait_events('agent_settled')


class _ScriptParser:
    """Parses case action strings (or (method, args) tuples) into context calls."""

    def __init__(self):
        self.settled_count = 0

    def parse(self, action):
        if isinstance(action, tuple):
            return action
        name, _, rest = action.partition(' ')
        rest = rest.strip()
        if name in ('prompt', 'steer', 'follow_up', 'release',
                    'set_steering_mode', 'set_follow_up_mode'):
            return (name, [rest])
        if name in ('abort', 'clear_queue', 'get_state', 'get_entries'):
            return (name, [])
        if name == 'settled':
            self.settled_count += 1
            return ('wait_events', ['agent_settled', self.settled_count])
        if name == 'wait':
            wait_name, _, wait_arg = rest.partition(' ')
            wait_arg = wait_arg.strip()
            if wait_name == 'marker':
                return ('wait_marker', [wait_arg])
            if wait_name == 'requests':
                return ('wait_provider_requests', [int(wait_arg)])
            if wait_arg:
                return ('wait_events', [wait_name, int(wait_arg)])
            return ('wait_events', [wait_name])
        if name == 'restart':
            return ('restart', [tuple(rest.split()) if rest else ()])
        raise ValueError(f'unknown case action: {action!r}')


def run_case(case, product, pi_root, pisharp_dll, work, case_timeout=180):
    provider = Provider(case['provider'], work / 'gates')
    run = ProductRun(product, pi_root, pisharp_dll, work, provider, work / 'gates')
    run.start()
    context = CaseContext(run, provider)
    error = None
    parser = _ScriptParser()
    try:
        # Deterministic warm-up barrier: the MCP server must be started before
        # the first prompt so tool declarations are stable for both products.
        run.wait_for(lambda records, run: run.marker_exists('mcp-ready'), 'mcp server ready')
        for action in case['script']:
            name, args = parser.parse(action)
            getattr(context, name)(*args)
    except Exception as exc:  # noqa: BLE001 - captured as case failure evidence
        error = repr(exc)
        run.kill()
    else:
        # Teardown: release every gate so blocked work cannot hold the run open,
        # then settle the process. Effects captured afterwards include any
        # side effects of in-flight work the product did not cancel.
        for token in case.get('gates', []):
            run.release(token)
        try:
            run.wait_for(lambda records, run: run.process.poll() is not None or
                         len(run.events('agent_settled')) >= case.get('settles', 1), 'final settlement')
        except TimeoutError:
            error = error or 'teardown: final settlement timeout'
        _settle_effects(run)
        run.pump()
        exit_code = None
        try:
            exit_code = run.exit_clean()
        except (subprocess.TimeoutExpired, OSError):
            run.kill()
        if exit_code not in (0, None):
            error = error or f'process exit code {exit_code}'
    provider.close()
    run._stderr.close()
    if error is not None:
        raise RuntimeError(f'{product} case failed: {error}')
    return collect_observations(run, provider, pi_root)


def _settle_effects(run):
    """Bounded quiescence wait after teardown releases: the effects file stops changing."""
    previous = None
    stable = 0
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        run.pump()
        current = run.effects()
        if current == previous:
            stable += 1
            if stable >= 4:
                return
        else:
            stable = 0
            previous = current
        time.sleep(0.05)


def collect_observations(run, provider, pi_checkout):
    state_calls = [r for r in run.records if r.get('type') == 'response' and r.get('command') == 'get_state']
    final_state = state_calls[-1]['data'] if state_calls else None
    return dict(
        product=run.product,
        providerRequests=[normalize_provider_request(r) for r in provider.requests],
        providerStepLog=provider.step_log,
        records=run.records,
        mcpCalls=run.mcp_calls(),
        effects=run.effects(),
        state=dict(final_state) if final_state else None,
        stateHistory=[r['data'] for r in state_calls],
        sessionEntries=project_session_entries(run.product, run.session_entries()),
        stderr=run.stderr_log.read_text()[-2000:],
        piSha=verify_pi_checkout(pi_checkout),
        pisharpSha=pisharp_sha(),
    )


def pisharp_sha():
    return subprocess.check_output(['git', '-C', str(ROOT), 'rev-parse', 'HEAD'], text=True).strip()


def main():
    # Smoke entry: verify the harness pieces start without products.
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--smoke', action='store_true')
    args = parser.parse_args()
    if args.smoke:
        with tempfile.TemporaryDirectory(prefix='cl-smoke-') as directory:
            provider = Provider([dict(t='text', text='OK')], Path(directory))
            print(json.dumps(dict(url=provider.url)))
            provider.close()


if __name__ == '__main__':
    import argparse
    main()
