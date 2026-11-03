#!/usr/bin/env python3
"""Extract compact pinned-Pi and PiSharp app-control terminal checkpoints.

The paired terminal harness keeps multi-gigabyte raw streams under the ignored
artifacts directory. This script reads only the fixture-selected assertion
frames and writes the compact editor/cursor/process evidence that is committed
under docs/parity/fixtures.
"""
import argparse
import gzip
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
FIXTURES = ("tree", "fork", "none", "bash", "suspend", "abort-stream")
TEMPORARY_PRIVATE_MODES = ("1000", "1002", "1003", "1004", "1006", "1049", "2004", "2026")


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


def checkpoint(frame):
    state = frame["state"]
    modes = state.get("modes", {})
    private = modes.get("private", {})
    return {
        "editor": editor_lines(state),
        "cursor": state.get("cursor", {}),
        "activeScreen": state.get("activeScreen"),
        "temporaryModes": sorted(mode for mode in TEMPORARY_PRIVATE_MODES if private.get(mode, False)),
        "bracketedPasteMode": bool(modes.get("bracketedPasteMode", False)),
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


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path,
                        default=ROOT / "artifacts/parity/keys-app-control")
    parser.add_argument("--output", type=Path,
                        default=ROOT / "docs/parity/fixtures/keys-app-control-checkpoints-2026-11-03.json")
    args = parser.parse_args()

    cases = {}
    pi_sha = None
    for fixture in FIXTURES:
        paired = args.artifacts / f"final-{fixture}-paired"
        calibration = args.artifacts / f"final-{fixture}-calibration"
        paired_report = json.loads((paired / "report.json").read_text(encoding="utf-8"))
        calibration_report = json.loads((calibration / "report.json").read_text(encoding="utf-8"))
        pi_sha = paired_report["piSha"]
        calibration_cases = {case["id"]: case for case in calibration_report["cases"]}
        for report_case in paired_report["cases"]:
            case_id = report_case["id"]
            assertion = load(paired / report_case["assertionEvidence"])
            calibration_case = calibration_cases[case_id]
            product = {}
            for name in ("pi", "pisharp"):
                frames = assertion[name]["frames"]
                product[name] = {
                    "checkpoints": {frame["id"]: checkpoint(frame) for frame in frames},
                    "requests": user_texts(assertion[name].get("http", [])),
                }
            cases[f"{fixture}/{case_id}"] = {
                "fixture": fixture,
                "case": case_id,
                "paired": {
                    "scenarioErrors": report_case.get("scenarioErrors") or {},
                    "httpMatch": report_case.get("httpMatch"),
                    "rawHttpDifferenceCount": report_case.get("rawHttpDifferenceCount"),
                    "renderDifferenceCount": report_case.get("renderDifferenceCount"),
                    "comparedRenderFrameCount": report_case.get("comparedRenderFrameCount"),
                },
                "calibration": {
                    "scenarioErrors": calibration_case.get("scenarioErrors") or {},
                    "differences": calibration_case.get("differences"),
                    "httpMatch": calibration_case.get("httpMatch"),
                    "renderDifferenceCount": calibration_case.get("renderDifferenceCount"),
                },
                "pi": product["pi"],
                "pisharp": product["pisharp"],
            }

    output = {
        "capability": "keys-app-control",
        "piSha": pi_sha,
        "source": "tools/parity/terminal/fixtures/terminal-app-control-*.json",
        "caseCount": len(cases),
        "cases": cases,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"wrote {len(cases)} paired cases to {args.output}")


if __name__ == "__main__":
    main()
