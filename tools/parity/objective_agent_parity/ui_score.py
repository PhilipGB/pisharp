#!/usr/bin/env python3
"""Score deterministic terminal progress and visual parity for ENV-PARSER-001.

Input is the evidence produced by tools/parity/terminal/run.py for the
objective-agent-progress fixture. The function only normalises the dynamic
values permitted by the evaluation rubric: workspace paths, usage values,
scroll counters, timings and animated spinner glyphs. Layout, colours, missing
indicators and progress states are compared exactly.
"""
from __future__ import annotations

import gzip
import json
import re
from pathlib import Path
from typing import Any

SPINNERS = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏"
PROGRESS_CHECKPOINTS = ["tool-start", "tool-progress", "tool-complete",
                        "assistant-resume", "final-idle"]
VISUAL_CHECKPOINTS = ["startup", "paste-tool-prompt", *PROGRESS_CHECKPOINTS]
SIZE_CASES = ["80x24-dark-fullscreen", "120x40-dark-fullscreen"]
RECOVERY_CHECKPOINTS = [
    ("tool-start", "working"),
    ("tool-failure", "failure_output"),
    ("tool-failure", "failure_hint"),
    ("run-recovery", "success_output"),
    ("final-idle", "final_idle"),
]
RECOVERY_VISUAL_CHECKPOINTS = ["startup", "paste-tool-prompt", "tool-start", "tool-failure",
                               "expand-failure", "collapse-failure", "run-recovery", "final-idle", "exit"]


def _text(line: dict[str, Any]) -> str:
    return "".join(cell.get("chars") or " " for cell in line["cells"])


def normalise_text(value: str) -> str:
    value = value.replace("/tmp/pisharp-terminal-fixture-workspace", "<workspace>")
    value = re.sub(r"\d+\.\d+%/\d+\.\dk", "<usage>", value)
    value = re.sub(r"[↑↓]\d+\s+[↑↓]\d+", "<scroll>", value)
    value = re.sub(r"(?:Elapsed|Took)\s+\d+\.\d+s", "<duration>", value)
    value = re.sub(f"[{SPINNERS}]", "<spinner>", value)
    return value.rstrip()


def normalised_lines(frame: dict[str, Any]) -> list[str]:
    return [normalise_text(_text(line)) for line in frame["state"]["viewport"]]


def semantics(frame: dict[str, Any]) -> dict[str, bool]:
    text = "\n".join(normalised_lines(frame))
    return dict(
        working="Working" in text,
        progress="objective-line-one" in text,
        completed="objective-line-three" in text and "<duration>" in text,
        resumed="Objective tool lifecycle complete." in text,
        idle="Working" not in text,
        footer="fixture-model" in text,
    )


def style_key(cell: dict[str, Any]) -> tuple:
    attributes = cell.get("attributes") or {}
    underline = cell.get("underline") or {}
    return (
        (cell.get("foreground") or {}).get("mode"),
        (cell.get("foreground") or {}).get("value"),
        (cell.get("background") or {}).get("mode"),
        (cell.get("background") or {}).get("value"),
        tuple(sorted((key, bool(value)) for key, value in attributes.items())),
        underline.get("style"),
        underline.get("color"),
        cell.get("hyperlink"),
    )


def style_grid(frame: dict[str, Any]) -> list[list[tuple]]:
    return [[style_key(cell) for cell in line["cells"]] for line in frame["state"]["viewport"]]


def layout_signature(frame: dict[str, Any]) -> list[tuple[int, int, int, bool]]:
    signature = []
    for row, line in enumerate(frame["state"]["viewport"]):
        text = _text(line)
        if text.strip():
            signature.append((row, len(text) - len(text.lstrip()), len(text.rstrip()), bool(line.get("wrapped"))))
    return signature


def load_frames(path: Path) -> dict[str, dict[str, Any]]:
    document = json.load(gzip.open(path, "rt"))
    return {product: {frame["id"]: frame for frame in document[product]["frames"]}
            for product in ("pi", "pisharp")}


def score_case(assertion_path: Path) -> dict[str, Any]:
    frames = load_frames(assertion_path)
    pi_frames, pisharp_frames = frames["pi"], frames["pisharp"]
    details: dict[str, Any] = {"checkpoints": {}, "visual": {}, "differences": []}

    progress = 0
    for checkpoint in PROGRESS_CHECKPOINTS:
        pi_frame = pi_frames.get(checkpoint)
        pisharp_frame = pisharp_frames.get(checkpoint)
        if pi_frame is None or pisharp_frame is None:
            details["checkpoints"][checkpoint] = dict(
                points=0, reason="missing checkpoint frame",
                piPresent=pi_frame is not None, pisharpPresent=pisharp_frame is not None)
            continue
        pi_semantics = semantics(pi_frame)
        pisharp_semantics = semantics(pisharp_frame)
        expected = {
            "tool-start": "working",
            "tool-progress": "progress",
            "tool-complete": "completed",
            "assistant-resume": "resumed",
            "final-idle": "footer",
        }[checkpoint]
        extra = None
        if checkpoint == "tool-progress":
            extra = pi_semantics["completed"] == pisharp_semantics["completed"]
        elif checkpoint in ("assistant-resume", "final-idle"):
            extra = pi_semantics["idle"] == pisharp_semantics["idle"]
        represented = (pi_semantics[expected] == pisharp_semantics[expected]
                       and pisharp_semantics[expected]
                       and (extra is None or extra))
        points = 2 if represented else 0
        progress += points
        details["checkpoints"][checkpoint] = dict(
            points=points, pi=pi_semantics, pisharp=pisharp_semantics, reason="represented" if represented else "state mismatch")
    details["progressPoints"] = progress
    details["progressMax"] = 10

    text_positions = 0
    spacing = 0
    colours = 0
    checkpoints_text = True
    checkpoints_layout = True
    checkpoints_styles = True
    for checkpoint in VISUAL_CHECKPOINTS:
        pi_frame = pi_frames.get(checkpoint)
        pisharp_frame = pisharp_frames.get(checkpoint)
        if pi_frame is None or pisharp_frame is None:
            checkpoints_text = checkpoints_layout = checkpoints_styles = False
            continue
        if normalised_lines(pi_frame) != normalised_lines(pisharp_frame):
            checkpoints_text = False
            details["differences"].append(dict(checkpoint=checkpoint, kind="text"))
        if layout_signature(pi_frame) != layout_signature(pisharp_frame):
            checkpoints_layout = False
            details["differences"].append(dict(checkpoint=checkpoint, kind="layout"))
        pi_styles = style_grid(pi_frame)
        pisharp_styles = style_grid(pisharp_frame)
        mismatched = sum(1 for row, cells in enumerate(pi_styles)
                         for column, key in enumerate(cells)
                         if key != pisharp_styles[row][column])
        if mismatched:
            checkpoints_styles = False
            details["differences"].append(dict(checkpoint=checkpoint, kind="styles", cells=mismatched,
                                               totalCells=sum(len(row) for row in pi_styles)))
    text_positions = 2 if checkpoints_text else 0
    spacing = 1 if checkpoints_layout else 0
    colours = 1 if checkpoints_styles else 0

    final_ok = True
    for checkpoint in ("final-idle", "exit"):
        pi_frame = pi_frames.get(checkpoint)
        pisharp_frame = pisharp_frames.get(checkpoint)
        if pi_frame is None or pisharp_frame is None:
            final_ok = False
            continue
        if normalised_lines(pi_frame)[-4:] != normalised_lines(pisharp_frame)[-4:]:
            final_ok = False
        pi_state = pi_frame["state"]
        pisharp_state = pisharp_frame["state"]
        if pi_state.get("cursor") != pisharp_state.get("cursor"):
            final_ok = False
        if pi_state.get("activeScreen") != pisharp_state.get("activeScreen"):
            final_ok = False
        if pi_state.get("modes") != pisharp_state.get("modes"):
            final_ok = False
    footer = 1 if final_ok else 0

    details["visual"] = dict(
        labelsIndicatorsPositions=dict(points=text_positions, max=2, match=checkpoints_text),
        spacingIndentationWrappingLayout=dict(points=spacing, max=1, match=checkpoints_layout),
        coloursAndStyles=dict(points=colours, max=1, match=checkpoints_styles),
        footerCursorFinalState=dict(points=footer, max=1, match=final_ok),
        total=text_positions + spacing + colours + footer,
        max=5,
    )
    details["caseTotal"] = progress + text_positions + spacing + colours + footer
    details["caseMax"] = 20
    return details


RECOVERY_CALIBRATION_CHECKPOINTS = RECOVERY_VISUAL_CHECKPOINTS


def recovery_viewport_calibration(directory: Path) -> dict[str, Any]:
    cases = []
    ok = True
    for case_id in SIZE_CASES:
        path = directory / f"{case_id}.assertions.json.gz"
        if not path.is_file():
            cases.append(dict(id=case_id, match=False, reason="missing assertion evidence"))
            ok = False
            continue
        frames = load_frames(path)
        mismatches = []
        for checkpoint in RECOVERY_CALIBRATION_CHECKPOINTS:
            pi_frame = frames["pi"].get(checkpoint)
            pisharp_frame = frames["pisharp"].get(checkpoint)
            if pi_frame is None or pisharp_frame is None:
                mismatches.append(dict(checkpoint=checkpoint, kind="missing"))
                continue
            if normalised_lines(pi_frame) != normalised_lines(pisharp_frame):
                mismatches.append(dict(checkpoint=checkpoint, kind="text"))
            if layout_signature(pi_frame) != layout_signature(pisharp_frame):
                mismatches.append(dict(checkpoint=checkpoint, kind="layout"))
            if style_grid(pi_frame) != style_grid(pisharp_frame):
                mismatches.append(dict(checkpoint=checkpoint, kind="styles"))
        cases.append(dict(id=case_id, match=not mismatches, differences=mismatches))
        ok = ok and not mismatches
    return dict(match=ok, checkpoints=RECOVERY_CALIBRATION_CHECKPOINTS, cases=cases)


def recovery_semantics(frame: dict[str, Any]) -> dict[str, bool]:
    text = "\n".join(normalised_lines(frame))
    return dict(
        working="Working" in text,
        failureOutput="Failed: 1" in text,
        failureStatus="Command exited with code 1" in text,
        failureHint="... (4 earlier lines, ctrl+o to expand)" in text,
        expanded="objective-recovery-one" in text,
        successOutput="Passed: 1" in text,
        finalResponse="Recovery complete." in text,
        idle="Working" not in text,
    )


def score_recovery_case(assertion_path: Path) -> dict[str, Any]:
    frames = load_frames(assertion_path)
    pi_frames, pisharp_frames = frames["pi"], frames["pisharp"]
    details: dict[str, Any] = {"checkpoints": {}, "visual": {}, "differences": []}
    progress = 0
    for checkpoint, semantic in RECOVERY_CHECKPOINTS:
        pi_frame = pi_frames.get(checkpoint)
        pisharp_frame = pisharp_frames.get(checkpoint)
        if pi_frame is None or pisharp_frame is None:
            details["checkpoints"][f"{checkpoint}:{semantic}"] = dict(points=0, reason="missing frame")
            continue
        pi_state = recovery_semantics(pi_frame)
        pisharp_state = recovery_semantics(pisharp_frame)
        key = {"failure_output": "failureOutput", "failure_status": "failureStatus",
               "failure_hint": "failureHint",
               "success_output": "successOutput", "final_idle": "finalResponse"}.get(semantic, semantic)
        represented = bool(pi_state[key] and pisharp_state[key])
        if semantic == "final_idle":
            represented = represented and pi_state["idle"] == pisharp_state["idle"] and pisharp_state["idle"]
        points = 2 if represented else 0
        progress += points
        details["checkpoints"][f"{checkpoint}:{semantic}"] = dict(
            points=points, pi=pi_state, pisharp=pisharp_state,
            reason="represented" if represented else "state mismatch")
    details["progressPoints"] = progress
    details["progressMax"] = 10

    text_ok = True
    layout_ok = True
    styles_ok = True
    for checkpoint in RECOVERY_VISUAL_CHECKPOINTS:
        pi_frame = pi_frames.get(checkpoint)
        pisharp_frame = pisharp_frames.get(checkpoint)
        if pi_frame is None or pisharp_frame is None:
            text_ok = layout_ok = styles_ok = False
            continue
        if normalised_lines(pi_frame) != normalised_lines(pisharp_frame):
            text_ok = False
            details["differences"].append(dict(checkpoint=checkpoint, kind="text"))
        if layout_signature(pi_frame) != layout_signature(pisharp_frame):
            layout_ok = False
            details["differences"].append(dict(checkpoint=checkpoint, kind="layout"))
        pi_styles = style_grid(pi_frame)
        pisharp_styles = style_grid(pisharp_frame)
        mismatched = sum(1 for row, cells in enumerate(pi_styles)
                         for column, key in enumerate(cells)
                         if key != pisharp_styles[row][column])
        if mismatched:
            styles_ok = False
            details["differences"].append(dict(checkpoint=checkpoint, kind="styles", cells=mismatched,
                                               totalCells=sum(len(row) for row in pi_styles)))
    footer_ok = True
    for checkpoint in ("final-idle", "exit"):
        pi_frame = pi_frames.get(checkpoint)
        pisharp_frame = pisharp_frames.get(checkpoint)
        if pi_frame is None or pisharp_frame is None:
            footer_ok = False
            continue
        if normalised_lines(pi_frame)[-4:] != normalised_lines(pisharp_frame)[-4:]:
            footer_ok = False
        if pi_frame["state"].get("cursor") != pisharp_frame["state"].get("cursor"):
            footer_ok = False
        if pi_frame["state"].get("activeScreen") != pisharp_frame["state"].get("activeScreen"):
            footer_ok = False
        if pi_frame["state"].get("modes") != pisharp_frame["state"].get("modes"):
            footer_ok = False
    text_points = 2 if text_ok else 0
    layout_points = 1 if layout_ok else 0
    style_points = 1 if styles_ok else 0
    footer_points = 1 if footer_ok else 0
    details["visual"] = dict(
        labelsIndicatorsPositions=dict(points=text_points, max=2, match=text_ok),
        spacingIndentationWrappingLayout=dict(points=layout_points, max=1, match=layout_ok),
        coloursAndStyles=dict(points=style_points, max=1, match=styles_ok),
        footerCursorFinalState=dict(points=footer_points, max=1, match=footer_ok),
        total=text_points + layout_points + style_points + footer_points,
        max=5,
    )
    details["caseTotal"] = progress + text_points + layout_points + style_points + footer_points
    details["caseMax"] = 20
    return details


def score_recovery_ui(calibration_directory: Path, comparison_directory: Path) -> dict[str, Any]:
    calibration_report = json.loads((calibration_directory / "report.json").read_text())
    comparison_report = json.loads((comparison_directory / "report.json").read_text())
    viewport = recovery_viewport_calibration(calibration_directory)
    calibration_ok = bool(viewport["match"])
    result: dict[str, Any] = {
        "fixture": "objective-agent-recovery",
        "calibration": {
            "directory": str(calibration_directory),
            "harnessMatch": bool(calibration_report.get("match")),
            "viewportMatch": calibration_ok,
            "match": calibration_ok,
            "checkpoints": viewport["checkpoints"],
            "cases": viewport["cases"],
            "note": "Same-Pi calibration requires every stable event-boundary checkpoint to match "
                    "in text, layout and styles; the raw render stream is retained as evidence.",
        },
        "comparison": {
            "directory": str(comparison_directory),
            "match": bool(comparison_report.get("match")),
        },
        "cases": [],
        "progressPoints": 0,
        "visualPoints": 0,
        "total": 0,
        "max": 20,
    }
    if not calibration_ok:
        result["status"] = "inconclusive"
        result["reason"] = "Same-Pi calibration failed; cross-product visual comparison is not valid."
        return result
    progress = 0
    visual = 0
    for case_id in SIZE_CASES:
        assertion_path = comparison_directory / f"{case_id}.assertions.json.gz"
        if not assertion_path.is_file():
            result["cases"].append(dict(id=case_id, error="missing assertion evidence"))
            continue
        scored = score_recovery_case(assertion_path)
        result["cases"].append(dict(id=case_id, **scored))
        progress += scored["progressPoints"]
        visual += scored["visual"]["total"]
    result["progressPoints"] = progress // len(SIZE_CASES)
    result["visualPoints"] = visual
    result["total"] = result["progressPoints"] + visual
    result["piScore"] = 20
    result["pisharpScore"] = result["total"]
    result["parityScore"] = result["total"]
    result["status"] = "scored"
    return result


def score_ui(calibration_directory: Path, comparison_directory: Path) -> dict[str, Any]:
    calibration_report = json.loads((calibration_directory / "report.json").read_text())
    comparison_report = json.loads((comparison_directory / "report.json").read_text())
    calibration_ok = bool(calibration_report.get("match"))
    result: dict[str, Any] = {
        "fixture": "objective-agent-progress",
        "calibration": {
            "directory": str(calibration_directory),
            "match": calibration_ok,
            "cases": [dict(id=case["id"], match=case["match"], differences=case["differences"])
                      for case in calibration_report.get("cases", [])],
        },
        "comparison": {
            "directory": str(comparison_directory),
            "match": bool(comparison_report.get("match")),
        },
        "cases": [],
        "progressPoints": 0,
        "visualPoints": 0,
        "total": 0,
        "max": 20,
    }
    if not calibration_ok:
        result["status"] = "inconclusive"
        result["reason"] = "Same-Pi calibration failed; cross-product visual comparison is not valid."
        return result
    progress = 0
    visual = 0
    for case_id in SIZE_CASES:
        assertion_path = comparison_directory / f"{case_id}.assertions.json.gz"
        if not assertion_path.is_file():
            result["cases"].append(dict(id=case_id, error="missing assertion evidence"))
            continue
        scored = score_case(assertion_path)
        result["cases"].append(dict(id=case_id, **scored))
        progress += scored["progressPoints"]
        visual += scored["visual"]["total"]
    result["progressPoints"] = progress // len(SIZE_CASES)
    result["visualPoints"] = visual
    result["total"] = result["progressPoints"] + visual
    result["piScore"] = 20
    result["pisharpScore"] = result["total"]
    result["parityScore"] = result["total"]
    result["status"] = "scored"
    return result
