import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Thread


class FixtureServer:
    def __init__(self):
        self.requests = []
        outer = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers.get('Content-Length', 0))))
                outer.requests.append(dict(method='POST', path=self.path,
                                           authorization=self.headers.get('Authorization'), body=body))
                if self.path != '/v1/chat/completions':
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

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)
