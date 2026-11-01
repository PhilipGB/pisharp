#!/usr/bin/env python3
"""Deterministic MCP stdio server for the core-loop active-queue parity corpus.

Tools exposed (both products see the identical declarations):
  append      {id}           appends one line to the effects file (non-idempotent).
  gate_append {id, token}    waits until the token is released, then appends one line.
  gate        {token}        waits until the token is released, then succeeds (no side effect).
  fail        {}             returns an MCP tool error.

Gate tokens are released by the driver creating `<markers>/<token>.released`.
Marker files record call start/release/completion so the driver can synchronise
on physical tool execution without sleeps.
"""
import json
import os
import sys
import time

MARKERS = os.environ['CL_MARKERS']
EFFECTS = os.environ['CL_EFFECTS']
LOG = os.environ.get('CL_REQUEST_LOG')


def marker(name):
    os.makedirs(MARKERS, exist_ok=True)
    with open(os.path.join(MARKERS, name), 'w', encoding='utf-8') as handle:
        handle.write('1')


def released(token):
    return os.path.exists(os.path.join(MARKERS, token + '.released'))


def wait_released(token):
    while not released(token):
        time.sleep(0.005)


def log_call(name, args):
    if LOG:
        with open(LOG, 'a', encoding='utf-8') as handle:
            handle.write(json.dumps({'name': name, 'args': args}, sort_keys=True) + '\n')


def respond(request, result):
    sys.stdout.write(json.dumps({'jsonrpc': '2.0', 'id': request['id'], 'result': result}) + '\n')
    sys.stdout.flush()


TOOLS = [
    {'name': 'append', 'description': 'Append one line.',
     'inputSchema': {'type': 'object', 'properties': {'id': {'type': 'string'}}, 'required': ['id']}},
    {'name': 'gate_append', 'description': 'Wait for a release, then append one line.',
     'inputSchema': {'type': 'object', 'properties': {'id': {'type': 'string'}, 'token': {'type': 'string'}},
                     'required': ['id', 'token']}},
    {'name': 'gate', 'description': 'Wait for a release.',
     'inputSchema': {'type': 'object', 'properties': {'token': {'type': 'string'}}, 'required': ['token']}},
    {'name': 'fail', 'description': 'Return a tool error.',
     'inputSchema': {'type': 'object', 'properties': {}}},
]


def handle_call(request, write_lock=None):
    params = request['params']
    name = params['name']
    args = params.get('arguments') or {}
    log_call(name, args)
    if name == 'append':
        marker(f'append-{args["id"]}-started')
        with open(EFFECTS, 'a', encoding='utf-8') as handle:
            handle.write(args['id'] + '\n')
        marker(f'append-{args["id"]}-done')
        _respond(request, {'content': [{'type': 'text', 'text': f'appended {args["id"]}'}], 'isError': False}, write_lock)
    elif name == 'gate_append':
        marker(f'gate-append-{args["id"]}-started')
        wait_released(args['token'])
        marker(f'gate-append-{args["id"]}-released')
        with open(EFFECTS, 'a', encoding='utf-8') as handle:
            handle.write(args['id'] + '\n')
        marker(f'gate-append-{args["id"]}-done')
        _respond(request, {'content': [{'type': 'text', 'text': f'appended {args["id"]}'}], 'isError': False}, write_lock)
    elif name == 'gate':
        marker(f'gate-{args["token"]}-started')
        wait_released(args['token'])
        marker(f'gate-{args["token"]}-done')
        _respond(request, {'content': [{'type': 'text', 'text': f'released {args["token"]}'}], 'isError': False}, write_lock)
    elif name == 'fail':
        _respond(request, {'content': [{'type': 'text', 'text': 'core-loop fixture failure'}], 'isError': True}, write_lock)
    else:
        _respond(request, {'content': [{'type': 'text', 'text': f'unknown tool {name}'}], 'isError': True}, write_lock)


def handle_call_locked(request, write_lock):
    handle_call(request, write_lock)


def _respond(request, result, write_lock=None):
    if write_lock is not None:
        with write_lock:
            sys.stdout.write(json.dumps({'jsonrpc': '2.0', 'id': request['id'], 'result': result}) + '\n')
            sys.stdout.flush()
    else:
        respond(request, result)


def main():
    import threading
    write_lock = threading.Lock()
    respond_locked = lambda request, result: (write_lock.acquire(),  # noqa: F841
                                               sys.stdout.write(json.dumps({'jsonrpc': '2.0', 'id': request['id'], 'result': result}) + '\n'),
                                               sys.stdout.flush(),
                                               write_lock.release())
    marker('mcp-ready')
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        request = json.loads(line)
        method = request.get('method')
        if method == 'initialize':
            respond_locked(request, {'protocolVersion': request['params']['protocolVersion'],
                                     'capabilities': {'tools': {}},
                                     'serverInfo': {'name': 'core-loop-fixture', 'version': '1.0.0'}})
        elif 'id' not in request:
            continue
        elif method == 'tools/list':
            respond_locked(request, {'tools': TOOLS})
        elif method == 'tools/call':
            # Tool calls may be concurrent: handle each in its own worker so a
            # gated call never blocks another in-flight call.
            threading.Thread(target=lambda r=request: handle_call_locked(r, write_lock), daemon=True).start()
        elif method == 'ping':
            respond_locked(request, {})
        elif method in ('shutdown', 'exit'):
            break


if __name__ == '__main__':
    main()
