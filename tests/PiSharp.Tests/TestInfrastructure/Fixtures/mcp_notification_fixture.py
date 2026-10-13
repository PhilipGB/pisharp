"""MCP stdio server that announces updated tool and resource lists after its first call."""

import json
import os
import sys


state_path = os.environ["MCP_FIXTURE_NOTIFICATION_STATE"]
state = {"calls": 0, "toolsList": 0, "resourcesList": 0, "templatesList": 0}


def save_state():
    temporary = state_path + ".tmp"
    with open(temporary, "w", encoding="utf-8") as output:
        json.dump(state, output)
    os.replace(temporary, state_path)


def send(message):
    sys.stdout.write(json.dumps(message, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def respond(request, result):
    send({"jsonrpc": "2.0", "id": request["id"], "result": result})


def tool(name, description):
    return {
        "name": name,
        "description": description,
        "inputSchema": {
            "type": "object",
            "properties": {"value": {"type": "string"}},
            "required": ["value"],
        },
    }


for line in sys.stdin:
    request = json.loads(line)
    if "id" not in request:
        continue
    method = request.get("method")
    if method == "initialize":
        respond(request, {
            "protocolVersion": request["params"]["protocolVersion"],
            "capabilities": {"tools": {}, "resources": {}},
            "serverInfo": {"name": "notification-fixture", "version": "1.0"},
        })
    elif method == "tools/list":
        state["toolsList"] += 1
        save_state()
        if state["calls"] > 0:
            tools = [tool("added", "Tool added after a list notification.")]
        else:
            tools = [tool("echo", "Echo a value before the list notification.")]
        respond(request, {"tools": tools})
    elif method == "resources/list":
        state["resourcesList"] += 1
        save_state()
        resource_name = "updated" if state["calls"] > 0 else "initial"
        respond(request, {"resources": [{
            "uri": "fixture://" + resource_name,
            "name": resource_name,
            "mimeType": "text/plain",
        }]})
    elif method == "resources/templates/list":
        state["templatesList"] += 1
        save_state()
        respond(request, {"resourceTemplates": []})
    elif method == "tools/call":
        name = request["params"]["name"]
        value = request["params"]["arguments"]["value"]
        state["calls"] += 1
        save_state()
        prefix = "added:" if name == "added" else "echo:"
        respond(request, {"content": [{"type": "text", "text": prefix + value}], "isError": False})
        if state["calls"] == 1:
            send({"jsonrpc": "2.0", "method": "notifications/tools/list_changed"})
            send({"jsonrpc": "2.0", "method": "notifications/resources/list_changed"})
    else:
        send({
            "jsonrpc": "2.0",
            "id": request["id"],
            "error": {"code": -32601, "message": "Method not found"},
        })
