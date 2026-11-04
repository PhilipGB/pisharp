#!/usr/bin/env python3
"""Extract compact paired checkpoints for the F01-F03 session-picker findings."""
import argparse
import json
import re
from pathlib import Path

import sys
sys.path.insert(0, str(Path(__file__).resolve().parent))
from keys_queue_session_evidence import load, viewport_lines, picker_panel, user_texts, request_facts

ROOT = Path(__file__).resolve().parent.parent.parent
FIXTURES = {
    "terminal-session-picker-findings": {
        "case": "140x40-dark-fullscreen",
        "artifacts": "main",
        "names": ("duplicate one", "duplicate two", "tree parent", "tree child", "orphan child"),
    },
    "terminal-session-picker-disabled-confirm": {
        "case": "140x40-dark-regular",
        "artifacts": "disabled",
        "names": ("victim session",),
    },
}
TEMPORARY_PRIVATE_MODES = ("1000", "1002", "1003", "1004", "1006", "1049", "2004", "2026")
PICKER_STATUS_PREFIXES = ("Session deleted", "Session moved to trash")


def picker_projection(lines, names):
    panel = picker_panel(lines)
    if not panel:
        return {"closed": True}
    title = next((line for line in panel if "Resume Session" in line), "")
    scope = None
    sort = None
    name_filter = None
    path_visible = None
    rows = []
    selected = None
    confirmation = False
    confirmation_keys = None
    statuses = []
    ordered_names = sorted(names, key=len, reverse=True)
    for line in panel:
        if "Resume Session (All)" in line or "◉ All" in line:
            scope = "all"
        elif "Resume Session (Current Folder)" in line or "◉ Current Folder" in line:
            scope = "current"
        match = re.search(r"Sort: (Threaded|Recent|Fuzzy)", line)
        if match:
            sort = match.group(1)
        match = re.search(r"Name: (All|Named)", line)
        if match:
            name_filter = match.group(1)
        if "path (on)" in line:
            path_visible = True
        elif "path (off)" in line:
            path_visible = False
        if "Delete " in line and "confirm" in line:
            confirmation = True
            segment = line[line.index("Delete "):]
            confirm = re.search(r"\?\s*(.*?)\s*confirm", segment)
            cancel = re.search(r"confirm\s*·\s*(.*?)(?:\s+(?:cancel|close)\b|$)", segment)
            confirmation_keys = {
                "confirm": (confirm.group(1) if confirm else "").strip().casefold(),
                "cancel": (cancel.group(1) if cancel else "").strip().casefold(),
            }
        for prefix in PICKER_STATUS_PREFIXES:
            if prefix in line:
                statuses.append(prefix)
        name = next((candidate for candidate in ordered_names if candidate in line), None)
        if name is None or any(word in line for word in
                               ("Resume Session", "Scope:", "Search:", "Sort:", "Name:", "Up/Down",
                                "Delete ", "Enter select", "Session deleted", "Session moved to trash")):
            continue
        stripped = line.strip()
        is_selected = (stripped.startswith("›") or "| >" in line or stripped.startswith("> ")) \
            if name in line else False
        if is_selected:
            selected = name
        branch = re.search(r"([│ ]*[├└]─ )", line[:line.index(name)])
        rows.append({"name": name, "prefix": branch.group(1).strip() if branch else ""})
    if confirmation or statuses:
        sort = None
        name_filter = None
        path_visible = None
    return {"scope": scope, "sort": sort, "nameFilter": name_filter,
            "pathVisible": path_visible, "rows": rows, "selected": selected,
            "confirmation": confirmation, "confirmationKeys": confirmation_keys,
            "statuses": statuses if "Resume Session" in title else []}


def checkpoint(frame, names):
    state = frame["state"]
    modes = state.get("modes", {})
    private = modes.get("private", {})
    cursor = state.get("cursor", {})
    action = frame["id"]
    result = {
        "action": action,
        "activeScreen": state.get("activeScreen"),
        "temporaryModes": sorted(mode for mode in TEMPORARY_PRIVATE_MODES if private.get(mode, False)),
        "bracketedPasteMode": bool(modes.get("bracketedPasteMode", False)),
        "cursorVisible": bool(cursor.get("visible", False)),
    }
    if action == "quit":
        return result
    if action.startswith("picker"):
        result["picker"] = picker_projection(viewport_lines(state), names)
    return result


def normalize(value):
    value = json.loads(json.dumps(value))
    if "2026" in value.get("temporaryModes", []):
        value["temporaryModes"] = [mode for mode in value["temporaryModes"] if mode != "2026"]
    picker = value.get("picker")
    if picker and (picker.get("confirmation") or picker.get("statuses")):
        picker["sort"] = None
        picker["nameFilter"] = None
        picker["pathVisible"] = None
    if picker:
        picker["statuses"] = []
        if value.get("action") == "picker-delete-applied":
            picker["selected"] = None
    return value


def session_stems(files):
    stems = set()
    for path in files:
        name = Path(path).name
        if name.endswith(".lock"):
            continue
        for suffix in (".session.json", ".jsonl"):
            if name.endswith(suffix):
                stems.add(name[:-len(suffix)])
                break
    return sorted(stems)


def product(fixture_id, assertion):
    names = FIXTURES[fixture_id]["names"]
    return {
        "checkpoints": {frame["id"]: checkpoint(frame, names) for frame in assertion["frames"]},
        "requests": user_texts(assertion.get("http", [])),
        "requestFacts": request_facts(assertion.get("http", [])),
        "sessionFiles": session_stems(assertion.get("capturedSessionFiles", [])),
    }


def differences(left, right):
    issues = []
    if set(left["checkpoints"]) != set(right["checkpoints"]):
        issues.append(f"checkpoint ids differ: {sorted(set(left['checkpoints']) ^ set(right['checkpoints']))}")
        return issues
    for checkpoint_id in left["checkpoints"]:
        if normalize(left["checkpoints"][checkpoint_id]) != normalize(right["checkpoints"][checkpoint_id]):
            issues.append(checkpoint_id)
    if left["requests"] != right["requests"]:
        issues.append("provider user requests")
    if left["requestFacts"] != right["requestFacts"]:
        issues.append("provider request facts")
    if left["sessionFiles"] != right["sessionFiles"]:
        issues.append(f"session files {left['sessionFiles']} != {right['sessionFiles']}")
    return issues


def load_product(artifact_dir, fixture_id, case_id):
    report = json.loads((artifact_dir / "report.json").read_text(encoding="utf-8"))
    report_case = next(case for case in report["cases"] if case["id"] == case_id)
    assertion = load(artifact_dir / report_case["assertionEvidence"])
    assertion["fixture"] = fixture_id
    return report, report_case, assertion


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, default=ROOT / "artifacts/parity")
    parser.add_argument("--output", type=Path,
                        default=ROOT / "docs/parity/fixtures/session-picker-post-merge-checkpoints-2026-11-04.json")
    args = parser.parse_args()

    cases = {}
    pi_sha = None
    for fixture_id, metadata in FIXTURES.items():
        case_id = metadata["case"]
        paired_dir = args.artifacts / f"session-picker-findings-{metadata['artifacts']}"
        calibration_dir = args.artifacts / f"session-picker-findings-{metadata['artifacts']}-calibration"
        paired_report, report_case, assertion = load_product(paired_dir, fixture_id, case_id)
        calibration_report, calibration_case, calibration_assertion = load_product(
            calibration_dir, fixture_id, case_id)
        pi_sha = paired_report["piSha"]
        paired = {name: product(fixture_id, assertion[name]) for name in ("pi", "pisharp")}
        calibration = {name: product(fixture_id, calibration_assertion[name]) for name in ("pi", "pisharp")}
        paired_issues = differences(paired["pi"], paired["pisharp"])
        calibration_issues = differences(calibration["pi"], calibration["pisharp"])
        cases[fixture_id] = {
            "fixture": fixture_id,
            "case": case_id,
            "paired": {
                "scenarioErrors": report_case.get("scenarioErrors") or {},
                "httpMatch": report_case.get("httpMatch"),
                "rawHttpDifferenceCount": report_case.get("rawHttpDifferenceCount"),
                "renderDifferenceCount": report_case.get("renderDifferenceCount"),
                "semanticDifferences": len(paired_issues),
                "semanticFirstDifferences": paired_issues[:20],
            },
            "calibration": {
                "scenarioErrors": calibration_case.get("scenarioErrors") or {},
                "httpMatch": calibration_case.get("httpMatch"),
                "renderDifferenceCount": calibration_case.get("renderDifferenceCount"),
                "semanticDifferences": len(calibration_issues),
                "semanticFirstDifferences": calibration_issues[:20],
            },
            "pi": paired["pi"],
            "pisharp": paired["pisharp"],
        }

    output = {
        "capability": "session-picker-post-merge-findings",
        "piSha": pi_sha,
        "source": ["tools/parity/terminal/fixtures/terminal-session-picker-findings.json",
                   "tools/parity/terminal/fixtures/terminal-session-picker-disabled-confirm.json"],
        "caseCount": len(cases),
        "cases": cases,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {len(cases)} paired cases to {args.output}")
    for fixture_id, case in cases.items():
        print(fixture_id, "paired differences", case["paired"]["semanticDifferences"],
              "calibration differences", case["calibration"]["semanticDifferences"])
        if case["paired"]["semanticFirstDifferences"]:
            print("  ", case["paired"]["semanticFirstDifferences"])
        if case["calibration"]["semanticFirstDifferences"]:
            print("  calibration:", case["calibration"]["semanticFirstDifferences"])


if __name__ == "__main__":
    main()
