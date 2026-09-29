"""MCP stdio server that drops its first tool call and succeeds after reconnect."""

import json
import os
import sys


counter_path = os.environ["MCP_FIXTURE_RECONNECT_COUNT"]
try:
    with open(counter_path, "r", encoding="utf-8") as counter:
        start_count = int(counter.read())
except FileNotFoundError:
    start_count = 0
start_count += 1
with open(counter_path, "w", encoding="utf-8") as counter:
    counter.write(str(start_count))


def respond(request, result):
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": request["id"], "result": result}) + "\n")
    sys.stdout.flush()


for line in sys.stdin:
    request = json.loads(line)
    if "id" not in request:
        continue
    method = request.get("method")
    if method == "initialize":
        respond(request, {
            "protocolVersion": request["params"]["protocolVersion"],
            "capabilities": {"tools": {}},
            "serverInfo": {"name": "reconnect-fixture", "version": "1.0"},
        })
    elif method == "tools/list":
        respond(request, {"tools": [{
            "name": "echo",
            "description": "Echo after reconnect.",
            "inputSchema": {"type": "object", "properties": {"value": {"type": "string"}}},
        }]})
    elif method == "tools/call":
        if start_count == 1:
            os._exit(17)
        respond(request, {"content": [{
            "type": "text",
            "text": "reconnected:" + request["params"]["arguments"]["value"],
        }]})
    else:
        sys.stdout.write(json.dumps({
            "jsonrpc": "2.0",
            "id": request["id"],
            "error": {"code": -32601, "message": "Method not found"},
        }) + "\n")
        sys.stdout.flush()
