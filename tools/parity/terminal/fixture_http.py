import json
import select
import socket
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Condition, Event, Thread
from urllib.parse import unquote, urlsplit


class FixtureServer:
    def __init__(self, models=None, props=None, behavior=None, huggingface=None):
        self.requests = []
        self.initial_models = json.loads(json.dumps(models or []))
        self.props = props or {}
        self.behavior = behavior or {}
        self.huggingface = huggingface or {}
        gate_names = [name for key, name in self.behavior.items()
                      if key.endswith('Gate') and isinstance(name, str)]
        gate_names.extend(name for key, models in self.behavior.items()
                          if key.endswith('GateByModel') and isinstance(models, dict)
                          for name in models.values() if isinstance(name, str))
        self.gates = {name: Event() for name in gate_names}
        self.closed = False
        self.stream_condition = Condition()
        self.active_sse_streams = 0
        self.reset()
        outer = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def send_json(self, value, status=200):
                payload = json.dumps(value).encode()
                self.send_response(status)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(payload)))
                self.end_headers()
                try:
                    self.wfile.write(payload)
                except (BrokenPipeError, ConnectionResetError):
                    pass

            def do_GET(self):
                path = urlsplit(self.path).path
                outer.requests.append(dict(method='GET', path=self.path,
                                           authorization=self.headers.get('Authorization')))
                if path == '/models':
                    if outer.catalog_failures_remaining > 0:
                        outer.signal_gate(outer.behavior.get('catalogFailureRequestGate'))
                        failure_gate = outer.behavior.get('catalogFailureResponseGate')
                        if failure_gate is not None:
                            outer.wait_for_gate(failure_gate)
                        outer.catalog_failures_remaining -= 1
                        self.send_json(dict(error=dict(message='fixture router unavailable')), status=503)
                        return
                    outer.wait_for_model_poll()
                    now = time.monotonic()
                    for entry in outer.models:
                        deadline = outer.pending_loads.get(entry.get('id'))
                        if deadline is not None and now >= deadline:
                            entry['status']['value'] = 'loaded'
                            del outer.pending_loads[entry['id']]
                            outer.pending_load_progress.pop(entry['id'], None)
                            outer.pending_load_progress_gates.pop(entry['id'], None)
                            outer.pending_load_finish_gates.pop(entry['id'], None)
                            outer.pending_load_poll_gates.pop(entry['id'], None)
                    self.send_json(dict(object='list', data=outer.models))
                    return
                if path == '/props':
                    self.send_json(outer.props)
                    return
                if path == '/models/sse':
                    outer.start_sse_stream()
                    self.send_response(200)
                    self.send_header('Content-Type', 'text/event-stream')
                    self.send_header('Cache-Control', 'no-cache')
                    self.end_headers()
                    sent_load_progress = set()
                    sent_download_progress = set()
                    try:
                        while not outer.closed:
                            if self.client_disconnected():
                                break
                            now = time.monotonic()
                            for model, deadline in list(outer.pending_loads.items()):
                                progress_deadline = outer.pending_load_progress.get(model, 0)
                                progress_gate = outer.pending_load_progress_gates.get(model)
                                progress_ready = ((progress_gate and outer.gate_released(progress_gate)) or
                                                  (progress_deadline is not None and now >= progress_deadline))
                                if model not in sent_load_progress and progress_ready:
                                    self.send_event(dict(model=model, event='status_change', data=dict(
                                        status='loading', progress=dict(stages=['text_model'],
                                                                        current='text_model', value=0.5))))
                                    sent_load_progress.add(model)
                                    outer.set_progress(model, 'loading')
                                finish_gate = outer.pending_load_finish_gates.get(model)
                                finish_ready = ((finish_gate and outer.gate_released(finish_gate)) or
                                                (deadline is not None and now >= deadline))
                                if finish_ready:
                                    outer.set_status(model, 'loaded')
                                    outer.pending_loads.pop(model, None)
                                    outer.pending_load_progress.pop(model, None)
                                    outer.pending_load_progress_gates.pop(model, None)
                                    outer.pending_load_finish_gates.pop(model, None)
                                    poll_gate = outer.pending_load_poll_gates.pop(model, None)
                                    outer.release_gate(poll_gate)
                                    self.send_event(dict(model=model, event='status_change', data=dict(status='loaded')))
                            for model, deadline in list(outer.pending_downloads.items()):
                                progress_deadline = outer.pending_download_progress.get(model, 0)
                                progress_gate = outer.pending_download_progress_gates.get(model)
                                progress_ready = ((progress_gate and outer.gate_released(progress_gate)) or
                                                  (progress_deadline is not None and now >= progress_deadline))
                                if model not in sent_download_progress and progress_ready:
                                    self.send_event(dict(model=model, event='download_progress', data=dict(progress={
                                        'https://fixture.invalid/model.gguf': dict(done=512, total=1024)})))
                                    sent_download_progress.add(model)
                                finish_gate = outer.pending_download_finish_gates.get(model)
                                finish_ready = ((finish_gate and outer.gate_released(finish_gate)) or
                                                (deadline is not None and now >= deadline))
                                if finish_ready:
                                    outer.set_status(model, 'unloaded')
                                    outer.pending_downloads.pop(model, None)
                                    outer.pending_download_progress.pop(model, None)
                                    outer.pending_download_progress_gates.pop(model, None)
                                    outer.pending_download_finish_gates.pop(model, None)
                                    poll_gate = outer.pending_download_poll_gates.pop(model, None)
                                    outer.release_gate(poll_gate)
                                    self.send_event(dict(model=model, event='download_finished', data={}))
                            self.wfile.flush()
                            time.sleep(0.05)
                    except (BrokenPipeError, ConnectionResetError):
                        pass
                    finally:
                        outer.finish_sse_stream()
                    return
                if path == '/api/models':
                    self.send_json(outer.huggingface.get('searchResults', []))
                    return
                if path.startswith('/api/models/'):
                    repository = unquote(path[len('/api/models/'):])
                    delay = float(outer.huggingface.get('detailsDelaySeconds', 0))
                    if delay > 0:
                        time.sleep(delay)
                    details = outer.huggingface.get('details', {}).get(repository)
                    if details is not None:
                        self.send_json(details)
                    else:
                        self.send_json(dict(error='Model not found'), status=404)
                    return
                self.send_error(404)

            def send_event(self, value):
                self.wfile.write(('data: ' + json.dumps(value) + '\n\n').encode())

            def client_disconnected(self):
                try:
                    readable, _, _ = select.select([self.connection], [], [], 0)
                    return bool(readable) and self.connection.recv(1, socket.MSG_PEEK) == b''
                except OSError:
                    return True

            def do_POST(self):
                raw = self.rfile.read(int(self.headers.get('Content-Length', 0)))
                body = json.loads(raw) if raw else {}
                path = urlsplit(self.path).path
                outer.requests.append(dict(method='POST', path=self.path,
                                           authorization=self.headers.get('Authorization'), body=body))
                if path == '/models/load':
                    model = body.get('model')
                    start_gate = None
                    request_gate = None
                    response_gate = None
                    for entry in outer.models:
                        if entry.get('id') == model:
                            delays = outer.behavior.get('loadDelaySecondsByModel', {})
                            delay = float(delays.get(model, outer.behavior.get('loadDelaySeconds', 0)))
                            progress_delays = outer.behavior.get('loadProgressDelaySecondsByModel', {})
                            progress_delay = float(progress_delays.get(
                                model, outer.behavior.get('loadProgressDelaySeconds', 0)))
                            finish_gate = outer.model_gate('loadFinish', model)
                            progress_gate = outer.model_gate('loadProgress', model)
                            request_gate = outer.model_gate('loadRequest', model)
                            response_gate = outer.model_gate('loadResponse', model)
                            start_gate = outer.model_gate('loadStart', model)
                            waiting = delay > 0 or finish_gate is not None
                            entry['status']['value'] = 'loading' if waiting else 'loaded'
                            entry['status'].pop('failed', None)
                            if waiting:
                                started = time.monotonic()
                                outer.pending_loads[model] = None if finish_gate else started + delay
                                outer.pending_load_progress[model] = (None if progress_gate else
                                                                      started + max(progress_delay, 0))
                                outer.pending_load_progress_gates[model] = progress_gate
                                outer.pending_load_finish_gates[model] = finish_gate
                                outer.pending_load_poll_gates[model] = outer.model_gate('loadPoll', model)
                                outer.model_poll_counts[model] = 0
                    outer.signal_gate(request_gate)
                    if response_gate is not None:
                        outer.wait_for_gate(response_gate)
                    self.send_json(dict(success=True))
                    outer.signal_gate(start_gate)
                    return
                if path == '/models/unload':
                    model = body.get('model')
                    cancelling_load = model in outer.pending_loads
                    cancelling_download = model in outer.pending_downloads
                    load_poll_gate = outer.pending_load_poll_gates.get(model)
                    download_poll_gate = outer.pending_download_poll_gates.get(model)
                    for entry in outer.models:
                        if entry.get('id') == model:
                            entry['status']['value'] = 'unloaded'
                    outer.pending_loads.pop(model, None)
                    outer.pending_load_progress.pop(model, None)
                    outer.pending_load_progress_gates.pop(model, None)
                    outer.pending_load_finish_gates.pop(model, None)
                    outer.pending_load_poll_gates.pop(model, None)
                    outer.pending_downloads.pop(model, None)
                    outer.pending_download_progress.pop(model, None)
                    outer.pending_download_progress_gates.pop(model, None)
                    outer.pending_download_finish_gates.pop(model, None)
                    outer.pending_download_poll_gates.pop(model, None)
                    if cancelling_load:
                        outer.release_gate(load_poll_gate)
                    if cancelling_download:
                        outer.release_gate(download_poll_gate)
                    self.send_json(dict(success=True))
                    return
                if path == '/models':
                    model = body.get('model')
                    outer.set_status(model, 'downloading')
                    delay = float(outer.behavior.get('downloadDelaySeconds', 0.75))
                    progress_delay = float(outer.behavior.get('downloadProgressDelaySeconds', 0))
                    started = time.monotonic()
                    finish_gate = outer.behavior.get('downloadFinishGate')
                    progress_gate = outer.behavior.get('downloadProgressGate')
                    outer.pending_downloads[model] = None if finish_gate else started + max(delay, 0)
                    outer.pending_download_progress[model] = (None if progress_gate else
                                                              started + max(progress_delay, 0))
                    outer.pending_download_progress_gates[model] = progress_gate
                    outer.pending_download_finish_gates[model] = finish_gate
                    outer.pending_download_poll_gates[model] = outer.behavior.get('downloadPollGate')
                    outer.model_poll_counts[model] = 0
                    if delay <= 0 and finish_gate is None:
                        outer.set_status(model, 'unloaded')
                        outer.pending_downloads.pop(model, None)
                        outer.pending_download_progress.pop(model, None)
                    self.send_json(dict(success=True))
                    return
                self.send_error(404)
                self.send_response(200)
                self.send_header('Content-Type', 'text/event-stream')
                self.end_headers()
                for index, content in enumerate(['## Fixture reply\n\n', '**bold** and `code`\n', '\n- first\n- second\n']):
                    chunk = dict(id='fixture-response', object='chat.completion.chunk', created=1,
                                 model='fixture-model', choices=[dict(index=0, delta=dict(role='assistant', content=content), finish_reason=None)])
                    self.wfile.write(('data: ' + json.dumps(chunk) + '\n\n').encode())
                    self.wfile.flush()
                chunk = dict(id='fixture-response', object='chat.completion.chunk', created=1,
                             model='fixture-model', choices=[dict(index=0, delta={}, finish_reason='stop')],
                             usage=dict(prompt_tokens=10, completion_tokens=12, total_tokens=22))
                self.wfile.write(('data: ' + json.dumps(chunk) + '\n\ndata: [DONE]\n\n').encode())
                self.wfile.flush()

        self.server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        self.url = f'http://127.0.0.1:{self.server.server_port}'
        self.thread = Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def reset(self):
        if not self.wait_for_sse_streams():
            raise TimeoutError('Fixture SSE streams did not close before reset.')
        self.models = json.loads(json.dumps(self.initial_models))
        self.pending_loads = {}
        self.pending_load_progress = {}
        self.pending_load_progress_gates = {}
        self.pending_load_finish_gates = {}
        self.pending_load_poll_gates = {}
        self.pending_downloads = {}
        self.pending_download_progress = {}
        self.pending_download_progress_gates = {}
        self.pending_download_finish_gates = {}
        self.pending_download_poll_gates = {}
        self.model_poll_counts = {}
        self.model_poll_waiting = Event()
        self.model_poll_waiting.clear()
        for gate in self.gates.values():
            gate.clear()
        self.catalog_failures_remaining = int(self.behavior.get('initialModelFailures', 0))
        self.requests.clear()

    def gate_released(self, name):
        return name is not None and self.gates[name].is_set()

    def model_gate(self, prefix, model):
        by_model = self.behavior.get(prefix + 'GateByModel', {})
        return by_model.get(model, self.behavior.get(prefix + 'Gate'))

    def signal_gate(self, name):
        if name is not None:
            if name not in self.gates:
                raise ValueError(f'Unknown fixture gate: {name}')
            self.gates[name].set()

    def start_sse_stream(self):
        with self.stream_condition:
            self.active_sse_streams += 1
            self.stream_condition.notify_all()

    def finish_sse_stream(self):
        with self.stream_condition:
            self.active_sse_streams -= 1
            self.stream_condition.notify_all()

    def wait_for_sse_streams(self, timeout=5):
        with self.stream_condition:
            return self.stream_condition.wait_for(lambda: self.active_sse_streams == 0, timeout)

    def release_gate(self, name):
        if name is None:
            return
        if name not in self.gates:
            raise ValueError(f'Unknown fixture gate: {name}')
        self.gates[name].set()
        for model, gate in list(self.pending_load_finish_gates.items()):
            if gate == name:
                self.set_status(model, 'loaded')
                self.release_gate(self.pending_load_poll_gates.get(model))
        for model, gate in list(self.pending_download_finish_gates.items()):
            if gate == name:
                self.set_status(model, 'unloaded')
                self.release_gate(self.pending_download_poll_gates.get(model))

    def wait_for_gate(self, name, timeout=None):
        if name is None:
            return True
        if name not in self.gates:
            raise ValueError(f'Unknown fixture gate: {name}')
        return self.gates[name].wait(timeout) and not self.closed

    def wait_for_model_poll(self):
        pending = [(model, gate, 'loading')
                   for model, gate in self.pending_load_poll_gates.items()]
        pending.extend((model, gate, 'downloading')
                       for model, gate in self.pending_download_poll_gates.items())
        for model, gate, status in pending:
            entry = next((value for value in self.models if value.get('id') == model), None)
            if gate is None or entry is None or entry.get('status', {}).get('value') != status:
                continue
            polls = self.model_poll_counts.get(model, 0)
            self.model_poll_counts[model] = polls + 1
            if polls > 0 and not self.gate_released(gate):
                self.model_poll_waiting.set()
                self.wait_for_gate(gate)

    def set_progress(self, model, status):
        entry = next((value for value in self.models if value.get('id') == model), None)
        if entry is None:
            return
        entry.setdefault('status', {})['progress'] = dict(stages=['text_model'],
                                                          current='text_model', value=0.5)
        entry['status']['value'] = status

    def set_status(self, model, status):
        entry = next((entry for entry in self.models if entry.get('id') == model), None)
        if entry is None:
            entry = dict(id=model, status=dict(value=status))
            self.models.append(entry)
        else:
            entry.setdefault('status', {})['value'] = status

    def close(self):
        self.closed = True
        for gate in self.gates.values():
            gate.set()
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)
