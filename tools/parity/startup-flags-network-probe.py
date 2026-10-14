#!/usr/bin/env python3
"""Exercise the startup-flags process fixture against a deliberately stalled local catalogue."""
import argparse
import concurrent.futures
import json
import os
from pathlib import Path
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = Path(__file__).resolve().parents[2]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--workers', type=int, choices=[1, 8], default=1)
    parser.add_argument('--stall', choices=['headers', 'body'], default='body')
    args = parser.parse_args()
    requests = []

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *values):
            pass

        def do_GET(self):
            requests.append(self.path)
            try:
                body = b'{"data":[]}'
                if args.stall == 'headers':
                    time.sleep(16)
                self.send_response(200)
                self.send_header('Content-Length', str(len(body)))
                self.end_headers()
                self.wfile.flush()
                if args.stall == 'body':
                    time.sleep(16)
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass

    server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, PISHARP_BASE_URL=f'http://127.0.0.1:{server.server_port}/v1')
    started = time.monotonic()

    def run(index):
        result = subprocess.run(['dotnet', 'vstest',
            str(ROOT / 'tests/PiSharp.Tests/bin/Debug/net10.0/PiSharp.Tests.dll'),
            '--TestCaseFilter:FullyQualifiedName~StartupModelAndNameArePersistedAndConflictingResumeIsRejected'],
            cwd=ROOT, env=env, capture_output=True, text=True, timeout=55)
        args.output.with_name(args.output.stem + f'-{index}.log').write_text(result.stdout + result.stderr)
        return result.returncode

    try:
        with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
            results = list(pool.map(run, range(args.workers)))
        summary = dict(exitCodes=results, requests=requests, stall=args.stall,
                       elapsedSeconds=round(time.monotonic() - started, 2))
        args.output.write_text(json.dumps(summary, indent=2) + '\n')
        print(json.dumps(summary))
    finally:
        server.shutdown()
        server.server_close()
        worker.join(timeout=5)


if __name__ == '__main__':
    main()
