#!/usr/bin/env python3
"""Extract compact pinned-Pi and PiSharp keys-model-display checkpoints.

The paired terminal harness keeps raw render streams under the ignored
artifacts directory. This script commits only the fixture-selected editor,
cursor, footer, status and provider-request evidence.
"""
import argparse
import gzip
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
CASES = (
    "80x24-dark-fullscreen",
    "80x24-dark-regular",
    "120x40-dark-fullscreen",
    "120x40-dark-regular",
)
TEMPORARY_PRIVATE_MODES = ("1000", "1002", "1003", "1004", "1006", "1049", "2004", "2026")
STATUS_PREFIXES = ("Switched to", "Model:", "Thinking level:", "Tool output:", "Thinking blocks:", "Copied")
STATUS_ACTIONS = ("model-cycle-", "thinking-cycle", "tool-expand", "thinking-toggle", "copy-last-assistant")


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
    return [line for line in lines[separators[-2] + 1:separators[-1]] if line.strip()]


def latest_status(lines, action_id):
    if not action_id.startswith(STATUS_ACTIONS):
        return ""
    for line in reversed(lines):
        text = line.strip()
        if any(text.startswith(prefix) for prefix in STATUS_PREFIXES):
            return text
    return ""


def footer(lines):
    for line in reversed(lines):
        text = line.strip()
        if "•" in text:
            # Pi renders a transient scroll indicator before the usage footer; PiSharp
            # keeps the same footer without it. Compare the model/thinking disclosure.
            parts = text.split()
            while parts and (parts[0].startswith("↑") or parts[0].startswith("↓")):
                parts = parts[1:]
            return " ".join(parts)
    return ""


def checkpoint(frame):
    state = frame["state"]
    modes = state.get("modes", {})
    private = modes.get("private", {})
    lines = viewport_lines(state)
    separators = [index for index, line in enumerate(lines) if line.lstrip().startswith("─")]
    editor_top = separators[-2] if len(separators) >= 2 else 0
    cursor = state.get("cursor", {})
    return {
        "editor": editor_lines(state),
        "cursor": cursor,
        "editorRow": cursor.get("y", 0) - editor_top,
        "activeScreen": state.get("activeScreen"),
        "temporaryModes": sorted(mode for mode in TEMPORARY_PRIVATE_MODES if private.get(mode, False)),
        "bracketedPasteMode": bool(modes.get("bracketedPasteMode", False)),
        "footer": footer(lines),
        "status": latest_status(lines, frame["id"]),
    }


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
            content = message.get("content")
            text = content if isinstance(content, str) else "".join(
                part.get("text", "") for part in content or []
                if isinstance(part, dict) and isinstance(part.get("text"), str))
            history.append(f"{message.get('role')}:{text}")
        facts.append({
            "model": body.get("model"),
            "thinking": body.get("reasoning_effort") or (body.get("reasoning") or {}).get("effort"),
            "users": user_texts([request]),
            "history": history,
        })
    return facts


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path,
                        default=ROOT / "artifacts/parity/keys-model-display")
    parser.add_argument("--output", type=Path,
                        default=ROOT / "docs/parity/fixtures/keys-model-display-checkpoints-2026-11-03.json")
    args = parser.parse_args()

    cases = {}
    pi_sha = None
    for case_id in CASES:
        paired = args.artifacts / f"final-{case_id}-paired"
        calibration = args.artifacts / f"final-{case_id}-calibration"
        paired_report = json.loads((paired / "report.json").read_text(encoding="utf-8"))
        calibration_report = (json.loads((calibration / "report.json").read_text(encoding="utf-8"))
                              if (calibration / "report.json").exists() else None)
        pi_sha = paired_report["piSha"]
        for report_case in paired_report["cases"]:
            assertion = load(paired / report_case["assertionEvidence"])
            product = {}
            for name in ("pi", "pisharp"):
                frames = assertion[name]["frames"]
                product[name] = {
                    "checkpoints": {frame["id"]: checkpoint(frame) for frame in frames},
                    "requests": user_texts(assertion[name].get("http", [])),
                    "requestFacts": request_facts(assertion[name].get("http", [])),
                }
            calibration_case = None
            if calibration_report is not None:
                calibration_case = next((case for case in calibration_report["cases"]
                                         if case["id"] == case_id), None)
            cases[f"{case_id}"] = {
                "fixture": "terminal-keys-model-display",
                "case": case_id,
                "paired": {
                    "scenarioErrors": report_case.get("scenarioErrors") or {},
                    "httpMatch": report_case.get("httpMatch"),
                    "rawHttpDifferenceCount": report_case.get("rawHttpDifferenceCount"),
                    "renderDifferenceCount": report_case.get("renderDifferenceCount"),
                    "comparedRenderFrameCount": report_case.get("comparedRenderFrameCount"),
                    "clipboardMatch": report_case.get("clipboardMatch"),
                },
                "calibration": None if calibration_case is None else {
                    "scenarioErrors": calibration_case.get("scenarioErrors") or {},
                    "differences": calibration_case.get("differences"),
                    "httpMatch": calibration_case.get("httpMatch"),
                    "renderDifferenceCount": calibration_case.get("renderDifferenceCount"),
                },
                "pi": product["pi"],
                "pisharp": product["pisharp"],
            }

    output = {
        "capability": "keys-model-display",
        "piSha": pi_sha,
        "source": "tools/parity/terminal/fixtures/terminal-keys-model-display.json",
        "caseCount": len(cases),
        "cases": cases,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {len(cases)} paired cases to {args.output}")


if __name__ == "__main__":
    main()
