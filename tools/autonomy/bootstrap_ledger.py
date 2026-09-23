#!/usr/bin/env python3
"""One-time ledger bootstrap from the checked-in parity inventory; never overwrite an existing ledger.

The user described an appended original plan, but no plan text followed the heading in that message.
Until it is provided the resulting ledger is a conservative provisional inventory, not a completion gate.
"""
import json
from pathlib import Path
import re
import sys

repo = Path(__file__).resolve().parents[2]
source = repo / "docs/parity/feature-matrix.md"
detail = repo / "docs/parity/detailed-inventory.md"
output = repo / "docs/parity/execution-ledger.json"


def slug(text):
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")[:100]


def clean(value):
    return re.sub(r"`|\*", "", value).strip()


def rows(section):
    return [(index, [clean(cell) for cell in line[2:-2].split(" | ")])
            for index, line in enumerate(section.splitlines(), start=1)
            if line.startswith("| ") and not line.startswith("| ---")]


def add(tasks, identifier, title, acceptance, source_path, source_line, status="not_started"):
    tasks.append({"id": identifier, "title": title, "status": status,
                  "source": f"{source_path}:{source_line}",
                  "acceptance": acceptance, "implementation": [], "evidence": [], "dependencies": []})


def main():
    if output.exists():
        raise SystemExit("Ledger already exists; refusing to overwrite maintained statuses and evidence")
    tasks = []
    matrix = source.read_text()
    for number, cells in rows(matrix):
        if len(cells) != 6 or cells[0].startswith("Category /"):
            continue
        category, reference, observable, failure, verification, status_text = cells
        status = "excluded" if category == "Pi Packages" else ("in_progress" if status_text.startswith("In progress") else "not_started")
        add(tasks, "feature-" + slug(category), category,
            [observable, failure, f"Verify through application workflow and compare {reference}; {verification}"],
            source.relative_to(repo).as_posix(), number, status)
    text = detail.read_text()
    heading = ""
    for number, line in enumerate(text.splitlines(), 1):
        if line.startswith("## "):
            heading = line[3:].split(" (`", 1)[0].strip()
        if line.startswith("| ") and not line.startswith("| ---"):
            cells = [clean(cell) for cell in line[2:-2].split(" | ")]
            if heading.startswith("Built-in interactive commands") and len(cells) == 4 and cells[0] != "Group":
                add(tasks, "commands-" + slug(cells[0]), "Interactive commands: " + cells[1],
                    [cells[2], cells[3], "Verify command results, errors and PTY workflows"],
                    detail.relative_to(repo).as_posix(), number)
            if heading == "CLI" and len(cells) == 4 and cells[0] != "Group":
                if cells[0] == "Pi Packages":
                    continue
                add(tasks, "cli-" + slug(cells[0]), "CLI flags: " + cells[1],
                    [cells[2], "Test parsed flags, conflict/recovery and process behaviour"],
                    detail.relative_to(repo).as_posix(), number)
            if heading.startswith("Terminal keyboard") and len(cells) == 3 and cells[0] != "Context":
                add(tasks, "keys-" + slug(cells[0]), "Terminal keys: " + cells[1],
                    [cells[2], "Test in PTY including configurable bindings and widget precedence"],
                    detail.relative_to(repo).as_posix(), number)
            if heading.startswith("Settings inventory") and len(cells) == 2 and cells[0] not in ("Group", "Excluded"):
                for setting in re.findall(r"`([^`]+)`", line.split("|", 2)[2]):
                    add(tasks, "settings-" + slug(setting), "Setting: " + setting,
                        [f"Support {setting} with upstream type/default and user/trusted-project precedence",
                         "Test malformed settings, trust boundaries, reload and startup behaviour"],
                        detail.relative_to(repo).as_posix(), number)
        if heading.startswith("RPC and JSON events") and line.startswith("- "):
            name, interface = line[2:].split(":", 1)
            for command in re.findall(r"`([^`]+)`", interface):
                if command in ("\n", "agent_settled", "agent_end"):
                    continue
                add(tasks, "rpc-" + slug(command), "RPC / event: " + command,
                    [f"Implement {command} using shared canonical runtime with correlated responses/event ordering",
                     "Test protocol through RPC process, including failures and cancellation"],
                    detail.relative_to(repo).as_posix(), number)
    ids = [task["id"] for task in tasks]
    if len(ids) != len(set(ids)):
        raise ValueError("Inventory generated duplicate requirement IDs")
    value = {"schema": 1, "plan_received": False,
             "source_note": "Original plan was announced after a heading but not appended. Conservative provisional inventory from pinned upstream feature matrix and detailed inventory; never treat as complete until reconciled with the missing plan and audited upstream.",
             "baseline": "earendil-works/pi@002fc8385268300ca91a5fc95f935c2afbbdac02",
             "required_ids": ids, "final_audit": {"complete": False, "evidence": []},
             "tasks": tasks}
    output.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n")
    print(f"Bootstrapped {len(tasks)} provisional requirements at {output}")


if __name__ == "__main__":
    main()
