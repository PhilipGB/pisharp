"""Deterministic stdio MCP server packaged beside the external extension."""
import json
import sys

revision = sys.argv[1]
operation = sys.argv[2]
tool_name = "echo_v2" if revision == "v2" else "echo_v3" if revision == "v3" else "echo"

for line in sys.stdin:
    request = json.loads(line)
    method = request.get("method")
    if method == "exit":
        break
    if "id" not in request:
        continue
    if method == "initialize":
        result = {
            "protocolVersion": request["params"]["protocolVersion"],
            "capabilities": {"tools": {}},
            "serverInfo": {"name": "native-fixture-mcp", "version": "1.0.0"},
        }
    elif method == "shutdown":
        result = {}
    elif method == "tools/list":
        result = {"tools": [{
            "name": tool_name,
            "description": "Return the supplied fixture value.",
            "inputSchema": {"type": "object", "properties": {"value": {"type": "string"}},
                            "required": ["value"]},
        }]}
    elif method == "tools/call":
        result = {"content": [{"type": "text", "text": "native-mcp-result:" + request["params"]["arguments"]["value"]}],
                  "isError": False}
    else:
        sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": request["id"],
                                     "error": {"code": -32601, "message": "Method not found"}}) + "\n")
        sys.stdout.flush()
        continue
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": request["id"], "result": result}) + "\n")
    sys.stdout.flush()
