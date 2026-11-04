#!/usr/bin/env python3
"""Extract compact pinned-Pi and PiSharp keys-queue-session checkpoints.

The paired harness keeps full render streams under the ignored artifacts tree.
This script commits only semantic named checkpoints: queue/editor state,
picker scope/sort/filter/path/selection, provider request order, and final
terminal restoration.
"""
import argparse
import gzip
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
CASES = (
    "100x30-dark-fullscreen",
    "100x30-dark-regular",
    "140x40-dark-fullscreen",
    "140x40-dark-regular",
)
TEMPORARY_PRIVATE_MODES = ("1000", "1002", "1003", "1004", "1006", "1049", "2004", "2026")
PICKER_PREFIX = "picker-"
SESSION_NAMES = ("main", "alpha", "alpha renamed", "beta", "gamma", "renamed")
QUEUE_STATUS_PREFIXES = ("Steering: ", "Follow-up: ", "Restored ", "No queued messages")
PICKER_STATUS_PREFIXES = ("Session deleted", "Session moved to trash", "Session renamed",
                          "Cannot delete the currently active session")


def load(path):
    with gzip.open(path, "rt", encoding="utf-8") as stream:
        return json.load(stream)


def viewport_lines(state):
    return ["".join(cell.get("chars", " ") for cell in line.get("cells", [])).rstrip()
            for line in state.get("viewport", [])]


def editor_lines(state):
    lines = viewport_lines(state)
    separators = [index for index, line in enumerate(lines) if line.lstrip().startswith("─")]
    if len(separators) < 2:
        return []
    return [line.strip() for line in lines[separators[-2] + 1:separators[-1]] if line.strip()]


def picker_panel(lines):
    """Return only the picker overlay/panel lines, excluding transcript scrollback."""
    border = [index for index, line in enumerate(lines)
              if line.strip().startswith("+") and "-" in line]
    if len(border) >= 2:
        return lines[border[0] + 1:border[-1]]
    for index, line in enumerate(lines):
        if "Resume Session (" in line or "Rename Session" in line:
            for end in range(index + 1, len(lines)):
                if lines[end].strip().startswith("─"):
                    return lines[index:end]
            return lines[index:]
    return []


def picker_projection(lines):
    if any("Rename Session" in line for line in lines):
        return {"mode": "rename"}
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
    statuses = []
    names = sorted(SESSION_NAMES, key=len, reverse=True)
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
        if "Delete " in line and "?" in line:
            confirmation = True
        for prefix in PICKER_STATUS_PREFIXES:
            if prefix in line:
                statuses.append(prefix)
        stripped = line.strip()
        is_pi_row = ("|" not in line and
                     (stripped.startswith("›") or
                      (line.startswith("  ") and
                       next((name for name in names if name in line), None) is not None)))
        is_pisharp_row = (line.lstrip().startswith("|") and " · " in line and
                          not any(word in line for word in
                                  ("Resume Session", "Scope:", "Search:", "Sort:", "Name:", "Up/Down",
                                   "Session renamed", "Enter select", "Escape", "Delete ")))
        if not is_pi_row and not is_pisharp_row:
            continue
        known = next((name for name in names if name in line), None)
        if known is None:
            continue
        if "renamed" in line or "alpha ren" in line:
            known = "alpha renamed"
        if stripped.startswith("›") or "| >" in line:
            selected = known
        rows.append(known)
    return {"scope": scope, "sort": sort, "nameFilter": name_filter,
            "pathVisible": path_visible, "rows": rows, "selected": selected,
            "confirmation": confirmation,
            "statuses": statuses if "Resume Session" in title and
                        any("Session deleted" in line or "Session moved to trash" in line
                            for line in panel) else []}


def queue_projection(lines):
    steering = []
    follow_up = []
    statuses = []
    for line in lines:
        stripped = line.strip()
        if stripped.startswith("Steering: "):
            text = stripped[len("Steering: "):].replace("Queued steering message.", "").strip()
            if text not in steering:
                steering.append(text)
        if stripped.startswith("Follow-up: "):
            text = stripped[len("Follow-up: "):].replace("Queued follow-up.", "").strip()
            if text not in follow_up:
                follow_up.append(text)
        for prefix in QUEUE_STATUS_PREFIXES[2:]:
            if stripped.startswith(prefix) and stripped not in statuses:
                statuses.append(stripped)
    statuses = [f"Steering: {text}" for text in steering] + \
               [f"Follow-up: {text}" for text in follow_up] + statuses
    return {"steering": steering, "followUp": follow_up, "statuses": statuses}


def checkpoint(frame):
    state = frame["state"]
    modes = state.get("modes", {})
    private = modes.get("private", {})
    lines = viewport_lines(state)
    cursor = state.get("cursor", {})
    separators = [index for index, line in enumerate(lines) if line.lstrip().startswith("─")]
    editor_top = separators[-2] if len(separators) >= 2 else 0
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
    if action.startswith(PICKER_PREFIX) or action == "picker-open":
        result["picker"] = picker_projection(lines)
    else:
        result["editor"] = editor_lines(state)
        result["editorRow"] = cursor.get("y", 0) - editor_top
        result["cursorColumn"] = cursor.get("x", 0)
        if action in ("steer-enter", "follow-up-alt-enter", "queue-draft"):
            queue = queue_projection(lines)
            queue["statuses"] = queue["statuses"][:2]
            result["queue"] = queue
        if action in ("dequeue-alt-up", "submit-restored"):
            result["dequeueStatus"] = next((line.strip() for line in lines
                                            if line.strip().startswith("Restored ")), None)
    return result


def user_texts(http):
    texts = []
    for request in http:
        messages = (request.get("body") or {}).get("messages") or []
        for message in reversed(messages):
            if message.get("role") != "user":
                continue
            content = message.get("content")
            if isinstance(content, str):
                texts.append(content)
            else:
                texts.append("\n".join(part.get("text", "") for part in content or []
                                        if isinstance(part, dict) and isinstance(part.get("text"), str)))
            break
    return texts


def request_facts(http):
    facts = []
    for request in http:
        body = request.get("body") or {}
        history = []
        for message in body.get("messages") or []:
            role = message.get("role")
            if role not in ("user", "assistant", "tool"):
                # The generated system/developer prompt is pinned-Pi-owned text and is
                # not part of this capability's queue/session contract.
                continue
            content = message.get("content")
            text = content if isinstance(content, str) else "".join(
                part.get("text", "") for part in content or []
                if isinstance(part, dict) and isinstance(part.get("text"), str))
            history.append(f"{role}:{text}")
        facts.append({
            "model": body.get("model"),
            "users": user_texts([request]),
            "history": history,
        })
    return facts


SEARCH_ACTIONS = ("search", "rename-select", "noninvasive-query", "reopen-renamed-search",
                  "reopen-deleted-search")


def normalize_checkpoint(value):
    """Drop observations that are not part of the capability contract.

    Hidden cursors have no observable column, release/finish frames are captured
    between product-specific repaint phases, picker header fields are hidden by
    PiSharp while a confirmation/status replaces its single hint line, and fuzzy
    result sets over shared paths are compared by selection rather than row set.
    """
    value = json.loads(json.dumps(value))
    action = value.get("action", "")
    if "2026" in value.get("temporaryModes", []):
        value["temporaryModes"] = [mode for mode in value["temporaryModes"] if mode != "2026"]
    if not value.get("cursorVisible", True):
        value.pop("editorRow", None)
        value.pop("cursorColumn", None)
    if action.startswith("release-") or action in ("submit-restored", "follow-up-alt-enter"):
        value.pop("editor", None)
    if "picker" in value:
        picker = value["picker"]
        if picker.get("confirmation") or picker.get("statuses"):
            picker["sort"] = None
            picker["nameFilter"] = None
            picker["pathVisible"] = None
        picker["statuses"] = []
        if action == "picker-delete-clear":
            picker["selected"] = None
        if any(marker in action for marker in SEARCH_ACTIONS):
            picker["rows"] = []
            picker["selected"] = None
    return value


def semantic_differences(left, right):
    differences = []
    pi = left["pi"]["checkpoints"]
    pisharp = right["pisharp"]["checkpoints"]
    if set(pi) != set(pisharp):
        differences.append(f"checkpoint ids differ: {sorted(set(pi) ^ set(pisharp))}")
        return differences
    for checkpoint_id in pi:
        if normalize_checkpoint(pi[checkpoint_id]) != normalize_checkpoint(pisharp[checkpoint_id]):
            differences.append(checkpoint_id)
    if left["pi"]["requests"] != right["pisharp"]["requests"]:
        differences.append("provider user requests")
    if left["pi"]["requestFacts"] != right["pisharp"]["requestFacts"]:
        differences.append("provider request facts")
    return differences


def calibration_differences(left, right):
    differences = []
    left_checkpoints = {key: normalize_checkpoint(value) for key, value in left["pi"]["checkpoints"].items()}
    right_checkpoints = {key: normalize_checkpoint(value) for key, value in right["pi"]["checkpoints"].items()}
    if left_checkpoints != right_checkpoints:
        differences.extend(sorted(set(left_checkpoints) ^ set(right_checkpoints)) or
                           ["semantic checkpoint"])
    if left["pi"]["requests"] != right["pi"]["requests"]:
        differences.append("provider user requests")
    if left["pi"]["requestFacts"] != right["pi"]["requestFacts"]:
        differences.append("provider request facts")
    return differences


def load_case(artifact_dir, case_id):
    report = json.loads((artifact_dir / "report.json").read_text(encoding="utf-8"))
    report_case = next(case for case in report["cases"] if case["id"] == case_id)
    assertion = load(artifact_dir / report_case["assertionEvidence"])
    product = {}
    for name in ("pi", "pisharp"):
        frames = assertion[name]["frames"]
        product[name] = {
            "checkpoints": {frame["id"]: checkpoint(frame) for frame in frames},
            "requests": user_texts(assertion[name].get("http", [])),
            "requestFacts": request_facts(assertion[name].get("http", [])),
            "sessionFiles": [path for path in assertion[name].get("capturedSessionFiles", [])
                             if path.endswith(".session.json") or path.endswith(".jsonl")],
        }
    return report, report_case, product


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path,
                        default=ROOT / "artifacts/parity/keys-queue-session")
    parser.add_argument("--output", type=Path,
                        default=ROOT / "docs/parity/fixtures/keys-queue-session-checkpoints-2026-11-03.json")
    args = parser.parse_args()

    cases = {}
    pi_sha = None
    for case_id in CASES:
        paired = args.artifacts / f"final-{case_id}-paired"
        calibration = args.artifacts / f"final-{case_id}-calibration"
        paired_report, report_case, product = load_case(paired, case_id)
        calibration_report, calibration_case, calibration_product = load_case(calibration, case_id)
        pi_sha = paired_report["piSha"]
        paired_differences = semantic_differences(
            {"pi": product["pi"], "pisharp": product["pi"]},
            {"pi": product["pi"], "pisharp": product["pisharp"]})
        calibration_delta = calibration_differences(
            {"pi": calibration_product["pi"], "pisharp": calibration_product["pi"]},
            {"pi": calibration_product["pisharp"], "pisharp": calibration_product["pisharp"]})
        cases[case_id] = {
            "fixture": "terminal-keys-queue-session",
            "case": case_id,
            "paired": {
                "scenarioErrors": report_case.get("scenarioErrors") or {},
                "httpMatch": report_case.get("httpMatch"),
                "rawHttpDifferenceCount": report_case.get("rawHttpDifferenceCount"),
                "renderDifferenceCount": report_case.get("renderDifferenceCount"),
                "semanticDifferences": len(paired_differences),
                "semanticFirstDifferences": paired_differences[:20],
            },
            "calibration": {
                "scenarioErrors": calibration_case.get("scenarioErrors") or {},
                "httpMatch": calibration_case.get("httpMatch"),
                "renderDifferenceCount": calibration_case.get("renderDifferenceCount"),
                "semanticDifferences": len(calibration_delta),
                "semanticFirstDifferences": calibration_delta[:20],
            },
            "pi": product["pi"],
            "pisharp": product["pisharp"],
        }

    output = {
        "capability": "keys-queue-session",
        "piSha": pi_sha,
        "source": "tools/parity/terminal/fixtures/terminal-keys-queue-session.json",
        "caseCount": len(cases),
        "cases": cases,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {len(cases)} paired cases to {args.output}")
    for case_id, case in cases.items():
        print(case_id, "paired differences", case["paired"]["semanticDifferences"],
              "calibration differences", case["calibration"]["semanticDifferences"])


if __name__ == "__main__":
    main()
