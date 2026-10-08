import json
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Thread
from urllib.parse import unquote, urlsplit


class FixtureServer:
    def __init__(self, models=None, props=None, behavior=None, huggingface=None):
        self.requests = []
        self.initial_models = json.loads(json.dumps(models or []))
        self.props = props or {}
        self.behavior = behavior or {}
        self.huggingface = huggingface or {}
        self.closed = False
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
                self.wfile.write(payload)

            def do_GET(self):
                path = urlsplit(self.path).path
                outer.requests.append(dict(method='GET', path=self.path,
                                           authorization=self.headers.get('Authorization')))
                if path == '/models':
                    if outer.catalog_failures_remaining > 0:
                        outer.catalog_failures_remaining -= 1
                        self.send_json(dict(error=dict(message='fixture router unavailable')), status=503)
                        return
                    now = time.monotonic()
                    for entry in outer.models:
                        deadline = outer.pending_loads.get(entry.get('id'))
                        if deadline is not None and now >= deadline:
                            entry['status']['value'] = 'loaded'
                            del outer.pending_loads[entry['id']]
                            outer.pending_load_progress.pop(entry['id'], None)
                    self.send_json(dict(object='list', data=outer.models))
                    return
                if path == '/props':
                    self.send_json(outer.props)
                    return
                if path == '/models/sse':
                    self.send_response(200)
                    self.send_header('Content-Type', 'text/event-stream')
                    self.send_header('Cache-Control', 'no-cache')
                    self.end_headers()
                    sent_load_progress = set()
                    sent_download_progress = set()
                    try:
                        while not outer.closed:
                            now = time.monotonic()
                            for model, deadline in list(outer.pending_loads.items()):
                                progress_deadline = outer.pending_load_progress.get(model, 0)
                                if model not in sent_load_progress and now >= progress_deadline:
                                    self.send_event(dict(model=model, event='status_change', data=dict(
                                        status='loading', progress=dict(stages=['text_model'],
                                                                        current='text_model', value=0.5))))
                                    sent_load_progress.add(model)
                                if now >= deadline:
                                    outer.set_status(model, 'loaded')
                                    outer.pending_loads.pop(model, None)
                                    outer.pending_load_progress.pop(model, None)
                                    self.send_event(dict(model=model, event='status_change', data=dict(status='loaded')))
                            for model, deadline in list(outer.pending_downloads.items()):
                                progress_deadline = outer.pending_download_progress.get(model, 0)
                                if model not in sent_download_progress and now >= progress_deadline:
                                    self.send_event(dict(model=model, event='download_progress', data=dict(progress={
                                        'https://fixture.invalid/model.gguf': dict(done=512, total=1024)})))
                                    sent_download_progress.add(model)
                                if now >= deadline:
                                    outer.set_status(model, 'unloaded')
                                    outer.pending_downloads.pop(model, None)
                                    outer.pending_download_progress.pop(model, None)
                                    self.send_event(dict(model=model, event='download_finished', data={}))
                            self.wfile.flush()
                            time.sleep(0.05)
                    except (BrokenPipeError, ConnectionResetError):
                        pass
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

            def do_POST(self):
                raw = self.rfile.read(int(self.headers.get('Content-Length', 0)))
                body = json.loads(raw) if raw else {}
                path = urlsplit(self.path).path
                outer.requests.append(dict(method='POST', path=self.path,
                                           authorization=self.headers.get('Authorization'), body=body))
                if path == '/models/load':
                    model = body.get('model')
                    for entry in outer.models:
                        if entry.get('id') == model:
                            delays = outer.behavior.get('loadDelaySecondsByModel', {})
                            delay = float(delays.get(model, outer.behavior.get('loadDelaySeconds', 0)))
                            progress_delays = outer.behavior.get('loadProgressDelaySecondsByModel', {})
                            progress_delay = float(progress_delays.get(
                                model, outer.behavior.get('loadProgressDelaySeconds', 0)))
                            entry['status']['value'] = 'loading' if delay > 0 else 'loaded'
                            entry['status'].pop('failed', None)
                            if delay > 0:
                                started = time.monotonic()
                                outer.pending_loads[model] = started + delay
                                outer.pending_load_progress[model] = started + max(progress_delay, 0)
                    self.send_json(dict(success=True))
                    return
                if path == '/models/unload':
                    model = body.get('model')
                    for entry in outer.models:
                        if entry.get('id') == model:
                            entry['status']['value'] = 'unloaded'
                    outer.pending_loads.pop(model, None)
                    outer.pending_load_progress.pop(model, None)
                    outer.pending_downloads.pop(model, None)
                    outer.pending_download_progress.pop(model, None)
                    self.send_json(dict(success=True))
                    return
                if path == '/models':
                    model = body.get('model')
                    outer.set_status(model, 'downloading')
                    delay = float(outer.behavior.get('downloadDelaySeconds', 0.75))
                    progress_delay = float(outer.behavior.get('downloadProgressDelaySeconds', 0))
                    started = time.monotonic()
                    outer.pending_downloads[model] = started + max(delay, 0)
                    outer.pending_download_progress[model] = started + max(progress_delay, 0)
                    if delay <= 0:
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
        self.models = json.loads(json.dumps(self.initial_models))
        self.pending_loads = {}
        self.pending_load_progress = {}
        self.pending_downloads = {}
        self.pending_download_progress = {}
        self.catalog_failures_remaining = int(self.behavior.get('initialModelFailures', 0))
        self.requests.clear()

    def set_status(self, model, status):
        entry = next((entry for entry in self.models if entry.get('id') == model), None)
        if entry is None:
            entry = dict(id=model, status=dict(value=status))
            self.models.append(entry)
        else:
            entry.setdefault('status', {})['value'] = status

    def close(self):
        self.closed = True
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)
