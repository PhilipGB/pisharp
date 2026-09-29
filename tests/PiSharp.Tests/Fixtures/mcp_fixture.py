"""Small deterministic MCP stdio server used by the integration test."""

import json
import os
import sys


def respond(request, result):
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": request["id"], "result": result}) + "\n")
    sys.stdout.flush()


for line in sys.stdin:
    request = json.loads(line)
    if "id" not in request:
        continue
    method = request.get("method")
    if method == "initialize":
        capabilities = {"resources": {}}
        if os.getenv("MCP_FIXTURE_RESOURCES_ONLY") != "1":
            capabilities["tools"] = {}
        respond(request, {
            "protocolVersion": request["params"]["protocolVersion"],
            "capabilities": capabilities,
            "serverInfo": {"name": "pisharp-fixture", "version": "1.0.0"},
        })
    elif method == "tools/list":
        respond(request, {"tools": [
            {"name": "echo", "description": "Echo input text.",
             "inputSchema": {"type": "object", "properties": {"value": {"type": "string"}},
                             "required": ["value"]},
             "outputSchema": {"type": "object", "properties": {"echo": {"type": "string"}},
                              "required": ["echo"]}},
            {"name": "fail", "description": "Return a tool error.",
             "inputSchema": {"type": "object", "properties": {}}},
        ]})
    elif method == "tools/call":
        params = request["params"]
        if params["name"] == "echo":
            value = params["arguments"]["value"]
            respond(request, {"content": [{"type": "text", "text": value}],
                              "structuredContent": {"echo": value}, "isError": False})
        else:
            respond(request, {"content": [{"type": "text", "text": "fixture failure"}],
                              "isError": True})
    elif method == "resources/list":
        respond(request, {"resources": [{"uri": "fixture://note", "name": "note", "mimeType": "text/plain"},
                                         {"uri": "ui://fixture", "name": "app", "mimeType": "text/html"}]})
    elif method == "resources/templates/list":
        respond(request, {"resourceTemplates": [
            {"uriTemplate": "fixture://notes/{id}", "name": "notes", "mimeType": "text/plain"}]})
    elif method == "resources/read":
        respond(request, {"contents": [
            {"uri": request["params"]["uri"], "mimeType": "text/plain", "text": "fixture resource"}]})
    else:
        sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": request["id"],
                                     "error": {"code": -32601, "message": "Method not found"}}) + "\n")
        sys.stdout.flush()
