#!/usr/bin/env python3
"""Deterministic RPC fixture; never invoke for real implementation work."""
import json
from pathlib import Path
import sys

ledger, log, mode = Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3]
for line in sys.stdin:
    request = json.loads(line)
    task_id = request["message"].split("Current task: ", 1)[1].split(":", 1)[0]
    with log.open("a", encoding="utf-8") as output:
        output.write(task_id + "\n")
    print(json.dumps({"type": "response", "id": request["id"], "command": "prompt", "success": True}), flush=True)
    if mode == "crash-once" and not log.with_suffix(".restarted").exists():
        log.with_suffix(".restarted").touch()
        sys.exit(23)
    if mode in ("limit", "model-error"):
        detail = "Codex error: The usage limit has been reached" if mode == "limit" else "Provider unavailable"
        print(json.dumps({"type": "message_end", "message": {"role": "assistant",
            "stopReason": "error", "errorMessage": detail}}), flush=True)
        print(json.dumps({"type": "agent_settled"}), flush=True)
        continue
    if mode != "never-update":
        value = json.loads(ledger.read_text())
        task = next(t for t in value["tasks"] if t["id"] == task_id)
        task.update(status="verified", implementation=["fake"], evidence=["deterministic RPC fixture"])
        ledger.write_text(json.dumps(value))
    print(json.dumps({"type": "agent_settled"}), flush=True)
