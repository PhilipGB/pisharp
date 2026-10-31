#!/usr/bin/env python3
"""Manual debug: start provider, spawn the product RPC, send one prompt, dump output."""
import json
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from harness import Provider, ProductRun  # noqa: E402

PI = Path('/home/philip/Projects/pi-pinned')
PISHARP_DLL = Path('/home/philip/Projects/pisharp/src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll')


def main():
    product = sys.argv[1] if len(sys.argv) > 1 else 'pisharp'
    work = Path(tempfile.mkdtemp(prefix='cl-debug-'))
    print('workdir:', work)
    provider = Provider([dict(t='text', text='PILOT_OK')], work / 'gates')
    run = ProductRun(product, PI, PISHARP_DLL, work, provider, work / 'gates')
    run.start()
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        run.pump()
        if run.marker_exists('mcp-ready'):
            break
        time.sleep(0.1)
    else:
        print('MCP never ready')
    print('stderr so far:', run.stderr_log.read_text()[:2000])
    run._send_raw(dict(id='p1', type='prompt', message='GO'))
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        run.pump()
        if any(r.get('type') == 'agent_settled' for r in run.records):
            break
        time.sleep(0.1)
    print(json.dumps(run.records, indent=1, default=str)[:8000])
    print('PROVIDER REQUESTS:', len(provider.requests))
    for r in provider.requests:
        print(json.dumps(r['body'])[:600])
    run.exit_clean()
    print('exit ok; workdir kept:', work)


if __name__ == '__main__':
    main()
