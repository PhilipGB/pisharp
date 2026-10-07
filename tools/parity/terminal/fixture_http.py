import json
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Thread
from urllib.parse import urlsplit


class FixtureServer:
    def __init__(self, models=None, props=None, behavior=None):
        self.requests = []
        self.initial_models = json.loads(json.dumps(models or []))
        self.props = props or {}
        self.behavior = behavior or {}
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
                    now = time.monotonic()
                    for entry in outer.models:
                        deadline = outer.pending_loads.get(entry.get('id'))
                        if deadline is not None and now >= deadline:
                            entry['status']['value'] = 'loaded'
                            del outer.pending_loads[entry['id']]
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
                    self.wfile.write(b'data: {}\n\n')
                    self.wfile.flush()
                    return
                self.send_error(404)

            def do_POST(self):
                raw = self.rfile.read(int(self.headers.get('Content-Length', 0)))
                body = json.loads(raw) if raw else {}
                path = urlsplit(self.path).path
                outer.requests.append(dict(method='POST', path=self.path,
                                           authorization=self.headers.get('Authorization'), body=body))
                if self.path != '/v1/chat/completions':
                    if path == '/models/load':
                        model = body.get('model')
                        for entry in outer.models:
                            if entry.get('id') == model:
                                delay = float(outer.behavior.get('loadDelaySeconds', 0))
                                entry['status']['value'] = 'loading' if delay > 0 else 'loaded'
                                entry['status'].pop('failed', None)
                                if delay > 0:
                                    outer.pending_loads[model] = time.monotonic() + delay
                        self.send_json(dict(success=True))
                        return
                    if path == '/models/unload':
                        model = body.get('model')
                        for entry in outer.models:
                            if entry.get('id') == model:
                                entry['status']['value'] = 'unloaded'
                        outer.pending_loads.pop(model, None)
                        self.send_json(dict(success=True))
                        return
                    if path == '/models':
                        model = body.get('model')
                        if not any(entry.get('id') == model for entry in outer.models):
                            outer.models.append(dict(id=model, status=dict(value='downloading')))
                        self.send_json(dict(success=True))
                        return
                    self.send_error(404)
                    return
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
        self.requests.clear()

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)
